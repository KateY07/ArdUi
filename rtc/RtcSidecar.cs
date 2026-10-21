namespace ArdUi;

// Where the WebRTC link lives: the small child process ardrtc.exe (see rtc/main.go). It does the WebRTC part (ICE, DTLS, data channels) and
// hands ArdUi one local TCP connection; ArdUi authenticates and encrypts on top of that connection itself.
sealed class RtcOptions
{
    // STUN servers that are reachable from mainland China first.
    public string[] Stun { get; init; } = ["stun:stun.miwifi.com:3478", "stun:stun.qq.com:3478", "stun:stun.l.google.com:19302"];
    public string? Turn { get; init; }
    public string? TurnUser { get; init; }
    public string? TurnPassword { get; init; }
    public bool Loopback { get; init; }                                        // tests: let two peers on one machine find each other
    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(60);            // the link must be open by then
    public TimeSpan Beat { get; init; } = TimeSpan.FromSeconds(1);             // heartbeat interval
    public TimeSpan Dead { get; init; } = TimeSpan.FromSeconds(6);             // no heartbeat for this long: the link is down
}

sealed class RtcSidecar : IAsyncDisposable
{
    readonly Process process;
    readonly ChildJob job;
    readonly StreamWriter input;
    readonly TaskCompletionSource<string> description = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<int> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentQueue<string> log = new();
    public string IceState { get; private set; } = "new";
    public string? PathLocal { get; private set; }       // the kind of candidate this side uses: host, srflx, prflx or relay
    public string? PathRemote { get; private set; }
    public string? RemoteAddress { get; private set; }
    public bool DatagramOpen { get; private set; }
    public string? CloseReason { get; private set; }
    public Task<string> Description => description.Task;   // the offer (host) or the answer (join), complete with candidates
    public Task<int> Ready => ready.Task;                    // the local TCP port, once the reliable channel is open
    public Task Closed => closed.Task;
    public string Log => string.Join('\n', log);

    public static string Locate()
    {
        var env = Environment.GetEnvironmentVariable("ARDUI_RTC_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "ardrtc.exe"), Path.Combine(AppContext.BaseDirectory, "rtc", "ardrtc.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "rtc", "ardrtc.exe")), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "rtc", "ardrtc.exe")) })
            if (File.Exists(candidate)) return candidate;
        throw new IOException("找不到 ardrtc.exe（WebRTC 链路组件）。");
    }

    RtcSidecar(string role, byte[] capability, RtcOptions options)
    {
        var exe = Locate();
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var argument in new[] { "--role", role, "--cap", Convert.ToHexString(capability).ToLowerInvariant(), "--stun", string.Join(',', options.Stun), "--wait", ((int)options.Wait.TotalSeconds) + "s" })
            start.ArgumentList.Add(argument);
        if (options.Loopback) start.ArgumentList.Add("--loopback");
        if (options.Turn != null) { start.ArgumentList.Add("--turn"); start.ArgumentList.Add(options.Turn); start.ArgumentList.Add("--turn-user"); start.ArgumentList.Add(options.TurnUser ?? ""); start.ArgumentList.Add("--turn-pass"); start.ArgumentList.Add(options.TurnPassword ?? ""); }
        process = Process.Start(start) ?? throw new IOException("无法启动 ardrtc.exe。");
        try { job = new ChildJob(process); }
        catch { process.Kill(true); process.Dispose(); throw; }
        input = process.StandardInput; input.AutoFlush = true;
        _ = Task.Run(() => Read(process.StandardOutput));
        _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) Note(line); });
        _ = process.WaitForExitAsync().ContinueWith(_ => { CloseReason ??= "进程已退出"; closed.TrySetResult(); ready.TrySetException(new IOException("WebRTC 链路没有建立：" + CloseReason)); description.TrySetException(new IOException("WebRTC 链路没有建立：" + CloseReason)); }, TaskScheduler.Default);
    }

    public static RtcSidecar Start(string role, byte[] capability, RtcOptions options) => new(role, capability, options);

    void Note(string line) { log.Enqueue(line); while (log.Count > 50) log.TryDequeue(out _); Diagnostics.Log("rtc", line); }

    async Task Read(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Note(line.Length > 300 ? line[..300] + "…" : line);
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                string Text(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "";
                switch (Text("event"))
                {
                    case "offer" or "answer": description.TrySetResult(Text("sdp")); break;
                    case "ready": ready.TrySetResult(root.GetProperty("tcp").GetInt32()); break;
                    case "state": IceState = Text("ice"); break;
                    case "path": PathLocal = Text("local"); PathRemote = Text("remote"); RemoteAddress = Text("remoteAddress"); break;
                    case "dgram": DatagramOpen = true; break;
                    case "error": CloseReason = Text("message"); Note("error: " + CloseReason); break;
                    case "closed": CloseReason ??= Text("reason"); closed.TrySetResult(); break;
                }
            }
            catch (JsonException) { }
        }
    }

    // The remote description, exactly once (the child refuses a second one).
    public void SetRemote(string kind, string sdp)
    {
        lock (input) input.WriteLine(JsonSerializer.Serialize(new { cmd = kind, sdp }));
    }

    public async ValueTask DisposeAsync()
    {
        try { input.Close(); } catch (IOException) { }
        try { if (!process.WaitForExit(1500)) process.Kill(true); } catch (InvalidOperationException) { }
        job.Dispose(); process.Dispose();
        await Task.CompletedTask;
    }

    // Test helper: the process dies without a goodbye (a crash, a cable pulled).
    public void Crash() { try { process.Kill(true); } catch (InvalidOperationException) { } }
}
