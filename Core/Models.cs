namespace ArdUi;

sealed class Settings
{
    public const string DefaultRelayKey = "spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f";
    // M's HTTPS domain: the one thing that says which network this client belongs to (see ROLES.md).
    public string Server { get; set; } = "https://f.visnova.cn/";
    // Only for test harnesses that run a private relay without M (StaticRelay). Before v5.5 config.json carried "relay" and "relayKey";
    // they are ignored now: M tells the client where its relay directory is (NetworkInfo), so a stale value cannot pull a self-hosted
    // network's traffic onto another network's relays.
    public string Relay { get; set; } = "http://175.27.160.144:8080";
    public string RelayKey { get; set; } = DefaultRelayKey;
    public bool StaticRelay { get; set; }
    // Learned from M at run time (GET <server>/api/v1/network), cached in the data folder; never read from config.json.
    [System.Text.Json.Serialization.JsonIgnore] public NetworkInfo? Network { get; set; }
    NetworkInfo Need => Network ?? throw new IOException("还没有从可信服务器取得中继信息，请检查网络后重试。");
    public string DirectoryUrl => Need.RelayDirectory;
    public string MasterUrl => Need.Master;
    public string MasterKey => Need.MasterKey["spki:".Length..];
    public string FrdPath { get; set; } = "";
    // -v (debug) by default: the log file is the place to look. -vv is per-packet trace output (thousands of lines a second during a
    // session); it is opt-in because it floods the log file and the UI.
    public bool DetailedArdDiagnostics { get; set; }
    public int[] TcpPorts { get; set; } = [3389];
    public int[] UdpPorts { get; set; } = [3389];
    public void Validate()
    {
        if (!Uri.TryCreate(Server, UriKind.Absolute, out var server) || server.Scheme != "https" || !string.IsNullOrEmpty(server.UserInfo))
            throw new InvalidDataException("arduiserver 必须使用有效 HTTPS 地址。");
        if (StaticRelay && (!Uri.TryCreate(Relay, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)))
            throw new InvalidDataException("config.json 中的 relay 必须是 HTTP/HTTPS 地址。");
        if (StaticRelay && !Regex.IsMatch(RelayKey ?? "", @"\Aspki:[0-9a-fA-F]{2,512}\z"))
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
    // A device accepted "only this once": it lives for the session, is never written to peers.json and is never reconnected.
    [System.Text.Json.Serialization.JsonIgnore] public bool Temporary { get; set; }
    // Unix seconds of the last established session (0 = never); devices are listed most recently used first.
    public long LastConnectedAt { get; set; }
    public string Group { get; set; } = "";
    // Reported by the controlled machine itself; shown next to the user's own note.
    public string ComputerName { get; set; } = "";
}

// Where this network's relay directory and address discovery are, and M's key (pinned by ard): served by M at /api/v1/network.
sealed record NetworkInfo(string RelayDirectory, string Master, string MasterKey)
{
    public NetworkInfo Checked()
    {
        foreach (var url in new[] { RelayDirectory, Master })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo) || url.Length > 300)
                throw new InvalidDataException("可信服务器返回的中继地址无效。");
        if (!Regex.IsMatch(MasterKey ?? "", @"\Aspki:[0-9a-f]{2,512}\z")) throw new InvalidDataException("可信服务器返回的中继密钥无效。");
        return this;
    }
}

sealed record SignedEnvelope(string Endpoint, long IssuedAt, string Nonce, string Payload, string Signature);
sealed record MachineInfo(string Code, string Endpoint);
// The ArdFerry of a session: chosen by the controller from its own measurements, written into its signed request, used by both sides.
sealed record FerryChoice(string Node, string Key, string[] Addresses);
sealed record ServerRequest(string Code, string TargetEndpoint, string SessionId, string RequestId, long Expires, string? Client = null, FerryChoice? Ferry = null);
sealed record ServerOffer(string SessionId, string RequestId, string ControllerEndpoint, string TargetEndpoint, string ClientSessionId, long Expires);
sealed record ServerTicket(string Id, string ControllerEndpoint, string ControllerCode, string ClientSessionId, long Expires, SignedEnvelope Proof);
sealed record PollReply(bool Enabled, ServerTicket[] Tickets);
sealed record PresenceEntry(bool Online, string Name);
sealed record PresenceReply(Dictionary<string, PresenceEntry> Peers);
sealed record TicketReply(string Status, SignedEnvelope? Offer);
sealed record PasswordRequest(bool Enroll, string Password);
sealed record GrantReceipt(string ControllerEndpoint, string TargetEndpoint, string GrantId);
// Perms as Permissions.Store writes them; grants stored before permissions existed deserialise as Legacy (everything that version knew)
sealed record ControllerGrant(string Code, SignedEnvelope Receipt, int Perms = Permissions.Legacy);
sealed record AdmissionReply(bool Accepted, string? Error, string? Token, int[] TcpPorts, int[] UdpPorts, SignedEnvelope? Grant, int Perms = Permissions.Legacy, bool Once = false);
sealed record Pairing(string Code, string Endpoint, bool Incoming);
sealed class AccessSettings
{
    public bool Enabled { get; set; }
    public bool AlwaysAsk { get; set; }   // every session needs a yes from the person at this machine, trusted devices included
    public string? EnrollmentSecret { get; set; }
    public Dictionary<string, ControllerGrant> Controllers { get; set; } = new();
    public Dictionary<string, string> Targets { get; set; } = new();
    public double SideWidth { get; set; } = 240;
    public string? SharedFolder { get; set; }   // the folder an authorised controller with "文件" may read and write (none until chosen)
}
