namespace ArdUi;

// `ArdUi.exe --chat-test`: two channels wired back to back stand in for the two ends of a session.
static class ChatTests
{
    static int failures, largest;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    public static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-chat-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Basics(root);
            await Tampering(root);
            await Transfers(root);
            await Delivery(root);
            await Nudges(root);
            await Resume(root);
            await Receipts(root);
            // a fast link lets the chunks grow to their largest; even then a request fits one link frame (the other side drops the link above 64 KB)
            var (fa, _) = Pair(root, "fast");
            var fast = Path.Combine(root, "fast.bin"); File.WriteAllBytes(fast, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
            await fa.SendFile(fast);
            Check(largest > ChatChannel.Chunk && largest <= OverlaySession.MaxControlBody, $"every request stays within one link frame ({largest} bytes at most, limit {OverlaySession.MaxControlBody})");
            // bytes whose base64 is all '+' (FB EF BE …), which JSON writes as \u002B each: still within one frame, and the file arrives whole
            largest = 0;
            var (pa, pb) = Pair(root, "plus");
            var plus = Path.Combine(root, "plus.bin"); var pattern = new byte[600 * 1024];
            for (var i = 0; i < pattern.Length; i++) pattern[i] = (i % 3) switch { 0 => 0xFB, 1 => 0xEF, _ => 0xBE };
            File.WriteAllBytes(plus, pattern);
            await pa.SendFile(plus);
            Check(largest <= OverlaySession.MaxControlBody - 1000 && File.ReadAllBytes(pb.Messages.Last().Path!).SequenceEqual(pattern),
                $"data that is all '+' in base64 is cut smaller and still arrives ({largest} bytes at most)");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        return failures == 0 ? 0 : 1;
    }

    // a link with a little latency, so transfers take a moment and can be watched and stopped
    static (ChatChannel A, ChatChannel B) Pair(string root, string name, int latency = 0)
    {
        ChatChannel? a = null, b = null;
        ChatSender ToB = (m, d, ct) => Deliver(b!, m, d, ct, latency), ToA = (m, d, ct) => Deliver(a!, m, d, ct, latency);
        a = new ChatChannel(Path.Combine(root, name, "a"), () => ToB);
        b = new ChatChannel(Path.Combine(root, name, "b"), () => ToA);
        return (a, b);
    }

