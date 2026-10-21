using NSec.Cryptography;

namespace ArdUi;

// ArdUi's own WebRTC link, step 1: a one-time session between two devices that finds a path with ICE, authenticates both identities, and keeps
// an encrypted heartbeat going in both directions. WebRTC's DTLS protects the path; this layer does not trust it: the session is signed by the
// two Ed25519 identities, bound to the DTLS fingerprints, keyed with a fresh ECDH exchange and gated by a one-time token.

sealed class RtcIdentity
{
    readonly Key key;
    public string Id { get; }   // the public key, 64 hex digits (the device's EndpointId in the full design)
    RtcIdentity(Key key) { this.key = key; Id = Convert.ToHexString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)).ToLowerInvariant(); }
    public static RtcIdentity Create() => new(Key.Create(SignatureAlgorithm.Ed25519));
    public static RtcIdentity FromSeed(byte[] seed) => new(Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey));
    public string Sign(string message) => Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, Encoding.UTF8.GetBytes(message)));
    public static bool Verify(string id, string message, string signature)
    {
        try
        {
            if (!Regex.IsMatch(id, @"\A[0-9a-f]{64}\z")) return false;
            var pub = PublicKey.Import(SignatureAlgorithm.Ed25519, Convert.FromHexString(id), KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(pub, Encoding.UTF8.GetBytes(message), Convert.FromBase64String(signature));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException) { return false; }
    }
}

// What the two sides hand to each other through signalling (later the directory server); both carry their device's signature.
sealed record RtcOffer(string Session, string Sdp, string HostId, string Sig);
sealed record RtcAnswer(string Session, string Sdp, string ClientId, string Sig);
sealed record RtcHello(string Session, string Key, int Version = 1);
sealed record RtcProof(string Proof, string Sig);
sealed record RtcAccept(bool Ok, string? Sig, string? Error);
sealed record RtcBeat(long Seq, long Echo, long Held);   // our number, the newest number of the peer we have seen, and how long we held it (ms)

static class RtcProtocol
{
    public const int Version = 1;
    public static string OfferText(string session, string sdp) => $"ArdUi/rtc1 offer\n{session}\n{sdp}";
    public static string AnswerText(string session, string sdp) => $"ArdUi/rtc1 answer\n{session}\n{sdp}";
    public static string Fingerprint(string sdp)
    {
        var match = Regex.Match(sdp, @"a=fingerprint:sha-256 ([0-9A-Fa-f:]{95})");
        if (!match.Success) throw new InvalidDataException("SDP 里没有 DTLS 指纹。");
        return match.Groups[1].Value.ToUpperInvariant();
    }
    public static byte[] Context(string session, string clientKey, string hostKey, string clientId, string hostId, string clientFingerprint, string hostFingerprint) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"ArdUi/rtc1\n{session}\n{clientKey}\n{hostKey}\n{clientId}\n{hostId}\n{clientFingerprint}\n{hostFingerprint}"));
    public static string Proof(byte[] token, byte[] context) => Convert.ToHexString(HMACSHA256.HashData(token, Encoding.ASCII.GetBytes("ArdUi/rtc1 proof\n" + Convert.ToHexString(context)))).ToLowerInvariant();
    public static string RoleText(string role, byte[] context) => role + "\n" + Convert.ToHexString(context).ToLowerInvariant();

    // The encrypted frames on the local stream: 4 bytes length, then the sealed packet. Receivers insist on the next number in line, so a
    // replayed, dropped or reordered frame ends the link (the channel underneath is reliable and ordered: any gap means tampering).
    public static async Task Write(Stream stream, OverlayCipher cipher, object message, CancellationToken ct)
    {
        var packet = cipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json), false);
        var frame = new byte[4 + packet.Length]; BinaryPrimitives.WriteInt32BigEndian(frame, packet.Length); packet.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
    }
    public static async Task<T> Read<T>(Stream stream, OverlayCipher cipher, RtcCounter counter, CancellationToken ct)
    {
        var size = BinaryPrimitives.ReadInt32BigEndian(await Wire.Read(stream, 4, ct));
        if (size is < 38 or > 8192) throw new InvalidDataException("心跳帧长度无效。");
        var packet = await Wire.Read(stream, size, ct);
        return Open<T>(cipher, counter, packet);
    }
    public static T Open<T>(OverlayCipher cipher, RtcCounter counter, byte[] packet)
    {
        if (packet.Length < 38) throw new InvalidDataException("心跳帧太短。");
        var number = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(1));
        if (number != counter.Last + 1) throw new InvalidDataException("心跳帧重放或乱序。");
        var plain = cipher.Decrypt(packet, false) ?? throw new InvalidDataException("心跳帧认证失败。");
        counter.Last = number;
        return JsonSerializer.Deserialize<T>(plain, Wire.Json) ?? throw new InvalidDataException("心跳帧为空。");
    }
}

