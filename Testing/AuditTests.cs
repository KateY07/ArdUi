namespace ArdUi;

// `ArdUi.exe --audit-test`: the budget is kept, recording produces real H.264 video (Windows Media Foundation), and bad input is refused.
static class AuditTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    public static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-audit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Pruning(Path.Combine(root, "prune"));
            Grabbing();
            await Recording(Path.Combine(root, "rec"));
            await OnDemand(Path.Combine(root, "demand"));
            await GatewayActivity();
            await Failures(Path.Combine(root, "fail"));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        return failures == 0 ? 0 : 1;
    }

    static void Pruning(string root)
    {
        var store = new AuditStore(root, limit: 1000);
        string Make(string session, string file, int size, int ageHours)
        {
            var folder = Path.Combine(root, session); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, file); File.WriteAllBytes(path, new byte[size]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours)); return path;
        }
        Make("old", "rec-0001.mkv", 400, 30); Make("old", "chat.jsonl", 50, 30);
        Make("mid", "rec-0001.mkv", 400, 20);
        var live1 = Make("live", "rec-0001.mkv", 300, 5); var live2 = Make("live", "rec-0002.mkv", 300, 1);
        File.WriteAllBytes(Path.Combine(root, "index.jsonl"), new byte[100]);
        var freed = store.Prune([Path.Combine(root, "live")]);
        Check(store.UsedBytes() <= 1000 && freed > 0, "the total is brought back under the budget");
        Check(!File.Exists(Path.Combine(root, "old", "rec-0001.mkv")) && !Directory.Exists(Path.Combine(root, "old")), "the oldest session goes first, with its folder");
        Check(File.Exists(live2) && File.Exists(Path.Combine(root, "index.jsonl")), "the segment being written and the index are never deleted");
        Check(store.Prune([Path.Combine(root, "live")]) == 0, "nothing is deleted while it fits");
        // a single huge session still has to fit: its oldest finished segments are overwritten
        var big = new AuditStore(Path.Combine(root, "big"), limit: 500); Directory.CreateDirectory(Path.Combine(root, "big", "s"));
        foreach (var (name, age) in new[] { ("rec-0001.mkv", 3), ("rec-0002.mkv", 2), ("rec-0003.mkv", 1) })
        { var p = Path.Combine(root, "big", "s", name); File.WriteAllBytes(p, new byte[300]); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-age)); }
        big.Prune([Path.Combine(root, "big", "s")]);
        Check(!File.Exists(Path.Combine(root, "big", "s", "rec-0001.mkv")) && File.Exists(Path.Combine(root, "big", "s", "rec-0003.mkv")), "inside one long session the oldest segment is overwritten, never the current one");
        Check(AuditStore.MaxBytes == 10L * 1024 * 1024 * 1024, "the budget is 10 GB");
    }

    static void Grabbing()
    {
        try
        {
            using var grabber = new ScreenGrabber();
            Check(grabber.Width % 2 == 0 && grabber.Height % 2 == 0 && grabber.Width <= 1280 && grabber.Width >= 2, "frames are even-sized and at most 1280 wide");
            var frame = new byte[grabber.FrameBytes];
            var ok = grabber.Grab(frame);
            Check(ok && frame.Any(b => b != 0), "the screen can be grabbed (not an empty frame)");
        }
        catch (IOException ex) { Check(false, "screen capture: " + ex.Message); }
    }

    static async Task Recording(string root)
    {
        var store = new AuditStore(root);
        var session = AuditSession.Open(store, "ABC123", new string('e', 64), true, Perm.Frd, fps: 10, segmentSeconds: 2);
        Check(session.Folder == null && !session.Recording, "nothing is recorded, nor any folder made, until there is something to record");
        var chat = new ChatChannel(Path.Combine(root, "chatfiles"), () => (m, d, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true })));
        session.Attach(chat);
        session.BeginVideo();
        await chat.SendText("审计测试消息");
        await Task.Delay(5200);
        Check(session.Recording && !session.Failed, "recording runs without failing while the screen is in use");
        await session.EndVideo();
        Check(!session.Recording, "recording stops when asked");
        var folder = session.Folder!;
        var first = Directory.GetFiles(folder, "rec-*.mp4").Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Check(first.Length >= 2 && first.All(f => new FileInfo(Path.Combine(folder, f!)).Length > 1000), "video is written in segments");
        // the screen is used again later in the same session: new segments, the old ones stay
        session.BeginVideo(); await Task.Delay(2600); await session.EndVideo();
        var second = Directory.GetFiles(folder, "rec-*.mp4").Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Check(second.Length > first.Length && first.All(second.Contains), "a later recording continues the numbering and never overwrites the earlier video");
        // a real MP4: an 'ftyp' box first, then movie data ('moov' or fragments 'moof') carrying H.264 ('avc1')
        var head = File.ReadAllBytes(Path.Combine(folder, first[0]!)); var headText = System.Text.Encoding.Latin1.GetString(head);
        if (Environment.GetEnvironmentVariable("ARDUI_AUDIT_TEST_KEEP") is { Length: > 0 } keep) File.Copy(Path.Combine(folder, first[0]!), keep, true);
        Check(headText[4..8] == "ftyp" && headText.Contains("avc1", StringComparison.Ordinal) && (headText.Contains("moof", StringComparison.Ordinal) || headText.Contains("moov", StringComparison.Ordinal)), "it is an MP4 file with H.264 video");
        await session.DisposeAsync();
        Check(File.ReadAllText(Path.Combine(folder, "session.json")).Contains("ABC123", StringComparison.Ordinal), "the session is described (who, when, how)");
        Check(File.ReadAllText(Path.Combine(folder, "chat.jsonl")).Contains("审计测试消息", StringComparison.Ordinal), "the conversation is part of the record");
        var index = File.ReadAllText(Path.Combine(root, "index.jsonl"));
        Check(index.Contains("ABC123", StringComparison.Ordinal) && index.Contains("\"once\":true", StringComparison.Ordinal), "the session appears in the index");

        // a connected device that never uses the screen and never speaks leaves nothing behind
        var idle = AuditSession.Open(store, "IDLE00", "e", false, Perm.Frd); await idle.DisposeAsync();
        Check(!File.ReadAllText(Path.Combine(root, "index.jsonl")).Contains("IDLE00", StringComparison.Ordinal) && !Directory.GetDirectories(root).Any(d => d.EndsWith("IDLE00", StringComparison.Ordinal)), "a session without screen use or conversation leaves no record");
        // a conversation alone is kept, without video
        var talk = AuditSession.Open(store, "TALK00", "e", false, Perm.Files);
        talk.Attach(chat); await chat.SendText("只聊天"); await talk.DisposeAsync();
        Check(talk.Folder != null && File.Exists(Path.Combine(talk.Folder, "chat.jsonl")) && Directory.GetFiles(talk.Folder, "rec-*").Length == 0, "a conversation is kept even without any video");
    }

    static async Task OnDemand(string root)
    {
        var service = new AuditService(root, TimeSpan.FromSeconds(1.2));
        var chat = new ChatChannel(Path.Combine(root, "c"), () => null);
        service.Open("ep", "DEM000", false, Perm.Frd, chat);
        Check(!service.IsRecording("ep"), "an open session is not recorded while the screen is idle");
        service.ScreenActivity("ep", true);
        Check(service.IsRecording("ep"), "recording starts when the screen is used");
        service.ScreenActivity("ep", false); await Task.Delay(500);
        Check(service.IsRecording("ep"), "a short pause does not stop it");
        service.ScreenActivity("ep", true); await Task.Delay(1800);
        Check(service.IsRecording("ep"), "using the screen again within the pause keeps one recording going");
        service.ScreenActivity("ep", false); await Task.Delay(2200);
        Check(!service.IsRecording("ep"), "it stops once the screen has been idle for the grace period");
        service.ScreenActivity("ep", true);
        Check(service.IsRecording("ep"), "and starts again with the next use");
        await service.DisposeAsync();
        Check(!service.IsRecording("ep"), "closing the service ends every recording");
    }

    // The gateway tells when the screen is in use: an RDP-style connection counts, FRD counts, files do not.
    static async Task GatewayActivity()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        var port = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () => { try { while (true) { var c = await echo.AcceptTcpClientAsync(); _ = Task.Run(async () => { using (c) { var b = new byte[16]; while (await c.GetStream().ReadAsync(b) > 0) { } } }); } } catch { } });
        var cap = RandomNumberGenerator.GetBytes(16);
        await using var gateway = new Gateway(new Settings { TcpPorts = [port], UdpPorts = [] }, cap);
        var events = new List<bool>(); gateway.ScreenActivity += events.Add;
        Check(!gateway.ScreenActive, "an idle gateway is not 'screen in use'");
        gateway.PermitFrdTcp(5555, true); Check(gateway.ScreenActive && events.SequenceEqual([true]), "FRD running counts as the screen in use");
        gateway.PermitFrdTcp(5555, false); Check(!gateway.ScreenActive && events.SequenceEqual([true, false]), "and stops counting when FRD stops");
        using var forward = new TcpClient(); await forward.ConnectAsync(IPAddress.Loopback, gateway.Port);
        var open = new byte[23]; "AUI1"u8.CopyTo(open); cap.CopyTo(open, 4); open[20] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(open.AsSpan(21), (ushort)port);
        await forward.GetStream().WriteAsync(open); var answer = new byte[1]; await forward.GetStream().ReadExactlyAsync(answer);
        await Task.Delay(200);
        Check(answer[0] == 0 && gateway.ScreenActive, "an open RDP-style connection counts as the screen in use");
        forward.Close(); await Task.Delay(800);
        Check(!gateway.ScreenActive && events.SequenceEqual([true, false, true, false]), "and stops counting when it closes");
    }

    static Task Failures(string root)
    {
        Check(H264Recorder.Available, "this machine has a Windows H.264 encoder");
        try { using var bad = new H264Recorder(root, 101, 64, 10, 60, 1); Check(false, "an odd frame size is refused"); }
        catch (ArgumentException) { Check(true, "an odd frame size is refused"); }
        return Task.CompletedTask;
    }
}
