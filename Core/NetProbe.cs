using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ArdUi;

// The part of the network diagnosis that needs a trusted server (M) to take part: it sees what the outside sees, answers from a second
// port, echoes and pushes measured traffic, and sends an unsolicited packet (UDP) or opens a connection (TCP) to a port this machine
// listens on. Protocol and abuse protection: docs/probe-protocol.md and server/probe_udp.py.
sealed record ProbeTicket(string Token, string Host, int[] Ports, string Ip, long Expires);

// The answer of POST /api/v1/probe/start.
sealed record ProbeReply(int V, string Token, long Expires, string Ip, string Host, int[] Ports);

static class NetProbe
{
    public const int Header = 48, TokenLength = 28, MinRequest = 96;
    public const byte Bind = 1, Echo = 2, Other = 3, Push = 4, ProbeUdp = 5, Confirm = 6, ProbeTcp = 7, Hello = 8, Auth = 9, Reply = 0x80, PushData = 0x84, ProbePacket = 0x90;

    // ---- packets --------------------------------------------------------------------------------------------------------
    public static byte[] Build(byte type, byte[] token, uint seq, ulong stamp, ReadOnlySpan<byte> body, int minLength = MinRequest)
    {
        var packet = new byte[Math.Max(minLength, Header + body.Length)];
        "ARDP"u8.CopyTo(packet); packet[4] = 1; packet[5] = type;
        token.CopyTo(packet, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(36), seq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(40), stamp);
        body.CopyTo(packet.AsSpan(Header));
        return packet;
    }