sealed class RtcCounter { public long Last; }

// The established link: the peer is who the signatures say, both sides beat once per interval, and silence for too long ends it.
sealed class RtcLink : IAsyncDisposable
{
    readonly RtcSidecar sidecar;
    readonly TcpClient tcp;
    readonly Stream stream;
    readonly OverlayCipher cipher;
    readonly RtcCounter received = new();
    readonly RtcOptions options;
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long sent, last = Environment.TickCount64, peerSeq, peerArrived;
    long beatsSent, beatsReceived;
    double rttTotal; long rttCount; double rttLast = double.NaN;
    int disposed;
    public bool Caller { get; }
    public string Session { get; }
    public string PeerId { get; }
    public string? CloseReason { get; private set; }
    public Task Closed => closed.Task;
    public long BeatsSent => Interlocked.Read(ref beatsSent);
    public long BeatsReceived => Interlocked.Read(ref beatsReceived);
    public double? Rtt => double.IsNaN(rttLast) ? null : rttLast;
    public double? RttAverage { get { lock (this) return rttCount == 0 ? null : rttTotal / rttCount; } }
    public string? PathLocal => sidecar.PathLocal;
    public string? PathRemote => sidecar.PathRemote;
    public string? RemoteAddress => sidecar.RemoteAddress;
    public bool DatagramOpen => sidecar.DatagramOpen;
    internal void Crash() => sidecar.Crash();   // tests: the peer process dies without a goodbye

    RtcLink(RtcSidecar sidecar, TcpClient tcp, OverlayCipher cipher, RtcOptions options, bool caller, string session, string peerId)
    { this.sidecar = sidecar; this.tcp = tcp; stream = tcp.GetStream(); this.cipher = cipher; this.options = options; Caller = caller; Session = session; PeerId = peerId; }

    static async Task<TcpClient> Attach(RtcSidecar sidecar, byte[] capability, CancellationToken ct)
    {
        var port = await sidecar.Ready.WaitAsync(ct);
        var tcp = new TcpClient { NoDelay = true };
        try { await tcp.ConnectAsync(IPAddress.Loopback, port, ct); await tcp.GetStream().WriteAsync(capability, ct); return tcp; }
        catch { tcp.Dispose(); throw; }
    }

