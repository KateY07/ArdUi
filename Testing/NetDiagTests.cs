using System.Globalization;
using System.Text;

namespace ArdUi;

// `ArdUi.exe --netdiag-test`: the parsing and judging in NetDiag, STUN against two local fake servers (cone and symmetric), and the
// UPnP flow against a fake router. Nothing here touches the real network.
static class NetDiagTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    public static async Task<int> Run()
    {
        Scopes(); StunParsing(); Nat(); Hops(); Description(); Verdicts();
        var firewall = WinFirewall.Inspect(); Console.WriteLine("     firewall: " + firewall.Text);
        Check(firewall.Text.Length > 0, "the Windows firewall state can be read (or is reported as unreadable)");
        await FakeStun();
        await FakeRouter();
        return failures == 0 ? 0 : 1;
    }

    static void Scopes()
    {
        AddressScope S(string a) => NetDiag.Scope(IPAddress.Parse(a));
        Check(S("192.168.1.5") == AddressScope.Private && S("10.1.2.3") == AddressScope.Private && S("172.20.10.1") == AddressScope.Private, "private IPv4 ranges");
        Check(S("172.32.0.1") == AddressScope.Public && S("8.8.8.8") == AddressScope.Public, "public IPv4");
        Check(S("100.64.0.1") == AddressScope.Cgnat && S("100.127.255.255") == AddressScope.Cgnat && S("100.128.0.1") == AddressScope.Public, "carrier-grade NAT range");
        Check(S("169.254.3.4") == AddressScope.LinkLocal && S("fe80::1") == AddressScope.LinkLocal, "link-local");
        Check(S("fd12::1") == AddressScope.Private && S("2400:3200::1") == AddressScope.Public, "IPv6 unique-local and global");
        Check(NetDiag.ParseEcho("当前 IP：113.88.1.2  来自于：中国 广东") is { } ip && ip.ToString() == "113.88.1.2", "an echo page with extra text");
        Check(NetDiag.ParseEcho("your address is 192.168.1.1") == null, "an echo page that reports a private address is not an answer");
    }

    static byte[] StunReply(byte[] request, IPEndPoint mapped)
    {
        var reply = new byte[32];
        reply[0] = 0x01; reply[1] = 0x01; reply[3] = 12;
        Array.Copy(request, 4, reply, 4, 16);   // cookie + transaction id
        reply[20] = 0x00; reply[21] = 0x20; reply[23] = 8;
        reply[25] = 1; reply[26] = (byte)(mapped.Port >> 8 ^ 0x21); reply[27] = (byte)(mapped.Port & 0xFF ^ 0x12);
        var address = mapped.Address.GetAddressBytes();
        for (var i = 0; i < 4; i++) reply[28 + i] = (byte)(address[i] ^ reply[4 + i]);
        return reply;
    }

    static void StunParsing()
    {
        var transaction = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var request = NetDiag.StunRequest(transaction);
        Check(request.Length == 20 && request[0] == 0 && request[1] == 1 && request[4] == 0x21, "a binding request has the cookie and transaction id");
        var mapped = NetDiag.ParseStun(StunReply(request, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40123)), 32, transaction);
        Check(mapped != null && mapped.ToString() == "203.0.113.7:40123", "an XOR-MAPPED-ADDRESS is decoded");
        Check(NetDiag.ParseStun(StunReply(request, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 1)), 32, new byte[12]) == null, "a reply for another transaction is ignored");
        Check(NetDiag.ParseStun(new byte[10], 10, transaction) == null, "a short datagram is ignored");
    }

    static void Nat()
    {
        IPEndPoint E(string a, int p) => new(IPAddress.Parse(a), p);
        Check(NetDiag.ClassifyNat([], false) == "unknown", "no answers: unknown");
        Check(NetDiag.ClassifyNat([E("1.2.3.4", 5000)], true) == "none", "the mapped address is our own: no NAT");
        Check(NetDiag.ClassifyNat([E("1.2.3.4", 5000)], false) == "unclassified", "one server cannot tell the kind");
        Check(NetDiag.ClassifyNat([E("1.2.3.4", 5000), E("1.2.3.4", 5000)], false) == "cone", "the same endpoint from two servers: cone");
        Check(NetDiag.ClassifyNat([E("1.2.3.4", 5000), E("1.2.3.4", 5001)], false) == "symmetric", "a different port per server: symmetric");
        Check(NetDiag.ClassifyNat([E("1.2.3.4", 5000), E("1.2.3.5", 5000)], false) == "multiexit", "a different address per server: several exits, not a symmetric NAT");
    }

    static void Hops()
    {
        NetDiag.Hop H(int ttl, string? a) => new(ttl, a == null ? null : IPAddress.Parse(a), 1, false);
        Check(NetDiag.PrivateHopsBeforePublic([H(1, "192.168.1.1"), H(2, "113.1.1.1")]) == 1, "one private hop before the first public one");
        Check(NetDiag.PrivateHopsBeforePublic([H(1, "192.168.1.1"), H(2, "10.0.0.1"), H(3, null), H(4, "100.64.0.1"), H(5, "113.1.1.1")]) == 3, "several private layers (unanswered hops are skipped)");
        Check(NetDiag.PrivateHopsBeforePublic([H(1, "113.1.1.1")]) == 0, "a public first hop");
    }

    const string DescriptionXml = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0"><device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
        <friendlyName>Test Router</friendlyName><manufacturer>Acme</manufacturer><modelName>R1000</modelName><modelNumber>3</modelNumber><UDN>uuid:1234</UDN>
        <deviceList><device><deviceList><device><serviceList>
          <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/ip</controlURL></service>
        </serviceList></device></deviceList></device></deviceList></device></root>
        """;

    static void Description()
    {
        var gateway = new UpnpGateway { Location = new Uri("http://192.168.1.1:5000/desc.xml") };
        NetDiag.ParseDescription(gateway, DescriptionXml);
        Check(gateway.FriendlyName == "Test Router" && gateway.Manufacturer == "Acme" && gateway.Model == "R1000", "the router's names are read");
        Check(gateway.Control?.ToString() == "http://192.168.1.1:5000/ctl/ip" && gateway.ServiceType.EndsWith("WANIPConnection:1", StringComparison.Ordinal), "the WAN service control URL is resolved against the description URL");
        var headers = NetDiag.ParseSsdp("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nLOCATION: http://192.168.1.1:5000/desc.xml\r\nSERVER: Linux UPnP/1.0\r\n\r\n");
        Check(headers["location"] == "http://192.168.1.1:5000/desc.xml" && headers["SERVER"] == "Linux UPnP/1.0", "an SSDP answer is parsed");
    }

    static DiagSection Verdict(string nat, bool upnp = false, bool v6 = false, bool multiExit = false, NetProbe.Findings? m = null)
    {
        var report = new NetDiagReport { Nat = nat, Probe = m };
        var natSection = new DiagSection { Title = "nat" };
        if (v6) natSection.Add("公网 IPv6", "2400::1", DiagLevel.Good);
        var egress = new DiagSection { Title = "egress", Level = multiExit ? DiagLevel.Warn : DiagLevel.Good };
        var connectivity = new DiagSection { Title = "c" }; connectivity.Add("ArdUi 中继 / M（TCP x:1）", "20 ms", DiagLevel.Good);
        return NetDiag.Verdict(report, new DiagSection { Title = "a" }, natSection, egress, connectivity, upnp);
    }

    static void Verdicts()
    {
        Check(Verdict("none").Level == DiagLevel.Good, "verdict: a public address connects directly");
        Check(Verdict("cone").Level == DiagLevel.Good, "verdict: a cone NAT can punch holes");
        Check(Verdict("cone", multiExit: true).Level == DiagLevel.Warn, "verdict: a cone NAT behind several exits is unstable");
        Check(Verdict("symmetric").Level == DiagLevel.Warn && Verdict("symmetric").Lines.Any(l => l.Label == "建议"), "verdict: a symmetric NAT needs the relay, with advice");
        Check(Verdict("symmetric", upnp: true).Summary.Contains("端口映射", StringComparison.Ordinal), "verdict: a symmetric NAT with UPnP can open a port");
        Check(Verdict("symmetric", v6: true).Level == DiagLevel.Good, "verdict: IPv6 gives a direct path past a symmetric IPv4 NAT");
        Check(Verdict("multiexit").Level == DiagLevel.Warn && Verdict("multiexit").Summary.Contains("出口不止一个", StringComparison.Ordinal), "verdict: several exits are named, not called a symmetric NAT");
        Check(Verdict("unknown").Level == DiagLevel.Bad, "verdict: no UDP means relay only");
        var measured = new NetProbe.Findings { Nat = "cone", PortRestricted = true, Loss = 0.5, Rtt = 30000, PathMtu = 1500 };
        var withM = Verdict("cone", m: measured);
        check_m(withM);
        void check_m(DiagSection v) => Check(v.Lines.Any(l => l.Label == "NAT 类型" && l.Value.Contains("M 实测", StringComparison.Ordinal) && l.Value.Contains("端口受限", StringComparison.Ordinal)) && v.Lines.Any(l => l.Label == "到 M 的链路"), "verdict: M's measurement is named as the source, with the link quality");
        Check(Verdict("cone", m: new NetProbe.Findings { Nat = "cone", DirectExposure = true }).Lines.Any(l => l.Label == "入站" && l.Level == DiagLevel.Warn), "verdict: a directly reachable machine is flagged as exposed");
    }

    // Two fake STUN servers on loopback. Each answers with the sender's endpoint; the "symmetric" one shifts the port, as a symmetric NAT would.
    static async Task FakeStun()
    {
        async Task<(UdpClient Client, CancellationTokenSource Stop)> Serve(int shift)
        {
            var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var stop = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var packet = await server.ReceiveAsync(stop.Token);
                        var seen = new IPEndPoint(packet.RemoteEndPoint.Address, packet.RemoteEndPoint.Port + shift);
                        await server.SendAsync(StunReply(packet.Buffer, seen), packet.RemoteEndPoint, stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
            });
            await Task.Yield();
            return (server, stop);
        }
        var (a, stopA) = await Serve(0); var (b, stopB) = await Serve(0); var (c, stopC) = await Serve(1);
        string Host(UdpClient u) => "127.0.0.1:" + ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        NetDiagOptions Options(params UdpClient[] servers) => new() { StunServers = servers.Select(Host).ToArray(), StunServersV6 = [], StunTimeout = TimeSpan.FromSeconds(1) };
        var (coneSection, cone) = await NetDiag.NatAndAddresses(Options(a, b), CancellationToken.None);
        Check(cone == "cone" && coneSection.Level == DiagLevel.Good, "two cooperating servers: cone NAT");
        var (_, symmetric) = await NetDiag.NatAndAddresses(Options(a, c), CancellationToken.None);
        Check(symmetric == "symmetric", "a server that sees another port: symmetric NAT");
        var (_, silent) = await NetDiag.NatAndAddresses(new NetDiagOptions { StunServers = ["127.0.0.1:9"], StunServersV6 = [], StunTimeout = TimeSpan.FromMilliseconds(500) }, CancellationToken.None);
        Check(silent == "unknown", "no server answers: unknown");
        stopA.Cancel(); stopB.Cancel(); stopC.Cancel(); a.Dispose(); b.Dispose(); c.Dispose();
    }

    // A router that speaks just enough UPnP: description, external address, status, one existing mapping, add / read / delete.
    static async Task FakeRouter()
    {
        var port = new Func<int>(() => { using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); return ((IPEndPoint)probe.LocalEndpoint).Port; })();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var mappings = new Dictionary<string, (string Client, string Internal)>();
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync();
                    string text;
                    if (context.Request.Url!.AbsolutePath == "/desc.xml") text = DescriptionXml;
                    else
                    {
                        using var reader = new StreamReader(context.Request.InputStream); var body = await reader.ReadToEndAsync();
                        var action = context.Request.Headers["SOAPACTION"]!.Trim('"').Split('#')[1];
                        string Tag(string name) => System.Text.RegularExpressions.Regex.Match(body, $"<{name}>([^<]*)</{name}>").Groups[1].Value;
                        string Reply(string inner) => $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">{inner}</u:{action}Response></s:Body></s:Envelope>";
                        string Fault(int code, string description) => $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorCode>{code}</errorCode><errorDescription>{description}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
                        text = action switch
                        {
                            "GetExternalIPAddress" => Reply("<NewExternalIPAddress>198.51.100.20</NewExternalIPAddress>"),
                            "GetStatusInfo" => Reply("<NewConnectionStatus>Connected</NewConnectionStatus><NewUptime>3600</NewUptime>"),
                            "GetGenericPortMappingEntry" => Tag("NewPortMappingIndex") == "0"
                                ? Reply("<NewProtocol>UDP</NewProtocol><NewExternalPort>7000</NewExternalPort><NewInternalClient>192.168.1.50</NewInternalClient><NewInternalPort>7000</NewInternalPort><NewPortMappingDescription>other app</NewPortMappingDescription>")
                                : Fault(713, "SpecifiedArrayIndexInvalid"),
                            "AddPortMapping" => AddMapping(Tag("NewExternalPort") + "/" + Tag("NewProtocol"), Tag("NewInternalClient"), Tag("NewInternalPort")),
                            "GetSpecificPortMappingEntry" => mappings.TryGetValue(Tag("NewExternalPort") + "/" + Tag("NewProtocol"), out var m) ? Reply($"<NewInternalClient>{m.Client}</NewInternalClient><NewInternalPort>{m.Internal}</NewInternalPort>") : Fault(714, "NoSuchEntryInArray"),
                            "DeletePortMapping" => DeleteMapping(Tag("NewExternalPort") + "/" + Tag("NewProtocol")),
                            _ => Fault(401, "Invalid Action"),
                        };
                        string AddMapping(string key, string client, string port) { mappings[key] = (client, port); return Reply(""); }
                        string DeleteMapping(string key) { mappings.Remove(key); return Reply(""); }
                        if (text.Contains("<s:Fault>", StringComparison.Ordinal)) context.Response.StatusCode = 500;
                    }
                    var bytes = Encoding.UTF8.GetBytes(text); context.Response.ContentType = "text/xml"; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        });
        var options = new NetDiagOptions { UseSsdp = false, ExtraGatewayLocations = [new Uri($"http://127.0.0.1:{port}/desc.xml")] };
        var (section, gateways) = await NetDiag.Upnp(options, CancellationToken.None);
        Check(gateways.Count == 1 && gateways[0].Control != null && section.Level == DiagLevel.Info && section.Summary.Contains("尚未实测", StringComparison.Ordinal), "a router with a WAN service is found, and its protocol answer is not presented as proof (still unverified)");
        Check(section.Lines.Any(l => l.Label.Contains("外网地址", StringComparison.Ordinal) && l.Value.StartsWith("198.51.100.20", StringComparison.Ordinal) && l.Level == DiagLevel.Good), "the router's external address is read");
        Check(section.Lines.Any(l => l.Value.Contains("other app", StringComparison.Ordinal)) && section.Lines.Any(l => l.Value == "1 条"), "existing mappings are listed");
        gateways[0].LocalAddress = IPAddress.Loopback;
        var test = await NetDiag.TestPortMapping(gateways[0], CancellationToken.None);
        Check(test.Level == DiagLevel.Good && test.Lines.Any(l => l.Label == "删除映射" && l.Value == "已删除"), "a test mapping is added, read back and removed");
        Check(mappings.Count == 0, "the router has no mapping left behind");
        stop.Cancel(); listener.Close();
        await Task.Delay(50);
        _ = CultureInfo.InvariantCulture;
    }
}
