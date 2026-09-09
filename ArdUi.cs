using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Styling;
using SkiaSharp;
using ZXing;

namespace ArdUi;

// All application implementation lives in this file. ARD owns peer cryptography.
static class Program
{
    public const string Version = "v1.pre10";
    public static string DataRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("ARDUI_DATA_ROOT") ?? Path.Combine(AppContext.BaseDirectory,"data"));
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--identity-store")
            { Console.WriteLine(IdentityStore.Prepare(args[1])); return 0; }
            if (args.Length == 4 && args[0] == "--verify-release")
            { Release.Verify(args[1],args[2],args[3]); return 0; }
            if (args.Contains("--self-test"))
                return SelfTest.RunAsync().GetAwaiter().GetResult();
            if (args.Contains("--prototype-test"))
                return SelfTest.Prototype().GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--render-preview")
                return Preview.Render(args[1]);
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var path = Path.Combine(Path.GetTempPath(), "ArdUi-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {ex}\n");
            if (args.Length > 0) Console.Error.WriteLine(ex.Message);
            if (OperatingSystem.IsWindows() && args.Length == 0) MessageBox(0, ex.Message + "\n\n" + path, "ArdUi", 16);
            return 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(nint window, string text, string caption, uint type);
}

sealed class Settings
{
    public const string DefaultRelayKey = "spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f";
    public string Server { get; set; } = "https://f.visnova.cn/";
    public string Relay { get; set; } = "http://175.27.160.144:8080";
    public string RelayKey { get; set; } = DefaultRelayKey;
    public int[] TcpPorts { get; set; } = [3389, 445];
    public int[] UdpPorts { get; set; } = [3389];
    public void Validate()
    {
        if (!Uri.TryCreate(Server, UriKind.Absolute, out var server) || server.Scheme != "https" || !string.IsNullOrEmpty(server.UserInfo))
            throw new InvalidDataException("arduiserver 必须使用有效 HTTPS 地址。");
        if (!Uri.TryCreate(Relay, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidDataException("config.json 中的 relay 必须是 HTTP/HTTPS 地址。");
        if (!Regex.IsMatch(RelayKey ?? "", @"\Aspki:[0-9a-fA-F]{2,512}\z"))
            throw new InvalidDataException("config.json 中的 relayKey 必须是固定的 ArdRelay SPKI 公钥。");
        if (TcpPorts is null || UdpPorts is null || TcpPorts.Concat(UdpPorts).Any(p => p is < 1 or > 65535))
            throw new InvalidDataException("允许访问的端口必须在 1–65535 之间。");
    }
}

sealed class Peer
{
    public string Id { get; set; } = "";
    public string Address { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public SignedEnvelope? Grant { get; set; }
}

sealed class State : IDisposable
{
    public string Root { get; }
    public string IdentityDirectory => Path.Combine(Root, "device");
    public List<Peer> Peers { get; }
    readonly FileStream processLock;
    public State(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        processLock = new FileStream(Path.Combine(Root, "app.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(Root, "peers.json");
            Peers = File.Exists(path) ? JsonSerializer.Deserialize<List<Peer>>(File.ReadAllText(path), Wire.Json)!
                : [];
            if (Peers is null || Peers.Any(p => p is null || Wire.Endpoint(p.Id) != p.Id || !IPAddress.TryParse(p.Address, out var ip) || !IPAddress.IsLoopback(ip) || p.Grant == null) ||
                Peers.Select(p => p.Id).Distinct().Count() != Peers.Count ||
                Peers.Select(p => p.Address).Distinct().Count() != Peers.Count)
                throw new InvalidDataException("设备地址记录无效，请检查 data/peers.json；不会自动重置已保存的地址。");
        }
        catch { processLock.Dispose(); throw; }
    }
    public Peer GetOrAdd(string id, string code, SignedEnvelope grant)
    {
        id = Wire.Endpoint(id);
        lock (Peers)
        {
            if (Peers.Find(p => p.Id == id) is { } existing) { existing.Grant = grant; existing.Code = code; Save(); return existing; }
            var address = Enumerable.Range(0, 64000).Select(i => $"127.77.{i / 250}.{i % 250 + 1}")
                .FirstOrDefault(ip => Peers.All(p => p.Address != ip))
                ?? throw new InvalidOperationException("已保存设备数量达到上限。");
            var peer = new Peer { Id = id, Address = address, Code = code, Grant = grant };
            var next = Peers.Append(peer).ToArray();
            var path = Path.Combine(Root, "peers.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next, Wire.Json));
            File.Move(path + ".tmp", path, true);
            Peers.Add(peer);
            return peer;
        }
    }
    public void Save()
    {
        lock (Peers)
        {
            var path = Path.Combine(Root, "peers.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Peers, Wire.Json));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void Remove(string id) { lock (Peers) { Peers.RemoveAll(p => p.Id == id); Save(); } }
    public string NewSessionDirectory()
    {
        var dir = Path.Combine(Root, "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    public void Dispose() => processLock.Dispose();
}

static class Wire
{
    public const int MaxUdp = 1482; // 16-byte session capability + u16 target port, within ARD's 1500 bytes.
    public static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Endpoint(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.StartsWith("ardui:")) value = value[6..].TrimStart('/');
        if (!Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z"))
            throw new InvalidDataException("EndpointId 应为 64 位十六进制公钥。请粘贴完整 ID 或选择二维码图片。");
        return value;
    }
    public static string Machine(string value)
    {
        value = value.Trim().ToUpperInvariant();
        if (value.StartsWith("ARDUI:")) value = value[6..].TrimStart('/');
        if (!Regex.IsMatch(value, @"\A[A-Z0-9]{6}\z")) throw new InvalidDataException("机器编号应为 6 位大写字母或数字。");
        return value;
    }
    public static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static async Task<byte[]> Read(Stream s, int size, CancellationToken ct)
    { var b = new byte[size]; await s.ReadExactlyAsync(b, ct); return b; }
    public static async Task WriteJson<T>(Stream s, T value, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (data.Length > 8192) throw new InvalidDataException("消息过大。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await s.WriteAsync(header, ct); await s.WriteAsync(data, ct);
    }
    public static async Task<T> ReadJson<T>(Stream s, CancellationToken ct)
    {
        var size = BinaryPrimitives.ReadInt32BigEndian(await Read(s, 4, ct));
        if (size is < 1 or > 8192) throw new InvalidDataException("无效的握手长度。");
        return JsonSerializer.Deserialize<T>(await Read(s, size, ct), Json) ?? throw new InvalidDataException("握手为空。");
    }
    public static byte[] Packet(byte[] token, int port, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxUdp) throw new InvalidDataException("UDP 报文超出虚拟网络 MTU。");
        var data = new byte[18 + payload.Length]; token.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), checked((ushort)port));
        payload.CopyTo(data.AsSpan(18)); return data;
    }
    public static bool ValidPacket(byte[] data, byte[] token) => data.Length is >= 18 and <= 1500 &&
        CryptographicOperations.FixedTimeEquals(data.AsSpan(0, 16), token);
    // FIN in one direction must not cancel the reverse direction (RDP/SMB rely on this).
    public static async Task Bridge(TcpClient a, TcpClient b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var aStream = a.GetStream(); var bStream = b.GetStream();
        async Task Copy(NetworkStream from, NetworkStream to, Socket socket)
        {
            try { await from.CopyToAsync(to, linked.Token); socket.Shutdown(SocketShutdown.Send); }
            catch { linked.Cancel(); throw; }
        }
        await Task.WhenAll(Copy(aStream, bStream, b.Client), Copy(bStream, aStream, a.Client));
    }
}

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

static class Release
{
    public static void Verify(string manifest,string signature,string binary)
    {
        using var stream=typeof(Release).Assembly.GetManifestResourceStream("ArdUi.release-public.pem") ?? throw new IOException("缺少发布公钥。");
        using var reader=new StreamReader(stream); using var rsa=RSA.Create(); rsa.ImportFromPem(reader.ReadToEnd());
        var content=File.ReadAllBytes(manifest);
        if(!rsa.VerifyData(content,Convert.FromBase64String(File.ReadAllText(signature).Trim()),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))
            throw new IOException("发布签名无效，拒绝更新。");
        using var document=JsonDocument.Parse(content); var release=document.RootElement;
        using var file=File.OpenRead(binary);
        if(release.GetProperty("size").GetInt64()!=file.Length ||
            !string.Equals(release.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(file)),StringComparison.OrdinalIgnoreCase))
            throw new IOException("发布文件校验失败。");
    }
}

sealed class Gateway : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly UdpClient udp;
    readonly Settings settings;
    readonly byte[] token;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> tasks = new();
    readonly ConcurrentDictionary<string, TargetUdp> udpFlows = new();
    readonly SemaphoreSlim tcpSlots = new(128);
    readonly Task acceptTask, udpTask;
    int serial;
    public int Port { get; }
    public event Action? Attached;
    readonly Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>>? authenticate;
    public Gateway(Settings settings, byte[] token, Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>>? authenticate = null)
    {
        this.settings = settings; this.token = token; this.authenticate = authenticate;
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port)); }
        catch { listener.Stop(); throw; }
        acceptTask = Accept(); udpTask = Receive();
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!tcpSlots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref serial);
                var task = Serve(client); tasks[id] = task;
                _ = task.ContinueWith(t => { tcpSlots.Release(); tasks.TryRemove(id, out _); }, TaskScheduler.Default);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client)
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var head = await Wire.Read(stream, 23, handshake.Token);
                if (!head.AsSpan(0, 4).SequenceEqual("AUI1"u8)) return;
                var command = head[20]; var port = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(21));
                if (command == 2 && port == 0 && authenticate != null)
                {
                    handshake.CancelAfter(TimeSpan.FromMinutes(5));
                    var request = await Wire.ReadJson<PasswordRequest>(stream, handshake.Token);
                    var grant = await authenticate(request, handshake.Token);
                    var accepted = grant != null;
                    await Wire.WriteJson(stream, new AdmissionReply(accepted,
                        accepted ? null : "密码错误、对端拒绝了首次指纹确认，或远程访问已关闭。",
                        accepted ? Convert.ToHexString(token) : null,
                        accepted ? settings.TcpPorts : [], accepted ? settings.UdpPorts : [], grant), handshake.Token);
                    if (accepted) Attached?.Invoke();
                    return;
                }
                if (!CryptographicOperations.FixedTimeEquals(head.AsSpan(4, 16), token)) return;
                if (command == 0 && port == 0)
                { Attached?.Invoke(); await stream.WriteAsync(new byte[] { 0 }, handshake.Token); return; }
                if (command != 1 || !settings.TcpPorts.Contains((int)port))
                { await stream.WriteAsync(new byte[] { 2 }, handshake.Token); return; }
                using var target = new TcpClient { NoDelay = true };
                try { await target.ConnectAsync(IPAddress.Loopback, port, handshake.Token); }
                catch { await stream.WriteAsync(new byte[] { 1 }, stop.Token); return; }
                client.NoDelay = true;
                await stream.WriteAsync(new byte[] { 0 }, handshake.Token);
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                await Wire.Bridge(client, target, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    async Task Receive()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(stop.Token);
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || !Wire.ValidPacket(packet.Buffer, token)) continue;
                var port = (int)BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16));
                if (port == 0)
                {
                    await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, stop.Token);
                    Attached?.Invoke();
                    continue;
                }
                if (!settings.UdpPorts.Contains(port)) continue;
                var key = $"{packet.RemoteEndPoint}/{port}";
                if (!udpFlows.TryGetValue(key, out var flow))
                {
                    if (udpFlows.Count >= 256) continue;
                    flow = new TargetUdp(port, async data =>
                        await udp.SendAsync(Wire.Packet(token, port, data), packet.RemoteEndPoint, stop.Token), stop.Token);
                    udpFlows[key] = flow;
                    var captured = flow;
                    _ = flow.Completion.ContinueWith(t =>
                    { udpFlows.TryRemove(new KeyValuePair<string, TargetUdp>(key, captured)); captured.Dispose(); }, TaskScheduler.Default);
                }
                try { await flow.Send(packet.Buffer.AsMemory(18)); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); udp.Dispose();
        foreach (var flow in udpFlows.Values) flow.Dispose();
        try { await Task.WhenAll(acceptTask, udpTask); } catch { }
        await Task.WhenAll(tasks.Values); stop.Dispose();
    }
}

