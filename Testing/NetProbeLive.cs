using System.Text;

namespace ArdUi;

// `ArdUi.exe --probe-live <data folder> [server]`: asks the real directory server for a probe token with this machine's identity and runs the
// M-assisted checks against it. Development aid: it prints what the "网络诊断" page would show.
static class NetProbeLive
{
    // A probe token for this machine's identity from the real directory server (a development aid, like the rest of this file).
    public static async Task<ProbeTicket?> Ticket(string dataRoot, string server)
    {
        var (reply, _) = await Request(dataRoot, server);
        return reply == null ? null : new ProbeTicket(reply.Token, reply.Host, reply.Ports, reply.Ip, reply.Expires);
    }

    static async Task<(ProbeReply? Reply, string Text)> Request(string dataRoot, string server)
    {
        var seed = File.ReadAllBytes(Path.Combine(dataRoot, "identity"));
        using var key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, seed, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        var endpoint = Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant();
        const string route = "/api/v1/probe/start";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var nonce = Guid.NewGuid().ToString("N");
        var signed = Encoding.UTF8.GetBytes($"ArdUiServer/1\n{route}\n{endpoint}\n{now}\n{nonce}\n{payload}");
        var envelope = new SignedEnvelope(endpoint, now, nonce, payload, Convert.ToBase64String(NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(key, signed)));
        using var http = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("X-ArdUi-Client", Program.Version);
        using var content = new StringContent(JsonSerializer.Serialize(envelope, Wire.Json), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content);
        var text = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode ? JsonSerializer.Deserialize<ProbeReply>(text, Wire.Json) : null, "HTTP " + (int)response.StatusCode + " " + (text.Length > 300 ? text[..300] : text));
    }

    public static async Task<int> Run(string dataRoot, string server)
    {
        var (reply, text) = await Request(dataRoot, server);
        Out(text + Environment.NewLine);
        if (reply == null) return 1;
        var ticket = new ProbeTicket(reply.Token, reply.Host, reply.Ports, reply.Ip, reply.Expires);
        var (section, _) = await NetProbe.Verify(ticket, new Uri(server).Host, CancellationToken.None);
        Out(Text(section));
        return 0;
    }

    static void Out(string text) { using var output = Console.OpenStandardOutput(); output.Write(System.Text.Encoding.UTF8.GetBytes(text)); }
    static string Text(DiagSection section) => section.Title + " - " + section.Summary + Environment.NewLine + string.Concat(section.Lines.Select(l => "  " + l.Label + ": " + l.Value + Environment.NewLine));

    // `--mapping-live <data folder>`: finds the router, then tests its UPnP and NAT-PMP port mappings with M probing from the outside.
    public static async Task<int> Mapping(string dataRoot, string server)
    {
        Func<CancellationToken, Task<ProbeTicket?>> source = _ => Ticket(dataRoot, server);
        var host = new Uri(server).Host;
        var report = await NetDiag.Run(new NetDiagOptions { UseTrace = false, EgressRounds = 1, EchoUrls = [] });
        Out("UPnP gateways: " + report.Gateways.Count + ", NAT-PMP routers: " + report.NatPmp.Count + Environment.NewLine);
        foreach (var (gateway, external) in report.NatPmp) Out(Text(await NetDiag.TestNatPmpMapping(gateway, external, CancellationToken.None, source, host)));
        foreach (var gateway in report.Gateways.Where(g => g.Control != null)) Out(Text(await NetDiag.TestPortMapping(gateway, CancellationToken.None, source, host)));
        return 0;
    }
}
