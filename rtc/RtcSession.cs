namespace ArdUi;

// The two ends of a one-time session. The host makes an offer (signed by its identity, with the DTLS fingerprint inside) and a one-time token;
// the client answers (signed too) and proves the token inside the encrypted handshake. Signalling carries offer and answer; in the full design
// that is the directory server, and the token travels with the signed ticket. Here it is whatever the caller uses.
sealed class RtcHost : IAsyncDisposable
{
    readonly RtcSidecar sidecar;
    readonly RtcIdentity identity;
    readonly byte[] capability;
    readonly RtcOptions options;
    int used;
    public RtcOffer Offer { get; }
    public byte[] Token { get; }             // handed to the client out of band (a signed ticket in the full design)
    public string? ExpectedClientId { get; init; }

    RtcHost(RtcSidecar sidecar, RtcIdentity identity, byte[] capability, RtcOffer offer, byte[] token, RtcOptions options)
    { this.sidecar = sidecar; this.identity = identity; this.capability = capability; Offer = offer; Token = token; this.options = options; }

    public static async Task<RtcHost> Create(RtcIdentity identity, RtcOptions options, CancellationToken ct, string? expectedClientId = null)
    {
        var capability = RandomNumberGenerator.GetBytes(16); var token = RandomNumberGenerator.GetBytes(16);
        var sidecar = RtcSidecar.Start("host", capability, options);
        try
        {
            var sdp = await sidecar.Description.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var offer = new RtcOffer(session, sdp, identity.Id, identity.Sign(RtcProtocol.OfferText(session, sdp)));
            return new RtcHost(sidecar, identity, capability, offer, token, options) { ExpectedClientId = expectedClientId };
        }
        catch { await sidecar.DisposeAsync(); throw; }
    }

    // Takes the client's answer. Exactly one answer is ever taken: whatever happens next, this session cannot be used again.
    public async Task<RtcLink> Accept(RtcAnswer answer, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("一次性会话已经使用过。");
        try
        {
            if (answer.Session != Offer.Session) throw new InvalidDataException("应答属于另一个会话。");
            if (!RtcIdentity.Verify(answer.ClientId, RtcProtocol.AnswerText(answer.Session, answer.Sdp), answer.Sig)) throw new InvalidDataException("应答的身份签名无效。");
            if (ExpectedClientId != null && !string.Equals(ExpectedClientId, answer.ClientId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("应答来自未授权的设备。");
            sidecar.SetRemote("answer", answer.Sdp);
            return await RtcLink.AcceptAsHost(sidecar, capability, identity, Offer, answer, Token, options, ct);
        }
        catch { await sidecar.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync() => await sidecar.DisposeAsync();
}

static class RtcClient
{
    // Answers an offer: checks who signed it, starts the connection, and returns the answer to send back plus the link that completes once the
    // host has accepted (path found, identities proven, token accepted).
    public static async Task<(RtcAnswer Answer, Task<RtcLink> Link)> Join(RtcOffer offer, byte[] token, RtcIdentity identity, RtcOptions options, CancellationToken ct, string? expectedHostId = null)
    {
        if (!RtcIdentity.Verify(offer.HostId, RtcProtocol.OfferText(offer.Session, offer.Sdp), offer.Sig)) throw new InvalidDataException("邀请的身份签名无效。");
        if (expectedHostId != null && !string.Equals(expectedHostId, offer.HostId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("邀请来自意料之外的设备（EndpointId 不一致）。");
        RtcProtocol.Fingerprint(offer.Sdp);
        var capability = RandomNumberGenerator.GetBytes(16);
        var sidecar = RtcSidecar.Start("join", capability, options);
        try
        {
            sidecar.SetRemote("offer", offer.Sdp);
            var sdp = await sidecar.Description.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var answer = new RtcAnswer(offer.Session, sdp, identity.Id, identity.Sign(RtcProtocol.AnswerText(offer.Session, sdp)));
            var link = Task.Run(async () =>
            {
                try { return await RtcLink.ConnectAsClient(sidecar, capability, identity, offer, answer, token, options, ct); }
                catch { await sidecar.DisposeAsync(); throw; }
            }, ct);
            return (answer, link);
        }
        catch { await sidecar.DisposeAsync(); throw; }
    }
}
