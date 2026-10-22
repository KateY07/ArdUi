namespace ArdUi;

// `ArdUi.exe --security-test`: the protections added after the v5.5 security review, each against the attack it is meant to stop.
// Everything runs in this process on loopback: a real Gateway and two real OverlaySessions, with test device keys.
static class SecurityTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    sealed record Device(string Id, NSec.Cryptography.Key Key)
    {
        public static Device New()
        {
            var key = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519);
            return new(Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant(), key);
        }
        public OverlayIdentity For(Device peer) => new(Id, peer.Id, data => NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(Key, data));
    }

    public static async Task<int> Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await SignedSessions(timeout.Token);
        Registry();
        await BusyControls(timeout.Token);
        Redaction();
        await ReceivedFiles();
        Console.WriteLine(failures == 0 ? "PASS: security protections hold." : failures + " failure(s).");
        return failures == 0 ? 0 : 1;
    }

    // A host whose gateway accepts the overlay only from the device it expects (as Session.AcceptOverlay does).
    sealed class Host : IAsyncDisposable
    {
        public readonly byte[] Cap = RandomNumberGenerator.GetBytes(16);
        public readonly Gateway Gateway;
        public OverlaySession? Overlay;
        public readonly List<string> Refused = new();
        public Host(OverlayIdentity? identity)
        {
            Gateway = new Gateway(new Settings { TcpPorts = [], UdpPorts = [] }, Cap);
            Gateway.AttachOverlay = async (tcp, command, ct) =>
            {
                if (command != 4 || Overlay != null) return;
                try { Overlay = await OverlaySession.AcceptHello(tcp, Gateway.Port, Cap, () => "test", ct, identity); }
                catch (Exception ex) { lock (Refused) Refused.Add(ex.Message); return; }
                Gateway.Overlay = Overlay;
                Overlay.Control = (_, _, _) => Task.FromResult<object>(new { ok = true });
                var link = new OverlayLink("base", tcp, Gateway.SendOverlayUdp, () => "test"); await Overlay.Add(link); await link.Reader;
            };
        }
        public async ValueTask DisposeAsync() { if (Overlay != null) await Overlay.DisposeAsync(); await Gateway.DisposeAsync(); }
    }

    static async Task<string?> TryConnect(Host host, OverlayIdentity? identity, CancellationToken ct)
    {
        try
        {
            await using var caller = await OverlaySession.Connect(host.Gateway.Port, host.Cap, () => "test", ct, identity: identity).WaitAsync(TimeSpan.FromSeconds(10), ct);
            var reply = await caller.Request("ping", new { }, ct);
            return reply.GetProperty("ok").GetBoolean() ? null : "no answer";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static async Task SignedSessions(CancellationToken ct)
    {
        var controller = Device.New(); var hostDevice = Device.New(); var stranger = Device.New();

        await using (var host = new Host(hostDevice.For(controller)))
            Check(await TryConnect(host, controller.For(hostDevice), ct) == null, "the authorised controller's device opens a signed session and talks to the host");

        // a local process that got the session token (e.g. by being admitted on the loopback gateway) but holds no device key
        await using (var host = new Host(hostDevice.For(controller)))
        {
            var unsigned = await TryConnect(host, null, ct);
            Check(unsigned != null && host.Overlay == null, "a session without a device signature is refused, and no overlay is set up");
            Check(host.Refused.Any(r => r.Contains("版本过旧")), "the refusal says the other side needs a newer ArdUi");
            var forged = await TryConnect(host, stranger.For(hostDevice), ct);
            Check(forged != null && host.Overlay == null && host.Refused.Any(r => r.Contains("设备签名不符")), "another device's key cannot open the session");
            Check(await TryConnect(host, controller.For(hostDevice), ct) == null, "after refused attempts the real controller still gets in");
        }

        // the controller checks the host too: a host that does not sign (an old version, or something in between) is not trusted
        await using (var oldHost = new Host(null))
        {
            var result = await TryConnect(oldHost, controller.For(hostDevice), ct);
            Check(result != null && result.Contains("版本过旧"), "a controller refuses a host that does not sign the session");
        }
        // a host signing with the wrong key
        await using (var wrongHost = new Host(stranger.For(controller)))
        {
            var result = await TryConnect(wrongHost, controller.For(hostDevice), ct);
            Check(result != null, "a controller refuses a host signed by a different device");
        }
        // a signature for one session token is not good for another
        var capA = RandomNumberGenerator.GetBytes(16); var capB = RandomNumberGenerator.GetBytes(16);
        var hello = controller.For(hostDevice).SignHello(new OverlayHello(Guid.NewGuid().ToString("N"), "key", 3, 16), capA, null);
        Check(Throws(() => hostDevice.For(controller).Check(hello, capB, null)) && !Throws(() => hostDevice.For(controller).Check(hello, capA, null)), "a signed hello is bound to its session token");
        Check(Throws(() => hostDevice.For(controller).Check(hello with { Key = "other" }, capA, null)), "a signed hello cannot have its key swapped");
    }

    static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }

    static void Registry()
    {
        var granted = Perm.Frd | Perm.Chat;
        Task<object> Ok(string m, JsonElement d, CancellationToken c) => Task.FromResult<object>(m);
        var host = new ControlRegistry(true, () => granted)
            .Add("frd-start", ControlRole.Host, Perm.Frd, Ok).Add("chat-text", ControlRole.Either, Perm.Chat, Ok)
            .Add("chat-file-begin", ControlRole.Either, Perm.Files, Ok).Add("transit-*", ControlRole.Either, Perm.None, Ok);
        var controller = new ControlRegistry(false, () => granted).Add("frd-start", ControlRole.Host, Perm.Frd, Ok);
        string Call(ControlRegistry r, string method) { try { return (string)r.Dispatch(method, default, default).Result; } catch (Exception ex) { return "!" + ex.Message; } }
        Check(Call(host, "frd-start") == "frd-start" && Call(host, "chat-text") == "chat-text", "allowed requests reach their handlers");
        Check(Call(host, "chat-file-begin").Contains("未允许文件"), "a request without its permission is refused before the handler runs");
        Check(Call(controller, "frd-start").Contains("方向无效"), "a host-only request sent to the controller is refused");
        Check(Call(host, "smb-open").Contains("不支持"), "a request that is not in the table is refused");
        Check(Call(host, "transit-status") == "transit-status", "a family of requests is matched by its prefix");
        Check(Throws(() => host.Add("chat-text", ControlRole.Either, Perm.Chat, Ok)), "a method cannot be registered twice");
        granted = Perm.Frd;
        Check(Call(host, "chat-text").Contains("未允许对话"), "the permission is read at each request (a change applies at once)");
    }

    // As many slow requests at once as a peer may have open (16) used to end the whole session (the receiver had 8 slots and dropped
    // the link on the 9th). Now they are all served, and anything beyond the slots would be answered "busy" instead of ending the session.
    static async Task BusyControls(CancellationToken ct)
    {
        var controller = Device.New(); var hostDevice = Device.New();
        await using var host = new Host(hostDevice.For(controller));
        await using var caller = await OverlaySession.Connect(host.Gateway.Port, host.Cap, () => "test", ct, identity: controller.For(hostDevice));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 50 && host.Overlay == null; i++) await Task.Delay(50, ct);
        host.Overlay!.Control = async (_, _, c) => { await release.Task.WaitAsync(c); return new { ok = true }; };
        var requests = Enumerable.Range(0, 16).Select(_ => caller.Request("slow", new { }, ct)).ToArray();
        await Task.Delay(1000, ct);
        Check(requests.All(r => !r.IsCompleted) && !caller.Token.IsCancellationRequested && !host.Overlay.Token.IsCancellationRequested, "16 requests held open at once do not end the session");
        release.SetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Check(requests.All(r => r.Result.GetProperty("ok").GetBoolean()), "all of them are answered");
        host.Overlay.Control = (_, _, _) => Task.FromResult<object>(new { ok = true });
        var after = await caller.Request("ping", new { }, ct);
        Check(after.GetProperty("ok").GetBoolean() && host.Overlay.ControlsRefused == 0, "the session keeps working afterwards");
    }

    static void Redaction()
    {
        var user = Environment.UserName; var machine = Environment.MachineName;
        var text = OverlaySession.ForPeer($@"无法启动 FRD：C:\Users\{user}\AppData\Local\ArdUi\versions\v5.5\frd\FRD.exe。拒绝访问 \\{machine}\share\x");
        Check(!text.Contains(@"C:\") && !text.Contains(@"\\") && !text.Contains(user, StringComparison.OrdinalIgnoreCase) && !text.Contains(machine, StringComparison.OrdinalIgnoreCase), "errors sent to the other side carry no local path, account or computer name");
        Check(text.StartsWith("无法启动 FRD：", StringComparison.Ordinal) && text.Contains("[本机路径]"), "the error still says what failed: " + text);
        Check(OverlaySession.ForPeer("被控端未允许 FRD。") == "被控端未允许 FRD。", "ordinary errors are unchanged");
    }

    static async Task ReceivedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-security-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            ChatChannel? a = null, b = null;
            a = new ChatChannel(Path.Combine(root, "a"), () => (m, d, c) => Deliver(b!, m, d, c));
            b = new ChatChannel(Path.Combine(root, "b"), () => (m, d, c) => Deliver(a!, m, d, c));
            var source = Path.Combine(root, "setup.exe"); Directory.CreateDirectory(root); File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(5000));
            await a.SendFile(source);
            var stored = b.Messages.Last().Path!;
            var mark = File.Exists(stored + ":Zone.Identifier") ? File.ReadAllText(stored + ":Zone.Identifier") : "";
            Check(mark.Contains("ZoneId=3"), "a received file carries the Internet zone mark, so Windows warns before running it");
            Check(!File.Exists(source + ":Zone.Identifier"), "the sender's own file is not marked");
            Check(ChatChannel.MaxPerSession >= ChatChannel.MaxFile && ChatChannel.MaxPerSession <= 16L * 1024 * 1024 * 1024, "one session may send a bounded amount in total");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    static async Task<JsonElement> Deliver(ChatChannel to, string method, object value, CancellationToken ct)
    {
        await Task.Yield();
        var data = JsonSerializer.SerializeToElement(value, Wire.Json);
        return JsonSerializer.SerializeToElement(await to.Handle(method, data, ct), Wire.Json);
    }
}
