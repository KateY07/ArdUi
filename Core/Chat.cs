namespace ArdUi;

enum ChatKind { Text, Image, File, Nudge }

// Sends one control request to the other side (the control channel of the session) and returns its answer.
delegate Task<JsonElement> ChatSender(string method, object value, CancellationToken ct);

enum Delivery { Sending, Delivered, Failed }

// One line of the conversation. Files and images are stored on this machine; Path points at the stored copy (for a file this side
// sends: the file it sends). A file is a message with data attached: what this side sends shows at once, with its delivery
// (sending, delivered, failed and retried under the same id) and, for a file, its transfer.
sealed record ChatMessage(string Id, bool Mine, ChatKind Kind, string Text, string? FileName, long Size, string? Path, DateTimeOffset At)
{
    public Delivery Delivery { get; set; } = Delivery.Delivered;
    public bool Read { get; set; }   // a message this side sent that the other side has seen (its read receipt)
    public string? Error { get; set; }
    public ChatTransfer? Transfer { get; set; }
}

enum TransferState { Active, Done, Cancelled, Failed }

// A file on its way, either direction: how far it is, how fast, and a way to stop it. Several run side by side with the conversation.
sealed class ChatTransfer
{
    public string Id { get; }
    public string Name { get; }
    public ChatKind Kind { get; }
    public long Size { get; }
    public bool Incoming { get; }
    public long Transferred { get; private set; }
    public TransferState State { get; private set; } = TransferState.Active;
    public string? Error { get; private set; }
    public double BytesPerSecond { get; private set; }
    public Task Completion => done.Task;
    public event Action? Changed;
    readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastBytes, lastMilliseconds, lastEvent;
    Action? stop;

    internal ChatTransfer(string id, string name, ChatKind kind, long size, bool incoming, Action stop)
    { Id = id; Name = name; Kind = kind; Size = size; Incoming = incoming; this.stop = stop; }

    public double Fraction => Size == 0 ? 0 : Math.Min(1, (double)Transferred / Size);
    public TimeSpan? Remaining => BytesPerSecond > 1 && State == TransferState.Active ? TimeSpan.FromSeconds((Size - Transferred) / BytesPerSecond) : null;

    internal void Report(long bytes)
    {
        Transferred = bytes;
        var now = clock.ElapsedMilliseconds;
        if (now - lastMilliseconds >= 250)
        {
            var instant = (bytes - lastBytes) * 1000.0 / Math.Max(1, now - lastMilliseconds);
            BytesPerSecond = BytesPerSecond == 0 ? instant : BytesPerSecond * 0.6 + instant * 0.4;   // smoothed
            lastBytes = bytes; lastMilliseconds = now;
        }
        if (now - lastEvent >= 100) { lastEvent = now; Changed?.Invoke(); }
    }

    internal void Finish(TransferState state, string? error = null)
    {
        if (State != TransferState.Active) return;
        State = state; Error = error; if (state == TransferState.Done) Transferred = Size; BytesPerSecond = 0;
        done.TrySetResult(); Changed?.Invoke();
    }

    // The user cancels: the transfer stops on this side and the other side is told.
    public void Cancel() { var action = stop; if (State == TransferState.Active) action?.Invoke(); }
}

