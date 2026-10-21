using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArdUi;

// Audit of the controlling side: while a device controls this machine's screen (FRD running or an RDP connection open) the screen is
// recorded here (10 frames a second, main display, with the mouse pointer), and the conversation is kept with it. Recording cannot be switched off: if it cannot run, the
// session is ended. Everything stays on this machine, under data\audit, within a fixed budget; the oldest material is overwritten.

// ---- screen capture (GDI) ------------------------------------------------------------------------------------------------
sealed class ScreenGrabber : IDisposable
{
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, int step, nint brush, int flags);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);

    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ColorUsed, ColorImportant; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int Size, Flags; public nint Handle; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { public bool Icon; public int XHotspot, YHotspot; public nint Mask, Color; }

    readonly nint screen, memory, bitmap, bits, previous;
    readonly int sourceWidth, sourceHeight;
    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    public ScreenGrabber(int maxWidth = 1280)
    {
        SetThreadDpiAwarenessContext(-4);   // physical pixels, whatever the scaling
        sourceWidth = GetSystemMetrics(0); sourceHeight = GetSystemMetrics(1);
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new IOException("无法读取屏幕尺寸。");
        var scale = Math.Min(1.0, (double)maxWidth / sourceWidth);
        Width = Math.Max(2, (int)(sourceWidth * scale) & ~1); Height = Math.Max(2, (int)(sourceHeight * scale) & ~1);   // even, as H.264 wants
        screen = GetDC(0);
        if (screen == 0) throw new IOException("无法访问屏幕。");
        memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        bitmap = CreateDIBSection(screen, ref info, 0, out bits, 0, 0);
        if (bitmap == 0 || bits == 0) { ReleaseDC(0, screen); DeleteDC(memory); throw new IOException("无法创建截屏缓冲区。"); }
        previous = SelectObject(memory, bitmap);
        SetStretchBltMode(memory, 4);   // HALFTONE: a downscaled desktop stays readable
    }

    // One frame (BGRA, Width x Height) into the buffer. False when the screen cannot be read right now (e.g. the secure desktop).
    public bool Grab(byte[] buffer)
    {
        SetThreadDpiAwarenessContext(-4);
        var ok = StretchBlt(memory, 0, 0, Width, Height, screen, 0, 0, sourceWidth, sourceHeight, 0x40CC0020);   // SRCCOPY | CAPTUREBLT
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (ok && GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0 && cursor.Handle != 0)
        {
            int hotX = 0, hotY = 0;
            if (GetIconInfo(cursor.Handle, out var icon)) { hotX = icon.XHotspot; hotY = icon.YHotspot; if (icon.Mask != 0) DeleteObject(icon.Mask); if (icon.Color != 0) DeleteObject(icon.Color); }
            var x = (int)((cursor.Position.X - hotX) * (double)Width / sourceWidth); var y = (int)((cursor.Position.Y - hotY) * (double)Height / sourceHeight);
            DrawIconEx(memory, x, y, cursor.Handle, 0, 0, 0, 0, 3);   // DI_NORMAL
        }
        Marshal.Copy(bits, buffer, 0, Math.Min(buffer.Length, FrameBytes));
        return ok;
    }

    public void Dispose()
    {
        SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen);
    }
}

// ---- the store: where recordings live and how much of the disk they may use -----------------------------------------------
sealed class AuditStore
{
    public const long MaxBytes = 10L * 1024 * 1024 * 1024;
    // One record per line, readable as it is (Chinese text is not escaped).
    public static readonly JsonSerializerOptions Line = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Root { get; }
    readonly long limit;
    public AuditStore(string root, long limit = MaxBytes) { Root = root; this.limit = limit; Directory.CreateDirectory(root); }

    public string NewSessionFolder(string code)
    {
        var folder = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + new string(code.Where(char.IsLetterOrDigit).ToArray()));
        for (var i = 2; Directory.Exists(folder); i++) folder = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}_{code}_{i}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public long UsedBytes() => Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    // Overwrite the oldest material until the total fits the budget again. Files being written (the newest recording segment of each
    // folder in `active`) and the small index are never touched. Returns the number of bytes freed.
    public long Prune(IReadOnlyCollection<string> active)
    {
        var all = new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var total = all.Sum(f => f.Length);
        if (total <= limit) return 0;
        var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(Root, "index.jsonl") };
        foreach (var folder in active)
            if (Directory.Exists(folder))
                foreach (var live in new DirectoryInfo(folder).EnumerateFiles("rec-*.mp4").OrderByDescending(f => f.Name).Take(1)) protectedFiles.Add(live.FullName);
        long freed = 0;
        foreach (var file in all.Where(f => !protectedFiles.Contains(f.FullName)).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.Ordinal))
        {
            if (total <= limit) break;
            try { var size = file.Length; file.Delete(); total -= size; freed += size; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // folders that are left empty go away too
        foreach (var directory in new DirectoryInfo(Root).EnumerateDirectories().Where(d => !active.Contains(d.FullName, StringComparer.OrdinalIgnoreCase)))
            try { if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete(); } catch (IOException) { }
        return freed;
    }

    // One line per session, kept even after its video was overwritten.
    public void Index(object entry)
    {
        var path = Path.Combine(Root, "index.jsonl");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) File.Move(path, Path.Combine(Root, "index-old.jsonl"), true);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, Line) + "\n");
        }
        catch (IOException ex) { Diagnostics.Log("audit-index", ex.Message); }
    }
}

