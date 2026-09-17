using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.IO.Compression;
using System.Threading.Channels;
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

// ARD authenticates each hop; the v2 overlay authenticates and preserves the A–B session.
static class Program
{
    public const string Version = "v2.pre1";
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
            if (args.Contains("--transit-test"))
                return TransitTest.Run(args).GetAwaiter().GetResult();
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
    public static void ConfigureUdp(UdpClient udp)
    {
        udp.Client.ReceiveBufferSize=1<<20;
        if(OperatingSystem.IsWindows())
            try{udp.Client.IOControl(unchecked((int)0x9800000C),new byte[4],null);}
            catch(SocketException ex){Diagnostics.Log("udp-socket-option",ex.Message);}
    }
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
    public Func<TcpClient,byte,CancellationToken,Task>? AttachOverlay;
    public OverlaySession? Overlay;
    IPEndPoint? overlayRemote;
    public async Task SendOverlayUdp(byte[] data,CancellationToken ct)
    {if(overlayRemote is{} remote)await udp.SendAsync(Wire.Packet(token,65535,data),remote,ct);}
    readonly Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>>? authenticate;
    public Gateway(Settings settings, byte[] token, Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>>? authenticate = null)
    {
        this.settings = settings; this.token = token; this.authenticate = authenticate;
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port)); }
        catch { listener.Stop(); throw; }
        Wire.ConfigureUdp(udp);
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
                if(command is 4 or 5 && port==0 && AttachOverlay!=null)
                {handshake.CancelAfter(Timeout.InfiniteTimeSpan);await AttachOverlay(client,command,stop.Token);return;}
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
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Diagnostics.Log("gateway-tcp",ex.Message); }
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
                if(port==65535&&Overlay!=null)
                {overlayRemote=packet.RemoteEndPoint;await Overlay.ReceiveBaseUdp(packet.Buffer[18..]);continue;}
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
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { Diagnostics.Log("gateway-udp",ex.Message); }
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
        Wire.ConfigureUdp(client);
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
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Diagnostics.Log("target-udp",ex.Message); }
    }
    int disposed;
    public void Dispose()
    { if (Interlocked.Exchange(ref disposed, 1) == 0) { stop.Cancel(); client.Dispose(); /* receive continuation still owns token */ } }
}

sealed class Session : IAsyncDisposable
{
    public string PeerId { get; }
    public string Address { get; }
    readonly int basePort;
    public int LocalPort => !Host&&Overlay!=null?Overlay.Port:basePort;
    public byte[] Capability { get; }
    public int[] TcpPorts { get; }
    public int[] UdpPorts { get; }
    public bool Host { get; }
    public Child Process { get; set; }
    public OverlaySession? Overlay { get; set; }
    public TransitCoordinator? Transit { get; set; }
    public event Action? OverlayReady;
    readonly string[] ardArguments;
    readonly TaskCompletionSource completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion=>completed.Task;
    public bool HasOverlay=>Overlay!=null;
    public CancellationToken Token => stop.Token;
    public bool Live => disposed == 0 && (Overlay!=null?!Overlay.Expired:!Process.Exited.IsCompleted);
    public string Network => Overlay?.Network??Process.Network;
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
        PeerId = peer; Address = address; basePort = port; Capability = capability;
        TcpPorts = tcpPorts; UdpPorts = udpPorts; Process = process; this.directory = directory;
        this.gateway = gateway; Hub=hub; Host = gateway != null;
        ardArguments=process.Process.StartInfo.ArgumentList.ToArray();
        if(gateway!=null)gateway.AttachOverlay=AcceptOverlay;
    }
    public async Task EnableOverlay(CancellationToken ct)
    {Overlay=await OverlaySession.Connect(basePort,Capability,()=>Process.Network,ct);OverlayReady?.Invoke();}
    async Task AcceptOverlay(TcpClient tcp,byte command,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if(command==4)
        {
            if(Overlay!=null)throw new IOException("覆盖层已建立，必须使用恢复入口。");
            Overlay=await OverlaySession.AcceptHello(tcp,basePort,Capability,()=>Process.Network,timeout.Token);gateway!.Overlay=Overlay;OverlayReady?.Invoke();
        }
        else
        {
            var resume=await Wire.ReadJson<JsonElement>(tcp.GetStream(),timeout.Token);
            if(Overlay==null||resume.GetProperty("session").GetString()!=Overlay.Id)throw new IOException("覆盖层恢复编号无效。");
            await tcp.GetStream().WriteAsync(new byte[]{0},timeout.Token);
        }
        var link=new OverlayLink("base",tcp,gateway!.SendOverlayUdp,()=>Process.Network);await Overlay!.Add(link);await link.Reader;
    }
    public async Task RestartArd(CancellationToken ct)
    {
        await Process.DisposeAsync();ct.ThrowIfCancellationRequested();Process=new Child(Ard.Exe,directory,ardArguments);
        Diagnostics.Log("ard-restart",PeerId);
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
                    if(++failures>=2&&Overlay==null){try{await Process.DisposeAsync();}catch(Exception ex){Diagnostics.Log("health-restart",ex.Message);}break;}
                    if(Overlay?.Expired==true){Diagnostics.Log("overlay-timeout","All paths unavailable for 45 seconds.");break;}
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
        completed.TrySetResult();
        Hub?.Detach(this);
        if(Transit!=null)await Transit.DisposeAsync();
        if(Overlay!=null)await Overlay.DisposeAsync();
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
    public DirectoryClient? DirectoryApi { get; set; }
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
    void ConfigureOverlay(Session session)
    {
        if(session.Overlay==null||DirectoryApi==null||session.Transit!=null)return;
        session.Transit=new TransitCoordinator(this,DirectoryApi,session);
        session.Overlay.Changed+=()=>Update(session.PeerId,Describe(session,session.Host));
    }
    async Task Monitor(Session session,bool host)
    {
        long baseLost=0;
        while(!session.Token.IsCancellationRequested)
        {
            var child=session.Process;
            try{await Task.WhenAny(child.Exited,Task.Delay(1000,session.Token));session.Token.ThrowIfCancellationRequested();}catch(OperationCanceledException){break;}
            if(!session.Live)break;
            if(session.Overlay?.Link("base") is{Live:false})
            {if(baseLost==0)baseLost=Environment.TickCount64;}
            else baseLost=0;
            var stalled=baseLost!=0&&Environment.TickCount64-baseLost>=10000;
            if(!child.Exited.IsCompleted&&!stalled)continue;
            if(session.Overlay==null||!session.Live)break;
            try
            {
                if(stalled)Diagnostics.Log("ard-stalled","Base transport remained unavailable for 10 seconds; restarting the same identity.");
                await Task.Delay(1000,session.Token);await session.RestartArd(session.Token);baseLost=0;Observe(session,host);
            }
            catch(OperationCanceledException){break;}
            catch(Exception ex){Diagnostics.Log("ard-restart-failed",ex.Message);break;}
        }
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
            session.OverlayReady+=()=>ConfigureOverlay(session);
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
            await session.EnableOverlay(ct);ConfigureOverlay(session);
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
        var exportDiagnostics=Button("导出诊断包",()=>
        {var path=Diagnostics.Export(Program.DataRoot,engine);Say("诊断包："+path);return Task.CompletedTask;});
        exportDiagnostics.FontSize=9;exportDiagnostics.Padding=new Thickness(5,1);
        diagnostics=Card(new StackPanel{Spacing=3,Children={networkLog,exportDiagnostics}});diagnostics.IsVisible=false;diagnostics.Padding=new Thickness(5,3);
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
            settings.Relay=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY")??settings.Relay;
            settings.RelayKey=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY_KEY")??settings.RelayKey;
            await using var host = await Engine.Create(a, settings);
            await using var caller = await Engine.Create(b, settings);
            await using var hd = new DirectoryClient(host,Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER"));
            await using var cd = new DirectoryClient(caller,Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER"));
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
            var first=caller.Outgoing[host.Id];var child=first.Process;var stablePort=first.Forward(port).Port;await child.DisposeAsync();
            using(var recovery=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                recovery.CancelAfter(TimeSpan.FromSeconds(90));
                while(!caller.Outgoing.TryGetValue(host.Id,out var restored)||ReferenceEquals(child,restored.Process)||!restored.Live||restored.Overlay?.Link("base")?.Live!=true)await Task.Delay(100,recovery.Token);
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
        engine.DirectoryApi=this;
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
    public SignedEnvelope ApproveTransit(string route,string controller,string candidate,string aSession,string bSession)
    {
        lock(sync)
        {
            if(!access.Enabled||!access.Controllers.ContainsKey(controller))throw new IOException("当前已无被控授权。");
            return Sign("/transit-approval/v2",new{route,a=controller,b=engine.Id,c=candidate,aSession,bSession,expires=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+90});
        }
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
                try{await active.Completion.WaitAsync(ct);}catch(OperationCanceledException){break;}
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
        if(udp!=null)Wire.ConfigureUdp(udp);
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
        Wire.ConfigureUdp(upstream);
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

sealed record OverlayHello(string Session, string Key);
sealed record OverlayControl(string Id, string Method, JsonElement Data, string? Error = null);

static class Diagnostics
{
    static readonly ConcurrentQueue<object> events = new();
    public static string Redact(string value)
    {
        value=Regex.Replace(value,@"\b(?:\d{1,3}\.){3}\d{1,3}\b","[IPv4]");
        return Regex.Replace(value,@"(?<![\w])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]+","[IPv6]");
    }
    public static void Log(string kind,string message)
    {
        var clean=Redact(message);events.Enqueue(new{time=DateTimeOffset.UtcNow,kind,message=clean});
        while(events.Count>2048)events.TryDequeue(out _);
        Trace.WriteLine($"[v2 {kind}] {clean}");
    }
    public static string Export(string root,Engine? engine)
    {
        var folder=Path.Combine(root,"diagnostics");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,$"ArdUi-{Program.Version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        void Entry(string name,object value)
        {using var writer=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Optimal).Open());writer.Write(JsonSerializer.Serialize(value,Wire.Json));}
        using var process=Process.GetCurrentProcess();
        Entry("status.json",new{version=Program.Version,ard="2.0.0-pre.6",time=DateTimeOffset.UtcNow,
            endpoint=engine?.Id,os=Environment.OSVersion.VersionString,memoryBytes=process.WorkingSet64,cpuSeconds=process.TotalProcessorTime.TotalSeconds,
            sessions=engine?.Outgoing.Values.Concat(engine.Incoming.Values).Select(s=>new{peer=s.PeerId,host=s.Host,network=s.Network,s.TcpRtt,s.UdpRtt,s.BandwidthMbps,overlay=s.Overlay?.Snapshot()}).ToArray()});
        Entry("events.json",events.ToArray());
        Entry("privacy.json",new{ipAddresses="redacted",excluded=new[]{"private keys","passwords","session keys","capabilities","tickets","payloads","handshakes"}});
        return path;
    }
}

sealed class OverlayCipher : IDisposable
{
    readonly byte[] sendKey,receiveKey,context;
    readonly long[] serial=new long[2];
    readonly HashSet<ulong> datagrams=new();
    readonly object gate=new();
    ulong high;
    public long Replays,Rejected;
    public OverlayCipher(byte[] secret,byte[] context,bool caller)
    {
        this.context=context;
        sendKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,context,Encoding.ASCII.GetBytes(caller?"ArdUi/2 A-B":"ArdUi/2 B-A"));
        receiveKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,context,Encoding.ASCII.GetBytes(caller?"ArdUi/2 B-A":"ArdUi/2 A-B"));
    }
    public byte[] Encrypt(byte[] plain,bool udp)
    {
        var packet=new byte[plain.Length+25];packet[0]=udp?(byte)1:(byte)0;
        var number=Interlocked.Increment(ref serial[udp?1:0]);if(number<=0)throw new CryptographicException("覆盖层 nonce 已耗尽。");
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1),number);
        Span<byte> nonce=stackalloc byte[12];nonce.Clear();nonce[3]=packet[0];packet.AsSpan(1,8).CopyTo(nonce[4..]);
        using var aes=new AesGcm(sendKey,16);aes.Encrypt(nonce,plain,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length,16),context);
        return packet;
    }
    public byte[]? Decrypt(byte[] packet,bool udp)
    {
        if(packet.Length is <38 or >65536||packet[0]!=(udp?1:0)){Interlocked.Increment(ref Rejected);return null;}
        var seq=BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(1));
        lock(gate)
        {
            if(udp&&(seq==0||datagrams.Contains(seq)||(high>8192&&seq<=high-8192))){Replays++;return null;}
            Span<byte> nonce=stackalloc byte[12];nonce.Clear();nonce[3]=packet[0];packet.AsSpan(1,8).CopyTo(nonce[4..]);
            var plain=new byte[packet.Length-25];
            try{using var aes=new AesGcm(receiveKey,16);aes.Decrypt(nonce,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length),plain,context);}
            catch(CryptographicException ex){Rejected++;Diagnostics.Log("authentication",ex.Message);return null;}
            if(udp){high=Math.Max(high,seq);datagrams.Add(seq);if(datagrams.Count>16384)datagrams.RemoveWhere(n=>high>8192&&n<=high-8192);}
            return plain;
        }
    }
    public void Dispose(){CryptographicOperations.ZeroMemory(sendKey);CryptographicOperations.ZeroMemory(receiveKey);}
}

