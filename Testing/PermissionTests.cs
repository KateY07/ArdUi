namespace ArdUi;

// `ArdUi.exe --perm-test`: permissions and "accept only once" behave as the UI and the gateway rely on.
static class PermissionTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    public static async Task<int> Run()
    {
        PortMapping();
        JsonCompatibility();
        TemporaryPeers();
        await GatewayEnforcement();
        return failures == 0 ? 0 : 1;
    }

    static void PortMapping()
    {
        var settings = new Settings(); // 3389 over TCP and UDP
        Check(Permissions.TcpPorts(settings, Perm.Frd).Length == 0 && Permissions.UdpPorts(settings, Perm.Frd).Length == 0, "FRD only opens no RDP port");
        Check(Permissions.TcpPorts(settings, Perm.Rdp).SequenceEqual([3389]) && Permissions.UdpPorts(settings, Perm.Rdp).SequenceEqual([3389]), "RDP opens 3389 tcp+udp");
        var old = new Settings { TcpPorts = [3389, 445], UdpPorts = [3389] };   // a config.json from before SMB was removed
        Check(Permissions.TcpPorts(old, Perm.All).SequenceEqual([3389]), "the SMB port stays closed even when an old config still lists it");
        Check(Permissions.Default == (Perm.Frd | Perm.Chat | Perm.Files), "default: FRD, the conversation (with files and calls) and the shared folder; not RDP");
        Check(Permissions.Parse(Permissions.Store(Perm.Frd)) == Perm.Frd, "a stored FRD-only grant stays FRD only");
        Check(Permissions.Parse(Permissions.Store(Perm.Frd | Perm.Chat | Perm.Files)) == (Perm.Frd | Perm.Chat | Perm.Files), "stored values round-trip");
        Check(Permissions.Parse(Permissions.Store(Perm.Frd | Perm.Voice)) == Perm.Frd, "the retired calls bit (calls are part of the conversation now) grants nothing");
        Check(Permissions.Parse(1) == (Perm.Frd | Perm.Chat), "an older FRD-only grant keeps the conversation it always had (not the shared folder, which did not exist)");
        Check(Permissions.Parse(4) == Perm.Chat, "the retired SMB bit grants nothing");
        Check(Permissions.Parse(0x1FF) == Perm.All, "unknown bits are dropped");
        Check(Permissions.Describe(Perm.Frd | Perm.Files) == "FRD / 文件", "description lists what is allowed");
    }

    static void JsonCompatibility()
    {
        var legacyGrant = JsonSerializer.Deserialize<ControllerGrant>("{\"code\":\"ABC123\",\"receipt\":{\"endpoint\":\"e\",\"issuedAt\":1,\"nonce\":\"n\",\"payload\":\"p\",\"signature\":\"s\"}}", Wire.Json)!;
        Check(Permissions.Parse(legacyGrant.Perms) == (Perm.Frd | Perm.Rdp | Perm.Chat), "a grant stored before permissions existed keeps what that version knew");
        var restricted = JsonSerializer.Deserialize<ControllerGrant>(JsonSerializer.Serialize(legacyGrant with { Perms = Permissions.Store(Perm.Frd) }, Wire.Json), Wire.Json)!;
        Check(Permissions.Parse(restricted.Perms) == Perm.Frd, "permissions survive a save and load");
        var legacyReply = JsonSerializer.Deserialize<AdmissionReply>("{\"accepted\":true,\"token\":\"00112233445566778899aabbccddeeff\",\"tcpPorts\":[3389],\"udpPorts\":[]}", Wire.Json)!;
        Check(Permissions.Parse(legacyReply.Perms) == (Perm.Frd | Perm.Rdp | Perm.Chat) && !legacyReply.Once, "an old host's reply means what it knew, not once");
    }

    static void TemporaryPeers()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-perm-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var state = new State(root);
            var id = new string('a', 64); var other = new string('b', 64);
            var grant = new SignedEnvelope(id, 1, "n", "p", "s");
            var once = state.GetOrAdd(id, "ABC123", grant, temporary: true);
            Check(once.Temporary && state.Peers.Contains(once), "a one-time device is known while connected");
            var saved = state.GetOrAdd(other, "XYZ789", grant);
            state.Save();
            var file = File.ReadAllText(Path.Combine(root, "peers.json"));
            Check(!file.Contains("ABC123") && file.Contains("XYZ789"), "a one-time device is never written to disk");
            Check(state.GetOrAdd(other, "XYZ789", grant, temporary: true) == saved && !saved.Temporary, "a saved device is not turned into a one-time one");
            state.Remove(id);
            Check(state.Peers.Count == 1, "a one-time device goes away with its session");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // Talks to a real Gateway on loopback: the admission reply carries the permissions, and ports outside them are refused.
    static async Task GatewayEnforcement()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () => { try { while (true) { var c = await echo.AcceptTcpClientAsync(); _ = Task.Run(async () => { using (c) { var b = new byte[16]; var n = await c.GetStream().ReadAsync(b); await c.GetStream().WriteAsync(b.AsMemory(0, n)); } }); } } catch { } });
        var grant = new SignedEnvelope("e", 1, "n", "p", "s");

        async Task<AdmissionReply> Admit(int port)
        {
            using var admit = new TcpClient(); await admit.ConnectAsync(IPAddress.Loopback, port);
            var head = new byte[23]; "AUI1"u8.CopyTo(head); head[20] = 2;
            await admit.GetStream().WriteAsync(head);
            await Wire.WriteJson(admit.GetStream(), new PasswordRequest(true, "123456"), CancellationToken.None);
            return await Wire.ReadJson<AdmissionReply>(admit.GetStream(), CancellationToken.None);
        }
        async Task<(AdmissionReply Reply, byte Port, AdmissionReply Second)> Probe(Perm perms, bool once)
        {
            var cap = RandomNumberGenerator.GetBytes(16);
            await using var gateway = new Gateway(new Settings { TcpPorts = [echoPort], UdpPorts = [] }, cap, (_, _) => Task.FromResult<Authorization?>(new Authorization(grant, perms, once)));
            var reply = await Admit(gateway.Port);
            var second = await Admit(gateway.Port);   // e.g. another local process reaching the loopback gateway
            // a forward to the echo port, as the controller would open it
            using var forward = new TcpClient(); await forward.ConnectAsync(IPAddress.Loopback, gateway.Port);
            var open = new byte[23]; "AUI1"u8.CopyTo(open); cap.CopyTo(open, 4); open[20] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(open.AsSpan(21), (ushort)echoPort);
            await forward.GetStream().WriteAsync(open);
            var answer = new byte[1]; await forward.GetStream().ReadExactlyAsync(answer);
            return (reply, answer[0], second);
        }

        var frd = await Probe(Perm.Frd, false);
        Check(frd.Reply.Accepted && Permissions.Parse(frd.Reply.Perms) == Perm.Frd && frd.Reply.TcpPorts.Length == 0, "FRD-only admission reports no ports");
        Check(frd.Port == 2, "FRD-only controller cannot open the RDP port");
        Check(!frd.Second.Accepted && frd.Second.Token == null, "a second admission on the same session is refused and gets no token");

        // a refused attempt does not use the session's admission up
        var tries = 0;
        var cap2 = RandomNumberGenerator.GetBytes(16);
        await using (var gateway = new Gateway(new Settings { TcpPorts = [echoPort], UdpPorts = [] }, cap2, (_, _) => Task.FromResult<Authorization?>(++tries == 1 ? null : new Authorization(grant, Perm.Frd, false))))
        {
            var refused = await Admit(gateway.Port); var accepted = await Admit(gateway.Port);
            Check(!refused.Accepted && accepted.Accepted && accepted.Token != null, "after a refused admission the real one still gets in");
        }
        var rdp = await Probe(Perm.Frd | Perm.Rdp, false);
        Check(rdp.Reply.TcpPorts.SequenceEqual([echoPort]) && rdp.Port == 0, "with RDP allowed the port opens");
        var once = await Probe(Perm.Frd, true);
        Check(once.Reply.Once && once.Reply.Accepted, "a one-time admission is flagged for the controller");
        var normal = await Probe(Perm.All, false);
        Check(!normal.Reply.Once, "a trusted admission is not flagged one-time");
    }
}