// ---- one audited session ------------------------------------------------------------------------------------------------------
// The record of one controlling device's session. The screen is recorded only while the screen is actually being used (FRD running, or
// an RDP connection open); the conversation is recorded whenever there is one. The folder is created when there is something to keep.
sealed class AuditSession : IAsyncDisposable
{
    public string Code { get; }
    public bool Failed { get; private set; }
    public bool Recording { get { lock (gate) return recorder != null && !Failed; } }
    public string? Folder { get { lock (gate) return folder; } }
    // Files exchanged in the conversation are copied into the record up to this size; bigger ones are only noted.
    public const long MaxCopyBytes = 100L * 1024 * 1024;
    public event Action<string>? Failure;
    readonly string endpoint; readonly bool once; readonly Perm perms; readonly DateTimeOffset started = DateTimeOffset.Now;
    readonly AuditStore store; readonly int fps, segmentSeconds;
    readonly object gate = new();
    string? folder; H264Recorder? recorder; Thread? loop; ScreenGrabber? grabber; volatile bool stopping;
    long frames; int videoRuns;
    ChatChannel? chat; Action<ChatMessage>? chatHook;

    AuditSession(AuditStore store, string code, string endpoint, bool once, Perm perms, int fps, int segmentSeconds)
    {
        this.store = store; Code = code; this.endpoint = endpoint; this.once = once; this.perms = perms; this.fps = fps; this.segmentSeconds = segmentSeconds;
    }

    // Nothing is recorded yet: this only prepares the record of a session that has just begun.
    public static AuditSession Open(AuditStore store, string code, string endpoint, bool once, Perm perms, int fps = 10, int segmentSeconds = 300) =>
        new(store, code, endpoint, once, perms, fps, segmentSeconds);