sealed class TargetUdp : IDisposable
{
    readonly UdpClient client = new(AddressFamily.InterNetwork);
    readonly CancellationTokenSource stop;
    readonly Func<byte[], Task> reply;
    readonly int maximum;
    public Task Completion { get; }
    public TargetUdp(int port, Func<byte[], Task> reply, CancellationToken ct, int maximum = Wire.MaxUdp)
    {
        this.reply = reply; this.maximum = maximum; stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        client.Connect(IPAddress.Loopback, port); stop.CancelAfter(TimeSpan.FromMinutes(2));
        Completion = Receive();
    }
    public async Task Send(ReadOnlyMemory<byte> data)
    { stop.CancelAfter(TimeSpan.FromMinutes(2)); await client.SendAsync(data, stop.Token); }
    async Task Receive()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(stop.Token);
                stop.CancelAfter(TimeSpan.FromMinutes(2));
                if (result.Buffer.Length <= maximum) await reply(result.Buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    int disposed;
    public void Dispose()
    { if (Interlocked.Exchange(ref disposed, 1) == 0) { stop.Cancel(); client.Dispose(); /* receive continuation still owns token */ } }
}

sealed class Session : IAsyncDisposable
{
    public string PeerId { get; }
    public string Address { get; }
    public int LocalPort { get; }
    public byte[] Capability { get; }
    public int[] TcpPorts { get; }
    public int[] UdpPorts { get; }
    public bool Host { get; }
    public Child Process { get; }
    public CancellationToken Token => stop.Token;
    public bool Live => disposed == 0 && !Process.Exited.IsCompleted;
    public string Network => Process.Network;
    public double? TcpRtt { get; set; }
    public double? UdpRtt { get; set; }
    readonly Gateway? gateway;
    readonly string directory;
    readonly CancellationTokenSource stop = new();
    readonly Dictionary<int,LocalForwarder> forwards = new();
    Task metrics = Task.CompletedTask;
    public Dictionary<string,string> Mappings { get; } = new();
    public LocalForwarder Forward(int target)
    {
        lock(forwards)
        {
            if(!Live) throw new IOException("设备连接已断开。");
            if(!forwards.TryGetValue(target,out var bridge)) forwards[target]=bridge=new LocalForwarder(this,target);
            return bridge;
        }
    }
    int disposed;
    public Session(string peer, string address, int port, byte[] capability, int[] tcpPorts, int[] udpPorts,
        Child process, string directory, Gateway? gateway = null)
    {
        PeerId = peer; Address = address; LocalPort = port; Capability = capability;
        TcpPorts = tcpPorts; UdpPorts = udpPorts; Process = process; this.directory = directory;
        this.gateway = gateway; Host = gateway != null;
    }
    public async Task<TcpClient> OpenTcp(int port, CancellationToken ct)
    {
        if (!Live) throw new IOException("设备连接已断开。");
        if (port != 0 && !TcpPorts.Contains(port)) throw new IOException("被控端未授权此 TCP 端口。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, LocalPort, timeout.Token);
            var head = new byte[23]; "AUI1"u8.CopyTo(head); Capability.CopyTo(head, 4);
            head[20] = port == 0 ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(21), checked((ushort)port));
            await tcp.GetStream().WriteAsync(head, timeout.Token);
            var status = (await Wire.Read(tcp.GetStream(), 1, timeout.Token))[0];
            if (status != 0) throw new IOException(status == 2 ? "被控端未授权此端口。" : "被控端本机服务不可用。");
            return tcp;
        }
        catch { tcp.Dispose(); throw; }
    }
    public void StartMetrics(Action changed)
    {
        if (Host || metrics != Task.CompletedTask) return;
        metrics = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    using (await OpenTcp(0, stop.Token)) { }
                    watch.Stop(); TcpRtt = watch.Elapsed.TotalMilliseconds;
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    udp.Connect(IPAddress.Loopback, LocalPort);
                    var nonce = RandomNumberGenerator.GetBytes(12);
                    watch.Restart();
                    await udp.SendAsync(Wire.Packet(Capability, 0, nonce), stop.Token);
                    var reply = await udp.ReceiveAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4), stop.Token);
                    watch.Stop();
                    UdpRtt = Wire.ValidPacket(reply.Buffer, Capability) && reply.Buffer.AsSpan(18).SequenceEqual(nonce)
                        ? watch.Elapsed.TotalMilliseconds : null;
                    changed();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch { TcpRtt = null; UdpRtt = null; changed(); }
                try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        KeyValuePair<string,string>[] mappings;lock(Mappings){mappings=Mappings.ToArray();Mappings.Clear();}
        foreach(var mapping in mappings) await WindowsShares.Remove(mapping.Key,mapping.Value);
        LocalForwarder[] bridges;lock(forwards){bridges=forwards.Values.ToArray();forwards.Clear();}
        foreach(var bridge in bridges)await bridge.DisposeAsync();
        try { await metrics; } catch { }
        await Process.DisposeAsync();
        if (gateway != null) await gateway.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
        // Keep the cancelled token source available to in-flight SOCKS handlers.
    }
}