sealed class OverlayLink : IAsyncDisposable
{
    readonly TcpClient tcp;
    readonly SemaphoreSlim write=new(1);
    readonly CancellationTokenSource stop=new();
    readonly Func<byte[],CancellationToken,Task> sendUdp;
    readonly Func<string> network;
    public string Name{get;}
    public CancellationToken Token=>stop.Token;
    public bool Live=>!stop.IsCancellationRequested;
    public string Network=>network();
    public double? RttMs, JitterMs, BandwidthMbps;
    readonly ConcurrentQueue<double?> samples=new();
    public PathQuality Quality=>PathQuality.From(samples.ToArray(),BandwidthMbps);
    public void Sample(double? ms){samples.Enqueue(ms);while(samples.Count>16)samples.TryDequeue(out _);}
    public long Sent,Received,Probes,Lost;
    public Task Reader{get;set;}=Task.CompletedTask;
    public Action? Closed;
    public OverlayLink(string name,TcpClient tcp,Func<byte[],CancellationToken,Task> sendUdp,Func<string> network)
    {Name=name;this.tcp=tcp;tcp.NoDelay=true;this.sendUdp=sendUdp;this.network=network;}
    public async Task Send(byte[] data,bool udp,CancellationToken ct)
    {
        if(!Live)throw new IOException("传输路径已关闭。");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token);linked.CancelAfter(TimeSpan.FromSeconds(5));
        if(udp){await sendUdp(data,linked.Token);Interlocked.Add(ref Sent,data.Length);return;}
        await write.WaitAsync(linked.Token);
        try
        {
            var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,data.Length);
            await tcp.GetStream().WriteAsync(header,linked.Token);await tcp.GetStream().WriteAsync(data,linked.Token);
            Interlocked.Add(ref Sent,data.Length);
        }
        finally{write.Release();}
    }
    public async Task Read(Func<OverlayLink,byte[],bool,Task> receive)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var size=BinaryPrimitives.ReadInt32BigEndian(await Wire.Read(tcp.GetStream(),4,stop.Token));
                if(size is <38 or >65536)throw new InvalidDataException("无效覆盖层帧大小。");
                var data=await Wire.Read(tcp.GetStream(),size,stop.Token);Interlocked.Add(ref Received,size);
                await receive(this,data,false);
            }
        }
        catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {Diagnostics.Log("link-ended",Name+": "+ex.Message);}
        finally{stop.Cancel();tcp.Dispose();Closed?.Invoke();}
    }
    public ValueTask DisposeAsync(){stop.Cancel();tcp.Dispose();return ValueTask.CompletedTask;}
}

