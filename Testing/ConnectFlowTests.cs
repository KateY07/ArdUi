namespace ArdUi;

// `ArdUi.exe --connect-flow-test`: the failure classes and the code parser behave as the UI relies on.
static class ConnectFlowTests
{
    public static int Run()
    {
        var failures = 0;
        void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }
        ConnectFailure Kind(Exception ex) => ConnectFlow.Classify(ex).Kind;

        // which ArdFerry a session runs over comes from the path ard selected, not from lines about this machine's own relays
        string? Relay(string line) => Engine.RelayName.Match(line) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1].Value : "direct") : null;
        const string ferry = "s-b82dae428052f7d8294dd36522766037c693df34.ard-relay.invalid";
        Check(Relay($"INFO ard: network path selected tunnel=1 path=PathId(8) transport=\"relay\" network=\"relay\" remote=relay:https://{ferry}/ local=Relay") == ferry,
            "the selected relay path names the ArdFerry");
        Check(Relay("INFO ard: network path selected tunnel=1 path=PathId(2) transport=\"udp\" network=\"ipv6\" remote=[2001:db8::5]:4433 local=x") == "direct",
            "a selected direct path clears the ArdFerry");
        Check(Relay($"INFO ard::relay_directory: candidate local registration verified relay=https://{ferry}/ revision=1") == null,
            "this machine's own relay registration is not the session's path");

        Check(Kind(new IOException(ConnectFlow.RejectedText)) == ConnectFailure.NoPermission, "rejected → NoPermission");
        Check(ConnectFlow.Classify(new IOException(ConnectFlow.RejectedText)).AskPassword, "NoPermission asks for the password again");
        Check(Kind(new IOException("对端未接受连接，请检查允许访问开关。")) == ConnectFailure.NoPermission, "ticket rejected → NoPermission");
        Check(Kind(new IOException("机器编号不存在。")) == ConnectFailure.NotFound, "unknown code → NotFound");
        Check(Kind(new IOException("对端离线或未开放远程访问。")) == ConnectFailure.Offline, "offline → Offline");
        Check(!ConnectFlow.Classify(new IOException("对端离线或未开放远程访问。")).AskPassword, "Offline does not ask for the password");
        Check(Kind(new InvalidDataException("该机器的 EndpointId 已改变！已阻止连接，请通过独立渠道核对。")) == ConnectFailure.SecurityWarning, "endpoint changed → SecurityWarning");
        Check(Kind(new OperationCanceledException("尚未核对对端 EndpointId。")) == ConnectFailure.Declined, "fingerprint refused → Declined");
        Check(Kind(new InvalidOperationException("不能连接本机。")) == ConnectFailure.Yourself, "own code → Yourself");
        Check(Kind(new HttpRequestException("x")) == ConnectFailure.Network, "http error → Network");
        Check(Kind(new IOException("可信服务器暂时不可用。")) == ConnectFailure.Network, "directory down → Network");
        Check(Kind(new Exception("boom")) == ConnectFailure.Other, "unknown → Other");
        Check(!ConnectFlow.Classify(new Exception("boom")).Title.Contains("可信服务器"), "no technical wording in titles");

        Check(ConnectFlow.NormaliseCode("a7b2k9") == "A7B2K9", "code upper-cased");
        Check(ConnectFlow.NormaliseCode(" A7B 2K9 ") == "A7B2K9", "code spaces dropped");
        Check(ConnectFlow.NormaliseCode("A7B2K") == null, "short code rejected");
        Check(ConnectFlow.NormaliseCode("A7B2K9X") == null, "long code rejected");
        Check(ConnectFlow.NormaliseCode("A7B2K!") == null, "symbols are not part of a code");
        Check(ConnectFlow.NormaliseCode("ＡＢＣ１２３") == null, "full-width letters are not a code");
        Check(ConnectFlow.NormaliseCode("设备A7B2K9") == "A7B2K9", "non-ASCII characters are ignored");
        Check(Kind(new IOException("客户端版本过旧，请升级到 ArdUi v5.pre1 或更高版本。")) == ConnectFailure.Outdated, "an outdated client is told to upgrade");
        Check(ClientVersion.Allowed("v6.pre1") && ClientVersion.Allowed("v6.pre12") && ClientVersion.Allowed("v7.pre1") && !ClientVersion.Allowed("v5.pre5") && !ClientVersion.Allowed("v6.pre0"), "v6.pre1 and newer are allowed: older ones poll the server instead of waiting for news");
        Check(!ClientVersion.Allowed("v4.pre9") && !ClientVersion.Allowed("v3.pre17") && !ClientVersion.Allowed(null) && !ClientVersion.Allowed("") && !ClientVersion.Allowed("5.0.0-pre.1") && !ClientVersion.Allowed("v5.pre"), "older, missing and malformed versions are refused");
        Check(Program.Version == "v6.pre1" && Program.WireVersion == "v6.pre1" && ClientVersion.Allowed(Program.WireVersion), "this build is v6.pre1 and announces itself in the form the version gate reads");

        // The relays come from M, never from config.json: a config written before v5.5 still loads, and its relay lines change nothing.
        var old = JsonSerializer.Deserialize<Settings>("{\"server\":\"https://bob.example/\",\"relay\":\"http://175.27.160.144:8080\",\"relayKey\":\"spki:00\",\"tcpPorts\":[3389],\"udpPorts\":[3389]}", Wire.Json)!;
        var validated = true; try { old.Validate(); } catch (InvalidDataException) { validated = false; }
        Check(validated && old.Network == null, "an old config.json with relay / relayKey still loads");
        var unknown = false; try { _ = old.DirectoryUrl; } catch (IOException) { unknown = true; }
        Check(unknown, "until M has answered, no relay is used at all (not the stale one from the config)");
        old.Network = new NetworkInfo("https://bob.example:8080/directory", "https://bob.example:7842/", "spki:3059ab").Checked();
        Check(old.DirectoryUrl == "https://bob.example:8080/directory" && old.MasterUrl == "https://bob.example:7842/" && old.MasterKey == "3059ab", "M's answer decides the relay directory, address discovery and key");
        bool Rejected(NetworkInfo n) { try { n.Checked(); return false; } catch (InvalidDataException) { return true; } }
        Check(Rejected(new("http://bob.example:8080/directory", "https://bob.example:7842/", "spki:00")) && Rejected(new("https://bob.example/", "https://bob.example:7842/", "nope"))
            && Rejected(new("https://u:p@bob.example/", "https://bob.example:7842/", "spki:00")), "a malformed answer from M is refused");
        Check(!JsonSerializer.Serialize(old, Wire.Json).Contains("relayDirectory", StringComparison.OrdinalIgnoreCase), "what M said is never written back into config.json");
        return failures == 0 ? 0 : 1;
    }
}
