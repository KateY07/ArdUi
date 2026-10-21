namespace ArdUi;

// `ArdUi.exe --rtc-test`: the WebRTC link between two ends on this machine (each end is an ardrtc.exe), then the checks that must fail:
// a wrong token, a second use of the one-time session, a forged offer, an unknown peer, replayed or tampered frames, a peer that vanishes.
// `--rtc-host` / `--rtc-join` run one end each, for a test between two machines (signalling is copy and paste: one JSON line each way).
static class RtcTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }
    static async Task<bool> Until(Func<bool> condition, int ms = 8000) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (condition()) return true; await Task.Delay(50); } return condition(); }
    static async Task<bool> Fails<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } catch (Exception) { return false; } }

    public static async Task<int> Run()
    {
        var options = new RtcOptions { Loopback = true, Stun = [], Beat = TimeSpan.FromMilliseconds(200), Dead = TimeSpan.FromSeconds(3), Wait = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = cts.Token;
        var hostIdentity = RtcIdentity.Create(); var clientIdentity = RtcIdentity.Create();

        // ---- the good path ----------------------------------------------------------------------------------------------------
        await using (var host = await RtcHost.Create(hostIdentity, options, ct, clientIdentity.Id))
        {
            Check(host.Offer.Sdp.Contains("a=fingerprint:sha-256") && host.Offer.HostId == hostIdentity.Id, "the host's offer carries its identity and its DTLS fingerprint");
            var (answer, pending) = await RtcClient.Join(host.Offer, host.Token, clientIdentity, options, ct, hostIdentity.Id);
            var hostLink = await host.Accept(answer, ct); var clientLink = await pending;
            Check(hostLink.PeerId == clientIdentity.Id && clientLink.PeerId == hostIdentity.Id, "each side knows who is on the other end (signed identities)");
            Check(await Until(() => hostLink.BeatsReceived >= 8 && clientLink.BeatsReceived >= 8), "encrypted heartbeats arrive in both directions (" + hostLink.BeatsReceived + " / " + clientLink.BeatsReceived + ")");
            Check(await Until(() => hostLink.Rtt != null && clientLink.Rtt != null) && hostLink.RttAverage < 200 && clientLink.RttAverage < 200, "the round trip is measured on both sides (" + hostLink.RttAverage?.ToString("0.0") + " / " + clientLink.RttAverage?.ToString("0.0") + " ms)");
            Check(await Until(() => hostLink.PathLocal != null) && hostLink.PathLocal == "host", "ICE found a path (" + hostLink.PathLocal + " -> " + hostLink.PathRemote + " " + hostLink.RemoteAddress + ")");
            Check(await Until(() => clientLink.DatagramOpen && hostLink.DatagramOpen), "the unreliable channel opened as well");

            // ---- the session cannot be used twice ---------------------------------------------------------------------------------
            var (answer2, pending2) = await RtcClient.Join(host.Offer, host.Token, clientIdentity, options, ct, hostIdentity.Id);
            Check(await Fails<InvalidOperationException>(() => host.Accept(answer2, ct)), "a one-time session takes one answer only");
            Check(await Fails<Exception>(async () => await pending2.WaitAsync(TimeSpan.FromSeconds(8))), "and a second client never gets in");
            Check(hostLink.BeatsReceived > 0 && !hostLink.Closed.IsCompleted, "the first link is not disturbed by the attempt");

            // ---- it ends when the peer vanishes ---------------------------------------------------------------------------------
            hostLink.Crash();
            Check(await Until(() => clientLink.Closed.IsCompleted, 10000) && clientLink.CloseReason != null, "a vanished peer ends the link by itself (" + clientLink.CloseReason + ")");
            await hostLink.DisposeAsync(); await clientLink.DisposeAsync();
        }

        // ---- wrong token --------------------------------------------------------------------------------------------------------
        await using (var host = await RtcHost.Create(hostIdentity, options, ct))
        {
            var (answer, pending) = await RtcClient.Join(host.Offer, RandomNumberGenerator.GetBytes(16), clientIdentity, options, ct);
            var hostSide = Fails<InvalidDataException>(() => host.Accept(answer, ct));
            Check(await hostSide, "a wrong one-time token is refused by the host");
            Check(await Fails<InvalidDataException>(() => pending), "and the client is told so");
        }

        // ---- forged offer / wrong host / unknown client --------------------------------------------------------------------------
        await using (var host = await RtcHost.Create(hostIdentity, options, ct, clientIdentity.Id))
        {
            var forged = host.Offer with { Sdp = host.Offer.Sdp.Replace("a=ice-ufrag:", "a=ice-ufrag:x") };
            Check(await Fails<InvalidDataException>(() => RtcClient.Join(forged, host.Token, clientIdentity, options, ct)), "an offer changed on the way fails its signature");
            Check(await Fails<InvalidDataException>(() => RtcClient.Join(host.Offer, host.Token, clientIdentity, options, ct, RtcIdentity.Create().Id)), "an offer from another device than expected is refused");
            var stranger = RtcIdentity.Create();
            var (answer, pending) = await RtcClient.Join(host.Offer, host.Token, stranger, options, ct);
            Check(await Fails<InvalidDataException>(() => host.Accept(answer, ct)), "an answer from a device that is not the expected one is refused");
            await Fails<Exception>(() => pending);
        }

        // ---- frames: tampered, replayed, out of order ----------------------------------------------------------------------------
        {
            var secret = RandomNumberGenerator.GetBytes(32); var context = RandomNumberGenerator.GetBytes(32);
            using var a = new OverlayCipher(secret, context, true); using var b = new OverlayCipher(secret, context, false);
            byte[] Beat(long n) => a.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new RtcBeat(n, 0, 0), Wire.Json), false);
            var counter = new RtcCounter(); var first = Beat(1);
            Check(RtcProtocol.Open<RtcBeat>(b, counter, first).Seq == 1, "a sealed frame opens on the other side");
            Check(Throws(() => RtcProtocol.Open<RtcBeat>(b, counter, first)), "a replayed frame is refused");
            var second = Beat(2); var flipped = (byte[])second.Clone(); flipped[20] ^= 1;
            Check(Throws(() => RtcProtocol.Open<RtcBeat>(b, counter, flipped)), "a frame with one changed bit is refused");
            Check(RtcProtocol.Open<RtcBeat>(b, counter, second).Seq == 2, "while the genuine one still goes through");
            Beat(3); var fourth = Beat(4);
            Check(Throws(() => RtcProtocol.Open<RtcBeat>(b, counter, fourth)), "a frame that skips ahead is refused (something was dropped)");
            using var other = new OverlayCipher(RandomNumberGenerator.GetBytes(32), context, false);
            Check(Throws(() => RtcProtocol.Open<RtcBeat>(other, new RtcCounter(), first)), "a frame sealed with another key is refused");
        }

        Console.WriteLine(failures == 0 ? "PASS: WebRTC link, one-time session, encrypted heartbeats." : failures + " failure(s).");
        return failures == 0 ? 0 : 1;
    }

    static bool Throws(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }

    // ---- two machines -----------------------------------------------------------------------------------------------------------
    static StreamWriter Out() => new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    static StreamReader In() => new(Console.OpenStandardInput(), Encoding.UTF8);

    static async Task<int> Report(RtcLink link, StreamWriter output, int seconds)
    {
        var end = Environment.TickCount64 + seconds * 1000L;
        while (Environment.TickCount64 < end && !link.Closed.IsCompleted)
        {
            await output.WriteLineAsync($"[{link.Session[..6]}] {(link.Caller ? "client" : "host")} peer={link.PeerId[..12]} sent={link.BeatsSent} received={link.BeatsReceived} rtt={link.Rtt?.ToString("0.0") ?? "-"}ms avg={link.RttAverage?.ToString("0.0") ?? "-"}ms path={link.PathLocal}->{link.PathRemote} {link.RemoteAddress} dgram={link.DatagramOpen}");
            await Task.Delay(1000);
        }
        var ok = !link.Closed.IsCompleted && link.BeatsSent >= 3 && link.BeatsReceived >= 3;
        await output.WriteLineAsync((ok ? "SMOKE OK" : "SMOKE FAILED") + $" sent={link.BeatsSent} received={link.BeatsReceived} avgRtt={link.RttAverage?.ToString("0.0")}ms path={link.PathLocal}->{link.PathRemote} {link.RemoteAddress}" + (link.CloseReason != null ? " closed: " + link.CloseReason : ""));
        await link.DisposeAsync();
        return ok ? 0 : 1;
    }

    static RtcOptions Smoke(string[] args) => new()
    { Stun = args.SkipWhile(a => a != "--stun").Skip(1).FirstOrDefault()?.Split(',') ?? new RtcOptions().Stun, Loopback = args.Contains("--loopback"), Wait = TimeSpan.FromSeconds(60) };

    static string? Arg(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
    // Signalling by file (for runs under a harness): the line is read from, or written to, a file instead of the console.
    static async Task<string> ReadLine(StreamReader input, string? file, CancellationToken ct)
    {
        if (file == null) return await input.ReadLineAsync(ct) ?? throw new IOException("没有收到输入。");
        while (!ct.IsCancellationRequested)
        {
            try { if (File.Exists(file)) { var text = (await File.ReadAllTextAsync(file, ct)).Trim(); if (text.Length > 0 && text.EndsWith('}')) return text; } }
            catch (IOException) { }
            await Task.Delay(200, ct);
        }
        throw new OperationCanceledException();
    }
    static async Task WriteLine(StreamWriter output, string? file, string line)
    { if (file == null) await output.WriteLineAsync(line); else { await File.WriteAllTextAsync(file + ".tmp", line); File.Move(file + ".tmp", file, true); } }

    public static async Task<int> Host(string[] args)
    {
        var output = Out(); var input = In(); var seconds = int.TryParse(args.SkipWhile(a => a != "--seconds").Skip(1).FirstOrDefault(), out var s) ? s : 20;
        var identity = RtcIdentity.Create(); using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var host = await RtcHost.Create(identity, Smoke(args), cts.Token);
        await WriteLine(output, Arg(args, "--offer-out"), JsonSerializer.Serialize(new { offer = host.Offer, token = Convert.ToHexString(host.Token).ToLowerInvariant() }));
        await output.WriteLineAsync("offer ready; waiting for the answer");
        var line = await ReadLine(input, Arg(args, "--answer-in"), cts.Token);
        var answer = JsonSerializer.Deserialize<RtcAnswer>(JsonDocument.Parse(line).RootElement.GetProperty("answer").GetRawText(), Wire.Json)!;
        var link = await host.Accept(answer, cts.Token);
        return await Report(link, output, seconds);
    }

    public static async Task<int> Join(string[] args)
    {
        var output = Out(); var input = In(); var seconds = int.TryParse(args.SkipWhile(a => a != "--seconds").Skip(1).FirstOrDefault(), out var s) ? s : 20;
        var identity = RtcIdentity.Create(); using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var line = await ReadLine(input, Arg(args, "--offer-in"), cts.Token);
        var root = JsonDocument.Parse(line).RootElement;
        var offer = JsonSerializer.Deserialize<RtcOffer>(root.GetProperty("offer").GetRawText(), Wire.Json)!;
        var (answer, pending) = await RtcClient.Join(offer, Convert.FromHexString(root.GetProperty("token").GetString()!), identity, Smoke(args), cts.Token);
        await WriteLine(output, Arg(args, "--answer-out"), JsonSerializer.Serialize(new { answer }));
        await output.WriteLineAsync("answer ready; waiting for the link");
        var link = await pending;
        return await Report(link, output, seconds);
    }
}