sealed class OverlaySession : IAsyncDisposable
{
    const int Chunk=16384,Window=512,Fragment=1100;
    readonly bool caller;
    readonly int basePort,targetPort;
    readonly byte[] capability;
    readonly Func<string> baseNetwork;
    readonly OverlayCipher cipher;
    readonly CancellationTokenSource stop=new();
    readonly ConcurrentDictionary<string,OverlayLink> links=new();
    readonly ConcurrentDictionary<uint,OverlayFlow> flows=new();
    readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> controls=new();
    readonly ConcurrentDictionary<long,TaskCompletionSource<bool>> probes=new();
    readonly SortedDictionary<long,byte[]> pending=new(),reorder=new();
    readonly SemaphoreSlim window=new(Window),incomingSignal=new(0,1),controlSlots=new(8),bandwidthGate=new(1);
    readonly Channel<(OverlayLink Link,byte[] Frame,bool Udp)> replies=Channel.CreateBounded<(OverlayLink,byte[],bool)>(128);
    readonly SemaphoreSlim ackSignal=new(0,1);
    readonly object incomingGate=new();
    readonly object sequenceGate=new(),udpGate=new();
    readonly TcpListener? listener;
    readonly UdpClient? facade;
    readonly Dictionary<IPEndPoint,uint> udpIds=new();
    readonly Dictionary<uint,long> udpTouched=new();
    readonly ConcurrentDictionary<uint,IPEndPoint> udpRemotes=new();
    readonly ConcurrentDictionary<uint,TargetUdp> targetUdp=new();
    readonly Dictionary<(uint,long),Fragments> fragments=new();
    readonly Task maintenance,orderedReceiver,replyWriter,ackWriter;
    Task accept=Task.CompletedTask,datagramReader=Task.CompletedTask,reconnect=Task.CompletedTask;
    long sendSequence,receiveSequence,probeId,udpSequence,missingSince,lastResend;
    int flowSerial,disposed;
    string selected="base";
    public string Id{get;}
    public int Port=>listener==null?0:((IPEndPoint)listener.LocalEndpoint).Port;
    public bool Expired=>stop.IsCancellationRequested||missingSince!=0&&Environment.TickCount64-missingSince>45000;
    public string Selected=>selected;
    public string Network=>selected=="base"?baseNetwork():"ArdTransit / "+selected[..Math.Min(6,selected.Length)];
    public Func<string,JsonElement,CancellationToken,Task<object>>? Control;
    public Action? Changed;
    public long Retransmits,Duplicates,UdpFragments,UdpDrops,Switches;
    public CancellationToken Token=>stop.Token;
    sealed class Fragments(int count,long time)
    {public byte[][] Parts{get;}=new byte[count][];public long Time{get;}=time;public int Bytes;}
    OverlaySession(string id,bool caller,int port,byte[] capability,byte[] secret,byte[] context,Func<string> network)
    {
        Id=id;this.caller=caller;this.capability=capability;this.baseNetwork=network;
        basePort=caller?port:0;targetPort=caller?0:port;cipher=new(secret,context,caller);
        if(caller)
        {
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start();facade=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port));Wire.ConfigureUdp(facade);
            accept=Accept();datagramReader=ReadFacade();
        }
        orderedReceiver=ReceiveOrdered();replyWriter=WriteReplies();ackWriter=WriteAcks();maintenance=Maintain();
    }
    static byte[] Derive(ECDiffieHellman key,string remote)
    {
        var blob=Convert.FromBase64String(remote);using var other=ECDiffieHellman.Create();other.ImportSubjectPublicKeyInfo(blob,out var consumed);
        if(consumed!=blob.Length||other.KeySize!=256||other.ExportParameters(false).Curve.Oid.Value!=ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("覆盖层密钥曲线无效。");
        return key.DeriveRawSecretAgreement(other.PublicKey);
    }
    static byte[] Context(OverlayHello a,OverlayHello b,byte[] cap)=>SHA256.HashData(Encoding.UTF8.GetBytes($"ArdUi/2\n{a.Session}\n{a.Key}\n{b.Key}\n{Convert.ToHexString(cap)}"));
    public static async Task<OverlaySession> Connect(int port,byte[] cap,Func<string> network,CancellationToken ct)
    {
        var tcp=await ConnectBase(port,cap,4,ct);
        try
        {
            using var key=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var hello=new OverlayHello(Guid.NewGuid().ToString("N"),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            await Wire.WriteJson(tcp.GetStream(),hello,ct);var reply=await Wire.ReadJson<OverlayHello>(tcp.GetStream(),ct);
            if(reply.Session!=hello.Session)throw new IOException("覆盖层会话编号不匹配。");
            var secret=Derive(key,reply.Key);OverlaySession session;
            try{session=new(hello.Session,true,port,cap,secret,Context(hello,reply,cap),network);}finally{CryptographicOperations.ZeroMemory(secret);}
            await session.AttachClientBase(tcp);session.reconnect=session.ReconnectBase();
            for(var attempt=0;attempt<3;attempt++)
            {
                try{await session.Probe(session.Link("base")!,true);break;}
                catch(Exception ex){Diagnostics.Log("udp-warmup",ex.Message);ct.ThrowIfCancellationRequested();}
            }
            return session;
        }
        catch{tcp.Dispose();throw;}
    }
    public static async Task<OverlaySession> AcceptHello(TcpClient tcp,int port,byte[] cap,Func<string> network,CancellationToken ct)
    {
        var hello=await Wire.ReadJson<OverlayHello>(tcp.GetStream(),ct);
        if(!Regex.IsMatch(hello.Session,@"\A[0-9a-f]{32}\z"))throw new IOException("覆盖层编号无效。");
        using var key=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var reply=new OverlayHello(hello.Session,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));var secret=Derive(key,hello.Key);
        try
        {
            var session=new OverlaySession(hello.Session,false,port,cap,secret,Context(hello,reply,cap),network);
            try{await Wire.WriteJson(tcp.GetStream(),reply,ct);return session;}catch{await session.DisposeAsync();throw;}
        }
        finally{CryptographicOperations.ZeroMemory(secret);}
    }
    static async Task<TcpClient> ConnectBase(int port,byte[] cap,byte command,CancellationToken ct)
    {
        var tcp=new TcpClient{NoDelay=true};
        try
        {await tcp.ConnectAsync(IPAddress.Loopback,port,ct);var header=new byte[23];"AUI1"u8.CopyTo(header);cap.CopyTo(header,4);header[20]=command;await tcp.GetStream().WriteAsync(header,ct);return tcp;}
        catch{tcp.Dispose();throw;}
    }
    async Task AttachClientBase(TcpClient tcp)
    {
        var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(udp);udp.Connect(IPAddress.Loopback,basePort);
        var link=new OverlayLink("base",tcp,async(data,ct)=>await udp.SendAsync(Wire.Packet(capability,65535,data),ct),baseNetwork);
        link.Closed+=()=>udp.Dispose();await Add(link);
        _=Task.Run(async()=>
        {
            try
            {
                while(!link.Token.IsCancellationRequested)
                {var p=await udp.ReceiveAsync(link.Token);if(Wire.ValidPacket(p.Buffer,capability)&&BinaryPrimitives.ReadUInt16BigEndian(p.Buffer.AsSpan(16))==65535)await Receive(link,p.Buffer[18..],true);}
            }
            catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("base-udp",ex.Message);}
        });
    }
    async Task ReconnectBase()
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000,stop.Token);if(links.TryGetValue("base",out var live)&&live.Live)continue;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var tcp=await ConnectBase(basePort,capability,5,timeout.Token);
                try
                {
                    await Wire.WriteJson(tcp.GetStream(),new{session=Id},timeout.Token);
                    if((await Wire.Read(tcp.GetStream(),1,timeout.Token))[0]!=0)throw new IOException("覆盖层恢复被拒绝。");
                    await AttachClientBase(tcp);
                }
                catch{tcp.Dispose();throw;}
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){Diagnostics.Log("base-reconnect",ex.Message);}
        }
    }
    public async Task Add(OverlayLink link)
    {
        if(links.TryGetValue(link.Name,out var previous))await previous.DisposeAsync();
        links[link.Name]=link;link.Reader=link.Read(Receive);Diagnostics.Log("path-added",link.Name);Changed?.Invoke();
    }
    public OverlayLink? Link(string name)=>links.TryGetValue(name,out var link)?link:null;
    public async Task Remove(string name)
    {if(links.TryRemove(name,out var link))await link.DisposeAsync();if(selected==name)Select("base");}
    public void Select(string name)
    {
        if(selected==name)return;if(!links.TryGetValue(name,out var link)||!link.Live)throw new IOException("目标路径未就绪。");
        selected=name;Interlocked.Increment(ref Switches);lastResend=0;Diagnostics.Log("path-selected",name);Changed?.Invoke();
    }
    static byte[] Frame(byte kind,long seq,uint flow,ReadOnlySpan<byte> body)
    {
        var data=new byte[13+body.Length];data[0]=kind;BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(1),seq);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(9),flow);body.CopyTo(data.AsSpan(13));return data;
    }
    async Task SendOn(OverlayLink link,byte[] plain,bool udp)
    {await link.Send(cipher.Encrypt(plain,udp),udp,stop.Token);}
    void Reply(OverlayLink link,byte[] frame,bool udp)
    {if(!replies.Writer.TryWrite((link,frame,udp)))Diagnostics.Log("probe-reply-dropped","Reply queue is full.");}
    void Ack()
    {lock(incomingGate){if(ackSignal.CurrentCount==0)ackSignal.Release();}}
    async Task WriteReplies()
    {
        try
        {
            await foreach(var reply in replies.Reader.ReadAllAsync(stop.Token))
                try{await SendOn(reply.Link,reply.Frame,reply.Udp);}
                catch(Exception ex){Diagnostics.Log("reply-deferred",ex.Message);}
        }
        catch(OperationCanceledException ex){Diagnostics.Log("reply-writer-stopped",ex.Message);}
    }
    async Task WriteAcks()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await ackSignal.WaitAsync(stop.Token);long acknowledged;
                lock(incomingGate)acknowledged=receiveSequence;
                if(Active() is{} link)
                    try{await SendOn(link,Frame(9,acknowledged,0,[]),false);}
                    catch(Exception ex){Diagnostics.Log("ack-deferred",ex.Message);}
            }
        }
        catch(OperationCanceledException ex){Diagnostics.Log("ack-writer-stopped",ex.Message);}
    }
    OverlayLink? Active()
    {
        if(links.TryGetValue(selected,out var active)&&active.Live)return active;
        if(links.TryGetValue("base",out var fallback)&&fallback.Live){Select("base");return fallback;}
        return null;
    }
    async Task SendReliable(byte kind,uint flow,byte[] body)
    {
        await window.WaitAsync(stop.Token);byte[] frame;
        lock(sequenceGate){frame=Frame(kind,++sendSequence,flow,body);pending.Add(sendSequence,frame);}
        if(Active() is{} link)
            try{await SendOn(link,frame,false);}catch(Exception ex)when(!stop.IsCancellationRequested){Diagnostics.Log("send-deferred",ex.Message);}
    }
    async Task Receive(OverlayLink link,byte[] packet,bool udp)
    {
        var frame=cipher.Decrypt(packet,udp);if(frame==null)return;
        var kind=frame[0];var seq=BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(1));var flow=BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9));
        if(udp)
        {
            if(kind==10){Reply(link,Frame(11,seq,flow,frame.AsSpan(13)),true);return;}
            if(kind==11){if(probes.TryRemove(seq,out var probe))probe.TrySetResult(true);return;}
            if(kind==12)await Datagram(flow,seq,frame[13..]);return;
        }
        if(kind==9)
        {
            lock(sequenceGate)
            {
                if(seq<0||seq>sendSequence)throw new IOException("覆盖层确认越界。");
                foreach(var id in pending.Keys.TakeWhile(n=>n<=seq).ToArray()){pending.Remove(id);window.Release();}
            }
            return;
        }
        if(kind==10){Reply(link,Frame(11,seq,flow,frame.AsSpan(13)),false);return;}
        if(kind==11){if(probes.TryRemove(seq,out var probe))probe.TrySetResult(true);return;}
        if(kind is <1 or >6||seq<=0)throw new IOException("覆盖层指令无效。");
        lock(incomingGate)
        {
            if(seq<=receiveSequence){Duplicates++;}
            else
            {
                if(seq>receiveSequence+Window)throw new IOException("覆盖层接收窗口超限。");
                reorder.TryAdd(seq,frame);
                if(incomingSignal.CurrentCount==0)incomingSignal.Release();
            }
        }
        Ack();
    }
    async Task ReceiveOrdered()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await incomingSignal.WaitAsync(stop.Token);
                while(true)
                {
                    byte[]? next;lock(incomingGate)reorder.TryGetValue(receiveSequence+1,out next);
                    if(next==null)break;
                    await Dispatch(next);
                    lock(incomingGate)reorder.Remove(++receiveSequence);
                    Ack();
                }
            }
        }
        catch(Exception ex){Diagnostics.Log("ordered-receiver",ex.Message);if(!stop.IsCancellationRequested)stop.Cancel();}
    }
    async Task Dispatch(byte[] frame)
    {
        var kind=frame[0];var id=BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9));var body=frame[13..];
        if(kind==5||kind==6)
        {
            var message=JsonSerializer.Deserialize<OverlayControl>(body,Wire.Json)??throw new IOException("空控制消息。");
            if(kind==6)
            {if(controls.TryRemove(message.Id,out var waiter)){if(message.Error!=null)waiter.TrySetException(new IOException(message.Error));else waiter.TrySetResult(message.Data);}return;}
            if(!controlSlots.Wait(0))throw new IOException("对端控制请求过多。");
            _=HandleControl(message);return;
        }
        if(kind==1)
        {
            if(caller||id==0||flows.Count>=128||flows.ContainsKey(id)){_=ResetFlow(id);return;}
            var flow=new OverlayFlow(this,id,null);if(flows.TryAdd(id,flow))flow.Start();return;
        }
        if(!flows.TryGetValue(id,out var existing))return;
        if(kind==4){existing.Close();return;}
        try{await existing.Enqueue(kind==3?[]:body);}
        catch(Exception ex){Diagnostics.Log("flow-queue",ex.Message);existing.Close();_=ResetFlow(id);}
    }
    async Task ResetFlow(uint id)
    {try{await SendReliable(4,id,[]);}catch(Exception ex){Diagnostics.Log("flow-reset",ex.Message);}}
    async Task HandleControl(OverlayControl message)
    {
        try
        {
            object result;
            if(message.Method=="select")
            {
                var name=message.Data.GetProperty("path").GetString()!;
                if(name!="base"&&(!(Link(name)?.Network.StartsWith("P2P",StringComparison.Ordinal)??false)))throw new IOException("本端候选未直连。");
                Select(name);result=new{ok=true};
            }
            else result=Control!=null?await Control(message.Method,message.Data,stop.Token):throw new IOException("中继协调尚未就绪。");
            await SendReliable(6,0,JsonSerializer.SerializeToUtf8Bytes(message with{Data=JsonSerializer.SerializeToElement(result,Wire.Json)},Wire.Json));
        }
        catch(Exception ex)
        {
            Diagnostics.Log("control",message.Method+": "+ex.Message);
            if(!stop.IsCancellationRequested)
                try{await SendReliable(6,0,JsonSerializer.SerializeToUtf8Bytes(message with{Data=JsonSerializer.SerializeToElement(new{}),Error=ex.Message},Wire.Json));}
                catch(Exception sendError){Diagnostics.Log("control-reply",sendError.Message);}
        }
        finally{controlSlots.Release();}
    }
    public async Task<JsonElement> Request(string method,object value,CancellationToken ct)
    {
        if(controls.Count>=16)throw new IOException("协调请求过多。");
        var id=Guid.NewGuid().ToString("N");var waiter=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);controls[id]=waiter;
        try
        {
            await SendReliable(5,0,JsonSerializer.SerializeToUtf8Bytes(new OverlayControl(id,method,JsonSerializer.SerializeToElement(value,Wire.Json)),Wire.Json));
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(75),ct);
        }
        finally{controls.TryRemove(id,out _);}
    }
    public async Task<double> Probe(OverlayLink link,bool udp,int bytes=32)
    {
        var id=Interlocked.Increment(ref probeId);var waiter=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);probes[id]=waiter;
        var watch=Stopwatch.StartNew();Interlocked.Increment(ref link.Probes);
        try
        {
            await SendOn(link,Frame(10,id,0,new byte[bytes]),udp);
            await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2),stop.Token);
            var rtt=watch.Elapsed.TotalMilliseconds;
            if(udp){link.JitterMs=link.RttMs is{} old?Math.Abs(old-rtt):0;link.RttMs=rtt;link.Sample(rtt);}
            else if(bytes>1024)link.BandwidthMbps=bytes*8d/Math.Max(.001,watch.Elapsed.TotalSeconds)/1e6;
            return rtt;
        }
        catch{Interlocked.Increment(ref link.Lost);if(udp)link.Sample(null);throw;}
        finally{probes.TryRemove(id,out _);}
    }
    async Task Maintain()
    {
        var round=0;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var active=Active();
                ExpireUdp(Environment.TickCount64);
                if(active==null){if(missingSince==0)missingSince=Environment.TickCount64;}
                else{missingSince=0;if(active.Name!=selected)Select(active.Name);}
                if(Environment.TickCount64-lastResend>=1000&&active!=null)
                {
                    lastResend=Environment.TickCount64;byte[][] frames;lock(sequenceGate)frames=pending.Values.Take(64).ToArray();
                    foreach(var frame in frames){await SendOn(active,frame,false);Retransmits++;}
                }
                foreach(var link in links.Values.Where(l=>l.Live).ToArray())
                {
                    try
                    {
                        await Probe(link,true);
                        if(round%15==0&&bandwidthGate.Wait(0))
                            try{await Probe(link,false,32768);}finally{bandwidthGate.Release();}
                    }
                    catch(Exception ex)when(!stop.IsCancellationRequested)
                    {
                        Diagnostics.Log("probe",link.Name+": "+ex.Message);
                        try{await Probe(link,false);}catch(Exception tcpError){Diagnostics.Log("path-unhealthy",tcpError.Message);await link.DisposeAsync();}
                    }
                }
                round++;await Task.Delay(500,stop.Token);
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){Diagnostics.Log("overlay-maintenance",ex.Message);await Task.Delay(300,stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);}
        }
    }
    public async Task<PathQuality> Evaluate(OverlayLink link,CancellationToken ct)
    {
        for(var warmup=0;warmup<3;warmup++)
        {try{await Probe(link,true);break;}catch(Exception ex){Diagnostics.Log("candidate-warmup",ex.Message);ct.ThrowIfCancellationRequested();}}
        var samples=new List<double?>();
        for(var i=0;i<8;i++)
        {
            ct.ThrowIfCancellationRequested();
            try{samples.Add(await Probe(link,true));}catch(Exception ex){Diagnostics.Log("quality-probe",ex.Message);samples.Add(null);}
            await Task.Delay(100,ct);
        }
        double? bandwidth=null;
        await bandwidthGate.WaitAsync(ct);
        try
        {
            var watch=Stopwatch.StartNew();await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Probe(link,false,32768)));
            bandwidth=8*32768*8d/Math.Max(.001,watch.Elapsed.TotalSeconds)/1e6;link.BandwidthMbps=bandwidth;
        }
        catch(Exception ex){Diagnostics.Log("quality-bandwidth",ex.Message);ct.ThrowIfCancellationRequested();}
        finally{bandwidthGate.Release();}
        return PathQuality.From(samples.ToArray(),bandwidth);
    }
    async Task Accept()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var tcp=await listener!.AcceptTcpClientAsync(stop.Token);
                if(flows.Count>=128){tcp.Dispose();continue;}
                var id=checked((uint)Interlocked.Increment(ref flowSerial));var flow=new OverlayFlow(this,id,tcp);
                if(!flows.TryAdd(id,flow)){tcp.Dispose();continue;}await SendReliable(1,id,[]);flow.Start();
            }
        }
        catch(Exception ex)when(stop.IsCancellationRequested){Diagnostics.Log("listener-stopped",ex.Message);}
    }
    internal void ExpireUdp(long now)
    {
        lock(udpGate)
            foreach(var flow in udpTouched.Where(p=>now-p.Value>120000).Select(p=>p.Key).ToArray())
            {
                udpTouched.Remove(flow);
                if(udpRemotes.TryRemove(flow,out var remote))udpIds.Remove(remote);
            }
    }
    internal int UdpFlowCount=>udpRemotes.Count;
    async Task ReadFacade()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await facade!.ReceiveAsync(stop.Token);if(packet.Buffer.Length>1500)continue;
                uint flow;lock(udpGate)
                {
                    if(!udpIds.TryGetValue(packet.RemoteEndPoint,out flow))
                    {if(udpIds.Count>=256){UdpDrops++;continue;}flow=checked((uint)Interlocked.Increment(ref flowSerial));udpIds[packet.RemoteEndPoint]=flow;udpRemotes[flow]=packet.RemoteEndPoint;}
                    udpTouched[flow]=Environment.TickCount64;
                }
                await SendDatagram(flow,packet.Buffer);
            }
        }
        catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("facade-udp",ex.Message);}
    }
    async Task SendDatagram(uint flow,byte[] data)
    {
        var link=Active();if(link==null){UdpDrops++;return;}
        var id=Interlocked.Increment(ref udpSequence);var count=(data.Length+Fragment-1)/Fragment;
        for(var i=0;i<count;i++)
        {
            var payload=new byte[2+Math.Min(Fragment,data.Length-i*Fragment)];payload[0]=(byte)i;payload[1]=(byte)count;data.AsSpan(i*Fragment,payload.Length-2).CopyTo(payload.AsSpan(2));
            try{await SendOn(link,Frame(12,id,flow,payload),true);UdpFragments++;}
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){UdpDrops++;Diagnostics.Log("udp-send",ex.Message);}
        }
    }
    async Task Datagram(uint flow,long id,byte[] payload)
    {
        if(payload.Length<3||payload[1] is <1 or >2||payload[0]>=payload[1]||payload.Length>Fragment+2){UdpDrops++;return;}
        byte[]? data=null;
        lock(udpGate)
        {
            foreach(var old in fragments.Where(p=>Environment.TickCount64-p.Value.Time>2000).Select(p=>p.Key).ToArray()){fragments.Remove(old);UdpDrops++;}
            var key=(flow,id);
            if(!fragments.TryGetValue(key,out var buffer))
            {if(fragments.Count>=256){UdpDrops++;return;}fragments[key]=buffer=new(payload[1],Environment.TickCount64);}
            if(buffer.Parts.Length!=payload[1]){UdpDrops++;return;}
            if(buffer.Parts[payload[0]]==null){buffer.Parts[payload[0]]=payload[2..];buffer.Bytes+=payload.Length-2;}
            if(buffer.Bytes>1500){fragments.Remove(key);UdpDrops++;return;}
            if(buffer.Parts.All(p=>p!=null)){data=buffer.Parts.SelectMany(p=>p).ToArray();fragments.Remove(key);}
        }
        if(data==null)return;
        if(caller)
        {
            IPEndPoint? remote;
            lock(udpGate){if(udpRemotes.TryGetValue(flow,out remote))udpTouched[flow]=Environment.TickCount64;}
            if(remote!=null)await facade!.SendAsync(data,remote,stop.Token);return;
        }
        if(!targetUdp.TryGetValue(flow,out var target))
        {
            if(targetUdp.Count>=256){UdpDrops++;return;}
            target=new TargetUdp(targetPort,reply=>SendDatagram(flow,reply),stop.Token,1500);targetUdp[flow]=target;
            var captured=target;_=target.Completion.ContinueWith(t=>{targetUdp.TryRemove(new KeyValuePair<uint,TargetUdp>(flow,captured));captured.Dispose();},TaskScheduler.Default);
        }
        await target.Send(data);
    }
    public async Task ReceiveBaseUdp(byte[] data)
    {if(Link("base") is{} link)await Receive(link,data,true);}
    public async Task ReceiveUdp(OverlayLink link,byte[] data)=>await Receive(link,data,true);
    public object Snapshot()
    {
        int buffered;lock(sequenceGate)buffered=pending.Values.Sum(p=>p.Length);
        return new{selected,flows=flows.Count,udpFlows=caller?udpRemotes.Count:targetUdp.Count,bufferedBytes=buffered,Retransmits,Duplicates,UdpFragments,UdpDrops,Switches,
            replayRejected=cipher.Replays,authenticationRejected=cipher.Rejected,
            paths=links.Values.Select(l=>new{l.Name,l.Live,l.Network,l.RttMs,l.JitterMs,l.BandwidthMbps,l.Probes,l.Lost,l.Sent,l.Received,quality=l.Quality}).ToArray()};
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();listener?.Stop();facade?.Dispose();
        foreach(var pendingControl in controls.Values)pendingControl.TrySetCanceled();
        foreach(var link in links.Values)await link.DisposeAsync();foreach(var flow in flows.Values)flow.Close();foreach(var udp in targetUdp.Values)udp.Dispose();
        try{await Task.WhenAll(links.Values.Select(l=>l.Reader).Concat([maintenance,orderedReceiver,replyWriter,ackWriter,accept,datagramReader,reconnect]));}catch(Exception ex){Diagnostics.Log("overlay-close",ex.Message);}
        cipher.Dispose();
    }
    sealed class OverlayFlow(OverlaySession owner,uint id,TcpClient? accepted)
    {
        readonly Channel<byte[]> received=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        readonly CancellationTokenSource stop=CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        TcpClient? socket=accepted;
        public ValueTask Enqueue(byte[] data)=>received.Writer.WriteAsync(data,stop.Token);
        public void Start()=>_=Run();
        public void Close(){stop.Cancel();socket?.Dispose();received.Writer.TryComplete();}
        async Task Run()
        {
            try
            {
                if(socket==null){socket=new TcpClient{NoDelay=true};await socket.ConnectAsync(IPAddress.Loopback,owner.targetPort,stop.Token);}
                socket.NoDelay=true;var stream=socket.GetStream();
                async Task Read()
                {
                    var buffer=new byte[Chunk];int size;
                    while((size=await stream.ReadAsync(buffer,stop.Token))>0)await owner.SendReliable(2,id,buffer[..size]);
                    await owner.SendReliable(3,id,[]);
                }
                async Task Write()
                {
                    await foreach(var data in received.Reader.ReadAllAsync(stop.Token))
                    {if(data.Length==0){socket.Client.Shutdown(SocketShutdown.Send);break;}await stream.WriteAsync(data,stop.Token);}
                }
                var read=Read();var write=Write();var first=await Task.WhenAny(read,write);if(first.IsFaulted||first.IsCanceled)Close();await Task.WhenAll(read,write);
            }
            catch(Exception ex)
            {
                Diagnostics.Log("tcp-flow",ex.Message);
                if(!owner.stop.IsCancellationRequested)try{await owner.SendReliable(4,id,[]);}catch(Exception sendError){Diagnostics.Log("tcp-reset",sendError.Message);}
            }
            finally{Close();owner.flows.TryRemove(new KeyValuePair<uint,OverlayFlow>(id,this));}
        }
    }
}

