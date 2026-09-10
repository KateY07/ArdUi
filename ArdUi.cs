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
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.Styling;
using SkiaSharp;

namespace ArdUi;

// All application implementation lives in this file. ARD owns peer cryptography.
static class Program
{
    public const string Version = "v1.pre11";
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
    public bool AutoConnect { get; set; } = true;
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
            var peer = new Peer { Id = id, Address = address, Code = code, Grant = grant, AutoConnect=false };
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
    public const int BandwidthProbeSize=65536;
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
                if(command==3&&port==0)
                {
                    Attached?.Invoke();client.NoDelay=true;await stream.WriteAsync(new byte[]{0},handshake.Token);
                    var block=new byte[8192];for(var sent=0;sent<Wire.BandwidthProbeSize;sent+=block.Length)await stream.WriteAsync(block,handshake.Token);
                    return;
                }
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
    public double? BandwidthMbps { get; set; }
    public ForwardHub? Hub { get; }
    readonly Gateway? gateway;
    readonly string directory;
    readonly CancellationTokenSource stop = new();
    Task metrics = Task.CompletedTask;
    public LocalForwarder Forward(int target)=>Hub?.Forward(target)??throw new IOException("被控会话不提供本地转发入口。");
    int disposed;
    public Session(string peer, string address, int port, byte[] capability, int[] tcpPorts, int[] udpPorts,
        Child process, string directory, Gateway? gateway = null, ForwardHub? hub = null)
    {
        PeerId = peer; Address = address; LocalPort = port; Capability = capability;
        TcpPorts = tcpPorts; UdpPorts = udpPorts; Process = process; this.directory = directory;
        this.gateway = gateway; Hub=hub; Host = gateway != null;
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
    async Task<double> MeasureBandwidth(CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,Token);timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,LocalPort,timeout.Token);
        var head=new byte[23];"AUI1"u8.CopyTo(head);Capability.CopyTo(head,4);head[20]=3;
        var watch=Stopwatch.StartNew();await tcp.GetStream().WriteAsync(head,timeout.Token);
        if((await Wire.Read(tcp.GetStream(),1,timeout.Token))[0]!=0)throw new IOException("带宽探测被拒绝。");
        _=await Wire.Read(tcp.GetStream(),Wire.BandwidthProbeSize,timeout.Token);watch.Stop();
        return Wire.BandwidthProbeSize*8d/Math.Max(watch.Elapsed.TotalSeconds,.001)/1_000_000d;
    }
    public void StartMetrics(Action changed)
    {
        if (Host || metrics != Task.CompletedTask) return;
        metrics = Task.Run(async () =>
        {
            var failures=0;var round=0;
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
                    if(round++%12==0)
                    {
                        try{BandwidthMbps=await MeasureBandwidth(stop.Token);}
                        catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}
                        catch{BandwidthMbps=null;}
                    }
                    failures=0;
                    changed();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch
                {
                    TcpRtt=null;UdpRtt=null;BandwidthMbps=null;changed();
                    if(++failures>=2){try{await Process.DisposeAsync();}catch{}break;}
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        Hub?.Detach(this);
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
    readonly ConcurrentDictionary<string,ForwardHub> hubs = new();
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
    public void SetStatus(string id,string message)=>Update(id,message);
    string Describe(Session session, bool host)
    {
        var metric=session.UdpRtt is{} udp?$" · RTT {udp:F1} ms":" · RTT -- ms";
        metric+=session.BandwidthMbps is{} bandwidth?$" · ~{bandwidth:F1} Mbps":" · ~-- Mbps";
        if(host)metric="";
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
    public async Task DropOutgoing(string id)
    {
        await operation.WaitAsync();
        try { if(Outgoing.TryRemove(id,out var session))await session.DisposeAsync();Update(id,"未连接"); }
        finally { operation.Release(); }
    }
    public async Task RemoveOutgoing(string id)
    {
        await DropOutgoing(id);
        if(hubs.TryRemove(id,out var hub))await hub.DisposeAsync();
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
            var hub=hubs.GetOrAdd(peer.Id,_=>new ForwardHub());
            session = new Session(peer.Id, peer.Address, port, Convert.FromHexString(admission.Token), admission.TcpPorts, admission.UdpPorts, child, dir, hub:hub);
            using (await session.OpenTcp(0, ct)) { }
            if (!Outgoing.TryAdd(peer.Id, session)) throw new IOException("该设备已有连接。");
            hub.Attach(session);
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
            foreach(var hub in hubs.Values)await hub.DisposeAsync();hubs.Clear();
        }
        finally { operation.Release(); }
    }
}

static class AppIcon
{
    public static WindowIcon Create()
    {
        using var bitmap=new SKBitmap(64,64,true);using var canvas=new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var blue=new SKPaint{Color=new SKColor(42,96,210),IsAntialias=true};
        using var white=new SKPaint{Color=SKColors.White,IsAntialias=true,StrokeWidth=5,StrokeCap=SKStrokeCap.Round};
        canvas.DrawRoundRect(new SKRect(3,3,61,61),13,13,blue);
        canvas.DrawLine(19,42,32,19,white);canvas.DrawLine(32,19,47,42,white);canvas.DrawLine(19,42,47,42,white);
        canvas.DrawCircle(19,42,5,white);canvas.DrawCircle(32,19,5,white);canvas.DrawCircle(47,42,5,white);
        using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);
        return new WindowIcon(new MemoryStream(data.ToArray(),false));
    }
}

sealed class MainWindow : Window
{
    static readonly IBrush Ink = Brush.Parse("#16243A"), Muted = Brush.Parse("#6A778A"),
        Online=Brush.Parse("#2E9D61"),Relay=Brush.Parse("#D09218"),Idle=Brush.Parse("#98A2B1");
    readonly TextBlock machine = new() { Text = "------", FontSize = 18, FontWeight = FontWeight.Bold, LetterSpacing = 2, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(8,0) };
    readonly TextBox endpoint = new() { IsReadOnly = true,FontFamily=new FontFamily("Consolas"),FontSize=10,Height=25,Width=210,VerticalContentAlignment=VerticalAlignment.Center };
    readonly TextBox remote = new() { Watermark = "ABC123", MaxLength = 6,Width=82 };
    readonly TextBox remotePassword = new() { Watermark = "123456",MaxLength=6,Width=82 };
    readonly CheckBox allow = new() { Content = "正在读取被控状态…", IsEnabled=false };
    readonly TextBox hostPassword = new() { Text="••••••",MaxLength=6,IsReadOnly=true,Width=70,FontFamily=new FontFamily("Consolas"),FontWeight=FontWeight.SemiBold };
    readonly StackPanel hostDetails = new() { IsVisible=false,Spacing=3 };
    readonly Expander identityExpander=new(){Header="ID",IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Left};
    readonly Border identityCard;
    readonly TextBlock status = new() { Text = "正在读取设备身份…",TextTrimming=TextTrimming.CharacterEllipsis,Foreground = Muted,FontSize=10 };
    readonly StackPanel activeIncoming = new() { Spacing = 6 };
    readonly StackPanel incomingArea = new() { Spacing = 6, IsVisible = false };
    readonly StackPanel pendingIncoming = new() { Spacing = 6 };
    readonly StackPanel controllers = new() { Spacing = 6 };
    readonly Expander controllersExpander = new() { Header = "可访问本机的设备管理", IsExpanded = false };
    readonly TextBlock networkLog = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontFamily = new FontFamily("Consolas"), FontSize = 9 };
    readonly TextBlock networkSummary = new() { Text="暂无网络事件",TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Muted,FontFamily=new FontFamily("Consolas"),FontSize=9,VerticalAlignment=VerticalAlignment.Center };
    readonly StackPanel devices = new() { Spacing = 4 };
    readonly TextBlock noDevices=new(){Text="尚未添加设备。",Foreground=Muted};
    readonly Dictionary<string,DeviceDisplay> deviceViews=new();
    readonly DispatcherTimer passwordTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly DispatcherTimer undoTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly ColumnDefinition dividerColumn,sideColumn;
    readonly GridSplitter splitter;
    readonly ScrollViewer sideScroll;
    readonly Border diagnostics,undoBar;
    readonly TextBlock undoText=new(){FontSize=10,VerticalAlignment=VerticalAlignment.Center};
    Engine? engine;
    DirectoryClient? directory;
    bool closing,closed,busy,ready,passwordVisible;
    DateTimeOffset passwordVisibleUntil;
    DateTimeOffset undoUntil;
    Peer? undoPeer;
    double rememberedSideWidth=240;
    sealed record DeviceDisplay(Border Card,Ellipse Indicator,TextBlock Title,TextBlock Status);
    public MainWindow(bool preview = false)
    {
        Title = "ArdUi " + Program.Version; Width = 660; Height = 420; MinWidth = 600; MinHeight = 360;
        Icon=AppIcon.Create();
        Background = Brush.Parse("#F4F6FA"); FontFamily = new FontFamily("Microsoft YaHei UI"); Foreground = Ink; FontSize = 11;
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        header.Children.Add(new TextBlock { Text = "ArdUi " + Program.Version, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0,0,0,6) });
        Grid.SetColumn(allow,1);header.Children.Add(allow);root.Children.Add(header);
        var reveal=Button("查看 10 秒",RevealPassword);reveal.FontSize=10;reveal.Padding=new Thickness(7,3);
        var idDetails=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Children={endpoint,
            Button("复制码",async()=>await Clipboard!.SetTextAsync(directory?.Code??"")),Button("复制 ID",async()=>await Clipboard!.SetTextAsync(engine?.Id??""))}};
        identityExpander.Content=idDetails;identityExpander.FontSize=10;identityExpander.Padding=new Thickness(4,1);
        var access = new StackPanel { Orientation=Orientation.Horizontal,Spacing=6,Children={machine,new TextBlock{Text="首次密码",VerticalAlignment=VerticalAlignment.Center},hostPassword,reveal,identityExpander}};
        allow.PropertyChanged+=async(_,e)=>{if(e.Property==CheckBox.IsCheckedProperty && ready)await Guard(SaveAccess);};
        hostDetails.Children.Add(access);identityCard=Card(hostDetails);identityCard.IsVisible=false;Grid.SetRow(identityCard,1);root.Children.Add(identityCard);
        controllersExpander.Content=controllers;controllersExpander.IsVisible=false;
        var main = new Grid { Margin=new Thickness(0,7,0,0),ColumnDefinitions=new ColumnDefinitions("*,5,240") };
        dividerColumn=main.ColumnDefinitions[1];sideColumn=main.ColumnDefinitions[2];
        var addRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Children={new TextBlock{Text="6位ID:",VerticalAlignment=VerticalAlignment.Center},remote,
            new TextBlock{Text="6位密码:",VerticalAlignment=VerticalAlignment.Center},remotePassword,Button("连接",Connect)}};
        devices.Children.Add(noDevices);
        var undoButton=Button("撤销",UndoRemove);undoButton.FontSize=9;undoButton.Padding=new Thickness(5,1);
        undoBar=Card(new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,Children={undoText,undoButton}});
        undoBar.IsVisible=false;undoBar.Padding=new Thickness(6,3);undoBar.Background=Brush.Parse("#FFF8E8");
        var deviceArea=new StackPanel{Spacing=5,Children={addRow,Label("已获得授权连接（本机可主动控制）",13),undoBar,devices}};
        main.Children.Add(new ScrollViewer{Content=deviceArea});
        splitter=new GridSplitter{ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=Brush.Parse("#D8DEE8"),HorizontalAlignment=HorizontalAlignment.Stretch};
        splitter.PointerReleased+=(_,_)=>SaveSideWidth();
        Grid.SetColumn(splitter,1);main.Children.Add(splitter);
        incomingArea.Children.Add(Label("正在访问本机",14));incomingArea.Children.Add(activeIncoming);
        var side=new StackPanel{Spacing=6,Children={incomingArea,pendingIncoming,controllersExpander}};
        sideScroll=new ScrollViewer{Content=side};Grid.SetColumn(sideScroll,2);main.Children.Add(sideScroll);Grid.SetRow(main,2);root.Children.Add(main);
        var diagnosticButton=new Button{Content="诊断⌄",FontSize=9,Padding=new Thickness(5,1)};
        diagnostics=Card(networkLog);diagnostics.IsVisible=false;diagnostics.Padding=new Thickness(5,3);
        diagnosticButton.Click+=(_,_)=>{diagnostics.IsVisible=!diagnostics.IsVisible;diagnosticButton.Content=diagnostics.IsVisible?"诊断⌃":"诊断⌄";};
        var networkRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};networkRow.Children.Add(networkSummary);
        Grid.SetColumn(diagnosticButton,1);networkRow.Children.Add(diagnosticButton);
        var footer=new StackPanel{Spacing=2,Margin=new Thickness(0,5,0,0),Children={status,networkRow,diagnostics}};
        Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;
        ToolTip.SetTip(endpoint,"本机永久 EndpointId，用于首次连接时核对身份。");
        Opened += async (_,_) =>
        {
            if(preview)return;
            await Guard(async () =>
            {
                var settings=JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Program.DataRoot,"config.json")),Wire.Json)!;
                engine=await Engine.Create(Program.DataRoot,settings);endpoint.Text=engine.Id;
                engine.Changed+=()=>Dispatcher.UIThread.Post(Refresh);
                directory=new DirectoryClient(engine);rememberedSideWidth=directory.SideWidth;
                directory.Confirm=async(pair,ct)=>await Dispatcher.UIThread.InvokeAsync(()=>ConfirmPair(pair,ct));
                directory.Changed+=()=>Dispatcher.UIThread.Post(Refresh);
                directory.Notice+=message=>Dispatcher.UIThread.Post(()=>Say(message));
                allow.IsChecked=directory.Enabled;hostDetails.IsVisible=directory.Enabled;identityCard.IsVisible=directory.Enabled;allow.Content="允许被控";allow.IsEnabled=true;ready=true;SetSideVisible(directory.Enabled);directory.Start();passwordTimer.Start();UpdatePassword();Say("正在连接可信服务器…");
            });
        };
        Closing+=async(_,e)=>
        {
            if(closed)return;e.Cancel=true;if(closing)return;closing=true;IsEnabled=false;
            try{passwordTimer.Stop();undoTimer.Stop();if(directory!=null)await directory.DisposeAsync();if(engine!=null)await engine.DisposeAsync();}
            finally{closed=true;Close();}
        };
        if(preview)
        {
            machine.Text="A7B2K9";endpoint.Text="5889f4c2e3c89af503a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1";
            allow.Content="允许被控";allow.IsEnabled=true;allow.IsChecked=false;Say("可信服务器 · https://f.visnova.cn/ · 本机被控功能已关闭");
            var sample=Device(new Peer{Id=new string('a',64),Code="C8M3P6",Name="办公电脑"});noDevices.IsVisible=false;devices.Children.Add(sample.Card);
            controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});SetSideVisible(false);
        }
        passwordTimer.Tick+=(_,_)=>UpdatePassword();
        undoTimer.Tick+=(_,_)=>UpdateUndo();
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
    void Say(string text){status.Text=text;ToolTip.SetTip(status,text);}
    static string PeerLabel(Peer peer)=>string.IsNullOrWhiteSpace(peer.Name)||peer.Name==peer.Code?peer.Code:peer.Name+" · "+peer.Code;
    static IBrush ConnectionBrush(string text)
    {
        if(text.Contains("中继",StringComparison.OrdinalIgnoreCase)||text.Contains("relay",StringComparison.OrdinalIgnoreCase)||
           text.Contains("正在",StringComparison.Ordinal)||text.Contains("重连",StringComparison.Ordinal)||text.Contains("等待",StringComparison.Ordinal))return Relay;
        if(text.Contains("已连接",StringComparison.Ordinal)||text.Contains("直连",StringComparison.Ordinal))return Online;
        return Idle;
    }
    void SetSideVisible(bool visible)
    {
        sideScroll.IsVisible=visible;splitter.IsVisible=visible;
        dividerColumn.Width=new GridLength(visible?5:0);
        sideColumn.Width=new GridLength(visible?rememberedSideWidth:0);
    }
    void SaveSideWidth()
    {
        if(directory?.Enabled!=true||sideColumn.ActualWidth<180)return;
        rememberedSideWidth=Math.Clamp(sideColumn.ActualWidth,180,360);directory.SetSideWidth(rememberedSideWidth);
    }
    void Refresh()
    {
        if(engine==null)return;
        if(directory!=null && directory.Code.Length!=0 && machine.Text!=directory.Code)
        {machine.Text=directory.Code;Say("设备已注册 · "+engine.Settings.Server);}
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.ToArray();
        var structural=deviceViews.Count!=peers.Length||peers.Any(p=>!deviceViews.ContainsKey(p.Id));
        foreach(var stale in deviceViews.Keys.Except(peers.Select(p=>p.Id)).ToArray())deviceViews.Remove(stale);
        foreach(var peer in peers)if(!deviceViews.ContainsKey(peer.Id))deviceViews[peer.Id]=Device(peer);
        if(structural)
        {
            devices.Children.Clear();devices.Children.Add(noDevices);
            foreach(var peer in peers)devices.Children.Add(deviceViews[peer.Id].Card);
        }
        noDevices.IsVisible=peers.Length==0;
        foreach(var peer in peers)
        {
            var view=deviceViews[peer.Id];var title=PeerLabel(peer);
            var stateText=engine.Status.GetValueOrDefault(peer.Id,peer.AutoConnect?"已授权 · 正在连接":"已授权 · 已暂停");
            view.Title.Text=title;view.Status.Text=stateText;view.Indicator.Fill=ConnectionBrush(stateText);
            ToolTip.SetTip(view.Title,title);ToolTip.SetTip(view.Status,stateText);
        }
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
            var controllerText=new TextBlock{Text=pair.Value.Code+" · "+endpointId[..8]+"…",VerticalAlignment=VerticalAlignment.Center};
            ToolTip.SetTip(controllerText,endpointId);row.Children.Add(controllerText);
            row.Children.Add(Button("撤销",async()=>await directory.Revoke(endpointId)));controllers.Children.Add(row);
        }
        if(controllers.Children.Count==0)controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});
        var logs=engine.Logs.TakeLast(3).ToArray();networkSummary.Text=logs.LastOrDefault()??"暂无网络事件";
        networkLog.Text=logs.Length==0?"暂无网络事件":string.Join(Environment.NewLine,logs);ToolTip.SetTip(networkSummary,networkSummary.Text);
        if(directory!=null)
        {
            if(allow.IsChecked!=directory.Enabled){ready=false;allow.IsChecked=directory.Enabled;ready=true;}
            hostDetails.IsVisible=directory.Enabled;
            identityCard.IsVisible=directory.Enabled;controllersExpander.IsVisible=directory.Enabled;SetSideVisible(directory.Enabled);
            if(!directory.Enabled){identityExpander.IsExpanded=false;passwordVisible=false;}UpdatePassword();
            Title="ArdUi "+Program.Version+(directory.Enabled&&directory.Code.Length!=0?" · "+directory.Code:"");
        }
    }
    async Task Connect()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        var password=remotePassword.Text??"";remotePassword.Text="";Say("正在核对对端身份并申请授权…");
        await directory.Connect(remote.Text??"",password);Say("已获授权，可以从设备列表打开远程桌面和文件共享。");
    }
    Task RevealPassword()
    {
        if(directory==null||!directory.Enabled)throw new InvalidOperationException("请先允许被控。");
        passwordVisible=true;passwordVisibleUntil=DateTimeOffset.UtcNow.AddSeconds(10);UpdatePassword();return Task.CompletedTask;
    }
    void UpdatePassword()
    {
        if(directory==null||!directory.Enabled){passwordVisible=false;hostPassword.Text="••••••";return;}
        if(passwordVisible&&DateTimeOffset.UtcNow>=passwordVisibleUntil)passwordVisible=false;
        var text=passwordVisible?directory.AccessPassword:"••••••";if(hostPassword.Text!=text)hostPassword.Text=text;
    }
    async Task SaveAccess()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        await directory.SetAccess(allow.IsChecked==true);
        Say(directory.Enabled?"已自动保存：允许远程访问。":"已自动保存：关闭远程访问并断开被控会话。");
    }
    DeviceDisplay Device(Peer peer)
    {
        var displayName=PeerLabel(peer);
        var title=new TextBlock{Text=displayName,FontSize=11,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=110};
        var state=new TextBlock{Text=engine?.Status.GetValueOrDefault(peer.Id,"正在连接")??"已连接 · P2P 直连 / IPv4 · RTT 18.2 ms · ~86.4 Mbps",Foreground=Muted,FontSize=9,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(8,0,4,0)};
        var indicator=new Ellipse{Width=7,Height=7,Fill=ConnectionBrush(state.Text??""),Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};
        ToolTip.SetTip(title,displayName);ToolTip.SetTip(state,state.Text);
        var row=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,Auto,*,Auto"),MinHeight=26};
        row.Children.Add(indicator);Grid.SetColumn(title,1);row.Children.Add(title);Grid.SetColumn(state,2);row.Children.Add(state);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3,VerticalAlignment=VerticalAlignment.Center};
        Button Add(string caption,Func<Task> action)
        { var button=Button(caption,action);button.FontSize=9;button.Padding=new Thickness(5,1);button.MinHeight=22;actions.Children.Add(button);return button; }
        Add("桌面",async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var session=engine!.Outgoing[peer.Id];var bridge=session.Forward(3389);
            await Launch("mstsc.exe","/v:127.0.0.1:"+bridge.Port);
        });
        Add("文件",async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var input=await ShareDetails();
            var path=await WindowsShares.Map(engine!.Outgoing[peer.Id],input.Share,input.User,input.Password,CancellationToken.None);
            await Launch("explorer.exe",path);
        });
        var pause=Add("暂停",async()=>await directory!.Pause(peer));ToolTip.SetTip(pause,"断开当前会话并暂停自动重连；再次打开桌面或文件时恢复。");
        var moreActions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3};
        void More(string caption,Func<Task> action){var button=Button(caption,action);button.FontSize=9;button.Padding=new Thickness(5,1);button.MinHeight=22;moreActions.Children.Add(button);}
        More("EndpointId",async()=>{await Clipboard!.SetTextAsync(peer.Id);Say("完整 EndpointId 已复制："+peer.Id);});
        More("备注",async()=>{peer.Name=await AskText("设备备注",peer.Name);engine!.State.Save();});
        More("移除",async()=>await RemoveWithUndo(peer));
        var moreRow=new Border{Child=moreActions,IsVisible=false,Padding=new Thickness(0,3,0,0)};
        var more=new Button{Content="更多⌄",FontSize=9,Padding=new Thickness(5,1),MinHeight=22};
        more.Click+=(_,_)=>{moreRow.IsVisible=!moreRow.IsVisible;more.Content=moreRow.IsVisible?"收起⌃":"更多⌄";};
        actions.Children.Add(more);Grid.SetColumn(actions,3);row.Children.Add(actions);
        var stack=new StackPanel{Spacing=0,Children={row,moreRow}};var card=Card(stack);card.Padding=new Thickness(6,3);return new DeviceDisplay(card,indicator,title,state);
    }
    async Task RemoveWithUndo(Peer peer)
    {
        var snapshot=new Peer{Id=peer.Id,Address=peer.Address,Code=peer.Code,Name=peer.Name,Grant=peer.Grant,AutoConnect=peer.AutoConnect};
        await directory!.RemoveLocal(peer);undoPeer=snapshot;undoUntil=DateTimeOffset.UtcNow.AddSeconds(8);undoBar.IsVisible=true;undoTimer.Start();UpdateUndo();
        Say($"已移除 {peer.Code}，可在 8 秒内撤销。");
    }
    Task UndoRemove()
    {
        var snapshot=undoPeer??throw new InvalidOperationException("撤销期限已结束。");
        undoPeer=null;undoTimer.Stop();undoBar.IsVisible=false;directory!.RestoreLocal(snapshot);Say("已撤销移除："+snapshot.Code);return Task.CompletedTask;
    }
    void UpdateUndo()
    {
        if(undoPeer==null)return;
        var seconds=(int)Math.Ceiling((undoUntil-DateTimeOffset.UtcNow).TotalSeconds);
        if(seconds<=0){undoPeer=null;undoTimer.Stop();undoBar.IsVisible=false;return;}
        undoText.Text=$"已移除 {undoPeer.Code} · {seconds} 秒内可撤销";
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
    { Styles.Add(new SimpleTheme()); RequestedThemeVariant = ThemeVariant.Light; }
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
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
            var minute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;var enrollmentPassword=hd.AccessPassword;
            if(!Regex.IsMatch(enrollmentPassword,@"\A[0-9]{6}\z")||enrollmentPassword==hd.AccessPasswordFor(minute+1))throw new Exception("Rotating password failure.");
            await hd.SetAccess(true,ct);hd.Start();cd.Start();
            var connection = cd.Connect(hd.Code,enrollmentPassword,ct);
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
            await VerifyFlow();Console.WriteLine("PASS: loopback TCP proxy through authenticated ARD to remote service.");
            var first=caller.Outgoing[host.Id];var stablePort=first.Forward(port).Port;await first.Process.DisposeAsync();
            using(var recovery=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                recovery.CancelAfter(TimeSpan.FromSeconds(90));
                while(!caller.Outgoing.TryGetValue(host.Id,out var restored)||ReferenceEquals(first,restored)||!restored.Live)await Task.Delay(100,recovery.Token);
            }
            if(caller.Outgoing[host.Id].Forward(port).Port!=stablePort)throw new Exception("Local forwarding port changed across reconnect.");
            await VerifyFlow();Console.WriteLine("PASS: killed ARD child recovered automatically with the same local forwarding port.");
            var savedPeer=caller.State.Peers.Single();await cd.Pause(savedPeer);await host.Disconnect(caller.Id);
            await cd.Connect(hd.Code,"",ct,enrolling:false);
            if (hostChecks != 1 || callerChecks != 1) throw new Exception("Authorized reconnect prompted again.");
            await VerifyFlow(); Console.WriteLine("PASS: authorized reconnect without enrollment password or consent.");
            await hd.Revoke(caller.Id);
            if (host.Incoming.Count != 0 || hd.Controllers.Length != 0) throw new Exception("Revoke failed.");
            await cd.Pause(caller.State.Peers.Single());
            try { await cd.Connect(hd.Code,"",ct,enrolling:false); throw new Exception("Revoked caller admitted."); }
            catch (IOException) { }
            Console.WriteLine("PASS: revocation removes ACL and denies reconnect.");
            hd.Confirm=(pair,token)=>Task.FromResult(pair.Incoming&&pair.Endpoint==caller.Id);
            for(var round=1;round<=3;round++)
            {
                Console.WriteLine($"TEST: repeated add round {round} connecting.");
                await cd.Connect(hd.Code,hd.AccessPassword,ct).WaitAsync(TimeSpan.FromSeconds(75),ct);
                await VerifyFlow();
                var saved=caller.State.Peers.Single();await cd.RemoveLocal(saved);
                if(caller.State.Peers.Count!=0||caller.Outgoing.Count!=0)throw new Exception("Repeated local removal failed.");
                Console.WriteLine($"TEST: repeated add round {round} removed.");
            }
            await hd.Revoke(caller.Id);
            Console.WriteLine("PASS: three add, transfer, remove, and immediate re-add cycles.");
            await hd.SetAccess(false,ct);
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
    public string? EnrollmentSecret { get; set; }
    public Dictionary<string, ControllerGrant> Controllers { get; set; } = new();
    public Dictionary<string, string> Targets { get; set; } = new();
    public double SideWidth { get; set; } = 240;
}