    static string Key(ECDiffieHellman key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    static byte[] Agree(ECDiffieHellman key, string remote)
    {
        var blob = Convert.FromBase64String(remote); using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(blob, out var consumed);
        if (consumed != blob.Length || other.KeySize != 256 || other.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new CryptographicException("密钥曲线无效。");
        return key.DeriveRawSecretAgreement(other.PublicKey);
    }

    // The host: the client speaks first with its ephemeral key, the host answers, then the proof of the one-time token and both signatures.
    internal static async Task<RtcLink> AcceptAsHost(RtcSidecar sidecar, byte[] capability, RtcIdentity identity, RtcOffer offer, RtcAnswer answer, byte[] token, RtcOptions options, CancellationToken ct)
    {
        var tcp = await Attach(sidecar, capability, ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stream = tcp.GetStream();
            var hello = await Wire.ReadJson<RtcHello>(stream, timeout.Token);
            if (hello.Session != offer.Session || hello.Version != RtcProtocol.Version) throw new InvalidDataException("会话编号或版本不匹配。");
            using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var mine = new RtcHello(offer.Session, Key(key), RtcProtocol.Version);
            await Wire.WriteJson(stream, mine, timeout.Token);
            var secret = Agree(key, hello.Key);
            var context = RtcProtocol.Context(offer.Session, hello.Key, mine.Key, answer.ClientId, identity.Id, RtcProtocol.Fingerprint(answer.Sdp), RtcProtocol.Fingerprint(offer.Sdp));
            var cipher = new OverlayCipher(secret, context, false); CryptographicOperations.ZeroMemory(secret);
            var counter = new RtcCounter();
            var proof = await RtcProtocol.Read<RtcProof>(stream, cipher, counter, timeout.Token);
            var good = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(proof.Proof), Encoding.ASCII.GetBytes(RtcProtocol.Proof(token, context)))
                       & RtcIdentity.Verify(answer.ClientId, RtcProtocol.RoleText("client", context), proof.Sig);
            if (!good)
            {
                await RtcProtocol.Write(stream, cipher, new RtcAccept(false, null, "一次性口令或身份签名不正确。"), timeout.Token);
                throw new InvalidDataException("客户端没有通过一次性口令或身份签名验证。");
            }
            await RtcProtocol.Write(stream, cipher, new RtcAccept(true, identity.Sign(RtcProtocol.RoleText("host", context)), null), timeout.Token);
            return Start(new RtcLink(sidecar, tcp, cipher, options, false, offer.Session, answer.ClientId) { }, counter);
        }
        catch { tcp.Dispose(); throw; }
    }

    internal static async Task<RtcLink> ConnectAsClient(RtcSidecar sidecar, byte[] capability, RtcIdentity identity, RtcOffer offer, RtcAnswer answer, byte[] token, RtcOptions options, CancellationToken ct)
    {
        var tcp = await Attach(sidecar, capability, ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stream = tcp.GetStream();
            using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var mine = new RtcHello(offer.Session, Key(key), RtcProtocol.Version);
            await Wire.WriteJson(stream, mine, timeout.Token);
            var hello = await Wire.ReadJson<RtcHello>(stream, timeout.Token);
            if (hello.Session != offer.Session || hello.Version != RtcProtocol.Version) throw new InvalidDataException("会话编号或版本不匹配。");
            var secret = Agree(key, hello.Key);
            var context = RtcProtocol.Context(offer.Session, mine.Key, hello.Key, identity.Id, offer.HostId, RtcProtocol.Fingerprint(answer.Sdp), RtcProtocol.Fingerprint(offer.Sdp));
            var cipher = new OverlayCipher(secret, context, true); CryptographicOperations.ZeroMemory(secret);
            await RtcProtocol.Write(stream, cipher, new RtcProof(RtcProtocol.Proof(token, context), identity.Sign(RtcProtocol.RoleText("client", context))), timeout.Token);
            var counter = new RtcCounter();
            var accept = await RtcProtocol.Read<RtcAccept>(stream, cipher, counter, timeout.Token);
            if (!accept.Ok) throw new InvalidDataException("被控端拒绝了这次会话：" + accept.Error);
            if (accept.Sig == null || !RtcIdentity.Verify(offer.HostId, RtcProtocol.RoleText("host", context), accept.Sig)) throw new InvalidDataException("被控端的身份签名无效。");
            return Start(new RtcLink(sidecar, tcp, cipher, options, true, offer.Session, offer.HostId), counter);
        }
        catch { tcp.Dispose(); throw; }
    }

    static RtcLink Start(RtcLink link, RtcCounter counter)
    {
        link.received.Last = counter.Last;
        _ = Task.Run(link.Beat); _ = Task.Run(link.Listen); _ = Task.Run(link.Watch);
        return link;
    }

    static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    // Beat n is stamped with our clock when it leaves; the peer echoes the newest number of ours it has seen and says how long it held that
    // echo before sending. The round trip is now minus our stamp minus the time the peer held it: only our own clock is compared with itself.
    readonly ConcurrentDictionary<long, double> stamps = new();

    async Task Beat()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var seq = Interlocked.Increment(ref sent);
                var echo = Interlocked.Read(ref peerSeq);
                var held = echo == 0 ? 0 : (long)Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref peerArrived));
                stamps[seq] = Now; foreach (var old in stamps.Keys.Where(k => k < seq - 32)) stamps.TryRemove(old, out _);
                await RtcProtocol.Write(stream, cipher, new RtcBeat(seq, echo, held), stop.Token);
                Interlocked.Increment(ref beatsSent);
                await Task.Delay(options.Beat, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { Close("心跳发送结束：" + ex.Message); }
        catch (Exception ex) { Close("心跳发送失败：" + ex.Message); }
    }

    async Task Listen()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var beat = await RtcProtocol.Read<RtcBeat>(stream, cipher, received, stop.Token);
                var arrived = Environment.TickCount64;
                Interlocked.Exchange(ref last, arrived); Interlocked.Exchange(ref peerSeq, beat.Seq); Interlocked.Exchange(ref peerArrived, arrived);
                Interlocked.Increment(ref beatsReceived);
                if (beat.Echo > 0 && stamps.TryRemove(beat.Echo, out var stamp))
                {
                    var rtt = Math.Max(0, Now - stamp - Math.Clamp(beat.Held, 0, 60000));
                    lock (this) { rttLast = rtt; rttTotal += rtt; rttCount++; }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or EndOfStreamException) { Close("链路已关闭：" + ex.Message); }
        catch (InvalidDataException ex) { Close("安全检查失败：" + ex.Message); }
        catch (Exception ex) { Close("接收失败：" + ex.Message); }
    }

    async Task Watch()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(250, stop.Token);
                if (Environment.TickCount64 - Interlocked.Read(ref last) > options.Dead.TotalMilliseconds) { Close("心跳超时：对方长时间没有回应。"); return; }
                if (sidecar.Closed.IsCompleted) { Close("WebRTC 通道已关闭：" + sidecar.CloseReason); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    void Close(string reason)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CloseReason = reason; stop.Cancel();
        try { tcp.Dispose(); } catch (ObjectDisposedException) { }
        closed.TrySetResult();
        _ = sidecar.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync() { Close("已关闭。"); await closed.Task; }
}
