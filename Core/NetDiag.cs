using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace ArdUi;

// Network diagnosis: this machine's addresses, what the outside sees (STUN), the kind of NAT, and what the router offers (UPnP IGD,
// NAT-PMP). Everything here is read-only except TestPortMapping, which the user starts on purpose and which removes its mapping again.
enum DiagLevel { Info, Good, Warn, Bad }

sealed record DiagLine(string Label, string Value, DiagLevel Level = DiagLevel.Info);

sealed class DiagSection
{
    public required string Title { get; init; }
    public string Summary { get; set; } = "";
    public DiagLevel Level { get; set; } = DiagLevel.Info;
    public List<DiagLine> Lines { get; } = new();
    public DiagSection Add(string label, string value, DiagLevel level = DiagLevel.Info) { Lines.Add(new DiagLine(label, value, level)); return this; }
}

enum AddressScope { Loopback, LinkLocal, Private, Cgnat, Public }

sealed class UpnpGateway
{
    public required Uri Location { get; init; }
    public string Server { get; init; } = "";
    public string FriendlyName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string ModelNumber { get; set; } = "";
    public string Udn { get; set; } = "";
    public Uri? Control { get; set; }
    public string ServiceType { get; set; } = "";
    public IPAddress? LocalAddress { get; set; }   // the address of this machine that reached the gateway
}

sealed class NetDiagOptions
{
    public string[] StunServers { get; init; } = ["stun.miwifi.com:3478", "stun.qq.com:3478", "stun.l.google.com:19302", "stun.cloudflare.com:3478", "stun.syncthing.net:3478"];
    public string[] StunServersV6 { get; init; } = ["stun.l.google.com:19302", "stun.cloudflare.com:3478"];
    public TimeSpan StunTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool UseSsdp { get; init; } = true;
    public TimeSpan SsdpTimeout { get; init; } = TimeSpan.FromSeconds(2.5);
    public Uri[] ExtraGatewayLocations { get; init; } = [];   // tests: a description URL served by a fake router
    public bool UseNatPmp { get; init; } = true;
    public string[] EchoUrls { get; init; } = ["http://4.ipw.cn", "http://myip.ipip.net", "http://api.ipify.org", "https://api.ip.sb/ip"];   // what the outside sees over TCP
    public int EgressRounds { get; init; } = 5;                  // new UDP source port each round: load balancers pick an exit per flow
    public string TraceTarget { get; init; } = "223.5.5.5";
    public string[] PingTargets { get; init; } = ["223.5.5.5", "1.1.1.1"];
    public string[] DnsNames { get; init; } = ["www.baidu.com", "www.microsoft.com", "github.com"];
    public string? DirectoryUrl { get; init; }                   // the ArdUi directory (HTTPS), for a reachability and latency check
    public string? RelayHostPort { get; init; }                  // host:port of the relay / M that ArdUi uses
    public bool UseTrace { get; init; } = true;
    public Func<CancellationToken, Task<ProbeTicket?>>? Probe { get; init; }   // asks the directory server for a probe token; null: no M-assisted checks
    public bool TestMappings { get; init; } = true;   // with a probe: really test UPnP / NAT-PMP (a mapping is added for a minute and removed again)
    public string ProbeFallbackHost { get; init; } = "";                      // M's host when the ticket does not name one
}

sealed class NetDiagReport
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public List<DiagSection> Sections { get; } = new();
    public List<UpnpGateway> Gateways { get; } = new();
    public string Nat { get; set; } = "unknown";   // none, cone, symmetric, unknown
    public bool UpnpMappingAvailable { get; set; }
    public bool? UpnpVerified { get; set; }     // M probed the mapped port from the outside: true arrived, false did not, null not tested or inconclusive
    public bool? NatPmpVerified { get; set; }
    public List<(IPAddress Gateway, IPAddress External)> NatPmp { get; } = new();   // routers that answered NAT-PMP, with their external address
    public NetProbe.Findings? Probe { get; set; }   // what M measured, when it took part

    public string ToText()
    {
        var text = new StringBuilder($"ArdUi {Program.Version} network diagnosis {At:yyyy-MM-dd HH:mm:ss}\n");
        foreach (var section in Sections)
        {
            text.Append('\n').Append(section.Title).Append(" - ").Append(section.Summary).Append('\n');
            foreach (var line in section.Lines) text.Append("  ").Append(line.Label).Append(": ").Append(line.Value).Append('\n');
        }
        return text.ToString();
    }
}