// The directory routes signed public session descriptions. Passwords and flow
// capabilities only cross ARD after both long-term identity signatures verify.
sealed class DirectoryClient : IAsyncDisposable
{
    sealed class ReconnectJob(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; }=stop;
        public Task Task { get; set; }=Task.CompletedTask;
    }
    readonly Engine engine;
    readonly HttpClient http;
    readonly NSec.Cryptography.Key key;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim edits = new(1), confirmations = new(1);
    readonly object sync = new();
    readonly string path;
    readonly AccessSettings access;
    readonly byte[] enrollmentSecret;
    readonly ConcurrentDictionary<string, Task> jobs = new();
    readonly ConcurrentDictionary<string,ReconnectJob> reconnects=new();
    readonly Dictionary<string, long> seen = new();
    readonly Queue<DateTime> attempts = new();
    CancellationTokenSource incoming = new();
    Task loop = Task.CompletedTask;
    string code = "";
    bool online;
    public string Code => code;
    public bool Online => online;
    public bool Enabled { get { lock (sync) return access.Enabled; } }
    public double SideWidth { get { lock(sync)return Math.Clamp(access.SideWidth,180,360); } }
    public string AccessPassword=>AccessPasswordFor(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60);
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
        if(access.EnrollmentSecret==null)
        {
            var secret=RandomNumberGenerator.GetBytes(32);
            try{access.EnrollmentSecret=Convert.ToBase64String(ProtectedData.Protect(secret,null,DataProtectionScope.CurrentUser));Save();}
            finally{CryptographicOperations.ZeroMemory(secret);}
        }
        try{enrollmentSecret=ProtectedData.Unprotect(Convert.FromBase64String(access.EnrollmentSecret),null,DataProtectionScope.CurrentUser);}
        catch(Exception ex){throw new InvalidDataException("首次连接密码密钥损坏。",ex);}
        if(enrollmentSecret.Length!=32){CryptographicOperations.ZeroMemory(enrollmentSecret);throw new InvalidDataException("首次连接密码密钥长度无效。");}
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
    public string AccessPasswordFor(long minute)
    {
        Span<byte> period=stackalloc byte[8];BinaryPrimitives.WriteInt64BigEndian(period,minute);
        var digest=HMACSHA256.HashData(enrollmentSecret,period);
        try{return (BinaryPrimitives.ReadUInt32BigEndian(digest)%1_000_000).ToString("D6");}
        finally{CryptographicOperations.ZeroMemory(digest);}
    }
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
    public void Start()=>loop=Run();
    void StartReconnects()
    {
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.Where(p=>p.AutoConnect).ToArray();
        foreach(var peer in peers)EnsureReconnect(peer);
    }
    void EnsureReconnect(Peer peer)
    {
        if(!peer.AutoConnect||stop.IsCancellationRequested||reconnects.ContainsKey(peer.Id))return;
        var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);var job=new ReconnectJob(linked);
        if(!reconnects.TryAdd(peer.Id,job)){linked.Dispose();return;}
        job.Task=Supervise(peer,linked.Token);_=CleanupReconnect(peer.Id,job);
    }
    async Task CleanupReconnect(string id,ReconnectJob job)
    {
        try{await job.Task;}catch{}
        finally{if(reconnects.TryRemove(new KeyValuePair<string,ReconnectJob>(id,job)))job.Stop.Dispose();}
    }
    async Task Supervise(Peer peer,CancellationToken ct)
    {
        var delay=1;var announced=false;
        while(!ct.IsCancellationRequested&&peer.AutoConnect)
        {
            if(engine.Outgoing.TryGetValue(peer.Id,out var active)&&active.Live)
            {
                try{await active.Process.Exited.WaitAsync(ct);}catch(OperationCanceledException){break;}
                continue;
            }
            engine.SetStatus(peer.Id,delay==1?"正在自动恢复连接…":$"自动重连等待 {delay} 秒…");
            try
            {
                await ConnectOnce(peer.Code,"",false,ct,true);delay=1;announced=false;continue;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                engine.SetStatus(peer.Id,"自动重连中 · "+ex.Message);
                if(!announced){Notice?.Invoke(peer.Code+" 自动重连中："+ex.Message);announced=true;}
            }
            try{await Task.Delay(TimeSpan.FromSeconds(delay),ct);}catch(OperationCanceledException){break;}
            delay=Math.Min(delay*2,30);
        }
    }
    async Task StopReconnect(string id)
    {
        if(!reconnects.TryRemove(id,out var job))return;
        job.Stop.Cancel();try{await job.Task;}catch{}finally{job.Stop.Dispose();}
    }
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
                StartReconnects();
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
        lock (sync)
        {
            var now = DateTime.UtcNow;
            while (attempts.TryPeek(out var time) && time < now.AddMinutes(-1)) attempts.Dequeue();
            if (!access.Enabled || attempts.Count >= 10 || !Regex.IsMatch(request.Password,@"\A[0-9]{6}\z")) return null;
            attempts.Enqueue(now);
        }
        var supplied=Encoding.ASCII.GetBytes(request.Password);var valid=false;
        try
        {
            var minute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;
            for(var offset=0;offset<=1;offset++)
            {
                var expected=Encoding.ASCII.GetBytes(AccessPasswordFor(minute-offset));
                try{valid|=CryptographicOperations.FixedTimeEquals(supplied,expected);}finally{CryptographicOperations.ZeroMemory(expected);}
            }
        }
        finally{CryptographicOperations.ZeroMemory(supplied);}
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
    public async Task SetAccess(bool enabled, CancellationToken ct = default)
    {
        await edits.WaitAsync(ct);
        try
        {
            lock (sync) { access.Enabled = false; Save(); }
            incoming.Cancel();
            await engine.StopIncoming();
            await Task.WhenAll(jobs.Values);
            incoming.Dispose(); incoming = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            if (code.Length == 0) await Register(ct);
            await Call<JsonElement>("/api/v1/access", new { enabled }, ct);
            lock (sync) { access.Enabled = enabled; Save(); }
            Changed?.Invoke();
        }
        finally { edits.Release(); }
    }
    public async Task Connect(string machine,string password,CancellationToken ct=default,bool enrolling=true)
    {
        await ConnectOnce(machine,password,enrolling,ct,false);
        machine=Wire.Machine(machine);Peer peer;lock(engine.State.Peers)peer=engine.State.Peers.Single(p=>p.Code==machine);
        peer.AutoConnect=true;engine.State.Save();EnsureReconnect(peer);Changed?.Invoke();
    }
    async Task ConnectOnce(string machine,string password,bool enrolling,CancellationToken ct,bool background)
    {
        machine = Wire.Machine(machine);
        password=password.Trim();
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
        if(engine.Outgoing.TryGetValue(target.Endpoint,out var active))
        {
            if(active.Live)return;
            await engine.DropOutgoing(target.Endpoint);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(background?TimeSpan.FromSeconds(75):TimeSpan.FromMinutes(5));
        var dir = engine.State.NewSessionDirectory();
        var transferred = false;
        try
        {
            var local = await Ard.Identity(dir, timeout.Token);
            var request = new ServerRequest(machine,target.Endpoint,local,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddSeconds(background?60:300).ToUnixTimeSeconds());
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
        peer.AutoConnect=false;engine.State.Save();await StopReconnect(peer.Id);await engine.RemoveOutgoing(peer.Id);
        engine.State.Remove(peer.Id);
        lock(sync){access.Targets.Remove(peer.Code);Save();}
        Changed?.Invoke();
    }
    public void RestoreLocal(Peer snapshot)
    {
        if(snapshot.Grant==null)throw new InvalidDataException("设备授权记录不完整，无法撤销移除。");
        var peer=engine.State.GetOrAdd(snapshot.Id,snapshot.Code,snapshot.Grant);
        peer.Name=snapshot.Name;peer.AutoConnect=snapshot.AutoConnect;engine.State.Save();
        lock(sync){access.Targets[peer.Code]=peer.Id;Save();}
        if(peer.AutoConnect)EnsureReconnect(peer);Changed?.Invoke();
    }
    public void SetSideWidth(double width)
    {
        lock(sync){access.SideWidth=Math.Clamp(width,180,360);Save();}
    }
    public async Task Pause(Peer peer)
    {
        peer.AutoConnect=false;engine.State.Save();await StopReconnect(peer.Id);await engine.DropOutgoing(peer.Id);
        engine.SetStatus(peer.Id,"已授权 · 已暂停");Changed?.Invoke();
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
        foreach(var job in reconnects.Values)job.Stop.Cancel();
        try { await loop; } catch { }
        await engine.StopIncoming();
        try { await Task.WhenAll(jobs.Values); } catch { }
        try{await Task.WhenAll(reconnects.Values.Select(j=>j.Task));}catch{}
        foreach(var job in reconnects.Values)job.Stop.Dispose();reconnects.Clear();
        CryptographicOperations.ZeroMemory(enrollmentSecret);http.Dispose();key.Dispose();incoming.Dispose();stop.Dispose();
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

sealed class ForwardHub : IAsyncDisposable
{
    readonly object sync=new();
    readonly Dictionary<int,LocalForwarder> forwards=new();
    Session? current;
    int disposed;
    public Dictionary<string,string> Mappings { get; }=new();
    public Session? Current { get { lock(sync)return current; } }
    public void Attach(Session session)
    {
        LocalForwarder[] bridges;
        lock(sync){if(disposed!=0)throw new ObjectDisposedException(nameof(ForwardHub));current=session;bridges=forwards.Values.ToArray();}
        foreach(var bridge in bridges)bridge.Reset();
    }
    public void Detach(Session session)
    {
        LocalForwarder[] bridges;
        lock(sync)
        {
            if(!ReferenceEquals(current,session))return;
            current=null;bridges=forwards.Values.ToArray();
        }
        foreach(var bridge in bridges)bridge.Reset();
    }
    public LocalForwarder Forward(int target)
    {
        lock(sync)
        {
            if(disposed!=0)throw new ObjectDisposedException(nameof(ForwardHub));
            var session=current;
            if(session==null||!session.Live)throw new IOException("设备正在自动重连。");
            if(!session.TcpPorts.Contains(target))throw new IOException("被控端未授权此 TCP 端口。");
            if(!forwards.TryGetValue(target,out var bridge))forwards[target]=bridge=new LocalForwarder(this,target,session.UdpPorts.Contains(target));
            return bridge;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        LocalForwarder[] bridges;lock(sync){current=null;bridges=forwards.Values.ToArray();forwards.Clear();}
        KeyValuePair<string,string>[] mappings;lock(Mappings){mappings=Mappings.ToArray();Mappings.Clear();}
        foreach(var mapping in mappings)await WindowsShares.Remove(mapping.Key,mapping.Value);
        foreach(var bridge in bridges)await bridge.DisposeAsync();
    }
}

sealed class LocalForwarder : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly UdpClient? udp;
    readonly ForwardHub hub;
    readonly int target;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int,Task> tasks = new();
    readonly SemaphoreSlim slots = new(128);
    readonly Task accept;
    readonly Task receive;
    readonly ConcurrentDictionary<IPEndPoint,LocalUdpFlow> udpFlows = new();
    int serial;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public LocalForwarder(ForwardHub hub,int target,bool udpEnabled)
    {
        this.hub=hub;this.target=target;
        listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        if(udpEnabled)udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port));
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
        try
        {
            var session=hub.Current??throw new IOException("设备正在自动重连。");
            using var upstream=await session.OpenTcp(target,stop.Token);await Wire.Bridge(client,upstream,stop.Token);
        }
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
                    var session=hub.Current;
                    if(session==null||!session.Live||!session.UdpPorts.Contains(target))continue;
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
    public void Reset()
    {
        foreach(var pair in udpFlows.ToArray())if(udpFlows.TryRemove(pair.Key,out var flow))flow.Dispose();
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
        var mappings=session.Hub?.Mappings??throw new IOException("共享转发入口不可用。");
        lock(mappings)mappings[drive]=remote;
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