    // What this side sends shows at once with its delivery; one that did not get through can be sent again under the same id, and the
    // other side shows it once whichever attempt arrives (also when the first one arrived and only its answer was lost).
    static async Task Delivery(string root)
    {
        var dir = Path.Combine(root, "delivery");
        ChatChannel? b = null; var down = true; var answerLost = false;
        ChatSender toB = async (m, d, ct) =>
        {
            if (down) throw new IOException("设备连接已断开，无法发送。");
            var reply = await Deliver(b!, m, d, ct, 0);
            if (answerLost) { answerLost = false; throw new TimeoutException("没有收到回执。"); }
            return reply;
        };
        var a = new ChatChannel(Path.Combine(dir, "a"), () => toB);
        b = new ChatChannel(Path.Combine(dir, "b"), () => null);
        var shown = new List<ChatMessage>(); a.MessageAdded += shown.Add;
        try { await a.SendText("线路断开时发出"); } catch (IOException) { }
        Check(shown.Count == 1 && a.Messages[0].Delivery == ArdUi.Delivery.Failed && b.Messages.Count == 0, "a message that cannot go shows at once, as not delivered");
        down = false; a.Retry(a.Messages[0].Id);
        for (var i = 0; i < 100 && a.Messages[0].Delivery != ArdUi.Delivery.Delivered; i++) await Task.Delay(20);
        Check(a.Messages[0].Delivery == ArdUi.Delivery.Delivered && b.Messages.Count == 1 && b.Messages[0].Id == a.Messages[0].Id, "sent again, it is delivered under the same id");

        answerLost = true;
        try { await a.SendText("回执丢了"); } catch (TimeoutException) { }
        var lost = a.Messages.Last();
        Check(lost.Delivery == ArdUi.Delivery.Failed && b.Messages.Count == 2, "the other side got it although this side saw no answer");
        a.Retry(lost.Id);
        for (var i = 0; i < 100 && lost.Delivery != ArdUi.Delivery.Delivered; i++) await Task.Delay(20);
        Check(lost.Delivery == ArdUi.Delivery.Delivered && b.Messages.Count == 2, "sending it again does not show it twice");

        var file = Path.Combine(root, "delivery.bin"); File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(100_000));
        answerLost = false;
        var transfer = a.StartSend(file); var message = a.Messages.Last();
        Check(message.Kind == ChatKind.File && message.Transfer == transfer, "a file shows at once as a message with its transfer");
        await transfer.Completion;
        Check(message.Delivery == ArdUi.Delivery.Delivered && b.Messages.Count == 3, "and is delivered when it has arrived");
        message.Delivery = ArdUi.Delivery.Failed; a.Retry(message.Id);
        await message.Transfer!.Completion;
        Check(message.Delivery == ArdUi.Delivery.Delivered && b.Messages.Count == 3 && message.Transfer.Transferred == message.Size, "a file the other side already has is not sent again");
    }

    // "抖一抖": shown on both sides, at most one every NudgeSeconds from this side, and the receiving side ignores more than that too;
    // the island keeps its bubble until it is clicked or closed.
    static async Task Nudges(string root)
    {
        var (a, b) = Pair(root, "nudge");
        await a.SendNudge();
        Check(a.Messages.Last().Kind == ChatKind.Nudge && a.Messages.Last().Delivery == ArdUi.Delivery.Delivered && b.Messages.Count == 1 && b.Messages[0].Kind == ChatKind.Nudge && b.Unread == 1,
            "a nudge is delivered and shows on the other side");
        try { await a.SendNudge(); Check(false, "a second nudge right away is refused"); }
        catch (InvalidOperationException) { Check(a.NudgeWait > TimeSpan.Zero && b.Messages.Count == 1, "a second nudge right away is refused"); }
        await b.Handle("chat-nudge", JsonSerializer.SerializeToElement(new { id = Guid.NewGuid().ToString("N") }), CancellationToken.None);
        Check(b.Messages.Count == 1, "and the receiving side ignores one that comes too soon anyway");

        var clock = DateTimeOffset.Now; var island = new IslandModel(() => clock);
        island.SetConnected(1);
        island.NudgeArrived("peer", "SY0812A");
        clock += TimeSpan.FromMinutes(10); island.Tick();
        Check(island.BubbleVisible && island.BubbleSticky && island.BubbleText == "SY0812A 发送了抖一抖", "the island's nudge bubble does not time out");
        island.MessageArrived("peer", "SY0812A", "你好");
        Check(island.BubbleText == "SY0812A 发送了抖一抖", "a message meanwhile does not push it away");
        island.DismissBubble();
        Check(!island.BubbleVisible && !island.BubbleSticky, "closing it puts it away");
    }

    // A file whose connection broke continues from where it stopped when it is sent again, and arrives whole (checksum over all of it).
    static async Task Resume(string root)
    {
        var dir = Path.Combine(root, "resume");
        ChatChannel? b = null; var down = false; long sentAfterRetry = 0; var counting = false;
        ChatSender toB = async (m, d, ct) =>
        {
            if (down) throw new IOException("设备连接已断开，无法发送。");
            if (counting && m == "chat-file-chunk") sentAfterRetry += Convert.FromBase64String(JsonSerializer.SerializeToElement(d).GetProperty("data").GetString()!).Length;
            await Task.Delay(3, ct);
            return await Deliver(b!, m, d, ct, 0);
        };
        var a = new ChatChannel(Path.Combine(dir, "a"), () => toB);
        b = new ChatChannel(Path.Combine(dir, "b"), () => null);
        var data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var source = Path.Combine(root, "resume.bin"); File.WriteAllBytes(source, data);
        ChatTransfer? incoming = null; b.TransferStarted += t => incoming = t;
        var transfer = a.StartSend(source); var message = a.Messages.Last();
        while (incoming == null || incoming.Transferred < data.Length / 3) await Task.Delay(10);
        down = true; b.Close();   // the connection breaks
        await transfer.Completion;
        var parts = Directory.GetFiles(Path.Combine(dir, "b"), "*.part");
        Check(message.Delivery == ArdUi.Delivery.Failed && parts.Length == 1 && new FileInfo(parts[0]).Length > 0, "a broken file keeps what arrived (as .part) and shows as not delivered");
        down = false; counting = true; a.Retry(message.Id);
        await message.Transfer!.Completion;
        Check(message.Delivery == ArdUi.Delivery.Delivered && b.Messages.Count == 1 && File.ReadAllBytes(b.Messages[0].Path!).SequenceEqual(data), "sent again, it continues and arrives whole");
        Check(sentAfterRetry > 0 && sentAfterRetry < data.Length, $"only the rest was sent again ({sentAfterRetry} of {data.Length} bytes)");
        Check(Directory.GetFiles(Path.Combine(dir, "b"), "*.part").Length == 0, "no .part is left behind");

        // the sender gives up while this side still thinks the file is under way (it never heard): a retry continues all the same
        var dir2 = Path.Combine(root, "resume-stale");
        ChatChannel? d = null; var cut = false; long sent2 = 0; var counting2 = false;
        ChatSender toD = async (m, data2, ct) =>
        {
            if (cut && m == "chat-file-chunk") throw new IOException("控制请求超时。");
            if (counting2 && m == "chat-file-chunk") sent2 += Convert.FromBase64String(JsonSerializer.SerializeToElement(data2).GetProperty("data").GetString()!).Length;
            await Task.Delay(3, ct);
            return await Deliver(d!, m, data2, ct, 0);
        };
        var c = new ChatChannel(Path.Combine(dir2, "c"), () => toD);
        d = new ChatChannel(Path.Combine(dir2, "d"), () => null);
        ChatTransfer? arriving = null; d.TransferStarted += t => arriving = t;
        var t2 = c.StartSend(source); var m2 = c.Messages.Last();
        while (arriving == null || arriving.Transferred < data.Length / 2) await Task.Delay(10);
        cut = true; await t2.Completion;   // the sender's side fails; the receiver is not told and still shows it as arriving
        Check(m2.Delivery == ArdUi.Delivery.Failed && arriving.State == TransferState.Active, "a sender that gave up leaves the receiver waiting");
        cut = false; counting2 = true; c.Retry(m2.Id);
        await m2.Transfer!.Completion;
        Check(m2.Delivery == ArdUi.Delivery.Delivered && d.Messages.Count == 1 && File.ReadAllBytes(d.Messages[0].Path!).SequenceEqual(data) && sent2 < data.Length,
            $"its retry continues from what arrived ({sent2} of {data.Length} bytes sent again)");

        // the receiving ArdUi was restarted meanwhile (nothing remembered): the retry still continues from the kept .part
        var dir3 = Path.Combine(root, "resume-restart");
        ChatChannel? f = null; var cut3 = false; long sent3 = 0; var counting3 = false;
        ChatSender toF = async (m, data3, ct) =>
        {
            if (cut3) throw new IOException("设备连接已断开，无法发送。");
            if (counting3 && m == "chat-file-chunk") sent3 += Convert.FromBase64String(JsonSerializer.SerializeToElement(data3).GetProperty("data").GetString()!).Length;
            await Task.Delay(3, ct);
            return await Deliver(f!, m, data3, ct, 0);
        };
        var e3 = new ChatChannel(Path.Combine(dir3, "e"), () => toF);
        f = new ChatChannel(Path.Combine(dir3, "f"), () => null);
        ChatTransfer? arriving3 = null; f.TransferStarted += t => arriving3 = t;
        var t3 = e3.StartSend(source); var m3 = e3.Messages.Last();
        while (arriving3 == null || arriving3.Transferred < data.Length / 2) await Task.Delay(10);
        cut3 = true; await t3.Completion;
        f.Close();
        f = new ChatChannel(Path.Combine(dir3, "f"), () => null);   // restarted: a new channel over the same folder
        cut3 = false; counting3 = true; e3.Retry(m3.Id);
        await m3.Transfer!.Completion;
        Check(m3.Delivery == ArdUi.Delivery.Delivered && File.ReadAllBytes(f.Messages[0].Path!).SequenceEqual(data) && sent3 < data.Length
              && Directory.GetFiles(Path.Combine(dir3, "f"), "*.part").Length == 0, $"after a restart the retry continues from the kept .part ({sent3} of {data.Length} bytes sent again)");
    }

    // Read receipts: seeing the conversation tells the other side, once, up to which message; with receipts off nothing is told.
    static async Task Receipts(string root)
    {
        var (a, b) = Pair(root, "receipts");
        await a.SendText("第一条"); await a.SendText("第二条");
        Check(a.Messages.All(m => !m.Read), "delivered is not read yet");
        var changed = 0; a.MessageChanged += _ => changed++;
        b.Seen(receipts: false); await Task.Delay(100);
        Check(a.Messages.All(m => !m.Read), "with receipts off the other side is not told");
        b.Seen(); for (var i = 0; i < 100 && !a.Messages.All(m => m.Read); i++) await Task.Delay(20);
        Check(a.Messages.All(m => m.Read) && changed == 2, "seeing the conversation marks what was sent as read");
        await a.SendText("第三条"); b.Seen(); for (var i = 0; i < 100 && !a.Messages[2].Read; i++) await Task.Delay(20);
        Check(a.Messages[2].Read, "a later message is marked read when it is seen");
    }

    static async Task Basics(string root)
    {
        var (a, b) = Pair(root, "basics");
        var heard = new List<ChatMessage>(); b.MessageAdded += heard.Add;
        await a.SendText("你好，ArdUi");
        Check(b.Messages.Count == 1 && b.Messages[0].Text == "你好，ArdUi" && !b.Messages[0].Mine, "text arrives and is theirs");
        Check(a.Messages.Count == 1 && a.Messages[0].Mine, "text is kept as mine on the sender");
        Check(b.Unread == 1 && a.Unread == 0, "only the receiver has an unread message");
        b.MarkRead(); Check(b.Unread == 0, "reading clears the count");
        Check(heard.Count == 1, "message event fires once");
        await ExpectFail(() => a.SendText(new string('x', ChatChannel.MaxText + 1)), "too long a text is refused");
        await a.SendText("   "); Check(a.Messages.Count == 1, "an empty text is ignored");

        var source = Path.Combine(root, "source.bin");
        var payload = RandomNumberGenerator.GetBytes(ChatChannel.Chunk * 7 + 777); File.WriteAllBytes(source, payload);
        await a.SendFile(source);
        var got = b.Messages.Last();
        Check(got.Kind == ChatKind.File && got.Size == payload.Length && File.ReadAllBytes(got.Path!).SequenceEqual(payload), "a multi-chunk file arrives byte for byte");
        Check(Path.GetDirectoryName(got.Path) == Path.Combine(root, "basics", "b"), "it is stored in the receiver's chat folder");
        var png = Path.Combine(root, "pic.png"); File.WriteAllBytes(png, RandomNumberGenerator.GetBytes(2000));
        await a.SendFile(png); Check(b.Messages.Last().Kind == ChatKind.Image, "a picture is shown as a picture");
        var bigPng = Path.Combine(root, "big.png"); File.WriteAllBytes(bigPng, new byte[ChatChannel.MaxImage + 10]);
        await a.SendFile(bigPng); Check(b.Messages.Last().Kind == ChatKind.File, "a very large picture is sent as a plain file");
        await ExpectFail(() => a.SendFile(Path.Combine(root, "missing.bin")), "a missing file is refused");
        var empty = Path.Combine(root, "empty.txt"); File.WriteAllBytes(empty, []); await ExpectFail(() => a.SendFile(empty), "an empty file is refused");
        Check(ChatChannel.SafeName(@"..\..\Windows\evil.exe") == "evil.exe" && ChatChannel.SafeName("../x/y.txt") == "y.txt", "path parts in a file name are dropped");
        Check(ChatChannel.SafeName("CON.txt") == "_CON.txt" && ChatChannel.SafeName("a<b>:c.txt") == "a_b__c.txt" && ChatChannel.SafeName("  ") == "file", "reserved and invalid names are made safe");
        Check(ChatChannel.MaxFile >= 2L * 1024 * 1024 * 1024, "large files (videos) are allowed");
    }

    static async Task Tampering(string root)
    {
        var (_, b) = Pair(root, "tamper");
        string Zero() => new('0', 64);
        await ExpectFail(() => Raw(b, "chat-file-begin", new { id = "1", kind = "File", name = "x", size = ChatChannel.MaxFile + 1 }), "a file over the limit is refused");
        await Raw(b, "chat-file-begin", new { id = "3", kind = "File", name = "t.bin", size = 4 });
        await Raw(b, "chat-file-chunk", new { id = "3", seq = 0, data = Convert.ToBase64String(new byte[4]) });
        var before = b.Messages.Count;
        await ExpectFail(() => Raw(b, "chat-file-end", new { id = "3", sha256 = Zero() }), "a file whose checksum does not match is discarded");
        Check(b.Messages.Count == before, "and never shows up in the conversation");
        await Raw(b, "chat-file-begin", new { id = "4", kind = "File", name = "o.bin", size = 100 });
        await ExpectFail(() => Raw(b, "chat-file-chunk", new { id = "4", seq = 99, data = Convert.ToBase64String(new byte[10]) }), "a chunk far out of order aborts the transfer");
        await ExpectFail(() => Raw(b, "chat-file-chunk", new { id = "4", seq = 0, data = Convert.ToBase64String(new byte[10]) }), "an aborted transfer cannot be continued");
        await Raw(b, "chat-file-begin", new { id = "5", kind = "File", name = "p.bin", size = 100 });
        await Raw(b, "chat-file-begin", new { id = "6", kind = "File", name = "q.bin", size = 100 });
        await Raw(b, "chat-file-begin", new { id = "7", kind = "File", name = "r.bin", size = 100 });
        await ExpectFail(() => Raw(b, "chat-file-begin", new { id = "8", kind = "File", name = "s.bin", size = 100 }), "only a few files are received at once");
        b.Close();
        Check(b.ActiveTransfers.Length == 0, "closing ends every transfer");
        Check(Directory.GetFiles(Path.Combine(root, "tamper", "b")).Length == 0, "and throws unfinished files away");
        await ExpectFail(() => Raw(b, "chat-wat", new { }), "an unknown chat request is refused");
    }

    static async Task Transfers(string root)
    {
        // chunks arriving out of order are still written in order
        var (_, ooo) = Pair(root, "order");
        var data = RandomNumberGenerator.GetBytes(ChatChannel.Chunk * 3 + 10);
        await Raw(ooo, "chat-file-begin", new { id = "o", kind = "File", name = "o.bin", size = data.Length });
        foreach (var seq in new[] { 1, 0, 3, 2 })
        {
            var from = seq * ChatChannel.Chunk; var piece = data[from..Math.Min(data.Length, from + ChatChannel.Chunk)];
            await Raw(ooo, "chat-file-chunk", new { id = "o", seq, data = Convert.ToBase64String(piece) });
        }
        await Raw(ooo, "chat-file-end", new { id = "o", sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() });
        Check(ooo.Messages.Count == 1 && File.ReadAllBytes(ooo.Messages[0].Path!).SequenceEqual(data), "out-of-order chunks assemble into the right file");

        // progress and speed while a file is on its way
        var (a, b) = Pair(root, "progress", latency: 30);
        var big = Path.Combine(root, "big.bin"); File.WriteAllBytes(big, RandomNumberGenerator.GetBytes(ChatChannel.Chunk * 80));
        var seen = new List<double>(); double fastest = 0;
        var transfer = a.StartSend(big);
        transfer.Changed += () => { seen.Add(transfer.Fraction); fastest = Math.Max(fastest, transfer.BytesPerSecond); };
        Check(transfer.State == TransferState.Active && !transfer.Incoming, "starting a send returns at once, the transfer is active");
        await transfer.Completion;
        Check(transfer.State == TransferState.Done && transfer.Fraction == 1 && seen.Count > 2 && seen.SequenceEqual(seen.OrderBy(x => x)), "progress only moves forward and ends at 100%");
        Check(fastest > 0, "a speed is reported while it runs");
        Check(b.Messages.Count == 1 && b.ActiveTransfers.Length == 0, "the receiver ends with the file and no transfer left");

        // the sender stops it
        var (a2, b2) = Pair(root, "cancel-send", latency: 15);
        var t2 = a2.StartSend(big); ChatTransfer? seenByB = null; b2.TransferStarted += t => seenByB = t;
        while (t2.Transferred < ChatChannel.Chunk * 4) await Task.Delay(20);
        t2.Cancel(); await t2.Completion; await Task.Delay(200);
        Check(t2.State == TransferState.Cancelled && b2.Messages.Count == 0, "the sender can cancel and nothing arrives");
        Check(seenByB?.State == TransferState.Cancelled && Directory.GetFiles(Path.Combine(root, "cancel-send", "b")).Length == 0, "the receiver is told, and the partial file is thrown away");

        // the receiver stops it
        var (a3, b3) = Pair(root, "cancel-receive", latency: 15);
        ChatTransfer? incoming = null; b3.TransferStarted += t => incoming = t;
        var t3 = a3.StartSend(big);
        while (incoming == null || incoming.Transferred < ChatChannel.Chunk * 4) await Task.Delay(20);
        incoming.Cancel(); await t3.Completion;
        Check(t3.State == TransferState.Cancelled && incoming.State == TransferState.Cancelled, "the receiver can turn a file down mid-way and the sender stops too");

        // a slow link: only a little is on the wire, so the session's own traffic is never stuck behind a file
        ChatChannel? recv = null; var concurrent = 0; var most = 0;
        ChatSender slow = async (m, d, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent); most = Math.Max(most, now);
            try { await Task.Delay(1600, ct); var r = await recv!.Handle(m, JsonSerializer.SerializeToElement(d, Wire.Json), ct); return JsonSerializer.SerializeToElement(r, Wire.Json); }
            finally { Interlocked.Decrement(ref concurrent); }
        };
        var sender = new ChatChannel(Path.Combine(root, "slow", "a"), () => slow); recv = new ChatChannel(Path.Combine(root, "slow", "b"), () => null);
        var small = Path.Combine(root, "small.bin"); File.WriteAllBytes(small, RandomNumberGenerator.GetBytes(70_000));
        await sender.SendFile(small);
        Check(most <= 2 && recv.Messages.Count == 1, "on a slow link at most two chunks are in flight, and the file still arrives");

        // sending goes on beside the conversation, and several files at once
        var (a4, b4) = Pair(root, "parallel", latency: 15);
        var first = a4.StartSend(big); var second = a4.StartSend(big);
        var watch = Stopwatch.StartNew();
        await a4.SendText("发送文件的同时还能聊天");
        Check(first.State == TransferState.Active && second.State == TransferState.Active && watch.Elapsed < TimeSpan.FromSeconds(2), "a message goes through while two files are still being sent");
        await Task.WhenAll(first.Completion, second.Completion);
        Check(first.State == TransferState.Done && second.State == TransferState.Done && b4.Messages.Count == 3, "both files and the message arrive");
        Check(b4.Messages.First().Kind == ChatKind.Text, "the message did not wait for the files");
    }

    static Task<JsonElement> Deliver(ChatChannel to, string method, object value, CancellationToken ct, int latency) =>
        Task.Run(async () =>
        {
            largest = Math.Max(largest, JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json).Length);   // what the link would have to carry as one frame
            if (latency > 0) await Task.Delay(latency, ct);
            var result = await to.Handle(method, JsonSerializer.SerializeToElement(value, Wire.Json), ct);
            return JsonSerializer.SerializeToElement(result, Wire.Json);
        }, ct);
    static Task Raw(ChatChannel to, string method, object value) => to.Handle(method, JsonSerializer.SerializeToElement(value, Wire.Json), CancellationToken.None);
    static async Task ExpectFail(Func<Task> action, string name)
    {
        try { await action(); Check(false, name); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException) { Check(true, name); }
    }
}
