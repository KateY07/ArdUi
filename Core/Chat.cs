namespace ArdUi;

enum ChatKind { Text, Image, File }

// Sends one control request to the other side (the control channel of the session) and returns its answer.
delegate Task<JsonElement> ChatSender(string method, object value, CancellationToken ct);

// One line of the conversation. Files and images are stored on this machine; Path points at the stored copy.
sealed record ChatMessage(string Id, bool Mine, ChatKind Kind, string Text, string? FileName, long Size, string? Path, DateTimeOffset At);

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
    public List<ChatMessage> Messages { get; } = new();
    public int Unread { get; private set; }
    public event Action<ChatMessage>? MessageAdded;      // either direction
    public event Action<ChatTransfer>? TransferStarted;  // either direction
    public event Action? Changed;

    public ChatChannel(string folder, Func<ChatSender?> sender) { this.folder = folder; this.sender = sender; }

    sealed class Incoming
    {
        public required string Path, Name; public required ChatKind Kind; public required long Size; public required ChatTransfer Transfer;
        public FileStream? Stream; public long Written; public int Next; public IncrementalHash? Sha;
        public readonly Dictionary<int, byte[]> Pending = new();
    }

    public ChatTransfer[] ActiveTransfers { get { lock (gate) return transfers.Values.Where(t => t.State == TransferState.Active).ToArray(); } }
    public void MarkRead() { if (Unread == 0) return; Unread = 0; Changed?.Invoke(); }

    void Add(ChatMessage message, bool received)
    {
        lock (gate) { Messages.Add(message); if (received) Unread++; }
        MessageAdded?.Invoke(message); Changed?.Invoke();
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

    string Unique(string name)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = System.IO.Path.Combine(folder, stamp + "_" + name);
        for (var i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(folder, $"{stamp}_{i}_{name}");
        return path;
    }

    // ---- sending ---------------------------------------------------------------------------------------------------
    async Task<JsonElement> Ask(string method, object value, CancellationToken ct)
    {
        var send = sender() ?? throw new IOException("设备连接已断开，无法发送。");
        await inflight.WaitAsync(ct);
        try { return await send(method, value, ct); }
        finally { inflight.Release(); }
    }

    public async Task SendText(string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxText) throw new InvalidOperationException($"一条消息最多 {MaxText} 个字符。");
        var id = Guid.NewGuid().ToString("N");
        await Ask("chat-text", new { id, text }, ct);
        Add(new ChatMessage(id, true, ChatKind.Text, text, null, 0, null, DateTimeOffset.Now), false);
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
        var id = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        var transfer = new ChatTransfer(id, name, kind, info.Length, false, () => cts.Cancel());
        lock (gate) { transfers[id] = transfer; outgoing[id] = cts; }
        TransferStarted?.Invoke(transfer);
        _ = Task.Run(() => SendCore(transfer, path, cts));
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

    async Task SendCore(ChatTransfer transfer, string path, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Ask("chat-file-begin", new { id = transfer.Id, kind = transfer.Kind.ToString(), name = transfer.Name, size = transfer.Size }, ct);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // How much is on the wire adapts to the link: a transfer must never queue up so much that the session's own keep-alive and
            // probes wait behind it (that makes the path look dead). Few, small chunks while acknowledgements are slow; more, bigger ones when fast.
            var acked = 0L; var seq = 0; var window = 2; var chunkSize = 16 * 1024; var clock = Stopwatch.StartNew();
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
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                sha.AppendData(buffer, 0, n);
                var data = Convert.ToBase64String(buffer, 0, n);
                pending.Enqueue((Ask("chat-file-chunk", new { id = transfer.Id, seq = seq++, data }, ct), n, clock.ElapsedMilliseconds));
                while (pending.Count >= window) await Settle();
            }
            while (pending.Count != 0) await Settle();
            await Ask("chat-file-end", new { id = transfer.Id, sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() }, ct);
            transfer.Finish(TransferState.Done);
            Add(new ChatMessage(transfer.Id, true, transfer.Kind, "", transfer.Name, transfer.Size, path, DateTimeOffset.Now), false);
        }
        catch (OperationCanceledException)
        {
            // stopped here (by the user), or because the other side stopped it: either way it is over
            try { using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await Ask("chat-file-cancel", new { id = transfer.Id }, brief.Token); } catch (Exception) { }
            transfer.Finish(TransferState.Cancelled, "已取消");
        }
        catch (Exception ex) { transfer.Finish(TransferState.Failed, ex.Message); }
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
                Add(new ChatMessage(data.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"), false, ChatKind.Text, text, null, 0, null, DateTimeOffset.Now), true);
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
        Incoming file; ChatTransfer transfer;
        lock (gate)
        {
            if (incoming.Count >= MaxIncoming || incoming.ContainsKey(id) || transfers.ContainsKey(id)) throw new IOException("正在接收的文件过多。");
            Directory.CreateDirectory(folder);
            var free = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!).AvailableFreeSpace;
            if (free < size + 256L * 1024 * 1024) throw new IOException("接收方磁盘空间不足。");
            var path = Unique(name);
            transfer = new ChatTransfer(id, name, kind, size, true, () => RejectIncoming(id));
            file = new Incoming { Path = path, Name = name, Kind = kind, Size = size, Transfer = transfer, Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), Sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256) };
            incoming[id] = file; transfers[id] = transfer;
        }
        TransferStarted?.Invoke(transfer);
        return new { ok = true };
    }

    object Chunked(JsonElement data)
    {
        var id = data.GetProperty("id").GetString() ?? "";
        Incoming file;
        lock (gate)
        {
            if (turnedDown.Contains(id)) return new { ok = true };
            file = incoming.GetValueOrDefault(id) ?? throw new IOException("没有这个文件的传输。");
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
            file = incoming.GetValueOrDefault(id) ?? throw new IOException("没有这个文件的传输。"); incoming.Remove(id);
        }
        string actual;
        lock (file) { file.Stream!.Dispose(); actual = Convert.ToHexString(file.Sha!.GetHashAndReset()).ToLowerInvariant(); file.Sha.Dispose(); }
        if (file.Written != file.Size || file.Pending.Count != 0 || actual != hash)
        {
            try { File.Delete(file.Path); } catch (IOException) { }
            file.Transfer.Finish(TransferState.Failed, "文件校验失败，已丢弃");
            throw new IOException("文件校验失败，已丢弃。");
        }
        file.Transfer.Finish(TransferState.Done);
        Add(new ChatMessage(id, false, file.Kind, "", file.Name, file.Size, file.Path, DateTimeOffset.Now), true);
        return new { ok = true };
    }

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

    void Abort(string id, TransferState state, string reason)
    {
        Incoming? file;
        lock (gate) { incoming.Remove(id, out file); }
        if (file == null) return;
        lock (file) { file.Stream?.Dispose(); file.Sha?.Dispose(); }
        try { File.Delete(file.Path); } catch (IOException) { }
        file.Transfer.Finish(state, reason);
    }

    // The session ended: every transfer still going is over, unfinished files are thrown away.
    public void Close()
    {
        string[] ids; CancellationTokenSource[] sending;
        lock (gate) { ids = incoming.Keys.ToArray(); sending = outgoing.Values.ToArray(); }
        foreach (var id in ids) Abort(id, TransferState.Failed, "连接已断开");
        foreach (var cts in sending) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