sealed class Engine : IAsyncDisposable
{
    public State State { get; }
    public Settings Settings { get; }
    public string Id { get; }
    public ConcurrentDictionary<string, Session> Outgoing { get; } = new();
    public ConcurrentDictionary<string, Session> Incoming { get; } = new();
    public ConcurrentDictionary<string, string> Status { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();
    public event Action? Changed;
    readonly SemaphoreSlim operation = new(1);
    Engine(State state, Settings settings, string id) { State=state; Settings=settings; Id=id; }
    public static async Task<Engine> Create(string root, Settings settings)
    {
        settings.Validate();
        if (!File.Exists(Path.Combine(root,"device","identity"))) throw new IOException("本机身份尚未安装，请重新执行官方 install.ps1。运行客户端不会自动生成新身份。");
        var state=new State(root);
        try { return new Engine(state,settings,await Ard.Identity(state.IdentityDirectory,CancellationToken.None)); }
        catch { state.Dispose(); throw; }
    }
    void Update(string id,string message) { Status[id]=message; Changed?.Invoke(); }
    string Describe(Session session, bool host)
    {
        var metric = session.TcpRtt is { } tcp ? $" · TCP/PsPing {tcp:F1} ms" : "";
        if (session.UdpRtt is { } udp) metric += $" · UDP {udp:F1} ms";
        return (host ? "正在访问本机" : "已连接") + " · " + session.Network + metric;
    }
    void Observe(Session session, bool host)
    {
        session.Process.Output += line =>
        {
            if (line.Contains("CONNECTED", StringComparison.Ordinal) || line.Contains("network path selected", StringComparison.Ordinal) || line.Contains("READY:", StringComparison.Ordinal))
            {
                Logs.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
                while (Logs.Count > 4) Logs.TryDequeue(out _);
            }
            Update(session.PeerId, Describe(session, host));
        };
        if (!host) session.StartMetrics(() => Update(session.PeerId, Describe(session, false)));
        Update(session.PeerId, Describe(session, host));
    }
    async Task Monitor(Session session,bool host)
    {
        await session.Process.Exited;
        var sessions=host ? Incoming : Outgoing;
        if(sessions.TryRemove(new KeyValuePair<string,Session>(session.PeerId,session)))
        { await session.DisposeAsync(); Update(session.PeerId,"连接已断开"); }
    }
    public async Task Disconnect(string id)
    {
        await operation.WaitAsync();
        try
        {
            if(Outgoing.TryRemove(id,out var outgoing)) await outgoing.DisposeAsync();
            if(Incoming.TryRemove(id,out var incoming)) await incoming.DisposeAsync();
            Update(id,"未连接");
        }
        finally { operation.Release(); }
    }
    public async Task StopIncoming()
    {
        await operation.WaitAsync();
        try { foreach(var pair in Incoming.ToArray()) if(Incoming.TryRemove(pair.Key,out var session)) { await session.DisposeAsync(); Update(pair.Key,"被控访问已关闭"); } }
        finally { operation.Release(); }
    }
    public async Task<string> AcceptServerSession(ServerTicket ticket, Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>> verifyPassword, CancellationToken ct)
    {
        await operation.WaitAsync(ct);
        string? dir = null; Gateway? gateway = null; Child? child = null; Session? session = null;
        try
        {
            if(Incoming.TryRemove(ticket.ControllerEndpoint,out var previous))await previous.DisposeAsync();
            var peer = new Peer { Id = ticket.ControllerEndpoint, Code = ticket.ControllerCode };
            dir = State.NewSessionDirectory();
            var local = await Ard.Identity(dir, ct);
            var capability = RandomNumberGenerator.GetBytes(16);
            gateway = new Gateway(Settings, capability, async (password, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                return await verifyPassword(password, linked.Token);
            });
            child = Ard.Start(dir, true, gateway.Port, ticket.ClientSessionId, Settings);
            await child.WaitFor("relay online", ct);
            session = new Session(peer.Id, peer.Address, gateway.Port, capability, Settings.TcpPorts, Settings.UdpPorts, child, dir, gateway);
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Attached += () => { attached.TrySetResult(); Update(peer.Id, "已连接 · 对方正在访问本机"); };
            if (!Incoming.TryAdd(peer.Id, session)) throw new IOException("已有同设备会话。");
            Observe(session, true);
            var owned = session;
            var registration = ct.Register(() => { _ = owned.DisposeAsync(); });
            _ = child.Exited.ContinueWith(t => registration.Dispose(), TaskScheduler.Default);
            _ = Monitor(session, true);
            _ = Task.Run(async () =>
            {
                try { await attached.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, ticket.Expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds())), owned.Token); }
                catch { await owned.DisposeAsync(); }
            });
            Update(peer.Id, "等待密码与首次身份确认");
            return local;
        }
        catch
        {
            if (session != null) { Incoming.TryRemove(ticket.ControllerEndpoint, out _); await session.DisposeAsync(); }
            else
            {
                if (child != null) await child.DisposeAsync();
                if (gateway != null) await gateway.DisposeAsync();
                if (dir != null) try { Directory.Delete(dir, true); } catch (IOException) { }
            }
            throw;
        }
        finally { operation.Release(); }
    }
    public async Task ConnectServerSession(MachineInfo target, string dir, ServerOffer offer, bool enrolling, string password, CancellationToken ct)
    {
        await operation.WaitAsync(ct);
        Child? child = null; Session? session = null;
        var peer = new Peer { Id = target.Endpoint, Code = target.Code };
        try
        {
            var port = Wire.Port();
            child = Ard.Start(dir, false, port, offer.SessionId, Settings);
            Update(peer.Id, "正在建立经过身份签名的连接…");
            await child.WaitFor("READY:", ct);
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            var header = new byte[23]; "AUI1"u8.CopyTo(header); header[20] = 2;
            await tcp.GetStream().WriteAsync(header, ct);
            await Wire.WriteJson(tcp.GetStream(), new PasswordRequest(enrolling, password), ct);
            Update(peer.Id, "等待对端验证密码和确认 EndpointId…");
            var admission = await Wire.ReadJson<AdmissionReply>(tcp.GetStream(), ct);
            if (!admission.Accepted) throw new IOException(admission.Error ?? "访问未获批准。");
            if (admission.Token == null || !Regex.IsMatch(admission.Token, @"\A[0-9a-fA-F]{32}\z") ||
                admission.TcpPorts == null || admission.UdpPorts == null || admission.TcpPorts.Concat(admission.UdpPorts).Any(p => p is < 1 or > 65535))
                throw new InvalidDataException("对端授权数据无效。");
            if (admission.Grant == null) throw new IOException("缺少被控端签名的授权。");
            var grant = DirectoryClient.Verify<GrantReceipt>(admission.Grant, "/grant/v1", target.Endpoint, false);
            if (grant.ControllerEndpoint != Id || grant.TargetEndpoint != target.Endpoint) throw new IOException("授权未绑定当前设备。");
            peer = State.GetOrAdd(target.Endpoint, target.Code, admission.Grant);
            session = new Session(peer.Id, peer.Address, port, Convert.FromHexString(admission.Token), admission.TcpPorts, admission.UdpPorts, child, dir);
            using (await session.OpenTcp(0, ct)) { }
            if (!Outgoing.TryAdd(peer.Id, session)) throw new IOException("该设备已有连接。");
            Observe(session, false); _ = Monitor(session, false);
        }
        catch
        {
            if (session != null) await session.DisposeAsync();
            else { if (child != null) await child.DisposeAsync(); try { Directory.Delete(dir, true); } catch (IOException) { } }
            Update(peer.Id, "连接未获批准或已失败"); throw;
        }
        finally { operation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await operation.WaitAsync();
        try
        {
            foreach(var session in Outgoing.Values.Concat(Incoming.Values)) await session.DisposeAsync();
            Outgoing.Clear(); Incoming.Clear(); State.Dispose();
        }
        finally { operation.Release(); }
    }
}

static class Qr
{
    public static byte[] Encode(string text)
    {
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 240, 240,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 2 });
        using var bitmap = new SKBitmap(matrix.Width, matrix.Height);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++) if (matrix[x, y]) canvas.DrawRect(x, y, 1, 1, paint);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
    public static string Decode(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("无法读取二维码图片。");
        if ((long)codec.Info.Width * codec.Info.Height > 24_000_000) throw new InvalidDataException("图片过大，请裁剪二维码后重试。");
        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888));
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new ZXing.Common.DecodingOptions
            { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var result = reader.Decode(new RGBLuminanceSource(bitmap.Bytes, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32));
        return result is null ? throw new InvalidDataException("没有识别到二维码。") : (Regex.IsMatch(result.Text.Trim(), @"(?i)\A(?:ardui:/*)?[A-Z0-9]{6}\z") ? Wire.Machine(result.Text) : Wire.Endpoint(result.Text));
    }
}

