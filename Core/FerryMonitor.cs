using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ArdUi;

// What this client sees of the network's ArdFerry nodes, reported to M (ROLES.md: nodes are untrusted and report nothing themselves).
// Every interval: the list of nodes from M's directory (accepted only from M's key), one TLS handshake to each published entrance
// (accepted only with the key M published for it), and which node this machine's relayed sessions run over. Sent as one small,
// signed summary; M says how long to wait before the next one.
sealed class FerryMonitor
{
    sealed record Ferry(string Node, string Family, bool Available, string[] Urls, string TlsName, string Spki);
    sealed record Probe(string Node, bool Ok, double? Ms);

    readonly Engine engine;
    readonly Func<string, object, CancellationToken, Task<JsonElement>> report;
    public TimeSpan Interval { get; private set; } = TimeSpan.FromSeconds(30);
    public IReadOnlyDictionary<string, (bool Ok, double? Ms)> Latest { get; private set; } = new Dictionary<string, (bool, double?)>();

    public FerryMonitor(Engine engine, Func<string, object, CancellationToken, Task<JsonElement>> report)
    { this.engine = engine; this.report = report; }

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Once(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Diagnostics.Log("ferry-report", ex.Message); }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    internal async Task Once(CancellationToken ct)
    {
        if (engine.Settings.Network is not { } network) return;
        var ferries = await Directory(network, ct);
        // only entrances this machine can reach at all: a failure over IPv6 from a machine without IPv6 says nothing about the node
        var ipv6 = HasGlobalIPv6();
        var probes = await Task.WhenAll(ferries.Where(f => f.Available && (f.Family != "ipv6" || ipv6))
            .SelectMany(f => f.Urls.Select(url => Measure(f, url, ct))));
        Latest = probes.GroupBy(p => p.Node).ToDictionary(g => g.Key, g => (g.Any(p => p.Ok), Median(g.Where(p => p.Ok).Select(p => p.Ms))));
        var names = ferries.GroupBy(f => f.Node).ToDictionary(g => ServiceName(g.Key, g.First().Spki), g => g.Key);
        var sessions = engine.Outgoing.Values.Concat(engine.Incoming.Values).Where(s => s.Live && s.Network.Contains("中继", StringComparison.Ordinal))
            .Select(s => (Node: s.RelayName is { } name && names.TryGetValue(name, out var node) ? node : null, Session: s))
            .Where(x => x.Node != null).ToArray();
        var rows = ferries.Select(f => f.Node).Distinct().Select(node =>
        {
            var mine = probes.Where(p => p.Node == node).ToArray();
            var used = sessions.Where(x => x.Node == node).Select(x => x.Session).ToArray();
            return new
            {
                node, probes = mine.Length, ok = mine.Count(p => p.Ok), rttMs = Median(mine.Where(p => p.Ok).Select(p => p.Ms)),
                // the overlay measures its selected path all the time (both ends); the session's own probes are the fallback
                sessions = used.Length, sessionRttMs = Median(used.Select(s => s.Overlay?.Link(s.Overlay.Selected)?.RttMs ?? s.UdpRtt ?? s.TcpRtt)),
                mbps = Median(used.Select(s => s.Overlay?.Link(s.Overlay.Selected)?.BandwidthMbps ?? s.BandwidthMbps)),
            };
        }).Where(r => r.probes > 0 || r.sessions > 0).Take(16).ToArray();
        if (rows.Length == 0) return;
        var reply = await report("/api/v1/ferry-report", new { ferries = rows }, ct);
        if (reply.TryGetProperty("next", out var next) && next.TryGetInt32(out var seconds))
            Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 1800));   // M sets the pace
    }

    // The name ard gives a directory relay in its logs ("relay=https://s-….ard-relay.invalid/"): SHA-256 of node and key, first 20 bytes.
    internal static string ServiceName(string node, string spkiHex) =>
        "s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node + "\n" + spkiHex.ToLowerInvariant()))[..20]).ToLowerInvariant() + ".ard-relay.invalid";

    static bool HasGlobalIPv6() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6SiteLocal
                      && !a.Address.IsIPv6UniqueLocal && !IPAddress.IsLoopback(a.Address) && !a.Address.IsIPv6Teredo);

    static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return null;
        var middle = sorted.Length / 2;
        return Math.Round(sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2, 1);
    }

    // M's ArdFerry directory, accepted only from a server holding M's key (as ard does).
    static async Task<Ferry[]> Directory(NetworkInfo network, CancellationToken ct)
    {
        var key = Convert.FromHexString(network.MasterKey["spki:".Length..]);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null && certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1 << 20 };
        using var document = JsonDocument.Parse(await http.GetStringAsync(network.RelayDirectory, ct));
        return document.RootElement.GetProperty("entries").EnumerateArray().Select(e => new Ferry(
            e.GetProperty("node").GetString()!, e.GetProperty("family").GetString()!, e.GetProperty("available").GetBoolean(),
            e.GetProperty("urls").EnumerateArray().Select(u => u.GetString()!).ToArray(),
            e.GetProperty("tls_name").GetString()!, e.GetProperty("spki").GetString()!)).ToArray();
    }

    // One TLS handshake to an entrance, accepted only with the key M published for that node: how long it takes from here.
    static async Task<Probe> Measure(Ferry ferry, string url, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url); var key = Convert.FromHexString(ferry.Spki);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            using var tcp = new TcpClient(uri.HostNameType == UriHostNameType.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            await tcp.ConnectAsync(uri.Host.Trim('[', ']'), uri.Port, timeout.Token);
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
                certificate != null && new X509Certificate2(certificate).PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ferry.TlsName }, timeout.Token);
            return new Probe(ferry.Node, true, Math.Round(watch.Elapsed.TotalMilliseconds, 1));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException or FormatException)
        {
            if (ct.IsCancellationRequested) throw;
            return new Probe(ferry.Node, false, null);
        }
    }
}
