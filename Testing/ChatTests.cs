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
            // a fast link lets the chunks grow to their largest; even then a request fits one link frame (the other side drops the link above 64 KB)
            var (fa, _) = Pair(root, "fast");
            var fast = Path.Combine(root, "fast.bin"); File.WriteAllBytes(fast, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
            await fa.SendFile(fast);
            Check(largest > ChatChannel.Chunk && largest <= OverlaySession.MaxControlBody, $"every request stays within one link frame ({largest} bytes at most, limit {OverlaySession.MaxControlBody})");
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