sealed class MainWindow : Window
{
    static readonly IBrush Ink = Brush.Parse("#16243A"), Muted = Brush.Parse("#6A778A");
    readonly TextBlock machine = new() { Text = "------", FontSize = 18, FontWeight = FontWeight.Bold, LetterSpacing = 2, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(8,0) };
    readonly TextBox endpoint = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, Height=28, VerticalContentAlignment=VerticalAlignment.Center };
    readonly TextBox remote = new() { Watermark = "6 位机器编号", MaxLength = 6 };
    readonly TextBox remotePassword = new() { Watermark = "对端访问密码", PasswordChar = '●', MaxLength = 128 };
    readonly CheckBox allow = new() { Content = "正在读取被控状态…", IsEnabled=false };
    readonly TextBox hostPassword = new() { Watermark = "被控密码", MaxLength = 128 };
    readonly Grid hostDetails = new() { IsVisible=false, ColumnDefinitions=new ColumnDefinitions("105,*"),RowDefinitions=new RowDefinitions("Auto,Auto") };
    readonly TextBlock status = new() { Text = "正在读取设备身份…", TextWrapping = TextWrapping.Wrap, Foreground = Muted };
    readonly StackPanel activeIncoming = new() { Spacing = 6 };
    readonly StackPanel incomingArea = new() { Spacing = 6, IsVisible = false };
    readonly StackPanel pendingIncoming = new() { Spacing = 6 };
    readonly StackPanel controllers = new() { Spacing = 6 };
    readonly Expander controllersExpander = new() { Header = "可访问本机的设备管理", IsExpanded = false };
    readonly TextBlock networkLog = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    readonly StackPanel devices = new() { Spacing = 10 };
    Engine? engine;
    DirectoryClient? directory;
    bool closing, closed, busy, ready;
    int passwordEdit;
    public MainWindow(bool preview = false)
    {
        Title = "ArdUi " + Program.Version; Width = 660; Height = 420; MinWidth = 600; MinHeight = 360;
        Background = Brush.Parse("#F4F6FA"); FontFamily = new FontFamily("Microsoft YaHei UI"); Foreground = Ink; FontSize = 11;
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(new TextBlock { Text = "ArdUi " + Program.Version, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0,0,0,6) });
        var identity = new StackPanel{Spacing=5};identity.Children.Add(allow);
        hostDetails.Children.Add(machine);Grid.SetColumn(endpoint,1);hostDetails.Children.Add(endpoint);
        var access = new StackPanel { Orientation=Orientation.Horizontal,Spacing=8,Children={hostPassword,
            Button("复制机器码",async()=>await Clipboard!.SetTextAsync(directory?.Code??"")),Button("复制 EndpointId",async()=>await Clipboard!.SetTextAsync(engine?.Id??""))}};
        allow.PropertyChanged+=async(_,e)=>{if(e.Property==CheckBox.IsCheckedProperty && ready)await Guard(SaveAccess);};
        hostPassword.TextChanged+=async(_,_)=>
        {
            var edit=Interlocked.Increment(ref passwordEdit);await Task.Delay(700);
            if(edit==passwordEdit && hostPassword.IsFocused && directory!=null)await Guard(SaveAccess);
        };
        Grid.SetRow(access,1);Grid.SetColumnSpan(access,2);hostDetails.Children.Add(access);identity.Children.Add(hostDetails);
        var identityCard=Card(identity);Grid.SetRow(identityCard,1);root.Children.Add(identityCard);
        controllersExpander.Content=controllers;
        var main = new Grid { Margin=new Thickness(0,7,0,0),ColumnDefinitions=new ColumnDefinitions("*,8,240") };
        var deviceArea=new StackPanel{Spacing=5,Children={Label("设备列表（本机可主动控制）",14),devices}};
        main.Children.Add(new ScrollViewer{Content=deviceArea});
        incomingArea.Children.Add(Label("正在访问本机",14));incomingArea.Children.Add(activeIncoming);
        var side=new StackPanel{Spacing=6,Children={incomingArea,pendingIncoming,controllersExpander,
            Label("添加设备",14),remote,remotePassword,Button("添加并申请授权",Connect),Button("扫码",Scan)}};
        var sideScroll=new ScrollViewer{Content=side};Grid.SetColumn(sideScroll,2);main.Children.Add(sideScroll);Grid.SetRow(main,2);root.Children.Add(main);
        var footer=new StackPanel{Spacing=3,Margin=new Thickness(0,6,0,0),Children={status,networkLog}};
        Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;
        Opened += async (_,_) =>
        {
            if(preview)return;
            await Guard(async () =>
            {
                var settings=JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Program.DataRoot,"config.json")),Wire.Json)!;
                engine=await Engine.Create(Program.DataRoot,settings);endpoint.Text=engine.Id;
                engine.Changed+=()=>Dispatcher.UIThread.Post(Refresh);
                directory=new DirectoryClient(engine);hostPassword.Text=directory.AccessPassword;
                directory.Confirm=async(pair,ct)=>await Dispatcher.UIThread.InvokeAsync(()=>ConfirmPair(pair,ct));
                directory.Changed+=()=>Dispatcher.UIThread.Post(Refresh);
                directory.Notice+=message=>Dispatcher.UIThread.Post(()=>Say(message));
                allow.IsChecked=directory.Enabled;hostDetails.IsVisible=directory.Enabled;allow.Content="允许被控";allow.IsEnabled=true;ready=true;directory.Start();Say("正在连接可信服务器…");
            });
        };
        Closing+=async(_,e)=>
        {
            if(closed)return;e.Cancel=true;if(closing)return;closing=true;IsEnabled=false;
            try{if(directory!=null)await directory.DisposeAsync();if(engine!=null)await engine.DisposeAsync();}
            finally{closed=true;Close();}
        };
        if(preview)
        {
            machine.Text="A7B2K9";endpoint.Text="5889f4c2e3c89af503a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1";
            allow.Content="允许被控";allow.IsEnabled=true;allow.IsChecked=false;Say("可信服务器 · https://f.visnova.cn/ · 本机被控功能已关闭");
            devices.Children.Add(Device(new Peer{Id=new string('a',64),Code="C8M3P6",Name="办公电脑"},true));
            controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});
        }
    }
    static TextBlock Label(string text,double size)=>new(){Text=text,FontSize=size,FontWeight=FontWeight.SemiBold};
    static Border Card(Control content)=>new(){Child=content,Padding=new Thickness(9),Background=Brushes.White,CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1),BorderBrush=Brush.Parse("#E4E9F0")};
    Button Button(string text,Func<Task> action)
    {
        var button=new Button{Content=text,HorizontalAlignment=HorizontalAlignment.Stretch};
        button.Click+=async(_,_)=>await Guard(action);return button;
    }
    async Task Guard(Func<Task> action)
    {
        if(busy||closing)return;busy=true;
        try{await action();}catch(OperationCanceledException){Say("操作已取消或等待确认超时。");}catch(Exception ex){Say(ex.Message);}
        finally{busy=false;Refresh();}
    }
    void Say(string text)=>status.Text=text;
    void Refresh()
    {
        if(engine==null)return;
        if(directory!=null && directory.Code.Length!=0 && machine.Text!=directory.Code)
        {machine.Text=directory.Code;Say("设备已注册 · "+engine.Settings.Server);}
        devices.Children.Clear();
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.ToArray();
        foreach(var peer in peers)devices.Children.Add(Device(peer,engine.Outgoing.TryGetValue(peer.Id,out var active)&&active.Live));
        if(peers.Length==0)devices.Children.Add(new TextBlock{Text="尚未添加设备；请在右侧输入另一台设备的机器码和密码。",Foreground=Muted,TextWrapping=TextWrapping.Wrap});
        activeIncoming.Children.Clear();
        foreach(var session in engine.Incoming.Values.Where(s=>s.Live))
        {
            var code=directory?.Controllers.FirstOrDefault(p=>p.Key==session.PeerId).Value?.Code??session.PeerId[..8];
            var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Children={new TextBlock{Text=code+" · "+engine.Status.GetValueOrDefault(session.PeerId,"正在控制"),VerticalAlignment=VerticalAlignment.Center},Button("断开",async()=>await engine.Disconnect(session.PeerId))}};
            activeIncoming.Children.Add(row);
        }
        incomingArea.IsVisible=activeIncoming.Children.Count!=0;
        controllers.Children.Clear();
        if(directory!=null)foreach(var pair in directory.Controllers)
        {
            var endpointId=pair.Key;var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            row.Children.Add(new TextBlock{Text=pair.Value.Code+" · "+endpointId[..8]+"…",VerticalAlignment=VerticalAlignment.Center});
            row.Children.Add(Button("撤销",async()=>await directory.Revoke(endpointId)));controllers.Children.Add(row);
        }
        if(controllers.Children.Count==0)controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});
        networkLog.Text=string.Join(Environment.NewLine,engine.Logs.TakeLast(3));
        if(directory!=null)
        {
            if(allow.IsChecked!=directory.Enabled){ready=false;allow.IsChecked=directory.Enabled;ready=true;}
            hostDetails.IsVisible=directory.Enabled;
            Title="ArdUi "+Program.Version+(directory.Enabled&&directory.Code.Length!=0?" · "+directory.Code:"");
        }
    }
    async Task Connect()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        var password=remotePassword.Text??"";remotePassword.Text="";Say("正在核对对端身份并申请授权…");
        await directory.Connect(remote.Text??"",password);Say("已获授权，可以从设备列表打开远程桌面和文件共享。");
    }
    async Task SaveAccess()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        var password=hostPassword.Text??"";
        await directory.SetAccess(allow.IsChecked==true,password);
        Say(directory.Enabled?"已自动保存：允许远程访问。":"已自动保存：关闭远程访问并断开被控会话。");
    }
    async Task Scan()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="选择机器编号二维码",AllowMultiple=false});
        if(files.Count==0)return;
        await using var stream=await files[0].OpenReadAsync();remote.Text=Wire.Machine(Qr.Decode(stream));
    }
    Control Device(Peer peer,bool connected)
    {
        var stack=new StackPanel{Spacing=8,Children={Label((string.IsNullOrEmpty(peer.Name)?peer.Code:peer.Name)+" · "+peer.Code,16),
            new TextBlock{Text=engine?.Status.GetValueOrDefault(peer.Id,"已授权 · 未连接")??"已授权 · 在线",Foreground=Muted,FontSize=12}}};
        var actions=new WrapPanel();
        void Add(string title,Func<Task> action,bool enabled=true)
        { var button=Button(title,action);button.IsEnabled=enabled;button.Margin=new Thickness(0,0,6,4);actions.Children.Add(button); }
        Add("远程桌面",async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var session=engine!.Outgoing[peer.Id];var bridge=session.Forward(3389);
            await Launch("mstsc.exe","/v:127.0.0.1:"+bridge.Port);
        });
        Add("文件共享",async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var input=await ShareDetails();
            var path=await WindowsShares.Map(engine!.Outgoing[peer.Id],input.Share,input.User,input.Password,CancellationToken.None);
            await Launch("explorer.exe",path);
        });
        Add("EndpointId",async () => { await Clipboard!.SetTextAsync(peer.Id);Say("完整 EndpointId 已复制："+peer.Id); });
        Add("备注",async () => { peer.Name=await AskText("设备备注",peer.Name);engine!.State.Save(); });
        Add("断开",async () => { await engine!.Disconnect(peer.Id); });
        Add("移除",async () => { await directory!.RemoveLocal(peer); });
        stack.Children.Add(actions); return Card(stack);
    }
    async Task<bool> ConfirmPair(Pairing pair,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(pair.Incoming)
        {
            var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var row=new StackPanel{Spacing=6};
            row.Children.Add(new TextBlock{Text=$"{pair.Code} 请求获得本机控制权",FontWeight=FontWeight.SemiBold});
            row.Children.Add(new TextBox{Text=pair.Endpoint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=11});
            var pendingButtons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            var yes=new Button{Content="是，已核对 EndpointId"};var no=new Button{Content="否"};
            yes.Click+=(_,_)=>result.TrySetResult(true);no.Click+=(_,_)=>result.TrySetResult(false);
            pendingButtons.Children.Add(yes);pendingButtons.Children.Add(no);row.Children.Add(pendingButtons);
            var pendingCard=Card(row);pendingIncoming.Children.Add(pendingCard);
            using var pendingCancel=ct.Register(()=>result.TrySetCanceled(ct));
            try{return await result.Task;}finally{pendingIncoming.Children.Remove(pendingCard);}
        }
        var dialog=new Window { Title="首次连接：核对 EndpointId",Width=620,Height=420,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var content=new StackPanel { Margin=new Thickness(24),Spacing=14 };
        content.Children.Add(Label((pair.Incoming ? "设备请求访问本机：" : "即将连接设备：")+pair.Code,18));
        content.Children.Add(new TextBlock { Text="请通过电话、当面等独立渠道，与对方 ArdUi 中显示的完整 EndpointId 逐字核对。机器编号由服务器提供，不能代替公钥核对。",TextWrapping=TextWrapping.Wrap });
        content.Children.Add(new TextBox { Text=pair.Endpoint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=15 });
        content.Children.Add(new TextBlock { Text=pair.Incoming ? "访问密码已验证正确。只有确认后，才会允许这台设备访问 RDP / SMB。" : "确认后才会通过经过身份验证的加密连接发送访问密码。",TextWrapping=TextWrapping.Wrap });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
        var reject=new Button { Content="拒绝 / 未核对",IsDefault=true }; reject.Click+=(_,_)=>dialog.Close(false);
        var accept=new Button { Content="已独立核对一致，信任此设备" }; accept.Click+=(_,_)=>dialog.Close(true);
        buttons.Children.Add(reject);buttons.Children.Add(accept);content.Children.Add(buttons);dialog.Content=content;
        using var cancel=ct.Register(()=>Dispatcher.UIThread.Post(()=>dialog.Close(false)));
        return await dialog.ShowDialog<bool>(this);
    }
    async Task<string> AskPassword(string title)
    {
        var dialog=new Window { Title=title,Width=430,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var input=new TextBox { PasswordChar='●',MaxLength=128,Watermark=title };
        var button=new Button { Content="确认" };button.Click+=(_,_)=>dialog.Close(input.Text ?? "");
        dialog.Content=new StackPanel { Margin=new Thickness(24),Spacing=16,Children={input,button} };
        return await dialog.ShowDialog<string?>(this) ?? throw new OperationCanceledException();
    }
    async Task<string> AskText(string title,string initial="")
    {
        var dialog=new Window {Title=title,Width=430,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var input=new TextBox {Text=initial,MaxLength=80};var button=new Button{Content="保存"};button.Click+=(_,_)=>dialog.Close(input.Text ?? "");
        dialog.Content=new StackPanel{Margin=new Thickness(24),Spacing=16,Children={input,button}};
        return await dialog.ShowDialog<string?>(this) ?? throw new OperationCanceledException();
    }
    sealed record ShareInput(string Share,string User,string Password);
    async Task<ShareInput> ShareDetails()
    {
        var dialog=new Window{Title="打开 SMB 文件共享",Width=460,Height=340,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var share=new TextBox{Watermark="共享名，例如 Documents",MaxLength=80};
        var user=new TextBox{Watermark="Windows 用户名（留空使用当前账户）",MaxLength=128};
        var password=new TextBox{Watermark="Windows 账户密码",PasswordChar='●',MaxLength=256};
        var open=new Button{Content="打开共享"};open.Click+=(_,_)=>dialog.Close(new ShareInput(share.Text??"",user.Text??"",password.Text??""));
        dialog.Content=new StackPanel{Margin=new Thickness(24),Spacing=14,Children={share,user,password,new TextBlock{Text="这是 Windows 业务账户，与 ArdUi 添加设备口令无关。",TextWrapping=TextWrapping.Wrap},open}};
        return await dialog.ShowDialog<ShareInput?>(this) ?? throw new OperationCanceledException();
    }
    static Task Launch(string file,string argument)
    { var start=new ProcessStartInfo(file) { UseShellExecute=true };start.ArgumentList.Add(argument);Process.Start(start);return Task.CompletedTask; }
}

sealed class App : Application
{
    public override void Initialize()
    { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Light; }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

static class Preview
{
    public static int Render(string path)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().SetupWithoutStarting();
        var window = new MainWindow(true); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            if (window.Title?.Contains(Program.Version, StringComparison.Ordinal) != true || window.Content is not Grid)
                throw new IOException("无头界面结构不完整。");
            using var bitmap = window.CaptureRenderedFrame() ?? throw new IOException("无法渲染界面。");
            if (bitmap.PixelSize.Width < 600 || bitmap.PixelSize.Height < 360) throw new IOException("无头界面尺寸异常。");
            bitmap.Save(path); return 0;
        }
        finally { window.Close(); }
    }
}

static class SelfTest
{
    public static async Task<int> Prototype()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-prototype-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        var port = ((IPEndPoint)echo.LocalEndpoint).Port;
        async Task Echo()
        {
            using var connection = await echo.AcceptTcpClientAsync(ct);
            var stream = connection.GetStream();
            var data = await Wire.Read(stream, 12, ct);
            await stream.WriteAsync(data, ct);
        }
        try
        {
            var a = Path.Combine(root,"a"); var b = Path.Combine(root,"b");
            IdentityStore.Prepare(a); IdentityStore.Prepare(b);
            var settings = new Settings { TcpPorts = [port], UdpPorts = [] };
            await using var host = await Engine.Create(a, settings);
            await using var caller = await Engine.Create(b, settings);
            await using var hd = new DirectoryClient(host);
            await using var cd = new DirectoryClient(caller);
            var hostChecks = 0; var callerChecks = 0;
            var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            hd.Confirm = (pair, token) =>
            {
                if (!pair.Incoming || pair.Endpoint != caller.Id) throw new Exception("Host fingerprint mismatch.");
                Interlocked.Increment(ref hostChecks); prompt.TrySetResult(); return approval.Task.WaitAsync(token);
            };
            cd.Confirm = (pair, token) =>
            {
                if (pair.Incoming || pair.Endpoint != host.Id) throw new Exception("Caller fingerprint mismatch.");
                Interlocked.Increment(ref callerChecks); return Task.FromResult(true);
            };
            hd.Notice += message => Console.WriteLine("HOST: " + message);
            await hd.Register(ct); await cd.Register(ct);
            var original = cd.Code; await cd.Register(ct);
            if (original != cd.Code || hd.Enabled) throw new Exception("Registration/default access failure.");
            Console.WriteLine("PASS: HTTPS registration, stable machine code, host disabled by default.");
            await hd.SetAccess(true, "Prototype-Access-7391", ct); hd.Start();
            var connection = cd.Connect(hd.Code, "Prototype-Access-7391", ct);
            await prompt.Task.WaitAsync(ct);
            if (caller.State.Peers.Count != 0 || connection.IsCompleted) throw new Exception("Peer granted before consent.");
            approval.SetResult(true); await connection;
            if (hostChecks != 1 || callerChecks != 1 || caller.State.Peers.Count != 1) throw new Exception("Consent failure.");
            Console.WriteLine("PASS: both fingerprints checked; peer saved only after host approval and signed grant.");
            async Task VerifyFlow()
            {
                var response = Echo();
                var session = caller.Outgoing[host.Id];
                using var local = new TcpClient();
                await local.ConnectAsync(IPAddress.Loopback,session.Forward(port).Port,ct);
                var message = "ardui-e2e-ok"u8.ToArray();
                await local.GetStream().WriteAsync(message,ct);
                if (!(await Wire.Read(local.GetStream(),message.Length,ct)).SequenceEqual(message)) throw new Exception("TCP proxy corrupted data.");
                await response;
            }
            await VerifyFlow(); Console.WriteLine("PASS: loopback TCP proxy through authenticated ARD to remote service.");
            await caller.Disconnect(host.Id); await host.Disconnect(caller.Id);
            await cd.Connect(hd.Code,"",ct,enrolling:false);
            if (hostChecks != 1 || callerChecks != 1) throw new Exception("Authorized reconnect prompted again.");
            await VerifyFlow(); Console.WriteLine("PASS: authorized reconnect without enrollment password or consent.");
            await hd.Revoke(caller.Id);
            if (host.Incoming.Count != 0 || hd.Controllers.Length != 0) throw new Exception("Revoke failed.");
            await caller.Disconnect(host.Id);
            try { await cd.Connect(hd.Code,"",ct,enrolling:false); throw new Exception("Revoked caller admitted."); }
            catch (IOException) { }
            Console.WriteLine("PASS: revocation removes ACL and denies reconnect.");
            hd.Confirm=(pair,token)=>Task.FromResult(pair.Incoming&&pair.Endpoint==caller.Id);
            for(var round=1;round<=3;round++)
            {
                Console.WriteLine($"TEST: repeated add round {round} connecting.");
                await cd.Connect(hd.Code,"Prototype-Access-7391",ct).WaitAsync(TimeSpan.FromSeconds(75),ct);
                await VerifyFlow();
                var saved=caller.State.Peers.Single();await cd.RemoveLocal(saved);
                if(caller.State.Peers.Count!=0||caller.Outgoing.Count!=0)throw new Exception("Repeated local removal failed.");
                Console.WriteLine($"TEST: repeated add round {round} removed.");
            }
            await hd.Revoke(caller.Id);
            Console.WriteLine("PASS: three add, transfer, remove, and immediate re-add cycles.");
            await hd.SetAccess(false,null,ct);
            if (hd.Enabled || host.Incoming.Count != 0) throw new Exception("Disable failed.");
            Console.WriteLine("PASS: host disable stops incoming sessions.");
            return 0;
        }
        finally
        {
            echo.Stop();
            if (Directory.Exists(root)) Directory.Delete(root,true);
        }
    }
    public static Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = IdentityStore.Prepare(root);
            var path = Path.Combine(root, "device", "identity");
            var digest = SHA256.HashData(File.ReadAllBytes(path));
            if (first.Length != 64 || IdentityStore.Prepare(root) != first ||
                !digest.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new Exception("重复安装改变了身份。");
            File.WriteAllBytes(path, [1, 2, 3]);
            try { IdentityStore.Prepare(root); throw new Exception("损坏身份被接受。"); }
            catch (IOException) { }
            if (!File.ReadAllBytes(path).SequenceEqual(new byte[] {1,2,3})) throw new Exception("损坏身份被覆盖。");
            Console.WriteLine("PASS: identity creation, reinstall preservation, corrupt identity refusal.");
            var preview=Path.Combine(root,"headless.png"); Preview.Render(preview);
            if(new FileInfo(preview).Length<1000)throw new Exception("无头渲染输出无效。");
            Console.WriteLine("PASS: Avalonia code-only headless layout and rendering.");
            return Task.FromResult(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

sealed record SignedEnvelope(string Endpoint, long IssuedAt, string Nonce, string Payload, string Signature);
sealed record MachineInfo(string Code, string Endpoint);
sealed record ServerRequest(string Code, string TargetEndpoint, string SessionId, string RequestId, long Expires);
sealed record ServerOffer(string SessionId, string RequestId, string ControllerEndpoint, string TargetEndpoint, string ClientSessionId, long Expires);
sealed record ServerTicket(string Id, string ControllerEndpoint, string ControllerCode, string ClientSessionId, long Expires, SignedEnvelope Proof);
sealed record PollReply(bool Enabled, ServerTicket[] Tickets);
sealed record TicketReply(string Status, SignedEnvelope? Offer);
sealed record PasswordRequest(bool Enroll, string Password);
sealed record GrantReceipt(string ControllerEndpoint, string TargetEndpoint, string GrantId);
sealed record ControllerGrant(string Code, SignedEnvelope Receipt);
sealed record AdmissionReply(bool Accepted, string? Error, string? Token, int[] TcpPorts, int[] UdpPorts, SignedEnvelope? Grant);
sealed record Pairing(string Code, string Endpoint, bool Incoming);
sealed class AccessSettings
{
    public bool Enabled { get; set; }
    public string? Password { get; set; }
    public string? Salt { get; set; }
    public string? Hash { get; set; }
    public Dictionary<string, ControllerGrant> Controllers { get; set; } = new();
    public Dictionary<string, string> Targets { get; set; } = new();
}

// The directory routes signed public session descriptions. Passwords and flow
// capabilities only cross ARD after both long-term identity signatures verify.
sealed class DirectoryClient : IAsyncDisposable
{
    readonly Engine engine;
    readonly HttpClient http;
    readonly NSec.Cryptography.Key key;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim edits = new(1), confirmations = new(1);
    readonly object sync = new();
    readonly string path;
    readonly AccessSettings access;
    readonly ConcurrentDictionary<string, Task> jobs = new();
    readonly Dictionary<string, long> seen = new();
    readonly Queue<DateTime> attempts = new();
    CancellationTokenSource incoming = new();
    Task loop = Task.CompletedTask;
    string code = "";
    bool online;
    public string Code => code;
    public bool Online => online;
    public bool Enabled { get { lock (sync) return access.Enabled; } }
    public string AccessPassword { get { lock(sync) return UnprotectPassword(access.Password!); } }
    public Func<Pairing, CancellationToken, Task<bool>>? Confirm { get; set; }
    public event Action? Changed;
    public event Action<string>? Notice;
    public DirectoryClient(Engine engine, string? testServer = null)
    {
        this.engine = engine;
        var server = new Uri(testServer ?? engine.Settings.Server);
        if (server.Scheme != "https" && !(testServer != null && server.IsLoopback && server.Scheme == "http"))
            throw new InvalidDataException("可信服务器必须使用 HTTPS。");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 65536 };
        var seed = File.ReadAllBytes(Path.Combine(engine.State.IdentityDirectory, "identity"));
        try { key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, seed, NSec.Cryptography.KeyBlobFormat.RawPrivateKey); }
        finally { CryptographicOperations.ZeroMemory(seed); }
        if (Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant() != engine.Id)
            throw new InvalidDataException("设备密钥与 EndpointId 不匹配。");
        path = Path.Combine(engine.State.Root, "access.json");
        access = File.Exists(path) ? JsonSerializer.Deserialize<AccessSettings>(File.ReadAllText(path), Wire.Json)!
            : new AccessSettings();
        if (access == null || access.Controllers == null || access.Targets == null) throw new InvalidDataException("访问配置损坏。");
        if (access.Hash != null && (Convert.FromBase64String(access.Hash).Length != 32 || access.Salt == null || Convert.FromBase64String(access.Salt).Length != 16))
            throw new InvalidDataException("访问密码记录损坏。");
        if(access.Password==null)
        {
            const string alphabet="ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var password=string.Concat(Enumerable.Range(0,12).Select(_=>alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
            var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);
            access.Password=ProtectPassword(password);access.Salt=Convert.ToBase64String(salt);access.Hash=Convert.ToBase64String(hash);access.Controllers.Clear();Save();
            CryptographicOperations.ZeroMemory(hash);
        }
        // A changed directory authority requires a separate trust namespace.
        var authorityPath = Path.Combine(engine.State.Root, "directory.txt");
        if (File.Exists(authorityPath) && File.ReadAllText(authorityPath) != server.AbsoluteUri)
            throw new InvalidDataException("可信服务器已改变。请先恢复原配置，或使用独立应用目录完成重新核对。");
        File.WriteAllText(authorityPath, server.AbsoluteUri);
    }
    void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(access, Wire.Json));
        File.Move(path + ".tmp", path, true);
    }
    static string ProtectPassword(string password)=>Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password),null,DataProtectionScope.CurrentUser));
    static string UnprotectPassword(string password)=>Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(password),null,DataProtectionScope.CurrentUser));
    SignedEnvelope Sign(string route, object value)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var unsigned = new SignedEnvelope(engine.Id, now, nonce, payload, "");
        return unsigned with { Signature = Convert.ToBase64String(NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(key, SignedBytes(route, unsigned))) };
    }
    static byte[] SignedBytes(string route, SignedEnvelope value) => Encoding.UTF8.GetBytes(
        $"ArdUiServer/1\n{route}\n{value.Endpoint}\n{value.IssuedAt}\n{value.Nonce}\n{value.Payload}");
    public static T Verify<T>(SignedEnvelope value, string route, string endpoint, bool expiring = true)
    {
        if (Wire.Endpoint(value.Endpoint) != endpoint || (expiring && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - value.IssuedAt) > 360) ||
            value.Payload.Length > 20000 || !Regex.IsMatch(value.Nonce, @"\A[0-9a-f]{32}\z"))
            throw new InvalidDataException("对端身份签名无效或过期。");
        var pub = NSec.Cryptography.PublicKey.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519,
            Convert.FromHexString(endpoint), NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        if (!NSec.Cryptography.SignatureAlgorithm.Ed25519.Verify(pub, SignedBytes(route, value), Convert.FromBase64String(value.Signature)))
            throw new InvalidDataException("对端会话被替换或签名不正确，连接已阻止。");
        return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value.Payload), Wire.Json) ?? throw new InvalidDataException("对端会话为空。");
    }
    public async Task<T> Call<T>(string route, object value, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(Sign(route, value), Wire.Json), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string message;
            try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!; }
            catch { message = "可信服务器暂时不可用。"; }
            throw new IOException(message);
        }
        return JsonSerializer.Deserialize<T>(text, Wire.Json) ?? throw new InvalidDataException("服务器响应为空。");
    }
    public async Task Register(CancellationToken ct)
    {
        var info = await Call<MachineInfo>("/api/v1/register", new { }, ct);
        if (info.Endpoint != engine.Id || !Regex.IsMatch(info.Code, @"\A[A-Z0-9]{6}\z")) throw new InvalidDataException("服务器返回无效机器编号。");
        var saved = Path.Combine(engine.State.Root, "machine.json");
        if (File.Exists(saved))
        {
            var old = JsonSerializer.Deserialize<MachineInfo>(File.ReadAllText(saved), Wire.Json)!;
            if (old.Endpoint != info.Endpoint || old.Code != info.Code) throw new InvalidDataException("服务器返回的机器编号与本机记录不符，请检查服务器数据恢复情况。");
        }
        File.WriteAllText(saved, JsonSerializer.Serialize(info, Wire.Json));
        code = info.Code; online = true; Changed?.Invoke();
    }
    public void Start() => loop = Run();
    async Task Run()
    {
        var announced = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (code.Length == 0)
                {
                    await Register(stop.Token);
                    if (Enabled) await Call<JsonElement>("/api/v1/access", new { enabled = true }, stop.Token);
                }
                await Poll(stop.Token);
                online = true; announced = false; Changed?.Invoke();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            { online = false; Changed?.Invoke(); if (!announced) Notice?.Invoke(ex.Message); announced = true; }
            try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Poll(CancellationToken ct)
    {
        // Serialize access changes with heartbeats: a stale disabled poll must not undo enable.
        await edits.WaitAsync(ct);
        try
        {
            var reply = await Call<PollReply>("/api/v1/poll", new { enabled = Enabled }, ct);
            if (!Enabled) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var old in seen.Where(p => p.Value < now).Select(p => p.Key).ToArray()) seen.Remove(old);
            foreach (var ticket in reply.Tickets.Take(16))
            {
                if (seen.ContainsKey(ticket.Id) || jobs.Count >= 16) continue;
                seen[ticket.Id] = ticket.Expires;
                var token = incoming.Token;
                var task = Handle(ticket, token); jobs[ticket.Id] = task;
                _ = task.ContinueWith(t => jobs.TryRemove(ticket.Id, out _), TaskScheduler.Default);
            }
        }
        finally { edits.Release(); }
    }
    async Task Handle(ServerTicket ticket, CancellationToken ct)
    {
        try
        {
            if (!Enabled || ticket.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;
            var request = Verify<ServerRequest>(ticket.Proof, "/api/v1/connect", Wire.Endpoint(ticket.ControllerEndpoint));
            if (request.TargetEndpoint != engine.Id || request.Code != Code || request.SessionId != ticket.ClientSessionId || request.RequestId != ticket.Id || request.Expires != ticket.Expires)
                throw new InvalidDataException("连接请求与设备签名不匹配。");
            var sessionId = await engine.AcceptServerSession(ticket, (request, token) => AuthorizePassword(ticket, request, token), ct);
            await Call<JsonElement>($"/api/v1/tickets/{ticket.Id}/ready",
                new ServerOffer(sessionId, ticket.Id, ticket.ControllerEndpoint, engine.Id, ticket.ClientSessionId, ticket.Expires), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Notice?.Invoke(ex.Message);
            try { await Call<JsonElement>($"/api/v1/tickets/{ticket.Id}/reject", new { }, stop.Token); } catch { }
        }
    }
    async Task<SignedEnvelope?> AuthorizePassword(ServerTicket ticket, PasswordRequest request, CancellationToken ct)
    {
        lock(sync)
        {
            if(!access.Enabled) return null;
            if(!request.Enroll) return access.Controllers.TryGetValue(ticket.ControllerEndpoint,out var saved) ? saved.Receipt : null;
        }
        string? salt, hash;
        lock (sync)
        {
            var now = DateTime.UtcNow;
            while (attempts.TryPeek(out var time) && time < now.AddMinutes(-1)) attempts.Dequeue();
            if (!access.Enabled || attempts.Count >= 10 || request.Password.Length > 128) return null;
            attempts.Enqueue(now); salt = access.Salt; hash = access.Hash;
        }
        if (salt == null || hash == null) return null;
        var actual = Rfc2898DeriveBytes.Pbkdf2(request.Password, Convert.FromBase64String(salt), 600000, HashAlgorithmName.SHA256, 32);
        var valid = CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(hash));
        CryptographicOperations.ZeroMemory(actual);
        if (!valid) return null;
        await confirmations.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (sync) if (access.Enabled && access.Controllers.TryGetValue(ticket.ControllerEndpoint,out var known)) return known.Receipt;
            if (Confirm == null || !await Confirm(new Pairing(ticket.ControllerCode, ticket.ControllerEndpoint, true), ct)) return null;
            ct.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!access.Enabled) return null;
                var receipt = Sign("/grant/v1",new GrantReceipt(ticket.ControllerEndpoint,engine.Id,Guid.NewGuid().ToString("N")));
                access.Controllers[ticket.ControllerEndpoint] = new ControllerGrant(ticket.ControllerCode,receipt); Save(); Changed?.Invoke();
                return receipt;
            }
        }
        finally { confirmations.Release(); }
    }
    public async Task SetAccess(bool enabled, string? password, CancellationToken ct = default)
    {
        await edits.WaitAsync(ct);
        try
        {
            if (password != null && (password.Length < 8 || password.Length > 128)) throw new InvalidDataException("访问密码应为 8–128 个字符。");
            lock(sync)if(password==UnprotectPassword(access.Password!))password=null;
            lock (sync) if (enabled && password == null && access.Hash == null) throw new InvalidDataException("请先设置访问密码。");
            lock (sync) { access.Enabled = false; Save(); }
            incoming.Cancel();
            await engine.StopIncoming();
            await Task.WhenAll(jobs.Values);
            incoming.Dispose(); incoming = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            if (password != null)
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
                lock (sync) { access.Password=ProtectPassword(password);access.Salt = Convert.ToBase64String(salt); access.Hash = Convert.ToBase64String(hash); access.Controllers.Clear(); Save(); }
                CryptographicOperations.ZeroMemory(hash);
            }
            if (code.Length == 0) await Register(ct);
            await Call<JsonElement>("/api/v1/access", new { enabled }, ct);
            lock (sync) { access.Enabled = enabled; Save(); }
            Changed?.Invoke();
        }
        finally { edits.Release(); }
    }
    public async Task Connect(string machine, string password, CancellationToken ct = default, bool enrolling = true)
    {
        machine = Wire.Machine(machine);
        if (code.Length == 0) await Register(ct);
        if (machine == Code) throw new InvalidOperationException("不能连接本机。");
        var target = await Call<MachineInfo>("/api/v1/lookup", new { code = machine }, ct);
        if (target.Code != machine) throw new InvalidDataException("机器编号不匹配。");
        Wire.Endpoint(target.Endpoint);
        string? pinned;
        lock (sync) access.Targets.TryGetValue(machine, out pinned);
        if (pinned != null && pinned != target.Endpoint) throw new InvalidDataException("该机器的 EndpointId 已改变！已阻止连接，请通过独立渠道核对，不能自动信任服务器的新映射。");
        if (pinned == null)
        {
            if (Confirm == null || !await Confirm(new Pairing(machine, target.Endpoint, false), ct)) throw new OperationCanceledException("尚未核对对端 EndpointId。");
            lock (sync) { access.Targets[machine] = target.Endpoint; Save(); }
        }
        if (engine.Outgoing.TryGetValue(target.Endpoint, out var active) && active.Live) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var dir = engine.State.NewSessionDirectory();
        var transferred = false;
        try
        {
            var local = await Ard.Identity(dir, timeout.Token);
            var request = new ServerRequest(machine, target.Endpoint, local, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
            await Call<JsonElement>("/api/v1/connect", request, timeout.Token);
            ServerOffer? offer = null;
            while (offer == null)
            {
                await Task.Delay(1000, timeout.Token);
                var status = await Call<TicketReply>($"/api/v1/tickets/{request.RequestId}", new { }, timeout.Token);
                if (status.Status == "rejected") throw new IOException("对端未接受连接，请检查允许访问开关。");
                if (status.Status != "ready") continue;
                offer = Verify<ServerOffer>(status.Offer ?? throw new InvalidDataException("缺少对端签名。"), $"/api/v1/tickets/{request.RequestId}/ready", target.Endpoint);
                if (offer.RequestId != request.RequestId || offer.ControllerEndpoint != engine.Id || offer.TargetEndpoint != target.Endpoint || offer.ClientSessionId != local || offer.Expires != request.Expires)
                    throw new InvalidDataException("对端签名没有绑定当前会话，已阻止连接。");
                Wire.Endpoint(offer.SessionId);
            }
            transferred = true;
            await engine.ConnectServerSession(target, dir, offer, enrolling, password, timeout.Token);
        }
        finally { if (!transferred) try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
    public KeyValuePair<string,ControllerGrant>[] Controllers { get { lock(sync) return access.Controllers.ToArray(); } }
    public async Task Revoke(string endpoint)
    {
        lock(sync){access.Controllers.Remove(endpoint);Save();}
        if(engine.Incoming.TryRemove(endpoint,out var session))await session.DisposeAsync();
        Changed?.Invoke();
    }
    public async Task RemoveLocal(Peer peer)
    {
        if(engine.Outgoing.TryRemove(peer.Id,out var session))await session.DisposeAsync();
        engine.State.Remove(peer.Id);
        lock(sync){access.Targets.Remove(peer.Code);Save();}
        Changed?.Invoke();
    }
    public void ExportIdentity(string destination, string password)
    {
        if (password.Length < 12) throw new InvalidDataException("备份密码至少需要 12 个字符，请妥善保管。");
        var seed = File.ReadAllBytes(Path.Combine(engine.State.IdentityDirectory, "identity"));
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[32]; var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(derived, 16); aes.Encrypt(nonce, seed, cipher, tag, "ArdUi identity backup v1"u8);
            File.WriteAllText(destination, JsonSerializer.Serialize(new IdentityBackup(1, engine.Id, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)), Wire.Json));
        }
        finally { CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(derived); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); incoming.Cancel();
        try { await loop; } catch { }
        await engine.StopIncoming();
        try { await Task.WhenAll(jobs.Values); } catch { }
        http.Dispose(); key.Dispose(); incoming.Dispose(); stop.Dispose();
    }
}
sealed record IdentityBackup(int Version, string Endpoint, string Salt, string Nonce, string Ciphertext, string Tag);