static class NetDiag
{
    // ---- addresses ------------------------------------------------------------------------------------------------------
    public static AddressScope Scope(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return AddressScope.Loopback;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 169 && b[1] == 254) return AddressScope.LinkLocal;
            if (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168) return AddressScope.Private;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return AddressScope.Cgnat;
            return AddressScope.Public;
        }
        if (address.IsIPv6LinkLocal) return AddressScope.LinkLocal;
        if ((b[0] & 0xFE) == 0xFC || b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return AddressScope.Private;   // unique local fc00::/7, deprecated site-local fec0::/10
        return AddressScope.Public;
    }

    static string ScopeText(AddressScope scope) => scope switch
    {
        AddressScope.Loopback => "本机回环", AddressScope.LinkLocal => "链路本地（不可路由）", AddressScope.Private => "私网地址",
        AddressScope.Cgnat => "运营商级 NAT 地址（100.64.0.0/10）", _ => "公网地址",
    };

    // Virtual adapters and VPN clients change which address the outside sees, so they are named in the report.
    public static string? VirtualKind(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        foreach (var (word, kind) in new[] { ("wireguard", "VPN（WireGuard）"), ("tailscale", "VPN（Tailscale）"), ("zerotier", "VPN（ZeroTier）"), ("wintun", "VPN / 代理（Wintun）"), ("clash", "代理（Clash）"),
                     ("openvpn", "VPN（OpenVPN）"), ("tap-windows", "VPN（TAP）"), ("vpn", "VPN"), ("vethernet", "虚拟交换机（Hyper-V / WSL）"), ("hyper-v", "虚拟（Hyper-V）"), ("vmware", "虚拟（VMware）"),
                     ("virtualbox", "虚拟（VirtualBox）"), ("wsl", "虚拟（WSL）"), ("npcap", "抓包"), ("loopback", "回环"), ("bluetooth", "蓝牙") })
            if (text.Contains(word, StringComparison.Ordinal)) return kind;
        return null;
    }

    static IEnumerable<NetworkInterface> UsableInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel));

    public static DiagSection LocalAddresses()
    {
        var section = new DiagSection { Title = "本机地址" };
        var globalV4 = false; var globalV6 = false; var count = 0;
        foreach (var nic in UsableInterfaces())
        {
            var properties = nic.GetIPProperties();
            var unicast = properties.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToList();
            if (unicast.Count == 0) continue;
            count++;
            var speed = nic.Speed > 0 ? (nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1_000_000_000.0:0.#} Gbps" : $"{nic.Speed / 1_000_000.0:0} Mbps") : "速率未知";
            var virtualKind = VirtualKind(nic);
            section.Add("网卡", $"{nic.Name}（{nic.NetworkInterfaceType}，{speed}）" + (virtualKind != null ? "  ·  " + virtualKind : ""), virtualKind != null ? DiagLevel.Warn : DiagLevel.Info);
            try { section.Add("  MTU", properties.GetIPv4Properties().Mtu.ToString(CultureInfo.InvariantCulture)); } catch (NetworkInformationException) { }
            foreach (var address in unicast)
            {
                var scope = Scope(address);
                if (scope == AddressScope.Public) { if (address.AddressFamily == AddressFamily.InterNetwork) globalV4 = true; else globalV6 = true; }
                section.Add(address.AddressFamily == AddressFamily.InterNetwork ? "  IPv4" : "  IPv6", $"{address.ToString().Split('%')[0]}  ·  {ScopeText(scope)}",
                    scope == AddressScope.LinkLocal && address.AddressFamily == AddressFamily.InterNetwork ? DiagLevel.Warn : DiagLevel.Info);   // an IPv4 169.254 address means no DHCP answer; IPv6 fe80 is normal
            }
            foreach (var gateway in properties.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))) section.Add("  网关", gateway.ToString().Split('%')[0]);
            var dns = properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork || Scope(a) == AddressScope.Public).Select(a => a.ToString().Split('%')[0]).Take(3).ToArray();
            if (dns.Length != 0) section.Add("  DNS", string.Join("、", dns));
        }
        section.Summary = count == 0 ? "没有可用的网络连接" : (globalV6 ? "有全局 IPv6" : "没有全局 IPv6") + (globalV4 ? "，有公网 IPv4" : "，IPv4 为私网地址");
        section.Level = count == 0 ? DiagLevel.Bad : DiagLevel.Info;
        return section;
    }

    // ---- STUN (RFC 5389 binding request) ---------------------------------------------------------------------------
    public static byte[] StunRequest(byte[] transaction)
    {
        var message = new byte[20];
        message[0] = 0x00; message[1] = 0x01;                       // binding request
        message[4] = 0x21; message[5] = 0x12; message[6] = 0xA4; message[7] = 0x42;   // magic cookie
        transaction.CopyTo(message, 8);
        return message;
    }

    // The reflexive address a STUN server saw, or null when the datagram is not a matching binding success.
    public static IPEndPoint? ParseStun(byte[] data, int length, byte[] transaction)
    {
        if (length < 20 || data[0] != 0x01 || data[1] != 0x01) return null;
        for (var i = 0; i < 12; i++) if (data[8 + i] != transaction[i]) return null;
        var bodyLength = data[2] << 8 | data[3];
        if (20 + bodyLength > length) return null;
        IPEndPoint? plain = null;
        for (var offset = 20; offset + 4 <= 20 + bodyLength;)
        {
            var type = data[offset] << 8 | data[offset + 1]; var size = data[offset + 2] << 8 | data[offset + 3];
            var value = offset + 4;
            if (value + size > length) break;
            if ((type == 0x0020 || type == 0x0001) && size >= 8)
            {
                var family = data[value + 1]; var port = data[value + 2] << 8 | data[value + 3];
                var xor = type == 0x0020;
                if (xor) port ^= 0x2112;
                byte[]? address = null;
                if (family == 1) { address = data.AsSpan(value + 4, 4).ToArray(); if (xor) for (var i = 0; i < 4; i++) address[i] ^= data[4 + i]; }
                else if (family == 2 && size >= 20) { address = data.AsSpan(value + 4, 16).ToArray(); if (xor) for (var i = 0; i < 16; i++) address[i] ^= data[4 + i]; }   // magic cookie, then the transaction id
                if (address != null)
                {
                    var found = new IPEndPoint(new IPAddress(address), port);
                    if (xor) return found;
                    plain = found;
                }
            }
            offset = value + (size + 3 & ~3);
        }
        return plain;
    }

    // Asks one server over an existing socket (so that the NAT mapping is the one the other servers see, too).
    static async Task<IPEndPoint?> AskStun(Socket socket, IPEndPoint server, TimeSpan timeout, CancellationToken ct)
    {
        var transaction = new byte[12]; Random.Shared.NextBytes(transaction);
        var request = StunRequest(transaction);
        var buffer = new byte[1024];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        var sent = 0;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (sent++ < 3) await socket.SendToAsync(request, SocketFlags.None, server, deadline.Token);
                using var round = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); round.CancelAfter(TimeSpan.FromMilliseconds(600));
                try
                {
                    var any = socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, round.Token);
                    if (Equals(result.RemoteEndPoint, server) || ((IPEndPoint)result.RemoteEndPoint).Address.Equals(server.Address))
                        if (ParseStun(buffer, result.ReceivedBytes, transaction) is { } mapped) return mapped;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        return null;
    }

    static async Task<IPEndPoint[]> Resolve(string hostPort, AddressFamily family, CancellationToken ct)
    {
        var parts = hostPort.Split(':'); var port = int.Parse(parts[^1], CultureInfo.InvariantCulture); var host = string.Join(":", parts[..^1]);
        try
        {
            if (IPAddress.TryParse(host, out var literal)) return literal.AddressFamily == family ? [new IPEndPoint(literal, port)] : [];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync(host, family, timeout.Token);
            return addresses.Take(1).Select(a => new IPEndPoint(a, port)).ToArray();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { return []; }
    }

    // One mapping answer per server, all from the same local socket.
    sealed record StunResult(string Server, IPEndPoint Mapped);

    static async Task<(List<StunResult> Results, int LocalPort, int Asked)> StunRound(string[] servers, AddressFamily family, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try { socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0)); }
        catch (SocketException) { return ([], 0, 0); }
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var endpoints = (await Task.WhenAll(servers.Select(s => Resolve(s, family, ct)))).SelectMany((e, i) => e.Select(x => (Name: servers[i], Endpoint: x))).ToList();
        var results = new List<StunResult>();
        // one at a time on one socket: replies are matched by transaction id and sender, and the order keeps the mapping test simple
        foreach (var (name, endpoint) in endpoints)
            if (await AskStun(socket, endpoint, timeout, ct) is { } mapped) results.Add(new StunResult(name + " (" + endpoint.Address + ")", mapped));
        return (results, localPort, endpoints.Count);
    }

    // The kind of NAT from the reflexive endpoints that different servers reported for the same local socket.
    public static string ClassifyNat(IReadOnlyList<IPEndPoint> mapped, bool mappedIsLocal)
    {
        if (mapped.Count == 0) return "unknown";
        if (mappedIsLocal) return "none";
        if (mapped.Count < 2) return "unclassified";
        if (mapped.Select(m => m.Address).Distinct().Count() > 1) return "multiexit";   // another address per server: several exits (lines, address pool), not one NAT
        return mapped.Select(m => m.Port).Distinct().Count() == 1 ? "cone" : "symmetric";
    }

    public static async Task<(DiagSection Section, string Nat)> NatAndAddresses(NetDiagOptions options, CancellationToken ct)
    {
        var section = new DiagSection { Title = "公网地址与 NAT 类型" };
        var (v4, localPort, asked) = await StunRound(options.StunServers, AddressFamily.InterNetwork, options.StunTimeout, ct);
        var locals = UsableInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address)).ToList();
        section.Add("STUN 服务器", $"{v4.Count} / {asked} 台应答");
        foreach (var result in v4) section.Add("  " + result.Server, result.Mapped.ToString());
        var nat = "unknown";
        if (v4.Count == 0)
        {
            section.Summary = "没有 STUN 服务器应答（UDP 出站可能被封，或服务器不可达）"; section.Level = DiagLevel.Bad;
            section.Add("公网 IPv4", "未知", DiagLevel.Bad);
        }
        else
        {
            var distinct = v4.Select(r => r.Mapped.Address).Distinct().ToList();
            var publicV4 = v4[0].Mapped;
            var mappedIsLocal = locals.Any(l => l.Equals(publicV4.Address));
            nat = ClassifyNat(v4.Select(r => r.Mapped).ToList(), mappedIsLocal);
            section.Add("公网 IPv4", string.Join("、", distinct.Select(a => a.ToString())) + (Scope(publicV4.Address) == AddressScope.Public ? "" : "  ·  " + ScopeText(Scope(publicV4.Address))),
                Scope(publicV4.Address) == AddressScope.Public ? DiagLevel.Good : DiagLevel.Warn);
            section.Add("本机 UDP 端口", localPort + (v4.All(r => r.Mapped.Port == localPort) ? "，映射后端口不变" : "，映射后端口改变"));
            switch (nat)
            {
                case "none": section.Summary = "公网直连，没有 NAT"; section.Level = DiagLevel.Good; section.Add("NAT 类型", "无（本机直接拥有公网地址）", DiagLevel.Good); break;
                case "cone": section.Summary = "锥形 NAT（端点无关映射），适合打洞直连"; section.Level = DiagLevel.Good; section.Add("NAT 类型", "锥形（不同服务器看到同一个公网端口）", DiagLevel.Good); break;
                case "multiexit": section.Summary = "出口地址随目标变化：多线路负载均衡或运营商地址池，打洞不稳定"; section.Level = DiagLevel.Warn; section.Add("NAT 类型", "不是单一 NAT：不同服务器看到不同的公网地址", DiagLevel.Warn); break;
                case "symmetric": section.Summary = "对称型 NAT（端点相关映射），直连困难，通常要走中继"; section.Level = DiagLevel.Warn; section.Add("NAT 类型", "对称型（不同服务器看到不同的公网端口）", DiagLevel.Warn); break;
                default: section.Summary = "只有一台 STUN 服务器应答，无法判断 NAT 类型"; section.Level = DiagLevel.Warn; section.Add("NAT 类型", "无法判断（需要至少两台不同的服务器应答）", DiagLevel.Warn); break;
            }
            if (Scope(publicV4.Address) is AddressScope.Cgnat or AddressScope.Private) section.Add("多层 NAT", "STUN 看到的地址不是公网地址：上游还有一层 NAT（运营商级 NAT 或光猫拨号），端口映射通常无效", DiagLevel.Warn);
            section.Add("过滤类型", "未检测（完全锥形、受限锥形、端口受限需要支持 RFC 5780 的服务器）");
        }
        // IPv6: reachable without NAT when the reflexive address is one of ours
        var (v6, _, askedV6) = await StunRound(options.StunServersV6, AddressFamily.InterNetworkV6, options.StunTimeout, ct);
        if (v6.Count > 0)
        {
            var mapped = v6[0].Mapped.Address;
            var own = locals.Any(l => l.Equals(mapped));
            section.Add("公网 IPv6", mapped + (own ? "  ·  本机地址，没有 NAT" : "  ·  经过转换"), own ? DiagLevel.Good : DiagLevel.Info);
        }
        else section.Add("公网 IPv6", askedV6 == 0 ? "没有可用的 IPv6 地址或无法解析服务器" : "STUN 服务器没有应答", DiagLevel.Info);
        return (section, nat);
    }

    // ---- P2P ---------------------------------------------------------------------------------------------------------
    // What the above means for connecting two devices directly (the ICE view: host, server-reflexive and relayed candidates).
    public static DiagSection PeerToPeer(string nat, DiagSection addresses, DiagSection natSection, bool upnp)
    {
        var section = new DiagSection { Title = "点对点连接（ICE / STUN）能力" };
        var udp = nat != "unknown";
        var v6 = natSection.Lines.Any(l => l.Label == "公网 IPv6" && l.Level == DiagLevel.Good);
        section.Add("UDP 出站", udp ? "可用（收到了 STUN 应答）" : "未收到应答", udp ? DiagLevel.Good : DiagLevel.Bad);
        section.Add("反射地址（srflx 候选）", udp ? "可获得" : "无法获得", udp ? DiagLevel.Good : DiagLevel.Bad);
        section.Add("IPv6 直连", v6 ? "可用（本机拥有公网 IPv6，无需打洞）" : "不可用", v6 ? DiagLevel.Good : DiagLevel.Info);
        section.Add("UPnP / NAT-PMP", upnp ? "路由器有应答（未实测）" : "未发现或不可用", DiagLevel.Info);
        section.Add("中继（TURN 类）", "不在此检测；直连不通时 ArdUi 自动使用中继");
        (section.Summary, section.Level) = (nat, v6) switch
        {
            ("none", _) => ("直连条件很好：公网地址，无需打洞", DiagLevel.Good),
            (_, true) => ("可经 IPv6 直连；IPv4 " + (nat == "cone" ? "也适合打洞" : "可能要走中继"), DiagLevel.Good),
            ("cone", _) => ("适合打洞；对端也是锥形或公网时可直连", DiagLevel.Good),
            ("symmetric", _) => ("对称型 NAT：与另一端也是对称型时无法直连，要走中继", DiagLevel.Warn),
            ("unclassified", _) => ("无法确定 NAT 类型，直连是否可行取决于对端", DiagLevel.Warn),
            _ => ("没有 UDP 出站，无法直连，只能走中继", DiagLevel.Bad),
        };
        return section;
    }

    // ---- UPnP / NAT-PMP ---------------------------------------------------------------------------------------------
    static HttpClient Http() => new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };

    public static Dictionary<string, string> ParseSsdp(string response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in response.Split('\n').Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    static async Task<List<(Uri Location, string Server, IPAddress Local)>> Discover(TimeSpan timeout, CancellationToken ct)
    {
        var found = new Dictionary<string, (Uri, string, IPAddress)>();
        var targets = new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2" };
        var locals = UsableInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address)).Where(a => a.AddressFamily == AddressFamily.InterNetwork && Scope(a) != AddressScope.LinkLocal).Distinct().ToList();
        await Task.WhenAll(locals.Select(async local =>
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(local, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var target in targets)
                {
                    var message = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {target}\r\n\r\n");
                    await socket.SendToAsync(message, SocketFlags.None, group, ct);
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
                var buffer = new byte[4096];
                while (!deadline.IsCancellationRequested)
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                    var headers = ParseSsdp(Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes));
                    if (headers.TryGetValue("LOCATION", out var location) && Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
                        lock (found) found.TryAdd(uri.ToString(), (uri, headers.GetValueOrDefault("SERVER", ""), local));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }));
        return found.Values.ToList();
    }

    // Reads the device description: names, and the WAN connection service that port mappings go through.
    public static void ParseDescription(UpnpGateway gateway, string xml)
    {
        var document = XDocument.Parse(xml);
        string Text(XElement scope, string name) => scope.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
        var root = document.Root!;
        var baseUrl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        var origin = Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) ? b : gateway.Location;
        var devices = root.Descendants().Where(e => e.Name.LocalName == "device").ToList();
        var top = devices.FirstOrDefault();
        if (top != null) { gateway.FriendlyName = Text(top, "friendlyName"); gateway.Manufacturer = Text(top, "manufacturer"); gateway.Model = Text(top, "modelName"); gateway.ModelNumber = Text(top, "modelNumber"); gateway.Udn = Text(top, "UDN"); }
        foreach (var service in root.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = Text(service, "serviceType");
            if (!type.Contains("WANIPConnection", StringComparison.Ordinal) && !type.Contains("WANPPPConnection", StringComparison.Ordinal)) continue;
            if (gateway.Control != null && !type.Contains("WANIPConnection:2", StringComparison.Ordinal)) continue;   // prefer the newer one
            if (Uri.TryCreate(origin, Text(service, "controlURL"), out var control)) { gateway.Control = control; gateway.ServiceType = type; }
        }
    }

    public sealed class UpnpError(string code, string description) : Exception($"{code} {description}".Trim()) { public string Code { get; } = code; }

    static async Task<Dictionary<string, string>> Soap(HttpClient http, UpnpGateway gateway, string action, (string Name, string Value)[] arguments, CancellationToken ct)
    {
        var body = new StringBuilder($"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{gateway.ServiceType}\">");
        foreach (var (name, value) in arguments) body.Append('<').Append(name).Append('>').Append(System.Security.SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        body.Append($"</u:{action}></s:Body></s:Envelope>");
        using var request = new HttpRequestMessage(HttpMethod.Post, gateway.Control) { Content = new StringContent(body.ToString(), new UTF8Encoding(false), "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{gateway.ServiceType}#{action}\"");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        XDocument document;
        try { document = XDocument.Parse(text); } catch (System.Xml.XmlException) { throw new UpnpError("", "路由器返回了无法解析的内容"); }
        var leaves = document.Descendants().Where(e => !e.HasElements).GroupBy(e => e.Name.LocalName).ToDictionary(g => g.Key, g => g.First().Value.Trim());
        if (!response.IsSuccessStatusCode) throw new UpnpError(leaves.GetValueOrDefault("errorCode", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)), leaves.GetValueOrDefault("errorDescription", ""));
        return leaves;
    }

    static string ErrorText(Exception ex) => ex switch
    {
        UpnpError e => e.Code switch { "718" => "该端口已被占用（718）", "725" => "路由器只接受永久租约（725）", "402" or "501" => "路由器拒绝了请求（" + e.Code + "）", _ => e.Message },
        TaskCanceledException => "超时",
        _ => ex.Message,
    };

    public static async Task<(DiagSection Section, List<UpnpGateway> Gateways)> Upnp(NetDiagOptions options, CancellationToken ct)
    {
        var section = new DiagSection { Title = "UPnP 与端口映射" };
        var gateways = new List<UpnpGateway>();
        var locations = new List<(Uri Location, string Server, IPAddress? Local)>();
        if (options.UseSsdp) locations.AddRange((await Discover(options.SsdpTimeout, ct)).Select(d => (d.Location, d.Server, (IPAddress?)d.Local)));
        locations.AddRange(options.ExtraGatewayLocations.Select(l => (l, "", (IPAddress?)null)));
        if (locations.Count == 0)
        {
            section.Summary = "没有发现支持 UPnP 的路由器"; section.Level = DiagLevel.Warn;
            section.Add("UPnP", "SSDP 没有应答：路由器未开启 UPnP，或网络隔离（访客网络、公司网络），或防火墙拦截了组播", DiagLevel.Warn);
        }
        using var http = Http();
        foreach (var (location, server, local) in locations)
        {
            var gateway = new UpnpGateway { Location = location, Server = server, LocalAddress = local };
            gateways.Add(gateway);
            section.Add("网关", location.Host + ":" + location.Port);
            try { ParseDescription(gateway, await http.GetStringAsync(location, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { section.Add("  描述", "读取失败：" + ErrorText(ex), DiagLevel.Warn); continue; }
            section.Add("  设备", string.Join(" ", new[] { gateway.Manufacturer, gateway.Model, gateway.ModelNumber }.Where(s => s.Length != 0)) + (gateway.FriendlyName.Length != 0 ? "（" + gateway.FriendlyName + "）" : ""));
            if (server.Length != 0) section.Add("  SERVER", server);
            if (gateway.Control == null) { section.Add("  WAN 连接服务", "设备没有提供 WANIPConnection / WANPPPConnection", DiagLevel.Warn); continue; }
            section.Add("  WAN 连接服务", gateway.ServiceType.Split(':').Length > 3 ? string.Join(":", gateway.ServiceType.Split(':')[^2..]) : gateway.ServiceType);
            try
            {
                var external = (await Soap(http, gateway, "GetExternalIPAddress", [], ct)).GetValueOrDefault("NewExternalIPAddress", "");
                if (IPAddress.TryParse(external, out var ip))
                {
                    var scope = Scope(ip);
                    section.Add("  路由器外网地址", external + (scope == AddressScope.Public ? "" : "  ·  " + ScopeText(scope)), scope == AddressScope.Public ? DiagLevel.Good : DiagLevel.Warn);
                    if (scope != AddressScope.Public) section.Add("  双层 NAT", "路由器自己的外网地址不是公网地址：上游还有 NAT，UPnP 映射对外无效", DiagLevel.Warn);
                }
                else section.Add("  路由器外网地址", string.IsNullOrEmpty(external) ? "路由器没有返回（未拨号或未连接）" : external, DiagLevel.Warn);
            }
            catch (Exception ex) when (ex is UpnpError or HttpRequestException or TaskCanceledException) { section.Add("  路由器外网地址", "读取失败：" + ErrorText(ex), DiagLevel.Warn); }
            try
            {
                var status = await Soap(http, gateway, "GetStatusInfo", [], ct);
                section.Add("  连接状态", status.GetValueOrDefault("NewConnectionStatus", "?") + (status.TryGetValue("NewUptime", out var uptime) && long.TryParse(uptime, out var seconds) ? "，已连接 " + TimeSpan.FromSeconds(seconds).ToString(@"d\.hh\:mm\:ss") : ""));
            }
            catch (Exception ex) when (ex is UpnpError or HttpRequestException or TaskCanceledException) { }
            // the mappings that are already there (other programs, earlier tests)
            var entries = new List<string>();
            try
            {
                for (var index = 0; index < 40; index++)
                {
                    var entry = await Soap(http, gateway, "GetGenericPortMappingEntry", [("NewPortMappingIndex", index.ToString(CultureInfo.InvariantCulture))], ct);
                    entries.Add($"{entry.GetValueOrDefault("NewProtocol", "?")} {entry.GetValueOrDefault("NewExternalPort", "?")} → {entry.GetValueOrDefault("NewInternalClient", "?")}:{entry.GetValueOrDefault("NewInternalPort", "?")}  {entry.GetValueOrDefault("NewPortMappingDescription", "")}".TrimEnd());
                }
            }
            catch (Exception ex) when (ex is UpnpError or HttpRequestException or TaskCanceledException)
            {
                if (ex is UpnpError { Code: not "713" } && entries.Count == 0) section.Add("  现有映射", "无法列出：" + ErrorText(ex));
            }
            if (entries.Count != 0) { section.Add("  现有映射", entries.Count + " 条"); foreach (var e in entries.Take(12)) section.Add("    ", e); }
            else if (!section.Lines.Any(l => l.Label == "  现有映射")) section.Add("  现有映射", "无");
        }
        var usable = gateways.Any(g => g.Control != null);
        if (locations.Count != 0)
        {
            section.Summary = usable ? "路由器应答了 UPnP（协议层正常；端口是否真的可达尚未实测，点“测试端口映射”用 M 验证）" : "发现了 UPnP 设备，但没有可用的端口映射服务";
            section.Level = usable ? DiagLevel.Info : DiagLevel.Warn;
        }
        return (section, gateways);
    }

    // NAT-PMP / PCP's older sibling: ask each gateway for its external address on UDP 5351.
    public static async Task<(DiagSection Section, List<(IPAddress Gateway, IPAddress External)> Found)> NatPmp(CancellationToken ct)
    {
        var section = new DiagSection { Title = "NAT-PMP" };
        var found = new List<(IPAddress, IPAddress)>();
        var gateways = UsableInterfaces().SelectMany(n => n.GetIPProperties().GatewayAddresses.Select(g => g.Address)).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).Distinct().ToList();
        foreach (var gateway in gateways)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                await socket.SendToAsync(new byte[] { 0, 0 }, SocketFlags.None, new IPEndPoint(gateway, 5351), ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMilliseconds(900));
                var buffer = new byte[64];
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                if (result.ReceivedBytes >= 12 && buffer[1] == 128)
                {
                    var code = buffer[2] << 8 | buffer[3];
                    var external = new IPAddress(buffer.AsSpan(8, 4));
                    if (code == 0) found.Add((gateway, external));
                    section.Add("网关 " + gateway, code == 0 ? "路由器应答，外网地址 " + external + "（只是协议应答，端口是否真的可达尚未实测）" : "应答但拒绝（结果码 " + code + "）", code == 0 ? DiagLevel.Info : DiagLevel.Warn);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { section.Add("网关 " + gateway, "没有应答（不支持或未开启）"); }
        }
        section.Summary = gateways.Count == 0 ? "没有找到网关" : found.Count != 0 ? "路由器应答 NAT-PMP（协议层正常；点“测试端口映射”用 M 实测）" : "没有网关支持 NAT-PMP";
        section.Level = DiagLevel.Info;
        return (section, found);
    }

    // Maps a UDP port through NAT-PMP for a minute, lets M probe the mapped port from the outside, deletes the mapping and probes again.
    public static async Task<DiagSection> TestNatPmpMapping(IPAddress gateway, IPAddress external, CancellationToken ct, Func<CancellationToken, Task<ProbeTicket?>>? ticketSource = null, string fallbackHost = "", int gatewayPort = 5351)
    {
        var section = new DiagSection { Title = "NAT-PMP 端口映射测试" };
        var port = Random.Shared.Next(40000, 60000);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = new IPEndPoint(gateway, gatewayPort);
        async Task<(int Code, int External)> Map(int internalPort, int suggested, int lifetime)
        {
            var request = new byte[12]; request[1] = 1;   // version 0, opcode 1: map UDP
            request[4] = (byte)(internalPort >> 8); request[5] = (byte)internalPort; request[6] = (byte)(suggested >> 8); request[7] = (byte)suggested;
            request[8] = (byte)(lifetime >> 24); request[9] = (byte)(lifetime >> 16); request[10] = (byte)(lifetime >> 8); request[11] = (byte)lifetime;
            var buffer = new byte[32];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await socket.SendToAsync(request, SocketFlags.None, endpoint, ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(900);
                try
                {
                    var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                    if (result.ReceivedBytes >= 16 && buffer[1] == 129) return (buffer[2] << 8 | buffer[3], buffer[10] << 8 | buffer[11]);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            return (-1, 0);
        }
        var added = false; var externalPort = port; bool? reached = null;
        try
        {
            var (code, mapped) = await Map(port, port, 60);
            if (code != 0) { section.Add("添加映射", code < 0 ? "路由器没有应答" : "路由器拒绝（结果码 " + code + "）", DiagLevel.Bad); section.Summary = "NAT-PMP 端口映射不可用"; section.Level = DiagLevel.Bad; return section; }
            added = true; externalPort = mapped;
            section.Add("添加映射", $"UDP 外部 {external}:{mapped} → 本机 {port}（租约 60 秒）", DiagLevel.Good);
            section.Summary = "路由器接受了 NAT-PMP 映射"; section.Level = DiagLevel.Good;
            if (ticketSource != null)
            {
                var firewall = WinFirewall.Inspect(); section.Add("本机防火墙", firewall.Text, firewall.MayBlock ? DiagLevel.Warn : DiagLevel.Info);
                var lines = await NetProbe.VerifyUdpMapping(ticketSource, fallbackHost, port, true, ct, external, mapped, "NAT-PMP 映射");
                foreach (var line in lines) section.Lines.Add(line);
                reached = lines.Any(l => l.Level == DiagLevel.Good);
                var bad = lines.Any(l => l.Level == DiagLevel.Bad);
                section.Summary = reached == true ? "NAT-PMP 映射有效：M 从外部发来的包穿过了路由器" : bad && firewall.MayBlock ? "路由器接受了映射，但 M 的包没有到达：本机防火墙可能拦下了入站，结果不确定（放行 ArdUi 后再测）" : bad ? "路由器接受了映射，但 M 从外部发来的包没有到达（路由器没有真正转发，或上游还有一层 NAT）" : "路由器接受了映射，但 M 没能实测（见下）";
                section.Level = reached == true ? DiagLevel.Good : bad ? (firewall.MayBlock ? DiagLevel.Warn : DiagLevel.Bad) : DiagLevel.Warn;
            }
        }
        finally
        {
            if (added)
            {
                var (code, _) = await Map(port, 0, 0);
                section.Add("删除映射", code == 0 ? "已删除" : "删除没有得到确认（租约 60 秒后自动到期）", code == 0 ? DiagLevel.Good : DiagLevel.Warn);
                if (ticketSource != null && reached == true)
                {
                    try { foreach (var line in await NetProbe.VerifyUdpMapping(ticketSource, fallbackHost, port, false, CancellationToken.None, external, externalPort, "NAT-PMP 映射")) section.Lines.Add(line); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException) { }
                }
            }
        }
        return section;
    }

    // ---- several exits? ---------------------------------------------------------------------------------------------
    static readonly System.Text.RegularExpressions.Regex Ipv4Text = new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The public address an HTTP "what is my IP" page reports (TCP leaves by its own route, which can differ from UDP's).
    public static IPAddress? ParseEcho(string body)
    {
        foreach (System.Text.RegularExpressions.Match match in Ipv4Text.Matches(body.Length > 400 ? body[..400] : body))
            if (IPAddress.TryParse(match.Value, out var ip) && Scope(ip) == AddressScope.Public) return ip;
        return null;
    }

    public static async Task<DiagSection> Egress(NetDiagOptions options, CancellationToken ct)
    {
        var section = new DiagSection { Title = "公网出口（是否有多个出口 / 大内网）" };
        var seen = new Dictionary<IPAddress, int>();           // public IPv4 -> how many probes saw it
        var tcpSeen = new Dictionary<IPAddress, int>();
        var perNic = new List<(string Name, IPAddress? Public)>();
        void Count(Dictionary<IPAddress, int> map, IPAddress ip) { lock (map) map[ip] = map.GetValueOrDefault(ip) + 1; }

        // UDP: a new source port every round
        var udp = Enumerable.Range(0, Math.Max(1, options.EgressRounds)).Select(async _ =>
        {
            var (results, _, _) = await StunRound(options.StunServers.Take(3).ToArray(), AddressFamily.InterNetwork, options.StunTimeout, ct);
            foreach (var r in results) Count(seen, r.Mapped.Address);
        });
        // TCP: HTTP echo pages
        using var http = Http();
        var echoes = options.EchoUrls.Select(async url =>
        {
            try { if (ParseEcho(await http.GetStringAsync(url, ct)) is { } ip) { Count(tcpSeen, ip); return (url, ip); } }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
            return (url, (IPAddress?)null);
        }).ToList();
        // per network card: the same question asked from each card's own address
        var cards = UsableInterfaces().Select(async nic =>
        {
            var props = nic.GetIPProperties();
            var address = props.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && Scope(a) != AddressScope.LinkLocal && Scope(a) != AddressScope.Loopback);
            if (address == null || !props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) return;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(address, 0));
                foreach (var name in options.StunServers.Take(3))
                    foreach (var endpoint in await Resolve(name, AddressFamily.InterNetwork, ct))
                        if (await AskStun(socket, endpoint, options.StunTimeout, ct) is { } mapped) { lock (perNic) perNic.Add((nic.Name + "（" + address + "）", mapped.Address)); return; }
                lock (perNic) perNic.Add((nic.Name + "（" + address + "）", null));
            }
            catch (SocketException) { lock (perNic) perNic.Add((nic.Name + "（" + address + "）", null)); }
        });
        await Task.WhenAll(udp.Concat(cards));
        var echoResults = await Task.WhenAll(echoes);

        section.Add("UDP / STUN", seen.Count == 0 ? "没有应答" : string.Join("；", seen.OrderByDescending(p => p.Value).Select(p => $"{p.Key}（{p.Value} 次）")), seen.Count == 0 ? DiagLevel.Bad : DiagLevel.Info);
        foreach (var (url, ip) in echoResults) section.Add("TCP / " + new Uri(url).Host, ip?.ToString() ?? "无应答或无法解析", ip == null ? DiagLevel.Info : DiagLevel.Info);
        foreach (var (name, ip) in perNic.OrderBy(p => p.Name)) section.Add("网卡 " + name, ip?.ToString() ?? "无应答");
        var all = seen.Keys.Concat(tcpSeen.Keys).Concat(perNic.Where(p => p.Public != null).Select(p => p.Public!)).Distinct().ToList();
        var indicators = new List<string>();
        if (all.Count > 1) indicators.Add($"同一台电脑被看到 {all.Count} 个不同的公网地址（多线路出口、负载均衡或运营商的出口地址池）");
        if (seen.Count > 0 && tcpSeen.Count > 0 && !seen.Keys.Intersect(tcpSeen.Keys).Any()) indicators.Add("UDP 与 TCP 走的出口不同（策略路由或分流）");
        if (perNic.Count(p => p.Public != null) > 1 && perNic.Where(p => p.Public != null).Select(p => p.Public).Distinct().Count() > 1) indicators.Add("不同网卡的出口不同（多网卡同时在线）");
        if (all.Any(a => Scope(a) is AddressScope.Cgnat or AddressScope.Private)) indicators.Add("STUN 看到的不是公网地址（外面还有一层 NAT）");
        if (all.Count == 0) { section.Summary = "无法得知公网出口（STUN 与 HTTP 都没有结果）"; section.Level = DiagLevel.Bad; }
        else if (indicators.Count == 0) { section.Summary = "单一公网出口 " + all[0]; section.Level = DiagLevel.Good; }
        else { section.Summary = "可能有多个公网出口，或处在大型内网中"; section.Level = DiagLevel.Warn; foreach (var i in indicators) section.Add("判断", i, DiagLevel.Warn); }
        return section;
    }

    // ---- the path out -------------------------------------------------------------------------------------------------
    public sealed record Hop(int Ttl, IPAddress? Address, long RoundTrip, bool Final);

    // How many private (NAT / intranet) hops lie before the first public one.
    public static int PrivateHopsBeforePublic(IReadOnlyList<Hop> hops)
    {
        var count = 0;
        foreach (var hop in hops)
        {
            if (hop.Address == null) continue;
            if (Scope(hop.Address) == AddressScope.Public) break;
            count++;
        }
        return count;
    }

    public static async Task<DiagSection> Trace(NetDiagOptions options, CancellationToken ct)
    {
        var section = new DiagSection { Title = "出口路径（逐跳）" };
        var hops = new List<Hop>();
        try
        {
            using var ping = new Ping();
            var payload = new byte[32];
            for (var ttl = 1; ttl <= 12 && !ct.IsCancellationRequested; ttl++)
            {
                var reply = await ping.SendPingAsync(IPAddress.Parse(options.TraceTarget), 1200, payload, new PingOptions(ttl, true));
                var final = reply.Status == IPStatus.Success;
                hops.Add(new Hop(ttl, reply.Status is IPStatus.TtlExpired or IPStatus.Success ? reply.Address : null, reply.Status is IPStatus.TtlExpired or IPStatus.Success ? reply.RoundtripTime : -1, final));
                if (final) break;
            }
        }
        catch (Exception ex) when (ex is PingException or FormatException or InvalidOperationException) { section.Summary = "无法发送 ICMP（被防火墙或策略禁止）"; section.Level = DiagLevel.Info; return section; }
        foreach (var hop in hops)
        {
            if (hop.Address == null) { section.Add($"第 {hop.Ttl} 跳", "* 无应答"); continue; }
            section.Add($"第 {hop.Ttl} 跳", $"{hop.Address}  ·  {ScopeText(Scope(hop.Address))}" + (hop.RoundTrip > 0 ? $"  ·  {hop.RoundTrip} ms" : "") + (hop.Final ? "（目标）" : ""));
        }
        var privateHops = PrivateHopsBeforePublic(hops);
        var reached = hops.Any(h => h.Address != null && Scope(h.Address) == AddressScope.Public);
        if (!reached) { section.Summary = hops.Any(h => h.Final) ? "已到达目标" : "没有任何公网跳应答（ICMP 可能被过滤）"; section.Level = DiagLevel.Info; }
        else if (privateHops <= 1) { section.Summary = "到公网只经过 " + privateHops + " 层私网（普通家庭或办公网络）"; section.Level = DiagLevel.Good; }
        else { section.Summary = $"到达公网前经过 {privateHops} 个私网跳：多层 NAT 或大型内网"; section.Level = DiagLevel.Warn; }
        return section;
    }

    // ---- reachability and the rest of the state -----------------------------------------------------------------------
    static async Task<string> PingText(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping(); var times = new List<long>(); var lost = 0;
            for (var i = 0; i < 4; i++) { var r = await ping.SendPingAsync(host, 1000); if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++; }
            return times.Count == 0 ? "无应答（ICMP 可能被过滤）" : $"{times.Min()}–{times.Max()} ms，丢包 {lost}/4";
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException) { return "无法发送 ICMP"; }
    }

    static async Task<(bool Ok, long Millis, string Detail)> TcpConnect(string host, int port, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(host, port, deadline.Token);
            return (true, watch.ElapsedMilliseconds, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return (false, watch.ElapsedMilliseconds, ex is OperationCanceledException ? "超时" : ((SocketException)ex).SocketErrorCode.ToString()); }
    }

    public static async Task<DiagSection> Connectivity(NetDiagOptions options, CancellationToken ct)
    {
        var section = new DiagSection { Title = "连通性与网络状态" };
        var bad = 0;
        var gateways = UsableInterfaces().SelectMany(n => n.GetIPProperties().GatewayAddresses.Select(g => g.Address)).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).Distinct().ToList();
        if (gateways.Count > 1) section.Add("默认网关", $"{gateways.Count} 个：{string.Join("、", gateways)}（多网卡同时联网，出口取决于路由优先级）", DiagLevel.Warn);
        foreach (var gateway in gateways) section.Add("Ping 网关 " + gateway, await PingText(gateway.ToString(), ct));
        foreach (var target in options.PingTargets) section.Add("Ping " + target, await PingText(target, ct));

        // DNS
        foreach (var name in options.DnsNames)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var addresses = await Dns.GetHostAddressesAsync(name, deadline.Token);
                section.Add("DNS " + name, $"{watch.ElapsedMilliseconds} ms  ·  {string.Join("、", addresses.Take(3))}");
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { bad++; section.Add("DNS " + name, "解析失败", DiagLevel.Warn); }
        }

        // ArdUi's own servers
        if (options.DirectoryUrl != null && Uri.TryCreate(options.DirectoryUrl, UriKind.Absolute, out var directory))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var http = Http();
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(directory, "api/health"));
                request.Headers.TryAddWithoutValidation("X-ArdUi-Client", Program.WireVersion);
                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                section.Add("ArdUi 目录服务器", $"{(int)response.StatusCode}，{watch.ElapsedMilliseconds} ms" + (body.Contains("minClient", StringComparison.Ordinal) ? "  ·  " + (System.Text.RegularExpressions.Regex.Match(body, "\"version\":\\s*\"([^\"]+)\"") is { Success: true } m ? "服务端 " + m.Groups[1].Value : "") : ""), response.IsSuccessStatusCode ? DiagLevel.Good : DiagLevel.Warn);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { bad++; section.Add("ArdUi 目录服务器", "无法连接：" + (ex.InnerException?.Message ?? ex.Message), DiagLevel.Bad); }
        }
        if (options.RelayHostPort != null)
        {
            var parts = options.RelayHostPort.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var port))
            {
                var (ok, millis, detail) = await TcpConnect(parts[0], port, ct);
                if (!ok) bad++;
                section.Add("ArdUi 中继 / M（TCP " + options.RelayHostPort + "）", ok ? $"{millis} ms  ·  {detail}" : "无法连接：" + detail, ok ? DiagLevel.Good : DiagLevel.Bad);
            }
        }

        // IPv6 and proxy
        var v6 = await TcpConnect("2606:4700:4700::1111", 443, ct);
        section.Add("IPv6 访问外网", v6.Ok ? $"可用（{v6.Millis} ms）" : "不可用（" + v6.Detail + "）", v6.Ok ? DiagLevel.Good : DiagLevel.Info);
        try
        {
            var proxy = WebRequest.GetSystemWebProxy().GetProxy(new Uri("https://www.baidu.com/"));
            var envProxy = new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY" }.Select(n => Environment.GetEnvironmentVariable(n)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var text = proxy != null && proxy.Host != "www.baidu.com" ? proxy.Host + ":" + proxy.Port : envProxy ?? "";
            section.Add("系统代理", text.Length != 0 ? text + "（会影响 HTTP 类检测，STUN 的 UDP 不经过代理）" : "无", text.Length != 0 ? DiagLevel.Warn : DiagLevel.Info);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException) { }
        section.Summary = bad == 0 ? "基础连通正常" : $"{bad} 项异常";
        section.Level = bad == 0 ? DiagLevel.Good : DiagLevel.Warn;
        return section;
    }

    // ---- the whole thing --------------------------------------------------------------------------------------------
    public static async Task<NetDiagReport> Run(NetDiagOptions? options = null, CancellationToken ct = default)
    {
        options ??= new NetDiagOptions();
        var report = new NetDiagReport();
        var addresses = LocalAddresses();
        var natTask = NatAndAddresses(options, ct);
        var egressTask = Egress(options, ct);
        var upnpTask = Upnp(options, ct);
        var pmpTask = options.UseNatPmp ? NatPmp(ct) : Task.FromResult<(DiagSection Section, List<(IPAddress Gateway, IPAddress External)> Found)>((null!, new List<(IPAddress Gateway, IPAddress External)>()));
        var traceTask = options.UseTrace ? Trace(options, ct) : Task.FromResult<DiagSection?>(null)!;
        var connectTask = Connectivity(options, ct);
        var probeTask = RunProbe(options, ct);
        var (natSection, nat) = await natTask;
        var (upnpSection, gateways) = await upnpTask;
        var (pmp, pmpFound) = await pmpTask is { } scan ? (scan.Section, scan.Found) : (null, new List<(IPAddress, IPAddress)>()); var egress = await egressTask; var trace = await traceTask; var connectivity = await connectTask;
        var (probeSection, findings) = await probeTask;
        report.Nat = findings != null && findings.Nat != "unknown" ? findings.Nat : nat; report.Probe = findings; report.Gateways.AddRange(gateways);
        report.UpnpMappingAvailable = gateways.Any(g => g.Control != null);
        report.NatPmp.AddRange(pmpFound);
        var upnpUsable = report.UpnpMappingAvailable || pmpFound.Count != 0;
        // The router's protocol answer proves nothing: with M taking part, really test the mappings (added for a minute, probed from outside, removed).
        var mappingSections = new List<DiagSection>();
        if (options.Probe != null && options.TestMappings && findings != null)
        {
            var pmpTest = pmpFound.Count != 0 ? TestNatPmpMapping(pmpFound[0].Gateway, pmpFound[0].External, ct, options.Probe, options.ProbeFallbackHost) : Task.FromResult<DiagSection?>(null)!;
            var gatewayTest = gateways.FirstOrDefault(g => g.Control != null) is { } router ? TestPortMapping(router, ct, options.Probe, options.ProbeFallbackHost) : Task.FromResult<DiagSection?>(null)!;
            var pmpResult = await pmpTest; var upnpResult = await gatewayTest;
            bool? Verdict(DiagSection? t) => t == null ? null : t.Level == DiagLevel.Good ? true : t.Level == DiagLevel.Bad ? false : null;
            report.NatPmpVerified = Verdict(pmpResult); report.UpnpVerified = Verdict(upnpResult);
            if (upnpResult != null) mappingSections.Add(upnpResult);
            if (pmpResult != null) mappingSections.Add(pmpResult);
            if (report.UpnpVerified != null) { upnpSection.Summary = report.UpnpVerified == true ? "UPnP 端口映射实测有效（M 从外部收到了包）" : "路由器接受 UPnP 映射，但实测无效（M 从外部的包没有到达，见下方测试）"; upnpSection.Level = report.UpnpVerified == true ? DiagLevel.Good : DiagLevel.Warn; }
            if (report.NatPmpVerified != null && pmp != null) { pmp.Summary = report.NatPmpVerified == true ? "NAT-PMP 端口映射实测有效（M 从外部收到了包）" : "路由器接受 NAT-PMP 映射，但实测无效（M 从外部的包没有到达，见下方测试）"; pmp.Level = report.NatPmpVerified == true ? DiagLevel.Good : DiagLevel.Warn; }
        }
        bool? mappingVerified = report.UpnpVerified == true || report.NatPmpVerified == true ? true : (report.UpnpVerified == false || report.NatPmpVerified == false) && report.UpnpVerified != true && report.NatPmpVerified != true ? false : null;
        report.Sections.Add(Verdict(report, addresses, natSection, egress, connectivity, upnpUsable, mappingVerified));
        report.Sections.AddRange([addresses, egress, natSection]);
        if (probeSection != null) report.Sections.Add(probeSection);
        report.Sections.Add(PeerToPeer(report.Nat, addresses, natSection, upnpUsable));
        if (trace != null) report.Sections.Add(trace);
        report.Sections.Add(connectivity);
        report.Sections.Add(upnpSection);
        if (pmp != null) report.Sections.Add(pmp);
        report.Sections.AddRange(mappingSections);
        return report;
    }

    static async Task<(DiagSection? Section, NetProbe.Findings? Findings)> RunProbe(NetDiagOptions options, CancellationToken ct)
    {
        if (options.Probe == null) return (null, null);
        try
        {
            var ticket = await options.Probe(ct);
            if (ticket == null)
            {
                var skipped = new DiagSection { Title = "M 辅助验证（实际收发数据）", Summary = "没有取得 M 的探测令牌（设备尚未注册或没有联网）", Level = DiagLevel.Warn };
                return (skipped, null);
            }
            var (section, findings) = await NetProbe.Verify(ticket, options.ProbeFallbackHost, ct);
            return (section, findings);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or SocketException or FormatException)
        {
            return (new DiagSection { Title = "M 辅助验证（实际收发数据）", Summary = "无法取得 M 的探测令牌：" + ex.Message, Level = DiagLevel.Warn }, null);
        }
    }

    // The answer to "can two devices connect directly?", from everything above. M's measurements take precedence over public STUN servers.
    public static DiagSection Verdict(NetDiagReport report, DiagSection addresses, DiagSection natSection, DiagSection egress, DiagSection connectivity, bool upnpUsable, bool? mappingVerified = null)
    {
        var section = new DiagSection { Title = "P2P 直连可用性结论" };
        var m = report.Probe; var nat = report.Nat;
        var hasUdp = nat != "unknown";
        var v6 = natSection.Lines.Any(l => l.Label == "公网 IPv6" && l.Level == DiagLevel.Good);
        var multiExit = egress.Level == DiagLevel.Warn;
        var relayOk = connectivity.Lines.Any(l => l.Label.StartsWith("ArdUi 中继", StringComparison.Ordinal) && l.Level == DiagLevel.Good);
        string source = m != null && m.Nat != "unknown" ? "M 实测" : "公共 STUN 估计";
        var nature = nat switch
        {
            "none" => "公网直连，没有 NAT", "cone" => "锥形 NAT" + (m?.PortRestricted == true ? "（端口受限）" : ""), "symmetric" => "对称型 NAT", "multiexit" => "出口地址随连接变化（多线路 / 地址池）", "unclassified" => "NAT 类型无法确定", _ => "UDP 不通",
        };
        section.Add("NAT 类型", nature + "（" + source + "）", nat is "none" or "cone" ? DiagLevel.Good : nat is "symmetric" or "multiexit" ? DiagLevel.Warn : DiagLevel.Bad);
        if (m != null && m.Exits.Count > 1) section.Add("UDP 出口", string.Join("、", m.Exits), DiagLevel.Warn);
        if (m?.DirectExposure == true) section.Add("入站", "未映射的端口也能从外部打进来：本机直接暴露在公网上", DiagLevel.Warn);
        if (m != null && m.Loss >= 0) section.Add("到 M 的链路", $"丢包 {m.Loss:0.#}%，延迟 {m.Rtt / 1000:0} ms" + (m.PathMtu > 0 ? $"，路径 MTU 约 {m.PathMtu}" : ""), m.Loss <= 1 ? DiagLevel.Good : DiagLevel.Warn);
        section.Add("IPv6 直连", v6 ? "可用" : "不可用", v6 ? DiagLevel.Good : DiagLevel.Info);
        if (mappingVerified == true) section.Add("端口映射（UPnP / NAT-PMP）", "实测有效：M 从外部发来的包穿过了路由器映射", DiagLevel.Good);
        else if (mappingVerified == false) section.Add("端口映射（UPnP / NAT-PMP）", "路由器接受映射，但实测无效：M 从外部发来的包没有到达（上游还有运营商级 NAT 或防火墙）", DiagLevel.Warn);
        else section.Add("端口映射（UPnP / NAT-PMP）", upnpUsable ? "路由器有应答，但还没有实测（点“测试端口映射”，由 M 从外部验证）" : "没有", DiagLevel.Info);
        section.Add("公网出口", multiExit ? "检测到多个出口或多层内网，不同连接可能从不同地址出去，打洞结果不稳定" : "单一出口", multiExit ? DiagLevel.Warn : DiagLevel.Info);
        section.Add("中继兜底", relayOk ? "ArdUi 的中继可达，直连失败时自动使用" : "未确认中继可达", relayOk ? DiagLevel.Good : DiagLevel.Warn);
        (section.Summary, section.Level) = (hasUdp, nat, v6) switch
        {
            (false, _, _) => ("UDP 出站不通：无法直连，只能依靠中继（如果中继也不通则无法连接）", DiagLevel.Bad),
            (_, "none", _) => ("直连条件很好：本机有公网地址，对端可以直接连入", DiagLevel.Good),
            (_, _, true) => ("可经 IPv6 直连（对端也有 IPv6 时无需打洞）；IPv4 " + (nat == "cone" ? "也适合打洞" : "可能要走中继"), DiagLevel.Good),
            (_, "cone", _) when !multiExit => ("适合打洞：对端是公网、锥形 NAT 时通常可以直连", DiagLevel.Good),
            (_, "cone", _) => ("可以打洞，但出口不稳定，直连可能时好时坏", DiagLevel.Warn),
            (_, "multiexit", _) => ("出口不止一个：对不同目标看到不同的公网地址，打洞不稳定，通常要走中继；端口映射要映射到实际出口那条线路才有效", DiagLevel.Warn),
            (_, "symmetric", _) when mappingVerified == true => ("对称型 NAT，但端口映射实测有效：用映射的端口可以被直连", DiagLevel.Good),
            (_, "symmetric", _) when upnpUsable && mappingVerified != false => ("对称型 NAT，但路由器有应答端口映射：实测成功后可开放端口直连", DiagLevel.Warn),
            (_, "symmetric", _) => ("对称型 NAT：与另一端也是对称型时无法直连，通常走中继", DiagLevel.Warn),
            _ => ("无法确定，直连是否可行取决于对端", DiagLevel.Warn),
        };
        if (nat == "symmetric" && !upnpUsable && !v6) section.Add("建议", "在路由器上开启 UPnP，或手动转发一个 UDP 端口；有 IPv6 的话优先用 IPv6；在手机热点、校园网、公司网络下这是常见情况，走中继是正常的", DiagLevel.Info);
        if (multiExit) section.Add("建议", "出口地址会变：避免同时开 VPN 或多线路负载均衡；大内网（校园、企业、运营商级 NAT）里直连通常要靠中继", DiagLevel.Info);
        if (m?.PathMtu is > 0 and < 1400) section.Add("建议", "路径 MTU 偏小，隧道或 PPPoE 会让大包分片，必要时调低 MTU", DiagLevel.Info);
        return section;
    }

    // Opens a UDP port on the router for a minute, checks that it is listed, and removes it again. Only when the user asks for it.
    // With a probe ticket source, M then sends a packet to that port from the outside: the mapping is only credited when it arrives, and
    // the same probe after the mapping is removed shows whether the port was open anyway.
    public static async Task<DiagSection> TestPortMapping(UpnpGateway gateway, CancellationToken ct, Func<CancellationToken, Task<ProbeTicket?>>? ticketSource = null, string fallbackHost = "")
    {
        var section = new DiagSection { Title = "UPnP 端口映射测试" };
        if (gateway.Control == null) { section.Summary = "该网关没有端口映射服务"; section.Level = DiagLevel.Warn; return section; }
        var local = gateway.LocalAddress ?? LocalAddressToward(gateway.Location.Host);
        if (local == null) { section.Summary = "找不到到达网关的本机地址"; section.Level = DiagLevel.Bad; return section; }
        var port = Random.Shared.Next(40000, 60000);
        using var http = Http();
        var added = false; bool? reached = null;
        try
        {
            await Soap(http, gateway, "AddPortMapping",
                [("NewRemoteHost", ""), ("NewExternalPort", port.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", "UDP"), ("NewInternalPort", port.ToString(CultureInfo.InvariantCulture)),
                 ("NewInternalClient", local.ToString()), ("NewEnabled", "1"), ("NewPortMappingDescription", "ArdUi test"), ("NewLeaseDuration", "60")], ct);
            added = true;
            section.Add("添加映射", $"UDP {port} → {local}:{port}", DiagLevel.Good);
            var entry = await Soap(http, gateway, "GetSpecificPortMappingEntry", [("NewRemoteHost", ""), ("NewExternalPort", port.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", "UDP")], ct);
            var ok = entry.GetValueOrDefault("NewInternalClient") == local.ToString();
            section.Add("读回映射", ok ? "与添加的一致" : "读回的内容不一致：" + entry.GetValueOrDefault("NewInternalClient", "?"), ok ? DiagLevel.Good : DiagLevel.Warn);
            section.Summary = ok ? "路由器接受了端口映射" : "端口映射可添加，但读回不一致"; section.Level = ok ? DiagLevel.Good : DiagLevel.Warn;
            if (ticketSource != null)
            {
                var firewall = WinFirewall.Inspect(); section.Add("本机防火墙", firewall.Text, firewall.MayBlock ? DiagLevel.Warn : DiagLevel.Info);
                IPAddress? wan = null;
                try { if (IPAddress.TryParse((await Soap(http, gateway, "GetExternalIPAddress", [], ct)).GetValueOrDefault("NewExternalIPAddress", ""), out var parsed) && Scope(parsed) == AddressScope.Public) wan = parsed; }
                catch (Exception ex) when (ex is UpnpError or HttpRequestException or TaskCanceledException) { }
                var lines = await NetProbe.VerifyUdpMapping(ticketSource, fallbackHost, port, true, ct, wan, port, "UPnP 映射");
                foreach (var line in lines) section.Lines.Add(line);
                reached = lines.Any(l => l.Level == DiagLevel.Good);
                var failed = lines.Any(l => l.Level == DiagLevel.Bad);
                section.Summary = reached == true ? "端口映射有效：M 从外部发来的包穿过了路由器" : failed && firewall.MayBlock ? "路由器接受了映射，但 M 的包没有到达：本机防火墙可能拦下了入站，结果不确定（放行 ArdUi 后再测）" : failed ? "路由器接受了映射，但 M 从外部发来的包没有到达（运营商级 NAT、上游防火墙，或路由器没有真正转发）" : section.Summary;
                section.Level = reached == true ? DiagLevel.Good : failed ? (firewall.MayBlock ? DiagLevel.Warn : DiagLevel.Bad) : section.Level;
            }
        }
        catch (Exception ex) when (ex is UpnpError or HttpRequestException or TaskCanceledException)
        {
            section.Add(added ? "读回映射" : "添加映射", "失败：" + ErrorText(ex), DiagLevel.Bad);
            section.Summary = "端口映射不可用"; section.Level = DiagLevel.Bad;
        }
        finally
        {
            if (added)
            {
                try { await Soap(http, gateway, "DeletePortMapping", [("NewRemoteHost", ""), ("NewExternalPort", port.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", "UDP")], CancellationToken.None); section.Add("删除映射", "已删除", DiagLevel.Good); }
                catch (Exception ex) when (ex is UpnpError or HttpRequestException or TaskCanceledException) { section.Add("删除映射", "失败：" + ErrorText(ex) + "（租约 60 秒后自动到期）", DiagLevel.Warn); }
                if (ticketSource != null && reached == true)
                {
                    try { foreach (var line in await NetProbe.VerifyUdpMapping(ticketSource, fallbackHost, port, false, CancellationToken.None, null, port, "UPnP 映射")) section.Lines.Add(line); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException) { }
                }
            }
        }
        return section;
    }

    static IPAddress? LocalAddressToward(string host)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(host), 9);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (Exception ex) when (ex is SocketException or FormatException) { return null; }
    }
}

// What the Windows firewall will do with an unsolicited inbound UDP packet for this program: an inbound probe that never arrives proves nothing
// about the router when the machine itself would have dropped it.
static class WinFirewall
{
    public sealed record State(bool Known, bool Enabled, bool DefaultBlocks, bool Allowed, string Text)
    {
        // true when a packet that never arrived may have been stopped by this machine rather than by the router or the provider
        public bool MayBlock => Known && Enabled && DefaultBlocks && !Allowed;
    }

    public static State Inspect(string? program = null)
    {
        program ??= Environment.ProcessPath ?? "";
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return new State(false, false, false, false, "无法读取 Windows 防火墙状态");
            dynamic policy = Activator.CreateInstance(type)!;
            int profiles = policy.CurrentProfileTypes;
            bool enabled = false, blocks = false;
            foreach (var bit in new[] { 1, 2, 4 })
            {
                if ((profiles & bit) == 0) continue;
                if ((bool)policy.FirewallEnabled[bit]) { enabled = true; if ((int)policy.DefaultInboundAction[bit] == 0) blocks = true; }   // 0 = block
            }
            var allowed = false;
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1 || ((int)rule.Profiles & profiles) == 0) continue;
                    var protocol = (int)rule.Protocol; if (protocol != 17 && protocol != 256) continue;   // UDP or any
                    string? application = rule.ApplicationName; string? ports = rule.LocalPorts;
                    var appMatches = string.IsNullOrEmpty(application) ? true : string.Equals(application, program, StringComparison.OrdinalIgnoreCase);
                    var anyPort = string.IsNullOrEmpty(ports) || ports == "*";
                    if (appMatches && anyPort && (!string.IsNullOrEmpty(application) || string.IsNullOrEmpty(ports))) { allowed = true; break; }
                }
                catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException) { }
            }
            var text = !enabled ? "Windows 防火墙已关闭" : !blocks ? "Windows 防火墙开启，但入站默认放行" : allowed ? "Windows 防火墙开启，有放行 ArdUi（UDP）入站的规则" : "Windows 防火墙开启，入站默认阻止，没有放行 ArdUi 的规则";
            return new State(true, enabled, blocks, allowed, text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return new State(false, false, false, false, "无法读取 Windows 防火墙状态");
        }
    }
}