    public static bool IsPacket(ReadOnlySpan<byte> data, byte type) => data.Length >= Header && data[0] == (byte)'A' && data[1] == (byte)'R' && data[2] == (byte)'D' && data[3] == (byte)'P' && data[4] == 1 && data[5] == type;
    public static uint Sequence(ReadOnlySpan<byte> data) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[36..]);
    public static ulong Stamp(ReadOnlySpan<byte> data) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[40..]);

    // The endpoint a BIND / OTHER / CONFIRM / AUTH answer reports: family, 16 address bytes, port seen, port answered from.
    public static (IPEndPoint Seen, int ServerPort) ParseEndpoint(ReadOnlySpan<byte> body)
    {
        var address = new IPAddress(body.Slice(1, 16));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return (new IPEndPoint(address, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(body[17..])), body.Length >= 21 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(body[19..]) : 0);
    }

    public static byte[] Address16(IPAddress address) => (address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address).GetAddressBytes();

    static ulong Now() => (ulong)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency);   // microseconds, echoed back untouched by the server
    static string Ms(double micros) => (micros / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " ms";

    // ---- one request and its answer ----------------------------------------------------------------------------------------
    static async Task<(byte[] Data, IPEndPoint From)?> Exchange(Socket socket, IPEndPoint server, byte[] request, byte replyType, int attempts, CancellationToken ct, int waitMs = 500)
    {
        var buffer = new byte[2048];
        var sequence = Sequence(request);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await socket.SendToAsync(request, SocketFlags.None, server, ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(waitMs);
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), deadline.Token);
                    var data = buffer.AsMemory(0, result.ReceivedBytes);
                    if (IsPacket(data.Span, replyType) && Sequence(data.Span) == sequence) return (data.ToArray(), (IPEndPoint)result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return null;
    }

    // HELLO, then AUTH with the cookie: afterwards the address this socket leaves from is usable with the token. Returns the endpoint M saw.
    public static async Task<IPEndPoint?> Prove(Socket socket, IPEndPoint server, byte[] token, Func<uint> next, CancellationToken ct)
    {
        var hello = await Exchange(socket, server, Build(Hello, token, next(), Now(), []), Reply | Hello, 3, ct, 500);
        if (hello == null) return null;
        var cookie = hello.Value.Data.AsSpan(Header, 16).ToArray();
        var auth = await Exchange(socket, server, Build(Auth, token, next(), Now(), cookie), Reply | Auth, 2, ct, 500);
        if (auth == null || auth.Value.Data[Header] != 1) return null;
        return ParseEndpoint(auth.Value.Data.AsSpan(Header + 1)).Seen;
    }

    // ---- the checks --------------------------------------------------------------------------------------------------------
    public sealed class Findings
    {
        public IPEndPoint? Seen0, Seen1;
        public List<IPAddress> Exits = new();     // every public address M saw this machine's UDP leave from (each proven by a cookie)
        public string Nat = "unknown";            // none, cone, symmetric, multiexit, unknown (as M sees it)
        public bool? PortRestricted;              // null: not measured
        public bool? DirectExposure;              // an unsolicited packet to a port that was never used reached this machine
        public IPAddress? UdpIp;
        public double Loss = -1, Rtt = -1;
        public int PathMtu;
        public double DownLoss = -1;
    }

    static IPAddress? ResolveV4(string host)
    {
        try { return IPAddress.TryParse(host, out var literal) ? (literal.AddressFamily == AddressFamily.InterNetwork ? literal : null) : Dns.GetHostAddresses(host, AddressFamily.InterNetwork).FirstOrDefault(); }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return null; }
    }

    static Socket NewSocket() { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); socket.Bind(new IPEndPoint(IPAddress.Any, 0)); return socket; }

    public static async Task<(DiagSection Section, Findings Findings)> Verify(ProbeTicket ticket, string fallbackHost, CancellationToken ct, bool quick = false)
    {
        var section = new DiagSection { Title = "M 辅助验证（实际收发数据）" };
        var findings = new Findings();
        var token = Convert.FromHexString(ticket.Token);
        var host = ticket.Host.Length != 0 ? ticket.Host : fallbackHost;
        var address = ResolveV4(host);
        if (address == null || ticket.Ports.Length < 2) { section.Summary = "无法解析 M 的探测地址"; section.Level = DiagLevel.Bad; section.Add("M", host, DiagLevel.Bad); return (section, findings); }
        var server0 = new IPEndPoint(address, ticket.Ports[0]); var server1 = new IPEndPoint(address, ticket.Ports[1]);
        using var socket = NewSocket();
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        uint seq = 0; var number = () => ++seq;

        // 1. the first address proves itself; then more sockets (new source ports) show every line this machine leaves by
        var first = await Prove(socket, server0, token, number, ct);
        if (first == null)
        {
            section.Summary = "M 的探测端口没有应答（UDP " + ticket.Ports[0] + "–" + ticket.Ports[1] + " 被拦截，或 M 还没有开放探测端口）"; section.Level = DiagLevel.Bad;
            section.Add("UDP → " + server0, "没有应答（HELLO / 验证都没有回包）", DiagLevel.Bad);
            section.Add("HTTPS 看到的地址", ticket.Ip);
            section.Add("提示", "如果公共 STUN 有应答而这里没有，是这个网络挡住了 M 的探测端口，或 M 还没有放行这两个 UDP 端口。");
            return (section, findings);
        }
        findings.Seen0 = first; findings.UdpIp = first.Address; findings.Exits.Add(first.Address);
        var extra = new List<Socket>();
        try
        {
            for (var round = 0; round < 7 && !quick; round++)
            {
                var more = NewSocket(); extra.Add(more);
                var seen = await Prove(more, round % 2 == 0 ? server0 : server1, token, number, ct);
                if (seen != null && !findings.Exits.Contains(seen.Address)) findings.Exits.Add(seen.Address);
            }
        }
        finally { foreach (var s in extra) s.Dispose(); }
        section.Add("M 看到的 UDP 地址", first.ToString(), DiagLevel.Good);
        var tcpIp = IPAddress.TryParse(ticket.Ip, out var parsed) ? parsed : null;
        if (tcpIp != null)
        {
            var same = findings.Exits.Contains(tcpIp);
            section.Add("M 看到的 HTTPS 地址", tcpIp + (same ? "" : "  ·  不在 UDP 出口之列：UDP 与 TCP 走不同的出口"), same ? DiagLevel.Good : DiagLevel.Warn);
        }
        if (findings.Exits.Count > 1) section.Add("多个出口", $"同一台电脑的 UDP 从 {findings.Exits.Count} 个公网地址出去：{string.Join("、", findings.Exits)}（多线路负载均衡，或运营商出口地址池）", DiagLevel.Warn);

        // 2. what M sees from two server ports
        var firstBind = await Exchange(socket, server0, Build(Bind, token, number(), Now(), []), Reply | Bind, 3, ct);
        var secondBind = await Exchange(socket, server1, Build(Bind, token, number(), Now(), []), Reply | Bind, 3, ct);
        var locals = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address)).ToList();
        if (firstBind != null) findings.Seen0 = ParseEndpoint(firstBind.Value.Data.AsSpan(Header)).Seen;
        if (secondBind != null)
        {
            var seen1 = ParseEndpoint(secondBind.Value.Data.AsSpan(Header)).Seen; findings.Seen1 = seen1;
            section.Add("换一个 M 端口再看", seen1.ToString(), findings.Seen0 != null && seen1.Port == findings.Seen0.Port && seen1.Address.Equals(findings.Seen0.Address) ? DiagLevel.Good : DiagLevel.Warn);
            if (findings.Seen0 != null && locals.Any(l => l.Equals(findings.Seen0.Address))) findings.Nat = "none";
            else if (findings.Seen0 != null && !seen1.Address.Equals(findings.Seen0.Address)) findings.Nat = "multiexit";
            else findings.Nat = findings.Seen0 != null && seen1.Port == findings.Seen0.Port ? "cone" : "symmetric";
        }
        else section.Add("换一个 M 端口再看", "第二个端口没有应答，无法判断映射类型", DiagLevel.Warn);
        if (findings.Exits.Count > 1 && findings.Nat is not "none") findings.Nat = findings.Nat == "symmetric" ? "symmetric" : "multiexit";
        var seenPort = findings.Seen0?.Port ?? 0;
        section.Add("本机 UDP 端口", localPort + (seenPort == localPort ? "，映射后端口不变" : "，映射后端口改变"));

        // 3. filtering: a packet from a port this machine never contacted
        var other = await Exchange(socket, server0, Build(Other, token, number(), Now(), []), Reply | Other, 3, ct, 600);
        findings.PortRestricted = other != null ? false : null;
        if (findings.Nat == "none") section.Add("过滤", "公网直连，不经过 NAT 过滤", DiagLevel.Good);
        else if (other != null) section.Add("过滤", "M 的另一个端口也能打进来：完全锥形或地址受限锥形（对端更容易直连）", DiagLevel.Good);
        else if (findings.Nat is "cone" or "multiexit") { findings.PortRestricted = true; section.Add("过滤", "来自没联系过的端口的包被丢弃：端口受限（需要双方同时打洞）", DiagLevel.Warn); }
        else section.Add("过滤", findings.Nat == "symmetric" ? "对称型 NAT，不再区分过滤类型" : "无法判断");
        section.Add("NAT 类型（M 实测）", findings.Nat switch
        {
            "none" => "无 NAT（公网直连）", "cone" => findings.PortRestricted == true ? "端口受限锥形" : "完全 / 地址受限锥形",
            "multiexit" => "出口地址随连接变化（多线路 / 地址池），不是单一 NAT", "symmetric" => "对称型（不同 M 端口看到不同的公网端口）", _ => "无法判断",
        }, findings.Nat is "none" or "cone" ? DiagLevel.Good : findings.Nat is "symmetric" or "multiexit" ? DiagLevel.Warn : DiagLevel.Info);

        // 4. round trip, loss, jitter and path MTU
        if (!quick)
        {
            await Link(section, findings, socket, server0, token, number, ct);
            await Downlink(section, findings, socket, server0, token, number(), ct);
        }

        // 5. unsolicited packets to a port nobody on this machine ever talked out of
        await Control(section, findings, socket, server0, token, number, ct);

        section.Level = findings.DirectExposure == true ? DiagLevel.Warn : findings.Nat is "none" or "cone" ? DiagLevel.Good : findings.Nat is "symmetric" or "multiexit" ? DiagLevel.Warn : DiagLevel.Info;
        section.Summary = findings.Nat switch
        {
            "none" => "公网直连：本机地址就是公网地址", "cone" => "锥形 NAT，M 实测可以打洞" + (findings.PortRestricted == true ? "（端口受限）" : ""),
            "multiexit" => $"出口不止一个（{findings.Exits.Count} 个公网地址）：对每个目标看到的地址不同，打洞不稳定", "symmetric" => "对称型 NAT：M 实测每个目标看到不同的公网端口，直连困难", _ => "M 有应答，但无法判断 NAT 类型",
        };
        return (section, findings);
    }

    static async Task Link(DiagSection section, Findings findings, Socket socket, IPEndPoint server, byte[] token, Func<uint> next, CancellationToken ct)
    {
        const int count = 40, size = 1200;
        var rtts = new double[count]; Array.Fill(rtts, -1);
        var buffer = new byte[2048]; var received = 0;
        var reader = Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(count * 25 + 1500);
            try
            {
                while (received < count)
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                    var data = buffer.AsMemory(0, result.ReceivedBytes);
                    if (!IsPacket(data.Span, Reply | Echo)) continue;
                    var index = (int)(Sequence(data.Span) - 100000); if (index is < 0 or >= count) continue;
                    if (rtts[index] < 0) { rtts[index] = Now() - Stamp(data.Span); received++; }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        }, ct);
        for (var i = 0; i < count; i++) { await socket.SendToAsync(Build(Echo, token, (uint)(100000 + i), Now(), new byte[size - Header], size), SocketFlags.None, server, ct); await Task.Delay(20, ct); }
        await reader;
        var good = rtts.Where(r => r >= 0).ToArray();
        findings.Loss = (count - good.Length) * 100.0 / count;
        if (good.Length == 0) { section.Add("往返（40 个 1200 字节包）", "全部丢失", DiagLevel.Bad); return; }
        findings.Rtt = good.Average();
        var jitter = good.Zip(good.Skip(1), (a, b) => Math.Abs(a - b)).DefaultIfEmpty(0).Average();
        section.Add("往返（40 个 1200 字节包）", $"丢包 {findings.Loss:0.#}%  ·  延迟 {Ms(good.Min())} / {Ms(good.Average())} / {Ms(good.Max())}（最小/平均/最大）  ·  抖动 {Ms(jitter)}",
            findings.Loss <= 1 ? DiagLevel.Good : findings.Loss <= 5 ? DiagLevel.Warn : DiagLevel.Bad);

        // path MTU: the largest datagram that round-trips with "do not fragment" set
        var best = 0;
        try
        {
            socket.DontFragment = true;
            foreach (var payload in new[] { 1472, 1400, 1344, 1280, 1232, 1200, 1100, 1000 })
            {
                // a size over this machine's own interface MTU is refused at once by the stack: that is not the end of the search
                try
                {
                    var attempt = await Exchange(socket, server, Build(Echo, token, next(), Now(), new byte[payload - Header], payload), Reply | Echo, 2, ct, 400);
                    if (attempt != null) { best = payload; break; }
                }
                catch (SocketException) { }
            }
        }
        catch (SocketException) { }
        finally { try { socket.DontFragment = false; } catch (SocketException) { } }
        findings.PathMtu = best == 0 ? 0 : best + 28;
        section.Add("路径 MTU（不分片）", best == 0 ? "1000 字节的包也没有通过，路径上可能有严重的分片或封包限制" : $"约 {best + 28} 字节" + (best + 28 < 1500 ? "（小于 1500：路径上有隧道、PPPoE 或 VPN）" : ""), best == 0 ? DiagLevel.Warn : DiagLevel.Info);
    }

    static async Task Downlink(DiagSection section, Findings findings, Socket socket, IPEndPoint server, byte[] token, uint seq, CancellationToken ct)
    {
        const int count = 100, size = 1200, interval = 10;
        var body = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(body, count); System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), size); System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), interval);
        var seen = new HashSet<uint>(); var buffer = new byte[2048]; long firstTick = 0, lastTick = 0; var reordered = 0; long previous = -1; long bytes = 0;
        await socket.SendToAsync(Build(Push, token, seq, Now(), body), SocketFlags.None, server, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(count * interval + 2500);
        try
        {
            while (seen.Count < count)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                var data = buffer.AsMemory(0, result.ReceivedBytes);
                if (!IsPacket(data.Span, PushData)) continue;
                var number = Sequence(data.Span);
                if (!seen.Add(number)) continue;
                var now = Stopwatch.GetTimestamp(); if (firstTick == 0) firstTick = now; lastTick = now;
                if (previous >= 0 && number < previous) reordered++;
                previous = number; bytes += result.ReceivedBytes;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        findings.DownLoss = (count - seen.Count) * 100.0 / count;
        if (seen.Count == 0) { section.Add("M → 本机（100 个包）", "全部丢失", DiagLevel.Bad); return; }
        var seconds = Math.Max(0.001, (lastTick - firstTick) / (double)Stopwatch.Frequency);
        section.Add("M → 本机（100 个 1200 字节包）", $"丢包 {findings.DownLoss:0.#}%  ·  乱序 {reordered}  ·  速率 {bytes * 8 / seconds / 1e6:0.0} Mbps",
            findings.DownLoss <= 1 ? DiagLevel.Good : findings.DownLoss <= 5 ? DiagLevel.Warn : DiagLevel.Bad);
    }

    // A port that nothing on this machine has ever sent out of: only a host that is directly on the internet (public address, no filtering),
    // or a router that forwards it, lets M's packet through. Seeing it arrive is good for P2P and also means this machine is exposed.
    static async Task Control(DiagSection section, Findings findings, Socket socket, IPEndPoint server, byte[] token, Func<uint> next, CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var arrived = await ProbeInbound(socket, listener, server, token, next(), 0, ct);
        findings.DirectExposure = arrived;
        section.Add("未映射端口的入站探测", arrived ? $"UDP {port}：M 的主动包直接到达了（本机在公网上直接可达，没有 NAT 或防火墙拦截）" : $"UDP {port}：没有到达（正常：NAT 或防火墙只放行本机主动发起的流量）",
            arrived ? DiagLevel.Warn : DiagLevel.Info);
    }

    // Asks M to send an unsolicited packet to `listener`'s port (at `target` when given, else the control socket's own address); true when it arrives.
    public static async Task<bool> ProbeInbound(Socket control, Socket listener, IPEndPoint server, byte[] token, uint seq, int fromOther, CancellationToken ct, int portOverride = 0, IPAddress? target = null)
    {
        var port = portOverride != 0 ? portOverride : ((IPEndPoint)listener.LocalEndPoint!).Port;
        var nonce = new byte[8]; Random.Shared.NextBytes(nonce);
        var body = new byte[target == null ? 11 : 27];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)port); body[2] = (byte)fromOther; nonce.CopyTo(body, 3);
        if (target != null) Address16(target).CopyTo(body, 11);
        var buffer = new byte[512];
        var waiting = Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(2500);
            try
            {
                while (true)
                {
                    var result = await listener.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                    var data = buffer.AsMemory(0, result.ReceivedBytes);
                    if (IsPacket(data.Span, ProbePacket) && data.Span.Slice(Header, 8).SequenceEqual(nonce)) return true;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { return false; }
        }, ct);
        await Exchange(control, server, Build(ProbeUdp, token, seq, Now(), body), Reply | ProbeUdp, 2, ct, 500);
        return await waiting;
    }

    // ---- UPnP / NAT-PMP verification with M ----------------------------------------------------------------------------------
    // The mapping a router was asked for is only real if M's packet, sent from the outside, comes through it. The probe goes to the router's
    // WAN address (`wanIp`); behind a router with several lines that address may not be the one this machine's own packets leave by, so
    // sockets are tried until one leaves from it, which proves to M that the address is ours. Also run after the mapping is removed: if
    // the packet still arrives, the port was open without the mapping, and the result says so instead of crediting the router.
    public static async Task<List<DiagLine>> VerifyUdpMapping(Func<CancellationToken, Task<ProbeTicket?>> ticketSource, string fallbackHost, int port, bool mapped, CancellationToken ct, IPAddress? wanIp = null, int externalPort = 0, string kind = "映射")
    {
        var lines = new List<DiagLine>();
        var ticket = await ticketSource(ct);
        if (ticket == null) { lines.Add(new DiagLine("M 探测", "没有取得 M 的探测令牌（未联网或尚未注册）", DiagLevel.Warn)); return lines; }
        var token = Convert.FromHexString(ticket.Token); var address = ResolveV4(ticket.Host.Length != 0 ? ticket.Host : fallbackHost);
        if (address == null || ticket.Ports.Length < 2) { lines.Add(new DiagLine("M 探测", "无法解析 M 的地址", DiagLevel.Warn)); return lines; }
        var server = new IPEndPoint(address, ticket.Ports[0]);
        uint seq = 0; var number = () => ++seq;
        using var control = NewSocket();
        var own = await Prove(control, server, token, number, ct);
        if (own == null) { lines.Add(new DiagLine("M 探测", "M 的探测端口没有应答，无法实测", DiagLevel.Warn)); return lines; }
        var exits = new List<IPAddress> { own.Address };   // the addresses this machine's packets leave from that M has seen (each proven)
        IPAddress? target = null;
        var spare = new List<Socket>();
        try
        {
            if (wanIp != null && !wanIp.Equals(own.Address))
            {
                // the router's WAN address differs from where this machine's packets left: find a socket that leaves from it
                var found = false;
                for (var attempt = 0; attempt < 10 && !found; attempt++)
                {
                    var more = NewSocket(); spare.Add(more);
                    var seen = await Prove(more, server, token, number, ct);
                    if (seen != null && !exits.Contains(seen.Address)) exits.Add(seen.Address);
                    found = seen != null && seen.Address.Equals(wanIp);
                }
                if (!found) { lines.Add(new DiagLine(kind + " 探测", $"路由器的外网地址 {wanIp} 与本机包实际出去的地址 {own.Address} 不同，且多次尝试都没能让本机的包从 {wanIp} 出去，M 无法确认这个地址属于本机，所以不能实测", DiagLevel.Warn)); return lines; }
                target = wanIp;
            }
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { listener.Bind(new IPEndPoint(IPAddress.Any, port)); } catch (SocketException ex) { lines.Add(new DiagLine("M 探测", "本机端口 " + port + " 无法监听：" + ex.SocketErrorCode, DiagLevel.Warn)); return lines; }
            var probePort = externalPort != 0 ? externalPort : port;
            var arrived = await ProbeInbound(control, listener, server, token, number(), 0, ct, probePort, target);
            if (arrived) _ = await Exchange(listener, server, Build(Confirm, token, number(), Now(), Encoding.ASCII.GetBytes("00000000")), Reply | Confirm, 1, ct, 300);   // answer it, so that M sees the way back too
            var where = target ?? own.Address;
            lines.Add(new DiagLine(mapped ? $"M 从外部探测{kind}端口" : $"M 从外部探测同一端口（{kind}已删除）", arrived ? $"{where}:{probePort}（UDP）：到达" : $"{where}:{probePort}（UDP）：没有到达",
                mapped ? (arrived ? DiagLevel.Good : DiagLevel.Bad) : (arrived ? DiagLevel.Warn : DiagLevel.Good)));
            // a router with several lines maps the port on one of them: say whether the same port is reachable at this machine's other exits
            if (mapped && !arrived)
            {
                for (var attempt = 0; attempt < 8 && exits.Count < 3; attempt++)
                {
                    var more = NewSocket(); spare.Add(more);
                    var seen = await Prove(more, server, token, number, ct);
                    if (seen != null && !exits.Contains(seen.Address)) exits.Add(seen.Address);
                }
            }
            if (mapped && !arrived && exits.Count > 1)
                foreach (var exit in exits.Where(e => !e.Equals(where)).Take(2))
                {
                    var there = await ProbeInbound(control, listener, server, token, number(), 0, ct, probePort, exit);
                    lines.Add(new DiagLine($"对比：同一端口在另一个出口 {exit}", there ? "到达（映射在这条线路上有效）" : "也没有到达", there ? DiagLevel.Good : DiagLevel.Info));
                }
        }
        finally { foreach (var s in spare) s.Dispose(); }
        return lines;
    }

    public static async Task<(bool Connected, bool Acked)> ProbeTcpInbound(Socket control, TcpListener listener, IPEndPoint server, byte[] token, uint seq, CancellationToken ct)
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var nonce = new byte[8]; Random.Shared.NextBytes(nonce);
        var body = new byte[10];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)port); nonce.CopyTo(body, 2);
        var accepted = Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(4500);
            try
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = client.GetStream(); var banner = new byte[16]; var read = 0;
                while (read < 16) { var n = await stream.ReadAsync(banner.AsMemory(read), deadline.Token); if (n == 0) break; read += n; }
                if (read == 16 && Encoding.ASCII.GetString(banner, 0, 8) == "ARDPTCP1") await stream.WriteAsync(banner.AsMemory(8, 8), deadline.Token);
                return true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { return false; }
        }, ct);
        var reply = await Exchange(control, server, Build(ProbeTcp, token, seq, Now(), body), Reply | ProbeTcp, 1, ct, 5000);
        var connected = await accepted;
        return (connected, reply != null && reply.Value.Data.Length > Header + 1 && reply.Value.Data[Header] == 1 && reply.Value.Data[Header + 1] == 1);
    }
}