static class IdentityStore
{
    public static string Prepare(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("身份安装需要 Windows。");
        var folder = Path.Combine(Path.GetFullPath(root), "device");
        using var mutex = new Mutex(false, "Local\\ArdUi-Identity-Install");
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("另一项身份安装正在运行。");
        try
        {
            Directory.CreateDirectory(folder);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确定当前 Windows 用户。");
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
            var destination = Path.Combine(folder, "identity");
            if (!File.Exists(destination))
            {
                var seed = RandomNumberGenerator.GetBytes(32);
                var temporary = Path.Combine(folder, ".identity-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(seed); file.Flush(true); }
                    // Atomic creation only: never replace an existing identity.
                    try { File.Move(temporary, destination, false); }
                    catch (IOException) when (File.Exists(destination)) { }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seed);
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            var existing = File.ReadAllBytes(destination);
            try
            {
                if (existing.Length != 32) throw new IOException("已有身份文件无效，安装已停止，未覆盖原文件。");
                using var key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519,
                    existing, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
                return Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant();
            }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}

sealed class LocalForwarder : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly UdpClient? udp;
    readonly Session session;
    readonly int target;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int,Task> tasks = new();
    readonly SemaphoreSlim slots = new(128);
    readonly Task accept;
    readonly Task receive;
    readonly ConcurrentDictionary<IPEndPoint,LocalUdpFlow> udpFlows = new();
    int serial;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public LocalForwarder(Session session,int target)
    {
        this.session=session;this.target=target;
        listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        if(session.UdpPorts.Contains(target)) udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port));
        accept=Accept();receive=udp == null ? Task.CompletedTask : ReceiveUdp();
    }
    async Task Accept()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                if(!slots.Wait(0)){client.Dispose();continue;}
                var id=Interlocked.Increment(ref serial);var task=Forward(client);tasks[id]=task;
                _=task.ContinueWith(t=>{slots.Release();tasks.TryRemove(id,out _);},TaskScheduler.Default);
            }
        }
        catch(Exception) when(stop.IsCancellationRequested) { }
    }
    async Task Forward(TcpClient client)
    {
        using(client)
        try { using var upstream=await session.OpenTcp(target,stop.Token); await Wire.Bridge(client,upstream,stop.Token); }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    async Task ReceiveUdp()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await udp!.ReceiveAsync(stop.Token);
                if(packet.Buffer.Length > Wire.MaxUdp) continue;
                if(!udpFlows.TryGetValue(packet.RemoteEndPoint,out var flow))
                {
                    if(udpFlows.Count >= 64) continue;
                    flow=new LocalUdpFlow(session,target,packet.RemoteEndPoint,async (data,remote) =>
                        await udp.SendAsync(data,remote,stop.Token),stop.Token);
                    udpFlows[packet.RemoteEndPoint]=flow;
                    var captured=flow;
                    _=flow.Completion.ContinueWith(t =>
                    { udpFlows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,captured)); captured.Dispose(); },TaskScheduler.Default);
                }
                await flow.Send(packet.Buffer);
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();udp?.Dispose();foreach(var flow in udpFlows.Values)flow.Dispose();
        try{await Task.WhenAll(accept,receive);await Task.WhenAll(tasks.Values);}catch{}stop.Dispose();
    }
}