    string EnsureFolder()
    {
        lock (gate)
        {
            if (folder != null) return folder;
            folder = store.NewSessionFolder(Code);
            File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new { code = Code, endpoint, once, perms = Permissions.Describe(perms), started, fps }, AuditStore.Line));
            return folder;
        }
    }

    // ---- video ---------------------------------------------------------------------------------------------------------
    public void BeginVideo()
    {
        lock (gate)
        {
            if (recorder != null && !Failed) return;
            if (!H264Recorder.Available) throw new NotSupportedException("本机没有可用的 H.264 编码器（Windows 媒体功能包）。");
            var target = EnsureFolder();
            var screen = new ScreenGrabber();
            // a new run continues the numbering, so earlier segments of this session are never overwritten
            H264Recorder encoder;
            try { encoder = new H264Recorder(target, screen.Width, screen.Height, fps, segmentSeconds, Directory.GetFiles(target, "rec-*.mp4").Length + 1); }
            catch { screen.Dispose(); throw; }
            recorder = encoder; grabber = screen; stopping = false; Failed = false; videoRuns++;
            var capture = new Thread(() => Capture(encoder, screen)) { IsBackground = true, Name = "audit-capture", Priority = ThreadPriority.BelowNormal };
            loop = capture; capture.Start();
            FileLog.Write("audit", $"开始录屏：{Code}");
        }
    }

    void Capture(H264Recorder encoder, ScreenGrabber screen)
    {
        var frame = new byte[screen.FrameBytes];
        var watch = Stopwatch.StartNew(); long due = 0; var interval = 1000 / fps;
        try
        {
            while (!stopping)
            {
                screen.Grab(frame);
                encoder.Write(frame); Interlocked.Increment(ref frames);
                due += interval;
                var wait = due - watch.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait); else if (wait < -1000) due = watch.ElapsedMilliseconds;   // far behind: skip ahead instead of racing
            }
        }
        catch (Exception ex) when (!stopping)
        {
            Failed = true; FileLog.Write("audit", "录屏失败：" + ex.Message);
            Failure?.Invoke(ex.Message);
        }
    }

    public async Task EndVideo()
    {
        H264Recorder? encoder; Thread? capture; ScreenGrabber? screen;
        lock (gate) { encoder = recorder; capture = loop; screen = grabber; recorder = null; loop = null; grabber = null; stopping = true; }
        if (encoder == null) return;
        if (capture != null) await Task.Run(() => capture.Join(3000));
        try { await Task.Run(encoder.Dispose); } catch (Exception ex) { FileLog.Write("audit", "结束录屏时出错：" + ex.Message); }
        screen?.Dispose();
        FileLog.Write("audit", $"停止录屏：{Code}");
    }

    // ---- the conversation ---------------------------------------------------------------------------------------------------
    // Transcript, files and pictures go into the session's folder (created by the first message).
    public void Attach(ChatChannel channel)
    {
        lock (gate)
        {
            chat = channel;
            chatHook = message =>
            {
                try
                {
                    var target = EnsureFolder(); string? copy = null; string? note = null;
                    if (message.Path != null && File.Exists(message.Path))
                    {
                        if (message.Size > MaxCopyBytes) note = "文件过大，未保存副本";
                        else
                        {
                            var files = Path.Combine(target, "files"); Directory.CreateDirectory(files);
                            copy = Path.Combine(files, DateTime.Now.ToString("HHmmss-fff") + "_" + ChatChannel.SafeName(message.FileName));
                            var from = message.Path; var to = copy;
                            _ = Task.Run(() => { try { File.Copy(from, to, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FileLog.Write("audit", "保存文件副本失败：" + ex.Message); } });   // never on the thread of the session
                        }
                    }
                    lock (gate) File.AppendAllText(Path.Combine(target, "chat.jsonl"), JsonSerializer.Serialize(new { at = message.At, from = message.Mine ? "host" : Code, kind = message.Kind.ToString(), text = message.Text, file = message.FileName, size = message.Size, copy = copy == null ? null : Path.GetFileName(copy), note }, AuditStore.Line) + "\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FileLog.Write("audit", "对话记录失败：" + ex.Message); }
            };
            channel.MessageAdded += chatHook;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (chat != null && chatHook != null) chat.MessageAdded -= chatHook; }
        await EndVideo();
        string? target; lock (gate) target = folder;
        if (target == null) return;   // nothing was recorded or said: nothing to keep
        var segments = Directory.GetFiles(target, "rec-*.mp4");
        store.Index(new { code = Code, endpoint, once, perms = Permissions.Describe(perms), started, ended = DateTimeOffset.Now, folder = Path.GetFileName(target), segments = segments.Length, bytes = segments.Sum(s => new FileInfo(s).Length), frames = Interlocked.Read(ref frames), failed = Failed });
    }
}

// ---- the service the engine uses --------------------------------------------------------------------------------------------
sealed class AuditService : IAsyncDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);   // a short pause in screen use does not stop the recording
    public AuditStore Store { get; }
    readonly ConcurrentDictionary<string, AuditSession> sessions = new();
    readonly ConcurrentDictionary<string, int> idle = new();
    readonly Timer pruning;
    readonly TimeSpan grace;
    public event Action<string, string>? Failure;   // endpoint, reason: the session is to be ended
    public event Action? Changed;

    public AuditService(string root, TimeSpan? grace = null)
    {
        Store = new AuditStore(root); this.grace = grace ?? Grace;
        pruning = new Timer(_ => { try { Store.Prune(Active()); } catch (Exception ex) { FileLog.Write("audit", "清理失败：" + ex.Message); } }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
    }
    string[] Active() => sessions.Values.Select(s => s.Folder).Where(f => f != null).ToArray()!;
    public bool EncoderAvailable => H264Recorder.Available;
    public bool IsRecording(string endpoint) => sessions.TryGetValue(endpoint, out var s) && s.Recording;

    // A controlling device is in: its session is recorded from now on, screen included whenever the screen is being used.
    public void Open(string endpoint, string code, bool once, Perm perms, ChatChannel chat)
    {
        if (sessions.ContainsKey(endpoint)) return;
        var session = AuditSession.Open(Store, code, endpoint, once, perms);
        session.Attach(chat);
        session.Failure += reason => Failure?.Invoke(endpoint, reason);
        if (!sessions.TryAdd(endpoint, session)) { _ = session.DisposeAsync().AsTask(); }
    }

    // The screen is being used (FRD running or an RDP connection open) or not.
    public void ScreenActivity(string endpoint, bool active)
    {
        if (!sessions.TryGetValue(endpoint, out var session)) return;
        var token = idle.AddOrUpdate(endpoint, 1, (_, v) => v + 1);
        if (active)
        {
            Store.Prune(Active());
            session.BeginVideo(); Changed?.Invoke();
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(grace);
            if (idle.TryGetValue(endpoint, out var now) && now == token && sessions.TryGetValue(endpoint, out var s) && ReferenceEquals(s, session))
            { await s.EndVideo(); Changed?.Invoke(); }
        });
    }

    public async Task End(string endpoint)
    {
        idle.TryRemove(endpoint, out _);
        if (sessions.TryRemove(endpoint, out var session)) { await session.DisposeAsync(); Changed?.Invoke(); }
    }

    public async ValueTask DisposeAsync()
    {
        pruning.Dispose();
        foreach (var key in sessions.Keys.ToArray()) await End(key);
    }
}
