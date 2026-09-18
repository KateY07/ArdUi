namespace ArdUi;

// A per-process Windows job prevents abandoned ARD children after a crash.
sealed class ChildJob : IDisposable
{
    nint handle;
    [StructLayout(LayoutKind.Sequential)] struct Basic
    { public long User, Job; public uint Flags; public nuint Min, Max; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Counters
    { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Limits
    { public Basic Basic; public Counters Counters; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int kind, ref Limits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    public ChildJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        handle = CreateJobObject(0, null);
        var limits = new Limits { Basic = new Basic { Flags = 0x2000 } };
        if (handle == 0 || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>()) ||
            !AssignProcessToJobObject(handle, process.Handle))
        { Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法保护子进程生命周期。"); }
    }
    public void Dispose() { if (handle != 0) { CloseHandle(handle); handle = 0; } }
}

sealed class Child : IAsyncDisposable
{
    public Process Process { get; }
    public Task Exited { get; }
    readonly ChildJob job;
    readonly ConcurrentQueue<string> lines = new();
    readonly Task pump;
    string network = "正在连接";
    double? pathRtt;
    public string Network => network;
    public double? PathRtt => pathRtt;
    public event Action<string>? Output;
    int disposed;
    public Child(string exe, string cwd, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment.Remove("RUST_LOG"); start.Environment["NO_COLOR"] = "1";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process = Process.Start(start) ?? throw new IOException("无法启动 " + Path.GetFileName(exe));
        try { job = new ChildJob(Process); }
        catch { Process.Kill(true); Process.Dispose(); throw; }
        Exited = Process.WaitForExitAsync();
        pump = Task.WhenAll(Pump(Process.StandardError), Pump(Process.StandardOutput));
    }
    async Task Pump(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Enqueue(line); while (lines.Count > 100) lines.TryDequeue(out _);
            Diagnostics.Log("ard",line);
            var transport = Regex.Match(line, "transport=\\\"(?<v>direct|relay)\\\"");
            var kind = Regex.Match(line, "network=\\\"(?<v>ipv4|ipv6|relay)\\\"");
            var rtt = Regex.Match(line, @"rtt_ms=(?:Some\()?(?<v>[0-9]+(?:\.[0-9]+)?)");
            if (transport.Success)
                network = transport.Groups["v"].Value == "direct" ? "P2P 直连 / " + (kind.Success ? kind.Groups["v"].Value.ToUpperInvariant() : "IP") : "ArdRelay 中继";
            if (rtt.Success && double.TryParse(rtt.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture, out var value)) pathRtt = value;
            Output?.Invoke(line);
        }
    }
    public async Task WaitFor(string text, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        while (!lines.Any(l => l.Contains(text, StringComparison.Ordinal)))
        {
            if (Exited.IsCompleted) { await pump; throw new IOException($"{Path.GetFileName(Process.StartInfo.FileName)} 启动失败：\n{string.Join('\n', lines.TakeLast(5))}"); }
            try { await Task.Delay(70, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new IOException($"等待 {text} 超时。\n{string.Join('\n', lines.TakeLast(8))}"); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (!Process.HasExited) Process.Kill(true); await Exited; await pump; }
        finally { job.Dispose(); Process.Dispose(); }
    }
}

static class Ard
{
    static readonly Lazy<string> executable = new(() =>
    {
        const string expected="04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d";
        var path=Path.GetFullPath(Environment.GetEnvironmentVariable("ARDUI_ARD_PATH")??Path.Combine(AppContext.BaseDirectory,"ard.exe"));
        if(!File.Exists(path))throw new IOException("缺少 ard.exe，请重新执行官方 install.ps1。");
        using var file=File.OpenRead(path);var hash=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if(hash!=expected)throw new IOException("ard.exe 校验失败，请重新执行官方 install.ps1。");
        return path;
    });
    public static string Exe => executable.Value;
    public static async Task<string> Identity(string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Exe) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--quiet"); start.ArgumentList.Add("id");
        using var p = Process.Start(start) ?? throw new IOException("无法启动 ard.exe。");
        var output = p.StandardOutput.ReadToEndAsync(ct); var error = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch { if (!p.HasExited) p.Kill(true); throw; }
        if (p.ExitCode != 0) throw new IOException(await error);
        return Wire.Endpoint(await output);
    }
    public static Child Start(string dir, bool host, int port, string peer, Settings settings) =>
        new(Exe, dir, host ? "open" : "forward", port.ToString(), "to", Wire.Endpoint(peer),
            "--relay", settings.Relay, "--relay-key", settings.RelayKey);
}
