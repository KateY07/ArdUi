namespace ArdUi;

sealed class Settings
{
    public const string DefaultRelayKey = "spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f";
    public string Server { get; set; } = "https://f.visnova.cn/";
    public string Relay { get; set; } = "http://175.27.160.144:8080";
    public string RelayKey { get; set; } = DefaultRelayKey;
    public string FrdPath { get; set; } = "";
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