sealed record PathQuality(int Samples,int Received,double MedianMs,double P90Ms,double JitterMs,double Loss,double? Mbps)
{
    public double Score=>P90Ms+2*JitterMs+1000*Loss;
    public bool Eligible=>Samples>=8&&Received>=7&&Mbps is >0;
    public static PathQuality From(double?[] samples,double? mbps)
    {
        var values=samples.Where(x=>x.HasValue).Select(x=>x!.Value).Order().ToArray();
        if(values.Length==0)return new(samples.Length,0,1e6,1e6,1e6,1,mbps);
        var median=values[values.Length/2];var p90=values[(int)Math.Ceiling(values.Length*.9)-1];
        return new(samples.Length,values.Length,median,p90,p90-median,1-values.Length/(double)samples.Length,mbps);
    }
    public bool BetterThan(PathQuality baseline)
    {
        if(!Eligible||Loss>Math.Max(.125,baseline.Loss)||baseline.Mbps is{} old&&Mbps<old*.7)return false;
        if(baseline.Samples<8||baseline.Received<4)return true;
        return baseline.Score-Score>=Math.Max(5,baseline.Score*.15)||
            (baseline.Mbps is >0&&Mbps>=baseline.Mbps*1.5&&P90Ms<=baseline.P90Ms+5&&Loss<=baseline.Loss);
    }
}
sealed record TransitCandidate(string Endpoint,int Capacity,int Active,int Mbps,long Seen);
sealed record TransitCandidates(TransitCandidate[] Candidates);
sealed record TransitTicket(string Route,string A,string B,string C,string ASession,string BSession,long Expires,int Mbps);
sealed record TransitOffer(string Route,string ARelaySession,string BRelaySession,string AToken,string BToken);
sealed record TransitReply(string Status,SignedEnvelope Ticket,SignedEnvelope? Offer);
sealed record TransitActivation(SignedEnvelope Ticket,SignedEnvelope Offer);