// The conversation with one device, carried by the control channel of its encrypted session (no server involved).
// Text goes in one request. A file goes as begin / numbered chunks (several in flight) / end with its checksum; each side shows the
// progress and either can stop it. Transfers run beside each other and beside the conversation.
sealed class ChatChannel
{
    public const long MaxFile = 2L * 1024 * 1024 * 1024, MaxImage = 10L * 1024 * 1024;
    // A control request travels as ONE frame of the session's link, and the receiving side drops the whole link when a frame is over
    // 64 KB. A chunk is therefore small enough that its base64 JSON stays well under that (36 KB raw -> about 49 KB on the wire).
    public const int MaxText = 8000, Chunk = 36 * 1024, MaxIncoming = 3, Window = 6, MaxReorder = 16;
    const int MaxChunkJson = 52000;   // the chunk's data as written in JSON, leaving room under the 60 000-byte control message
    public const long MaxPerSession = 10L * 1024 * 1024 * 1024;   // all files one session may send to this machine
    long receivedThisSession;
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    readonly string folder;
    readonly Func<ChatSender?> sender;
    readonly object gate = new();
    readonly Dictionary<string, Incoming> incoming = new();
    readonly Dictionary<string, ChatTransfer> transfers = new();
    readonly Dictionary<string, CancellationTokenSource> outgoing = new();
    readonly HashSet<string> turnedDown = new();   // files the user refused: what is still on the way for them is dropped quietly
    // Chat never uses more than a few of the control channel's slots (FRD and path switching share it), nor much of the link.
    readonly SemaphoreSlim inflight = new(4);
    readonly SemaphoreSlim textSlots = new(2);   // text has its own: it never waits behind the chunks of a file
    readonly HashSet<string> seen = new();       // ids of messages and files in this conversation: a retry or a resend is shown once
    public List<ChatMessage> Messages { get; } = new();
    public int Unread { get; private set; }
    public event Action<ChatMessage>? MessageAdded;      // either direction
    public event Action<ChatMessage>? MessageChanged;    // a message this side sends: delivered, failed, retried
    public event Action<ChatMessage>? MessageCompleted;  // received, or delivered: what the audit records
    public event Action<ChatTransfer>? TransferStarted;  // either direction
    public event Action? Changed;

    public ChatChannel(string folder, Func<ChatSender?> sender) { this.folder = folder; this.sender = sender; }

    // A file arriving is written to <name>.part and gets its name when it is complete and checked. When the connection breaks, what
    // arrived is kept (paused) and the sender's retry continues from there; a file turned down or failing its check is thrown away.
    readonly Dictionary<string, Incoming> paused = new();
    sealed class Incoming
    {
        public required string Path, Name; public required ChatKind Kind; public required long Size; public required ChatTransfer Transfer;
        public string Part => Path + ".part";
        // each round of a transfer (the first, and every continuation) has its own name: pieces of an earlier round that arrive late
        // (they were already on their way when it failed) are let go instead of being written into this one
        public string Attempt = Guid.NewGuid().ToString("N");
        public FileStream? Stream; public long Written; public int Next; public IncrementalHash? Sha;
        public readonly Dictionary<int, byte[]> Pending = new();
    }

    public ChatTransfer[] ActiveTransfers { get { lock (gate) return transfers.Values.Where(t => t.State == TransferState.Active).ToArray(); } }
    public void MarkRead() { if (Unread == 0) return; Unread = 0; Changed?.Invoke(); }

    // Read receipts: while this conversation is on screen, the other side is told the last of its messages seen here (one small
    // message when that changes, not one per message). Off when the person turned receipts off in the settings.
    string? reportedRead;
    public void Seen(bool receipts = true)
    {
        string? last; lock (gate) last = Messages.LastOrDefault(m => !m.Mine)?.Id;
        if (!receipts || last == null || last == reportedRead) return;
        reportedRead = last;
        _ = Task.Run(async () =>
        {
            try { await Ask("chat-read", new { upTo = last }, CancellationToken.None, textSlots); }
            catch (Exception ex) { if (reportedRead == last) reportedRead = null; Diagnostics.Log("chat-read", ex.Message); }   // told again next time
        });
    }

    void Add(ChatMessage message, bool received)
    {
        lock (gate) { Messages.Add(message); seen.Add(message.Id); if (received) Unread++; }
        MessageAdded?.Invoke(message); Changed?.Invoke();
        if (message.Delivery == Delivery.Delivered) MessageCompleted?.Invoke(message);
    }

    void Mark(ChatMessage message, Delivery delivery, string? error = null)
    {
        message.Delivery = delivery; message.Error = error;
        MessageChanged?.Invoke(message); Changed?.Invoke();
        if (delivery == Delivery.Delivered) MessageCompleted?.Invoke(message);
    }