sealed class LocalUdpFlow : IDisposable
{
    readonly UdpClient upstream=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly Session session;
    readonly int target;
    readonly IPEndPoint remote;
    readonly Func<byte[],IPEndPoint,Task> reply;
    readonly CancellationTokenSource stop;
    public Task Completion { get; }
    public LocalUdpFlow(Session session,int target,IPEndPoint remote,Func<byte[],IPEndPoint,Task> reply,CancellationToken ct)
    {
        this.session=session;this.target=target;this.remote=remote;this.reply=reply;
        stop=CancellationTokenSource.CreateLinkedTokenSource(ct);upstream.Connect(IPAddress.Loopback,session.LocalPort);
        stop.CancelAfter(TimeSpan.FromMinutes(2));Completion=Receive();
    }
    public async Task Send(byte[] data)
    {
        stop.CancelAfter(TimeSpan.FromMinutes(2));
        await upstream.SendAsync(Wire.Packet(session.Capability,target,data),stop.Token);
    }
    async Task Receive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await upstream.ReceiveAsync(stop.Token);stop.CancelAfter(TimeSpan.FromMinutes(2));
                if(Wire.ValidPacket(packet.Buffer,session.Capability) && BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16)) == target)
                    await reply(packet.Buffer[18..],remote);
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    int disposed;
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){stop.Cancel();upstream.Dispose();}}
}