sealed class TransitCoordinator : IAsyncDisposable
{
    readonly Engine engine;
    readonly DirectoryClient directory;
    readonly Session session;
    readonly OverlaySession overlay;
    readonly ConcurrentDictionary<string,Leg> legs=new();
    readonly CancellationTokenSource stop;
    readonly SemaphoreSlim selecting=new(1),keyGate=new(1);
    readonly Task loop;
    string? directoryKey;
    long testSelectionUntil;
    long nextSelection;
    Task selectionTask=Task.CompletedTask;
    sealed class Leg(string route,string candidate,string folder,string endpoint)
    {
        public string Route{get;}=route;
        public string Candidate{get;}=candidate;
        public string Folder{get;}=folder;
        public string Endpoint{get;}=endpoint;
        public long Created{get;}=Environment.TickCount64;
        public Child? Child;
        public OverlayLink? Link;
        public UdpClient? Udp;
        public Task Reader=Task.CompletedTask;
        public bool Activated,PeerDirect;
        public PathQuality? Quality;
        public int Degraded;
    }
    public TransitCoordinator(Engine engine,DirectoryClient directory,Session session)
    {
        this.engine=engine;this.directory=directory;this.session=session;overlay=session.Overlay!;
        stop=CancellationTokenSource.CreateLinkedTokenSource(session.Token);overlay.Control=Handle;
        loop=Run();
    }
    async Task<string> ServerKey(CancellationToken ct)
    {
        await keyGate.WaitAsync(ct);
        try
        {
            if(directoryKey!=null)return directoryKey;
            var response=await directory.Call<JsonElement>("/api/v2/transit/key",new{},ct);
            var key=Wire.Endpoint(response.GetProperty("endpoint").GetString()!);
            var path=Path.Combine(engine.State.Root,"transit-directory-key");
            if(File.Exists(path)&&File.ReadAllText(path).Trim()!=key)throw new CryptographicException("NJ 中继票据公钥发生改变，已阻止候选中继。");
            File.WriteAllText(path,key);directoryKey=key;return key;
        }
        finally{keyGate.Release();}
    }
    async Task<Leg> Prepare(string route,string candidate,CancellationToken ct)
    {
        if(!Regex.IsMatch(route,@"\A[0-9a-f]{32}\z")||Wire.Endpoint(candidate)==engine.Id||candidate==session.PeerId||legs.Count>=3)
            throw new IOException("候选中继参数或数量无效。");
        if(legs.TryGetValue(route,out var existing))return existing;
        var folder=engine.State.NewSessionDirectory();var endpoint=await Ard.Identity(folder,ct);
        var leg=new Leg(route,candidate,folder,endpoint);
        if(!legs.TryAdd(route,leg)){Directory.Delete(folder,true);return legs[route];}
        return leg;
    }
    async Task<object> Handle(string method,JsonElement data,CancellationToken ct)
    {
        if(method=="transit-prepare")
        {
            if(!session.Host)throw new IOException("只有主控可以发起中继探测。");
            var leg=await Prepare(data.GetProperty("route").GetString()!,data.GetProperty("candidate").GetString()!,ct);
            var aSession=Wire.Endpoint(data.GetProperty("aSession").GetString()!);
            return new{endpoint=leg.Endpoint,approval=directory.ApproveTransit(leg.Route,session.PeerId,leg.Candidate,aSession,leg.Endpoint)};
        }
        if(method=="transit-activate")
        {
            if(!session.Host)throw new IOException("中继激活方向无效。");
            var activation=data.Deserialize<TransitActivation>(Wire.Json)!;
            var ticket=DirectoryClient.Verify<TransitTicket>(activation.Ticket,"/transit-ticket/v2",await ServerKey(ct));
            if(!legs.TryGetValue(ticket.Route,out var leg))throw new IOException("未知候选中继会话。");
            await Activate(leg,activation,ct);return new{ok=true};
        }
        var route=data.GetProperty("route").GetString()!;
        if(method=="transit-close"){await Close(route,false);return new{ok=true};}
        if(method=="transit-status")
        {
            if(!legs.TryGetValue(route,out var leg))return new{direct=false,network="closed",live=false};
            leg.PeerDirect=data.TryGetProperty("direct",out var direct)&&direct.GetBoolean();
            return new{direct=IsDirect(leg),network=leg.Child?.Network,live=leg.Link?.Live??false,rttMs=leg.Child?.PathRtt};
        }
        throw new IOException("未知中继协调指令。");
    }
    static bool IsDirect(Leg leg)=>leg.Child!=null&&!leg.Child.Exited.IsCompleted&&leg.Child.Network.StartsWith("P2P",StringComparison.Ordinal)&&leg.Link?.Live==true;
    async Task Activate(Leg leg,TransitActivation activation,CancellationToken ct)
    {
        if(leg.Activated)throw new IOException("中继入口已激活。");
        var ticket=DirectoryClient.Verify<TransitTicket>(activation.Ticket,"/transit-ticket/v2",await ServerKey(ct));
        if(ticket.Route!=leg.Route||ticket.C!=leg.Candidate||ticket.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()||
            ticket.A!=(session.Host?session.PeerId:engine.Id)||ticket.B!=(session.Host?engine.Id:session.PeerId)||
            (session.Host?ticket.BSession:ticket.ASession)!=leg.Endpoint)throw new CryptographicException("中继票据没有绑定当前双方和临时身份。");
        var offer=DirectoryClient.Verify<TransitOffer>(activation.Offer,$"/api/v2/transit/routes/{leg.Route}/ready",leg.Candidate);
        if(offer.Route!=leg.Route)throw new CryptographicException("C 的签名绑定了错误会话。");
        var remote=Wire.Endpoint(session.Host?offer.BRelaySession:offer.ARelaySession);
        var token=Convert.FromHexString(session.Host?offer.BToken:offer.AToken);if(token.Length!=16)throw new IOException("中继能力令牌无效。");
        var port=Wire.Port();leg.Child=Ard.Start(leg.Folder,false,port,remote,engine.Settings);
        leg.Child.Output+=line=>Diagnostics.Log("transit-ard",line);
        await leg.Child.WaitFor("READY:",ct);
        var tcp=new TcpClient{NoDelay=true};
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback,port,ct);
            await Wire.WriteJson(tcp.GetStream(),new{route=leg.Route,token=Convert.ToHexString(token).ToLowerInvariant()},ct);
            if((await Wire.Read(tcp.GetStream(),1,ct))[0]!=0)throw new IOException("C 拒绝了转发入口。");
            var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(udp);leg.Udp=udp;udp.Connect(IPAddress.Loopback,port);
            var link=new OverlayLink(leg.Route,tcp,async(payload,cancel)=>
            {var packet=new byte[16+payload.Length];token.CopyTo(packet,0);payload.CopyTo(packet,16);await udp.SendAsync(packet,cancel);},()=>leg.Child.Network);
            leg.Link=link;await overlay.Add(link);leg.Activated=true;
            leg.Reader=ReadDatagrams(leg,token);Diagnostics.Log("transit-active",leg.Route);
        }
        catch{tcp.Dispose();throw;}
    }
    async Task ReadDatagrams(Leg leg,byte[] token)
    {
        try
        {
            while(!stop.IsCancellationRequested&&leg.Link!.Live)
            {
                var packet=await leg.Udp!.ReceiveAsync(stop.Token);
                if(packet.Buffer.Length>=54&&CryptographicOperations.FixedTimeEquals(packet.Buffer.AsSpan(0,16),token))
                    await overlay.ReceiveUdp(leg.Link,packet.Buffer[16..]);
            }
        }
        catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("transit-udp",ex.Message);}
    }
    async Task<Leg?> EvaluateCandidate(TransitCandidate candidate,CancellationToken ct)
    {
        var route=Guid.NewGuid().ToString("N");var keep=false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(85));
        try
        {
            var leg=await Prepare(route,candidate.Endpoint,timeout.Token);
            var b=await overlay.Request("transit-prepare",new{route,candidate=candidate.Endpoint,aSession=leg.Endpoint},timeout.Token);
            Peer peer;lock(engine.State.Peers)peer=engine.State.Peers.Single(p=>p.Id==session.PeerId);
            await directory.Call<JsonElement>("/api/v2/transit/request",new{route,target=session.PeerId,candidate=candidate.Endpoint,aSession=leg.Endpoint,bSession=b.GetProperty("endpoint").GetString(),grant=peer.Grant,approval=b.GetProperty("approval")},timeout.Token);
            TransitReply? reply=null;
            while(reply?.Status!="ready")
            {
                await Task.Delay(500,timeout.Token);
                reply=await directory.Call<TransitReply>("/api/v2/transit/routes/"+route,new{},timeout.Token);
                if(reply.Status=="closed")throw new IOException("候选中继已关闭。");
            }
            var activation=new TransitActivation(reply.Ticket,reply.Offer??throw new IOException("缺少 C 签名。"));
            await Task.WhenAll(Activate(leg,activation,timeout.Token),overlay.Request("transit-activate",activation,timeout.Token));
            while(true)
            {
                var bStatus=await overlay.Request("transit-status",new{route,direct=IsDirect(leg)},timeout.Token);
                leg.PeerDirect=bStatus.GetProperty("direct").GetBoolean();
                if(IsDirect(leg)&&leg.PeerDirect)break;
                await Task.Delay(1000,timeout.Token);
            }
            leg.Quality=await overlay.Evaluate(leg.Link!,timeout.Token);
            Diagnostics.Log("candidate-quality",route+" "+JsonSerializer.Serialize(leg.Quality,Wire.Json));
            if(!leg.Quality.Eligible||!IsDirect(leg)||!leg.PeerDirect)return null;
            keep=true;return leg;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){Diagnostics.Log("candidate-failed",candidate.Endpoint+": "+ex.Message);return null;}
        finally{if(!keep)await Close(route,true);}
    }
    async Task Choose(Leg leg,CancellationToken ct)
    {
        var status=await overlay.Request("transit-status",new{route=leg.Route,direct=IsDirect(leg)},ct);
        leg.PeerDirect=status.GetProperty("direct").GetBoolean();
        if(!IsDirect(leg)||!leg.PeerDirect)throw new IOException("切换前复核双段 Direct 失败。");
        await overlay.Request("select",new{path=leg.Route},ct);overlay.Select(leg.Route);
        nextSelection=Environment.TickCount64+60000;
        Diagnostics.Log("candidate-chosen",leg.Route+" "+JsonSerializer.Serialize(leg.Quality,Wire.Json));
    }
    public async Task<bool> ProbeCandidate(TransitCandidate candidate,bool testSelect,CancellationToken ct)
    {
        await selecting.WaitAsync(ct);Leg? leg=null;var chosen=false;
        try
        {
            leg=await EvaluateCandidate(candidate,ct);if(leg==null)return false;
            var baseline=await overlay.Evaluate(overlay.Link(overlay.Selected)!,ct);
            if(!testSelect&&(session.Process.Network.StartsWith("P2P",StringComparison.Ordinal)||!leg.Quality!.BetterThan(baseline)))return false;
            if(testSelect)testSelectionUntil=Environment.TickCount64+15000;
            await Choose(leg,ct);chosen=true;return true;
        }
        finally
        {
            if(leg!=null&&!chosen)await Close(leg.Route,true);selecting.Release();
        }
    }
    async Task SelectBest()
    {
        await selecting.WaitAsync(stop.Token);
        var evaluated=new List<Leg>();
        try
        {
            var list=await directory.Call<TransitCandidates>("/api/v2/transit/candidates",new{target=session.PeerId},stop.Token);
            var current=overlay.Selected;
            var candidates=list.Candidates.Where(c=>legs.Values.All(l=>l.Candidate!=c.Endpoint)).Take(Math.Max(0,3-legs.Count)).ToArray();
            if(candidates.Length==0)return;
            var baselineTask=overlay.Evaluate(overlay.Link(current)??overlay.Link("base")!,stop.Token);
            var results=await Task.WhenAll(candidates.Select(c=>EvaluateCandidate(c,stop.Token)));
            evaluated.AddRange(results.OfType<Leg>());var baseline=await baselineTask;
            Diagnostics.Log("baseline-quality",JsonSerializer.Serialize(baseline,Wire.Json));
            if(session.Process.Network.StartsWith("P2P",StringComparison.Ordinal))return;
            var best=evaluated.Where(l=>l.Quality!.BetterThan(baseline)).OrderBy(l=>l.Quality!.Score).ThenByDescending(l=>l.Quality!.Mbps).FirstOrDefault();
            if(best==null)return;
            await Choose(best,stop.Token);
            if(current!="base"&&current!=best.Route)await Close(current,true);
        }
        catch(Exception ex){Diagnostics.Log("candidate-selection",ex.Message);}
        finally
        {
            foreach(var leg in evaluated.Where(l=>l.Route!=overlay.Selected))await Close(leg.Route,true);
            nextSelection=Environment.TickCount64+60000;selecting.Release();
        }
    }
    async Task Run()
    {
        var round=0;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000,stop.Token);
                foreach(var leg in legs.Values.ToArray())
                {
                    if(!leg.Activated)
                    {if(Environment.TickCount64-leg.Created>100000)await Close(leg.Route,false);continue;}
                    if(leg.Child!.Exited.IsCompleted||!leg.Link!.Live)
                    {await Close(leg.Route,!session.Host);continue;}
                    if(overlay.Selected==leg.Route&&(!IsDirect(leg)||!leg.PeerDirect))
                    {await Fallback("候选路径已不满足双段 Direct");await Close(leg.Route,!session.Host);continue;}
                    if(round%5==0)
                        await directory.Call<JsonElement>($"/api/v2/transit/routes/{leg.Route}/renew",new{},stop.Token);
                    if(!session.Host)
                    {
                        var status=await overlay.Request("transit-status",new{route=leg.Route,direct=IsDirect(leg)},stop.Token);
                        leg.PeerDirect=status.GetProperty("direct").GetBoolean();
                        if(overlay.Selected==leg.Route&&overlay.Link("base") is{} fallback)
                        {
                            var quality=leg.Link.Quality;var baseline=fallback.Quality;
                            var worse=quality.Samples>=8&&baseline.Samples>=8&&
                                (quality.Loss>.25&&quality.Loss>baseline.Loss||quality.Score>baseline.Score*1.3+5);
                            leg.Degraded=worse?leg.Degraded+1:0;
                            if(leg.Degraded>=3){await Fallback("候选连续三轮退化");await Close(leg.Route,true);}
                        }
                    }
                }
                if(!session.Host&&Environment.TickCount64>=testSelectionUntil&&session.Process.Network.StartsWith("P2P",StringComparison.Ordinal)&&overlay.Selected!="base")
                {
                    await Fallback("A–B 已达成 Direct");foreach(var route in legs.Keys)await Close(route,true);
                }
                if(!session.Host&&selectionTask.IsCompleted&&selecting.CurrentCount!=0&&Environment.TickCount64>=nextSelection&&
                    !session.Process.Network.StartsWith("P2P",StringComparison.Ordinal))
                {
                    nextSelection=Environment.TickCount64+60000;selectionTask=SelectBest();
                }
                round++;
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){Diagnostics.Log("transit-coordinator",ex.Message);}
        }
    }
    async Task Fallback(string reason)
    {
        Diagnostics.Log("fallback",reason);
        nextSelection=Environment.TickCount64+5000;
        if(overlay.Link("base")?.Live==true)overlay.Select("base");
        try{await overlay.Request("select",new{path="base"},stop.Token);}
        catch(Exception ex){Diagnostics.Log("fallback-notify",ex.Message);}
    }
    async Task Close(string route,bool notify)
    {
        if(!legs.TryRemove(route,out var leg))return;
        if(overlay.Selected==route&&overlay.Link("base")?.Live==true)overlay.Select("base");
        await overlay.Remove(route);leg.Udp?.Dispose();
        if(leg.Child!=null)await leg.Child.DisposeAsync();
        try{await leg.Reader;}catch(Exception ex){Diagnostics.Log("transit-reader-close",ex.Message);}
        try{Directory.Delete(leg.Folder,true);}catch(IOException ex){Diagnostics.Log("transit-folder-close",ex.Message);}
        if(notify&&!stop.IsCancellationRequested)
        {
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await directory.Call<JsonElement>($"/api/v2/transit/routes/{route}/close",new{},timeout.Token);
                await overlay.Request("transit-close",new{route},timeout.Token);
            }
            catch(Exception ex){Diagnostics.Log("transit-close-notify",ex.Message);}
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();try{await Task.WhenAll(loop,selectionTask);}catch(Exception ex){Diagnostics.Log("transit-stop",ex.Message);}
        foreach(var route in legs.Keys)await Close(route,false);overlay.Control=null;
    }
}