    // ---- names -----------------------------------------------------------------------------------------------------
    // Whatever the other side calls the file, it is stored under a plain file name inside this device's chat folder.
    public static string SafeName(string? name)
    {
        var text = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/'));
        var clean = new string(text.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length == 0) clean = "file";
        if (clean.Length > 120) { var ext = System.IO.Path.GetExtension(clean); clean = clean[..Math.Max(1, 120 - ext.Length)] + ext; }
        var stem = System.IO.Path.GetFileNameWithoutExtension(clean).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])) clean = "_" + clean;
        return clean;
    }
    public static bool LooksLikeImage(string name) => ImageExtensions.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());

    // A received file carries the "downloaded from the Internet" mark (Mark of the Web), as a browser download would: opening a program or
    // a script from it brings up the Windows SmartScreen / security warning instead of running it silently.
    internal static void MarkFromInternet(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { Diagnostics.Log("chat-motw", ex.Message); }   // e.g. a FAT drive has no streams
    }

    string Unique(string name)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = System.IO.Path.Combine(folder, stamp + "_" + name);
        for (var i = 2; File.Exists(path) || File.Exists(path + ".part"); i++) path = System.IO.Path.Combine(folder, $"{stamp}_{i}_{name}");
        return path;
    }

    // ---- sending ---------------------------------------------------------------------------------------------------
    async Task<JsonElement> Ask(string method, object value, CancellationToken ct, SemaphoreSlim? slots = null)
    {
        var send = sender() ?? throw new IOException("设备连接已断开，无法发送。");
        slots ??= inflight;
        await slots.WaitAsync(ct);
        try { return await send(method, value, ct); }
        finally { slots.Release(); }
    }

    // The message shows at once as sending; this returns when it was delivered and throws when it was not (it then shows as failed
    // and can be sent again with Retry).
    public async Task SendText(string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxText) throw new InvalidOperationException($"一条消息最多 {MaxText} 个字符。");
        var message = new ChatMessage(Guid.NewGuid().ToString("N"), true, ChatKind.Text, text, null, 0, null, DateTimeOffset.Now) { Delivery = Delivery.Sending };
        Add(message, false);
        await DeliverText(message, ct);
    }

    async Task DeliverText(ChatMessage message, CancellationToken ct)
    {
        try
        {
            if (message.Kind == ChatKind.Nudge) await Ask("chat-nudge", new { id = message.Id }, ct, textSlots);
            else await Ask("chat-text", new { id = message.Id, text = message.Text }, ct, textSlots);
            Mark(message, Delivery.Delivered);
        }
        catch (Exception ex) { Mark(message, Delivery.Failed, ex is OperationCanceledException ? "已取消" : ex.Message); throw; }
    }

    // "抖一抖": gets the other side's attention (its island shakes and keeps a note until it is seen). At most one every NudgeSeconds,
    // on this side and, against anyone who changes that, on the receiving side too.
    public const int NudgeSeconds = 10;
    DateTimeOffset lastNudgeSent = DateTimeOffset.MinValue, lastNudgeReceived = DateTimeOffset.MinValue;
    public TimeSpan NudgeWait { get { var left = lastNudgeSent + TimeSpan.FromSeconds(NudgeSeconds) - DateTimeOffset.Now; return left > TimeSpan.Zero ? left : TimeSpan.Zero; } }
    public async Task SendNudge(CancellationToken ct = default)
    {
        if (NudgeWait > TimeSpan.Zero) throw new InvalidOperationException($"抖一抖太频繁了，请 {Math.Ceiling(NudgeWait.TotalSeconds)} 秒后再试。");
        lastNudgeSent = DateTimeOffset.Now;
        var message = new ChatMessage(Guid.NewGuid().ToString("N"), true, ChatKind.Nudge, "", null, 0, null, DateTimeOffset.Now) { Delivery = Delivery.Sending };
        Add(message, false);
        await DeliverText(message, ct);
    }

    // Sends a message that did not get through once more, under the same id (the other side shows it once, whichever attempt arrives).
    public void Retry(string id)
    {
        ChatMessage? message; lock (gate) message = Messages.FirstOrDefault(m => m.Id == id && m.Mine && m.Delivery == Delivery.Failed);
        if (message == null) return;
        Mark(message, Delivery.Sending);
        if (message.Kind is ChatKind.Text or ChatKind.Nudge) { _ = Task.Run(async () => { try { await DeliverText(message, CancellationToken.None); } catch { } }); return; }
        if (message.Path == null || !File.Exists(message.Path)) { Mark(message, Delivery.Failed, "原文件已不存在，无法重发"); return; }
        Begin(message, message.Path);
    }

    // Starts a transfer and returns at once; it goes on in the background (the transfer reports its progress and can be cancelled).
    public ChatTransfer StartSend(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("找不到文件。", path);
        if (info.Length == 0) throw new InvalidOperationException("文件是空的。");
        if (info.Length > MaxFile) throw new InvalidOperationException($"文件超过 {MaxFile / 1024 / 1024 / 1024} GB。");
        var name = SafeName(info.Name);
        var kind = LooksLikeImage(name) && info.Length <= MaxImage ? ChatKind.Image : ChatKind.File;
        var message = new ChatMessage(Guid.NewGuid().ToString("N"), true, kind, "", name, info.Length, path, DateTimeOffset.Now) { Delivery = Delivery.Sending };
        var transfer = Begin(message, path, add: true);
        return transfer;
    }

    // A file message on its way (first time or again): its transfer is attached to it and drives its delivery.
    ChatTransfer Begin(ChatMessage message, string path, bool add = false)
    {
        var cts = new CancellationTokenSource();
        var transfer = new ChatTransfer(message.Id, message.FileName!, message.Kind, message.Size, false, () => cts.Cancel());
        lock (gate) { transfers[message.Id] = transfer; outgoing[message.Id] = cts; }
        message.Transfer = transfer;
        if (add) Add(message, false); else { MessageChanged?.Invoke(message); Changed?.Invoke(); }
        TransferStarted?.Invoke(transfer);
        _ = Task.Run(() => SendCore(message, transfer, path, cts));
        return transfer;
    }

    // The awaitable form: returns when the file has arrived, throws if it did not.
    public async Task SendFile(string path, CancellationToken ct = default)
    {
        var transfer = StartSend(path);
        using var registration = ct.Register(transfer.Cancel);
        await transfer.Completion;
        if (transfer.State != TransferState.Done) throw new IOException(transfer.Error ?? (transfer.State == TransferState.Cancelled ? "已取消。" : "发送失败。"));
    }

    async Task SendCore(ChatMessage message, ChatTransfer transfer, string path, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var begun = await Ask("chat-file-begin", new { id = transfer.Id, kind = transfer.Kind.ToString(), name = transfer.Name, size = transfer.Size, resume = true }, ct);
            // the other side already has it (an earlier attempt arrived, only its answer did not): nothing to send again
            if (begun.ValueKind == JsonValueKind.Object && begun.TryGetProperty("have", out var have) && have.ValueKind == JsonValueKind.True)
            { transfer.Finish(TransferState.Done); Mark(message, Delivery.Delivered); return; }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // the other side kept what arrived before the connection broke: continue from there (the checksum still covers the whole file)
            var offset = begun.ValueKind == JsonValueKind.Object && begun.TryGetProperty("offset", out var at) && at.TryGetInt64(out var kept) && kept > 0 && kept < transfer.Size ? kept : 0;
            var attempt = begun.ValueKind == JsonValueKind.Object && begun.TryGetProperty("attempt", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            if (offset > 0)
            {
                var skip = new byte[1 << 20];
                for (var done = 0L; done < offset;)
                {
                    var read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, offset - done)), ct);
                    if (read == 0) throw new IOException("文件在发送中变短了。");
                    sha.AppendData(skip, 0, read); done += read;
                }
                transfer.Report(offset);
            }
            // How much is on the wire adapts to the link: a transfer must never queue up so much that the session's own keep-alive and
            // probes wait behind it (that makes the path look dead). Few, small chunks while acknowledgements are slow; more, bigger ones when fast.
            var acked = offset; var seq = 0; var window = 2; var chunkSize = 16 * 1024; var clock = Stopwatch.StartNew();
            var pending = new Queue<(Task Ack, int Size, long Started)>();
            async Task Settle()
            {
                var (ack, size, started) = pending.Dequeue(); await ack; acked += size; transfer.Report(acked);
                var latency = clock.ElapsedMilliseconds - started;
                if (latency > 1500) { window = 1; chunkSize = Math.Max(8 * 1024, chunkSize / 2); }
                else if (latency > 700) window = Math.Max(1, window - 1);
                else if (latency < 250) { if (window < Window) window++; else chunkSize = Math.Min(Chunk, chunkSize * 2); }
            }
            while (true)
            {
                var buffer = new byte[chunkSize];
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                // A chunk travels as base64 inside JSON, where each '+' is written as \u002B (six characters): data that turns into many
                // of them (some byte patterns are nothing else) is cut smaller, so the request still fits one link frame; the rest goes next.
                var n = read; var data = Convert.ToBase64String(buffer, 0, n);
                while (n > 1024 && data.Length + 5 * data.Count(c => c == '+') > MaxChunkJson) { n = n * 3 / 4; data = Convert.ToBase64String(buffer, 0, n); }
                if (n < read) stream.Seek(n - read, SeekOrigin.Current);
                sha.AppendData(buffer, 0, n);
                pending.Enqueue((Ask("chat-file-chunk", attempt == null ? new { id = transfer.Id, seq = seq++, data } : (object)new { id = transfer.Id, seq = seq++, data, attempt }, ct), n, clock.ElapsedMilliseconds));
                while (pending.Count >= window) await Settle();
            }
            while (pending.Count != 0) await Settle();
            var sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            await Ask("chat-file-end", attempt == null ? new { id = transfer.Id, sha256 } : (object)new { id = transfer.Id, sha256, attempt }, ct);
            transfer.Finish(TransferState.Done);
            Mark(message, Delivery.Delivered);
        }
        catch (OperationCanceledException)
        {
            // stopped here (by the user), or because the other side stopped it: either way it is over
            try { using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await Ask("chat-file-cancel", new { id = transfer.Id }, brief.Token); } catch (Exception) { }
            transfer.Finish(TransferState.Cancelled, "已取消");
            Mark(message, Delivery.Failed, transfer.Error ?? "已取消");
        }
        catch (Exception ex)
        {
            FileLog.Write("对话", $"发送文件 {transfer.Name} 失败（已发 {transfer.Transferred} / {transfer.Size} 字节）：{ex.GetType().Name}: {ex.Message}");
            transfer.Finish(TransferState.Failed, ex.Message); Mark(message, Delivery.Failed, ex.Message);
        }
        finally { lock (gate) outgoing.Remove(transfer.Id); cts.Dispose(); }
    }

    // ---- receiving -------------------------------------------------------------------------------------------------
    public Task<object> Handle(string method, JsonElement data, CancellationToken ct)
    {
        switch (method)
        {
            case "chat-text":
            {
                var text = data.GetProperty("text").GetString() ?? "";
                if (text.Length == 0 || text.Length > MaxText) throw new IOException("消息长度无效。");
                var id = data.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                lock (gate) if (seen.Contains(id)) return Task.FromResult<object>(new { ok = true });   // sent again: already here
                Add(new ChatMessage(id, false, ChatKind.Text, text, null, 0, null, DateTimeOffset.Now), true);
                return Task.FromResult<object>(new { ok = true });
            }
            case "chat-nudge":
            {
                var id = data.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                lock (gate)
                {
                    if (seen.Contains(id)) return Task.FromResult<object>(new { ok = true });
                    // too soon after the last one: taken, but not shown again
                    if (DateTimeOffset.Now - lastNudgeReceived < TimeSpan.FromSeconds(NudgeSeconds)) { seen.Add(id); return Task.FromResult<object>(new { ok = true }); }
                    lastNudgeReceived = DateTimeOffset.Now;
                }
                Add(new ChatMessage(id, false, ChatKind.Nudge, "", null, 0, null, DateTimeOffset.Now), true);
                return Task.FromResult<object>(new { ok = true });
            }
            case "chat-read":
            {
                // the other side has seen everything this side sent up to this message
                var upTo = data.GetProperty("upTo").GetString() ?? "";
                List<ChatMessage> now = new();
                lock (gate)
                {
                    var at = Messages.FindIndex(m => m.Id == upTo && m.Mine);
                    for (var i = 0; i <= at; i++) if (Messages[i].Mine && !Messages[i].Read && Messages[i].Delivery == Delivery.Delivered) { Messages[i].Read = true; now.Add(Messages[i]); }
                }
                foreach (var message in now) MessageChanged?.Invoke(message);
                if (now.Count != 0) Changed?.Invoke();
                return Task.FromResult<object>(new { ok = true });
            }
            case "chat-file-begin": return Task.FromResult(Begin(data));
            case "chat-file-chunk": return Task.FromResult(Chunked(data));
            case "chat-file-end": return Task.FromResult(End(data));
            case "chat-file-cancel": return Task.FromResult(Cancelled(data));
            default: throw new IOException("对端不支持此对话操作，请更新两端 ArdUi。");
        }
    }

    object Begin(JsonElement data)
    {
        var id = data.GetProperty("id").GetString() ?? throw new IOException("缺少标识。");
        var kind = Enum.TryParse<ChatKind>(data.GetProperty("kind").GetString(), out var k) && k != ChatKind.Text ? k : ChatKind.File;
        var size = data.GetProperty("size").GetInt64();
        var name = SafeName(data.GetProperty("name").GetString());
        if (size <= 0 || size > MaxFile || kind == ChatKind.Image && size > MaxImage) throw new IOException("文件大小超出限制。");
        var resume = data.TryGetProperty("resume", out var asked) && asked.ValueKind == JsonValueKind.True;
        // the sender starts this file again while it still looks under way here (the sender gave up, this side never heard): what
        // arrived so far is kept as if the connection had broken, and the new attempt continues from there
        bool stale; lock (gate) stale = resume && incoming.ContainsKey(id);
        if (stale) Abort(id, TransferState.Failed, "发送方重新开始了这次传输", keep: true);
        Incoming file; ChatTransfer transfer;
        lock (gate)
        {
            if (seen.Contains(id)) return new { ok = true, have = true };   // already received: the sender stops here
            // after a restart nothing is remembered: the newest unfinished copy of this file (<time>_<name>.part, smaller than the file)
            // is taken up again; the checksum at the end still decides whether the result is the sender's file
            if (resume && !paused.ContainsKey(id) && !incoming.ContainsKey(id) && Directory.Exists(folder))
            {
                var kept = new DirectoryInfo(folder).EnumerateFiles("*_" + name + ".part").Where(f => f.Length > 0 && f.Length < size)
                    .Where(f => incoming.Values.All(i => !string.Equals(i.Part, f.FullName, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (kept != null)
                    paused[id] = new Incoming { Path = kept.FullName[..^".part".Length], Name = name, Kind = kind, Size = size, Written = kept.Length,
                                                Transfer = new ChatTransfer(id, name, kind, size, true, () => { }) };
            }
            if (paused.Remove(id, out var stopped))
            {
                if (resume && stopped.Size == size && stopped.Name == name && File.Exists(stopped.Part) && !incoming.ContainsKey(id) && incoming.Count < MaxIncoming)
                {
                    // what arrived before stays: the checksum is brought up to it and the file continues where it stopped
                    var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var existing = File.OpenRead(stopped.Part)) { var buffer = new byte[1 << 20]; int read; while ((read = existing.Read(buffer)) > 0) sha.AppendData(buffer, 0, read); }
                    var written = new FileInfo(stopped.Part).Length;
                    if (written < size)
                    {
                        transfers.Remove(id);
                        transfer = new ChatTransfer(id, name, kind, size, true, () => RejectIncoming(id));
                        file = new Incoming { Path = stopped.Path, Name = name, Kind = kind, Size = size, Transfer = transfer, Stream = new FileStream(stopped.Part, FileMode.Append, FileAccess.Write, FileShare.None), Sha = sha, Written = written };
                        incoming[id] = file; transfers[id] = transfer; receivedThisSession += size - written;
                        transfer.Report(written);
                        TransferStarted?.Invoke(transfer);
                        return new { ok = true, offset = written, attempt = file.Attempt };
                    }
                    sha.Dispose();
                }
                try { File.Delete(stopped.Part); } catch (IOException) { }
            }
            // an earlier attempt of the same file that broke off is replaced by this one
            if (transfers.TryGetValue(id, out var earlier) && earlier.State != TransferState.Active && !incoming.ContainsKey(id)) transfers.Remove(id);
            if (incoming.Count >= MaxIncoming || incoming.ContainsKey(id) || transfers.ContainsKey(id)) throw new IOException("正在接收的文件过多。");
            // what one session may put on this disk, counting every file it announced (finished or not)
            if (receivedThisSession + size > MaxPerSession) throw new IOException($"本次连接接收的文件已达上限（{MaxPerSession / 1024 / 1024 / 1024} GB），请重新连接后再发送。");
            Directory.CreateDirectory(folder);
            var free = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!).AvailableFreeSpace;
            var promised = incoming.Values.Sum(f => f.Size - f.Written);   // still to come for the files already arriving
            if (free < size + promised + 1024L * 1024 * 1024) throw new IOException("接收方磁盘空间不足。");
            var path = Unique(name);
            transfer = new ChatTransfer(id, name, kind, size, true, () => RejectIncoming(id));
            file = new Incoming { Path = path, Name = name, Kind = kind, Size = size, Transfer = transfer, Stream = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None), Sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256) };
            incoming[id] = file; transfers[id] = transfer; receivedThisSession += size;
        }
        TransferStarted?.Invoke(transfer);
        return new { ok = true, attempt = file.Attempt };
    }

    object Chunked(JsonElement data)
    {
        var id = data.GetProperty("id").GetString() ?? "";
        Incoming file;
        lock (gate)
        {
            if (turnedDown.Contains(id)) return new { ok = true };
            file = incoming.GetValueOrDefault(id) ?? throw new IOException("没有这个文件的传输。");
            if (OtherRound(data, file)) return new { ok = true };
        }
        try
        {
            lock (file)
            {
                var seq = data.GetProperty("seq").GetInt32();
                var bytes = Convert.FromBase64String(data.GetProperty("data").GetString() ?? "");
                if (bytes.Length == 0 || bytes.Length > Chunk) throw new IOException("文件片段大小无效。");
                if (seq < file.Next || seq - file.Next > MaxReorder || file.Pending.ContainsKey(seq)) throw new IOException("文件片段顺序错误。");
                file.Pending[seq] = bytes;
                // chunks are written strictly in order, whatever order they arrive in
                while (file.Pending.Remove(file.Next, out var next))
                {
                    if (file.Written + next.Length > file.Size) throw new IOException("文件片段超出声明的大小。");
                    file.Stream!.Write(next); file.Sha!.AppendData(next); file.Written += next.Length; file.Next++;
                }
                file.Transfer.Report(file.Written);
            }
            return new { ok = true };
        }
        catch (Exception ex) { Abort(id, TransferState.Failed, ex.Message); throw; }
    }

    object End(JsonElement data)
    {
        var id = data.GetProperty("id").GetString() ?? "";
        var hash = (data.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
        Incoming file;
        lock (gate)
        {
            if (turnedDown.Contains(id)) return new { ok = true };
            file = incoming.GetValueOrDefault(id) ?? throw new IOException("没有这个文件的传输。");
            if (OtherRound(data, file)) return new { ok = true };
            incoming.Remove(id);
        }
        string actual;
        lock (file) { file.Stream!.Dispose(); actual = Convert.ToHexString(file.Sha!.GetHashAndReset()).ToLowerInvariant(); file.Sha.Dispose(); }
        if (file.Written != file.Size || file.Pending.Count != 0 || actual != hash)
        {
            try { File.Delete(file.Part); } catch (IOException) { }
            file.Transfer.Finish(TransferState.Failed, "文件校验失败，已丢弃");
            throw new IOException("文件校验失败，已丢弃。");
        }
        var final = File.Exists(file.Path) ? Unique(file.Name) : file.Path;   // the name was taken meanwhile
        File.Move(file.Part, final);
        MarkFromInternet(final);
        file.Transfer.Finish(TransferState.Done);
        Add(new ChatMessage(id, false, file.Kind, "", file.Name, file.Size, final, DateTimeOffset.Now), true);
        return new { ok = true };
    }

    // A sender that names rounds (since v6.7) and names another one than this transfer's; one that names none is taken as it is.
    static bool OtherRound(JsonElement data, Incoming file) =>
        data.TryGetProperty("attempt", out var round) && round.ValueKind == JsonValueKind.String && round.GetString() != file.Attempt;

    // The other side stopped a transfer: whichever way it was going, it ends here too.
    object Cancelled(JsonElement data)
    {
        var id = data.GetProperty("id").GetString() ?? "";
        CancellationTokenSource? cts;
        lock (gate) outgoing.TryGetValue(id, out cts);
        if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        Abort(id, TransferState.Cancelled, "对方已取消");
        return new { ok = true };
    }

    // The user turns a file that is arriving down: it is thrown away and the sender is told.
    void RejectIncoming(string id)
    {
        lock (gate) turnedDown.Add(id);
        Abort(id, TransferState.Cancelled, "已取消");
        _ = Task.Run(async () => { try { using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await Ask("chat-file-cancel", new { id }, brief.Token); } catch (Exception) { } });
    }

    // keep: the connection broke, what arrived stays for the sender's retry; otherwise it is thrown away.
    void Abort(string id, TransferState state, string reason, bool keep = false)
    {
        Incoming? file;
        lock (gate) { incoming.Remove(id, out file); }
        if (file == null) return;
        lock (file) { file.Stream?.Dispose(); file.Sha?.Dispose(); file.Pending.Clear(); }
        if (keep && file.Written > 0) lock (gate) paused[id] = file;
        else try { File.Delete(file.Part); } catch (IOException) { }
        FileLog.Write("对话", $"接收文件 {file.Name} 结束：{reason}（已收 {file.Written} / {file.Size} 字节{(keep ? "，保留以便续传" : "")}）");
        file.Transfer.Finish(state, reason);
    }

    // The session ended: every transfer still going is over, unfinished files are thrown away.
    public void Close()
    {
        string[] ids; CancellationTokenSource[] sending;
        lock (gate)
        {
            ids = incoming.Keys.ToArray(); sending = outgoing.Values.ToArray(); receivedThisSession = 0; turnedDown.Clear();
            foreach (var over in transfers.Where(p => p.Value.State != TransferState.Active).Select(p => p.Key).ToArray()) transfers.Remove(over);
        }
        foreach (var id in ids) Abort(id, TransferState.Failed, "连接已断开（已收到的部分保留，对方重试时继续）", keep: true);
        foreach (var cts in sending) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