static class WindowsShares
{
    public static async Task<string> Map(Session session,string share,string? user,string? password,CancellationToken ct)
    {
        if(!Regex.IsMatch(share,@"\A[^\\/:*?\""<>|\x00-\x1f]{1,80}\z")) throw new InvalidDataException("请输入单个共享名，例如 Documents。");
        var bridge=session.Forward(445);
        var remote=@"\\localhost\"+share;
        var script="""
        $ErrorActionPreference='Stop'
        $d=[Console]::In.ReadToEnd() | ConvertFrom-Json
        $used=@(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name)
        $letter=90..68 | ForEach-Object { [string][char]$_ } | Where-Object { $_ -notin $used } | Select-Object -First 1
        if(-not $letter){throw '没有可用的盘符。'}
        $argsMap=@{LocalPath=($letter+':');RemotePath=$d.Remote;TcpPort=[uint16]$d.Port;Persistent=$false;ErrorAction='Stop'}
        if($d.User){$argsMap.Credential=[pscredential]::new($d.User,(ConvertTo-SecureString $d.Password -AsPlainText -Force))}
        New-SmbMapping @argsMap | Out-Null
        Write-Output ($letter+':')
        """;
        var drive=(await Run(script,new { Remote=remote,Port=bridge.Port,User=user,Password=password },ct)).Trim();
        if(!Regex.IsMatch(drive,@"\A[D-Z]:\z")) throw new IOException("Windows 返回无效共享映射。");
        lock(session.Mappings) session.Mappings[drive]=remote;
        return drive+"\\";
    }
    public static async Task Remove(string drive,string remote)
    {
        var script="""
        $ErrorActionPreference='Stop'
        $d=[Console]::In.ReadToEnd() | ConvertFrom-Json
        $m=Get-SmbMapping -LocalPath $d.Drive -ErrorAction SilentlyContinue
        if($m -and $m.RemotePath -eq $d.Remote){Remove-SmbMapping -LocalPath $d.Drive -Force -Confirm:$false -ErrorAction Stop}
        """;
        try { await Run(script,new{Drive=drive,Remote=remote},CancellationToken.None); } catch { }
    }
    static async Task<string> Run(string script,object input,CancellationToken ct)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        using var process=Process.Start(start) ?? throw new IOException("无法启动 Windows SMB 客户端。");
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));process.StandardInput.Close();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try{await process.WaitForExitAsync(deadline.Token);}catch{try{process.Kill(true);}catch{}throw;}
        var message=await error;var text=await output;
        if(process.ExitCode!=0)throw new IOException("Windows SMB 连接失败，请检查共享名、账户及系统策略。"+Environment.NewLine+message);
        return text;
    }
}