static class TransitTest
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static async Task<int> Run(string[] args)
    {
        if(args.Contains("--integration-only")){await Integration(args);return 0;}
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));var ct=timeout.Token;
        var baseline=PathQuality.From([40,41,42,42,43,44,45,46],20);
        Check(PathQuality.From([20,21,22,22,23,24,25,26],20).BetterThan(baseline),"significant RTT improvement rejected");
        Check(!PathQuality.From([38,39,40,40,41,42,43,44],20).BetterThan(baseline),"marginal improvement triggered switch");
        Check(!PathQuality.From([10,11,12,12,13,14,null,null],20).BetterThan(baseline),"lossy candidate selected");
        Check(!PathQuality.From([10,11,12,12,13,14,15,16],10).BetterThan(baseline),"insufficient bandwidth selected");
        Check(PathQuality.From([40,41,42,42,43,44,45,46],31).BetterThan(baseline),"material bandwidth improvement rejected");
        Console.WriteLine("PASS: candidate quality rejects noise, loss and bandwidth regression; accepts meaningful gains.");
        using(var receive=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        using(var send=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            send.Connect((IPEndPoint)receive.Client.LocalEndPoint!);
            await send.SendAsync(new byte[8],ct);var hello=await receive.ReceiveAsync(ct);await receive.SendAsync(hello.Buffer,hello.RemoteEndPoint,ct);await send.ReceiveAsync(ct);
            await send.SendAsync(new byte[1158],ct);await send.SendAsync(new byte[458],ct);
            Check((await receive.ReceiveAsync(ct)).Buffer.Length==1158,"raw UDP first");
            Check((await receive.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2),ct)).Buffer.Length==458,"raw UDP second");
        }
        Console.WriteLine("PASS: raw localhost UDP burst.");
        var secret=RandomNumberGenerator.GetBytes(32);var context=RandomNumberGenerator.GetBytes(32);
        using(var a=new OverlayCipher(secret,context,true))
        using(var b=new OverlayCipher(secret,context,false))
        {
            var plain=RandomNumberGenerator.GetBytes(400);var cipher=a.Encrypt(plain,true);
            Check(b.Decrypt(cipher,true)!.SequenceEqual(plain),"AEAD round trip");
            Check(b.Decrypt(cipher,true)==null,"UDP replay accepted");
            var modified=a.Encrypt(plain,true);modified[^1]^=1;Check(b.Decrypt(modified,true)==null,"Tamper accepted");
            var wrong=RandomNumberGenerator.GetBytes(32);using var c=new OverlayCipher(wrong,context,false);
            Check(c.Decrypt(a.Encrypt(plain,false),false)==null,"C decrypted payload");
        }
        Console.WriteLine("PASS: directional AEAD, UDP replay rejection, tamper rejection, wrong-key rejection.");
        var echo=new TcpListener(IPAddress.Loopback,0);echo.Start();var echoPort=((IPEndPoint)echo.LocalEndpoint).Port;
        using var echoUdp=new UdpClient(new IPEndPoint(IPAddress.Loopback,echoPort));
        var echoTasks=new ConcurrentBag<Task>();
        var accept=Task.Run(async()=>
        {
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    var tcp=await echo.AcceptTcpClientAsync(ct);tcp.NoDelay=true;echoTasks.Add(Task.Run(async()=>
                    {
                        using(tcp)
                        try{var buffer=new byte[32768];int size;while((size=await tcp.GetStream().ReadAsync(buffer,ct))>0)await tcp.GetStream().WriteAsync(buffer.AsMemory(0,size),ct);tcp.Client.Shutdown(SocketShutdown.Send);}
                        catch(Exception ex){Diagnostics.Log("test-echo",ex.Message);}
                    }));
                }
            }
            catch(Exception ex){Diagnostics.Log("test-accept",ex.Message);}
        });
        var udpEcho=Task.Run(async()=>
        {
            try{while(!ct.IsCancellationRequested){var p=await echoUdp.ReceiveAsync(ct);await echoUdp.SendAsync(p.Buffer,p.RemoteEndPoint,ct);}}
            catch(Exception ex){Diagnostics.Log("test-udp",ex.Message);}
        });
        var cap=RandomNumberGenerator.GetBytes(16);OverlaySession? host=null;
        await using var gateway=new Gateway(new Settings{TcpPorts=[echoPort],UdpPorts=[echoPort]},cap);
        gateway.AttachOverlay=async(tcp,command,cancel)=>
        {
            if(command==4){host=await OverlaySession.AcceptHello(tcp,gateway.Port,cap,()=>"P2P / localhost-test",cancel);gateway.Overlay=host;}
            else{var id=await Wire.ReadJson<JsonElement>(tcp.GetStream(),cancel);Check(id.GetProperty("session").GetString()==host!.Id,"resume identity");await tcp.GetStream().WriteAsync(new byte[]{0},cancel);}
            var link=new OverlayLink("base",tcp,gateway.SendOverlayUdp,()=>"P2P / localhost-test");await host!.Add(link);await link.Reader;
        };
        var root=Path.Combine(Path.GetTempPath(),"ArdUi-v2-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var caller=await OverlaySession.Connect(gateway.Port,cap,()=>"P2P / localhost-test",ct);
            using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,caller.Port,ct);
            var header=new byte[23];"AUI1"u8.CopyTo(header);cap.CopyTo(header,4);header[20]=1;BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(21),(ushort)echoPort);
            await tcp.GetStream().WriteAsync(header,ct);Check((await Wire.Read(tcp.GetStream(),1,ct))[0]==0,"gateway open");
            async Task Exchange(int length)
            {
                var payload=RandomNumberGenerator.GetBytes(length);
                var read=Wire.Read(tcp.GetStream(),length,ct);await tcp.GetStream().WriteAsync(payload,ct);
                try{Check((await read.WaitAsync(TimeSpan.FromSeconds(12),ct)).SequenceEqual(payload),"TCP bytes changed or duplicated");}
                catch{Diagnostics.Log("test-caller-state",JsonSerializer.Serialize(caller.Snapshot()));Diagnostics.Log("test-host-state",JsonSerializer.Serialize(host!.Snapshot()));throw;}
            }
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));udp.Connect(IPAddress.Loopback,caller.Port);
            async Task Datagram()
            {
                var payload=RandomNumberGenerator.GetBytes(Wire.MaxUdp);var packet=Wire.Packet(cap,echoPort,payload);
                await udp.SendAsync(packet,ct);UdpReceiveResult response;
                try{response=await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5),ct);}
                catch{Diagnostics.Log("test-caller-state",JsonSerializer.Serialize(caller.Snapshot()));Diagnostics.Log("test-host-state",JsonSerializer.Serialize(host!.Snapshot()));throw;}
                Check(response.Buffer.SequenceEqual(packet),"fragmented native UDP corrupted");
            }
            await Exchange(100000);await Datagram();Console.WriteLine("PASS: multiplexed TCP and full-sized native UDP through the authenticated gateway.");
            await Exchange(10*1024*1024);Console.WriteLine("PASS: intact 10 MiB stream crosses the 8 MiB reliable window without deadlock.");
            Check(caller.UdpFlowCount>0,"UDP mapping was not recorded");caller.ExpireUdp(Environment.TickCount64+120001);
            Check(caller.UdpFlowCount==0,"idle UDP mappings did not expire");await Datagram();
            Console.WriteLine("PASS: idle UDP mapping is released and the same source socket reconnects.");
            await using var relay=await TestRelay.Create(caller,host!,ct);
            await caller.Request("select",new{path="test-C"},ct);caller.Select("test-C");
            await Exchange(100000);await Datagram();Console.WriteLine("PASS: same TCP socket survives base-to-C switch; UDP traverses C with fragmentation.");
            relay.DuplicateUdp=true;await Datagram();Console.WriteLine("PASS: duplicated relay datagrams rejected without duplicate business delivery.");
            await caller.Remove("base");await Exchange(50000);
            await Task.Delay(2000,ct);Check(caller.Link("base")?.Live==true,"base did not resume");
            Console.WriteLine("PASS: standby base TCP connection reconnects while C carries business.");
            await relay.Fail();await Exchange(100000);await Datagram();
            Check(caller.Selected=="base","C failure did not fall back");
            Console.WriteLine("PASS: C failure falls back automatically; original TCP socket and UDP mapping remain usable.");
            var business=tcp.GetStream();tcp.Client.Shutdown(SocketShutdown.Send);Check(await business.ReadAsync(new byte[1],ct)==0,"TCP half-close");
            var report=Diagnostics.Export(root,null);
            using(var archive=ZipFile.OpenRead(report))
            {
                Check(archive.Entries.Count>=3,"diagnostic ZIP incomplete");
                foreach(var entry in archive.Entries){using var reader=new StreamReader(entry.Open());var text=reader.ReadToEnd();Check(!text.Contains(Convert.ToHexString(cap),StringComparison.OrdinalIgnoreCase),"diagnostic capability leak");}
            }
            Console.WriteLine("PASS: TCP half-close and redacted diagnostic ZIP.");
            if(args.Contains("--integration"))await Integration(args);
            return 0;
        }
        catch
        {Console.Error.WriteLine("Diagnostics: "+Diagnostics.Export(root,null));throw;}
        finally
        {
            if(host!=null)await host.DisposeAsync();timeout.Cancel();echo.Stop();echoUdp.Dispose();
            await Task.WhenAll(accept,udpEcho);await Task.WhenAll(echoTasks);
        }
    }
    static async Task Integration(string[] args)
    {
        var root=Environment.GetEnvironmentVariable("ARDUI_TEST_ROOT")??throw new IOException("Run tests/integration_v2.py to start the isolated fixture.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(180));var ct=timeout.Token;
        var echo=new TcpListener(IPAddress.Loopback,0);echo.Start();var port=((IPEndPoint)echo.LocalEndpoint).Port;
        using var udpEcho=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));Wire.ConfigureUdp(udpEcho);
        var sockets=new ConcurrentBag<TcpClient>();var workers=new ConcurrentBag<Task>();
        var accept=Task.Run(async()=>
        {
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    var socket=await echo.AcceptTcpClientAsync(ct);socket.NoDelay=true;sockets.Add(socket);
                    workers.Add(Task.Run(async()=>
                    {try{var data=new byte[32768];int n;while((n=await socket.GetStream().ReadAsync(data,ct))>0)await socket.GetStream().WriteAsync(data.AsMemory(0,n),ct);}
                     catch(Exception ex){Diagnostics.Log("integration-echo",ex.Message);}}));
                }
            }
            catch(Exception ex){Diagnostics.Log("integration-accept",ex.Message);}
        });
        var datagrams=Task.Run(async()=>
        {
            try{while(!ct.IsCancellationRequested){var p=await udpEcho.ReceiveAsync(ct);await udpEcho.SendAsync(p.Buffer,p.RemoteEndPoint,ct);}}
            catch(Exception ex){Diagnostics.Log("integration-udp",ex.Message);}
        });
        var settings=new Settings{Relay=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY")!,RelayKey=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY_KEY")!,TcpPorts=[port],UdpPorts=[port]};
        var server=Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER")!;
        var a=Path.Combine(root,"a");var b=Path.Combine(root,"b");IdentityStore.Prepare(a);IdentityStore.Prepare(b);
        await using var caller=await Engine.Create(a,settings);await using var host=await Engine.Create(b,settings);
        await using var ad=new DirectoryClient(caller,server);await using var bd=new DirectoryClient(host,server);
        ad.Confirm=(_,_)=>Task.FromResult(true);bd.Confirm=(_,_)=>Task.FromResult(true);
        ad.Notice+=message=>Console.WriteLine("A: "+message);bd.Notice+=message=>Console.WriteLine("B: "+message);
        try
        {
            await ad.Register(ct);await bd.Register(ct);await bd.SetAccess(true,ct);ad.Start();bd.Start();
            await ad.Connect(bd.Code,bd.AccessPassword,ct);var session=caller.Outgoing[host.Id];
            Check(session.Overlay!=null&&host.Incoming[caller.Id].Overlay!=null,"v2 authenticated overlay missing");
            Console.WriteLine("PASS: two real ARD clients, signed identities, enrollment and inner E2E handshake.");
            var stablePort=session.Forward(port).Port;
            using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,stablePort,ct);
            var business=tcp.GetStream();
            async Task Exchange(int size=262144)
            {
                var payload=RandomNumberGenerator.GetBytes(size);var reply=Wire.Read(business,size,ct);
                await business.WriteAsync(payload,ct);Check((await reply.WaitAsync(TimeSpan.FromSeconds(20),ct)).SequenceEqual(payload),"real ARD business stream corruption");
            }
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(udp);udp.Connect(IPAddress.Loopback,stablePort);
            async Task Udp()
            {
                for(var i=0;i<3;i++)
                {
                    var payload=RandomNumberGenerator.GetBytes(Wire.MaxUdp);await udp.SendAsync(payload,ct);
                    using var attempt=CancellationTokenSource.CreateLinkedTokenSource(ct);attempt.CancelAfter(TimeSpan.FromSeconds(3));
                    try{var reply=await udp.ReceiveAsync(attempt.Token);Check(reply.Buffer.SequenceEqual(payload),"native RDP-sized UDP corruption");return;}
                    catch(OperationCanceledException)when(!ct.IsCancellationRequested){Diagnostics.Log("integration-udp-retry",i.ToString());}
                }
                throw new IOException("Native UDP failed three probes.");
            }
            await Exchange();await Udp();Console.WriteLine("PASS: TCP and native RDP-sized UDP through the real base ARD path.");
            TransitCandidate[] candidates=[];
            for(var i=0;i<20&&candidates.Length==0;i++)
            {candidates=(await ad.Call<TransitCandidates>("/api/v2/transit/candidates",new{target=host.Id},ct)).Candidates;if(candidates.Length==0)await Task.Delay(500,ct);}
            Check(candidates.Length>0,"ArdTransit was not registered");
            Check(await session.Transit!.ProbeCandidate(candidates[0],true,ct),"candidate rejected");
            var route=session.Overlay!.Selected;Check(route!="base","C was not selected");
            Check(session.Overlay.Link(route)!.Network.StartsWith("P2P",StringComparison.Ordinal),"A-C not Direct");
            Check(host.Incoming[caller.Id].Overlay!.Link(route)!.Network.StartsWith("P2P",StringComparison.Ordinal),"B-C not Direct");
            Console.WriteLine("PASS: independent Python C, NJ signed ticket, C signed temporary identities, both real ARD legs Direct.");
            await Exchange(1048576);await Udp();Console.WriteLine("PASS: same business TCP socket and native UDP switched to C (explicit test selection because localhost base is faster).");
            using(var c=Process.GetProcessById(int.Parse(Environment.GetEnvironmentVariable("ARDUI_TEST_TRANSIT_PID")!)))
            {c.Kill(true);await c.WaitForExitAsync(ct);}
            await Exchange(1048576);await Udp();Check(session.Overlay.Selected=="base","real C failure did not fall back");
            Console.WriteLine("PASS: killed C and its ARD children; same TCP stream survived automatic fallback with intact 1 MiB payload.");
            var old=session.Process;var recovery=Stopwatch.StartNew();await old.DisposeAsync();
            for(var i=0;i<300&&(ReferenceEquals(old,session.Process)||session.Overlay.Link("base")?.Live!=true);i++)await Task.Delay(100,ct);
            Check(!ReferenceEquals(old,session.Process),"base ARD was not restarted");
            Check(session.Overlay.Link("base")?.Live==true,"base transport did not resume within 30 seconds");
            await Exchange();await Udp();Check(session.Forward(port).Port==stablePort,"local port changed");
            Console.WriteLine($"PASS: killed base ARD process; overlay and original business socket resumed on the same local port in {recovery.Elapsed.TotalSeconds:F1}s.");
            await bd.Revoke(caller.Id);Check(host.Incoming.Count==0&&bd.Controllers.Length==0,"revoke did not destroy all host routes");
            Console.WriteLine("PASS: revocation destroys host overlay, transit paths and business sockets.");
            Console.WriteLine("Diagnostics: "+Diagnostics.Export(root,caller));
        }
        catch{Console.Error.WriteLine("Diagnostics: "+Diagnostics.Export(root,caller));throw;}
        finally
        {
            timeout.Cancel();echo.Stop();udpEcho.Dispose();foreach(var socket in sockets)socket.Dispose();
            await Task.WhenAll(accept,datagrams);await Task.WhenAll(workers);
        }
    }
    sealed class TestRelay : IAsyncDisposable
    {
        readonly TcpClient a,b;
        readonly UdpClient ua=new(new IPEndPoint(IPAddress.Loopback,0)),ub=new(new IPEndPoint(IPAddress.Loopback,0));
        readonly CancellationTokenSource stop=new();
        readonly List<Task> tasks=new();
        IPEndPoint? ra,rb;
        public bool DuplicateUdp;
        TestRelay(TcpClient a,TcpClient b){this.a=a;this.b=b;}
        public static async Task<TestRelay> Create(OverlaySession caller,OverlaySession host,CancellationToken ct)
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            var ca=new TcpClient();await ca.ConnectAsync(IPAddress.Loopback,port,ct);var a=await listener.AcceptTcpClientAsync(ct);
            var cb=new TcpClient();await cb.ConnectAsync(IPAddress.Loopback,port,ct);var b=await listener.AcceptTcpClientAsync(ct);listener.Stop();
            var relay=new TestRelay(a,b);
            relay.tasks.Add(Task.Run(async()=>{try{await Wire.Bridge(a,b,relay.stop.Token);}catch(Exception ex){Diagnostics.Log("test-relay",ex.Message);}}));
            relay.tasks.Add(relay.Udp(true));relay.tasks.Add(relay.Udp(false));
            async Task Add(OverlaySession target,TcpClient socket,int endpoint)
            {
                var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));udp.Connect(IPAddress.Loopback,endpoint);
                var link=new OverlayLink("test-C",socket,async(data,cancel)=>await udp.SendAsync(data,cancel),()=>"P2P / localhost-test");
                link.Closed+=()=>udp.Dispose();await target.Add(link);
                relay.tasks.Add(Task.Run(async()=>
                {try{while(!relay.stop.IsCancellationRequested){var p=await udp.ReceiveAsync(relay.stop.Token);await target.ReceiveUdp(link,p.Buffer);}}
                 catch(Exception ex){Diagnostics.Log("test-relay-udp",ex.Message);}}));
            }
            await Add(caller,ca,((IPEndPoint)relay.ua.Client.LocalEndPoint!).Port);
            await Add(host,cb,((IPEndPoint)relay.ub.Client.LocalEndPoint!).Port);
            await Task.Delay(2300,ct);return relay;
        }
        async Task Udp(bool side)
        {
            try
            {
                var from=side?ua:ub;var to=side?ub:ua;
                while(!stop.IsCancellationRequested)
                {
                    var packet=await from.ReceiveAsync(stop.Token);if(side)ra=packet.RemoteEndPoint;else rb=packet.RemoteEndPoint;
                    var remote=side?rb:ra;if(remote==null)continue;
                    await to.SendAsync(packet.Buffer,remote,stop.Token);if(DuplicateUdp)await to.SendAsync(packet.Buffer,remote,stop.Token);
                }
            }
            catch(Exception ex){Diagnostics.Log("test-relay-udp-stop",ex.Message);}
        }
        public Task Fail(){stop.Cancel();a.Dispose();b.Dispose();ua.Dispose();ub.Dispose();return Task.CompletedTask;}
        public async ValueTask DisposeAsync(){await Fail();await Task.WhenAll(tasks);}
    }
}
