using System.Runtime.InteropServices;

namespace ArdUi;

// The floating "island": a small pill at the top centre of the main screen, an enhancement of the tray icon. It shows that a device is
// connected, announces a new message in a bubble for four seconds, counts unread messages and shows the progress of files on their way.
// The window never takes focus, and the mouse passes straight through everything except the pill itself.
sealed class IslandWindow : Window
{
    const int GwlExStyle = -20;
    const long NoActivate = 0x08000000, ToolWindow = 0x80, Transparent = 0x20;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point32 point);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [StructLayout(LayoutKind.Sequential)] struct Point32 { public int X, Y; }

    readonly Engine engine;
    readonly Func<string, string> labelOf;
    readonly Func<string, bool> chatIsOpen;
    readonly Action<string> openChat;
    readonly IslandModel model = new();
    readonly IslandView view;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(120) }, hit = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly HashSet<string> watched = new();
    readonly Dictionary<ChatTransfer, string> owners = new();
    readonly Dictionary<ChatTransfer, DateTimeOffset> lingering = new();
    readonly HashSet<ChatTransfer> seen = new();
    nint handle;
    bool through = true, closing;
    int ticks;
    Point32 dragFrom; PixelPoint dragOrigin; bool dragged;
    string[] live = [];

    public IslandWindow(Engine engine, Func<string, string> labelOf, Func<string, bool> chatIsOpen, Action<string> openChat)
    {
        this.engine = engine; this.labelOf = labelOf; this.chatIsOpen = chatIsOpen; this.openChat = openChat;
        view = new IslandView(model);
        Width = view.Width; Height = view.Height; CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false; Focusable = false;
        SystemDecorations = SystemDecorations.None; Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = view;
        model.SetEnabled(!Motion.NoIsland);
        view.PillDown += () => { GetCursorPos(out dragFrom); dragOrigin = Position; dragged = false; };
        view.PillMove += Drag; view.PillUp += () => { if (!dragged) OnPillClicked(); else Remember(); };
        view.CancelClicked += t => t.Cancel();
        model.Changed += () => { if (model.Visible) EnsureShown(); else HideSoon(); };
        tick.Tick += (_, _) => Tick(); hit.Tick += (_, _) => HitTest();
    }

    public void Start() { tick.Start(); }
    public void Enable(bool enabled) => model.SetEnabled(enabled);

    public void Shutdown() { closing = true; tick.Stop(); hit.Stop(); Close(); }

    // ---- window ----------------------------------------------------------------------------------------------------
    void EnsureShown()
    {
        if (closing) return;
        if (!IsVisible)
        {
            Place(); Show();
            if (TryGetPlatformHandle()?.Handle is { } h && h != 0)
            {
                handle = h; var style = GetWindowLongPtr(h, GwlExStyle).ToInt64();
                SetWindowLongPtr(h, GwlExStyle, (nint)(style | NoActivate | ToolWindow | Transparent)); through = true;
            }
            hit.Start();
        }
        view.Apply();
    }

    // Leaves after the pill has faded out, unless something new came meanwhile.
    void HideSoon() => DispatcherTimer.RunOnce(() => { if (!model.Visible && IsVisible && !closing) { hit.Stop(); Hide(); } }, TimeSpan.FromMilliseconds(Motion.Enabled ? 380 : 0));

    // Bottom right, just above the tray, unless the user dragged it elsewhere (and that place is still on a screen).
    void Place()
    {
        var screen = Screens.Primary; if (screen == null) return;
        var size = new PixelSize((int)(Width * screen.Scaling), (int)(Height * screen.Scaling));
        if (Motion.IslandPosition is { } saved && Screens.All.FirstOrDefault(sc => sc.WorkingArea.Contains(new PixelPoint(saved.X + size.Width / 2, saved.Y + size.Height / 2))) != null)
        { Position = new PixelPoint(saved.X, saved.Y); return; }
        var area = screen.WorkingArea;
        Position = new PixelPoint(area.Right - size.Width, area.Bottom - size.Height);
    }

    void Drag()
    {
        if (!GetCursorPos(out var now)) return;
        var dx = now.X - dragFrom.X; var dy = now.Y - dragFrom.Y;
        if (!dragged && Math.Abs(dx) + Math.Abs(dy) < 5) return;
        dragged = true;
        var target = new PixelPoint(dragOrigin.X + dx, dragOrigin.Y + dy);
        var screen = Screens.All.FirstOrDefault(sc => sc.WorkingArea.Contains(new PixelPoint(now.X, now.Y))) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea; var size = new PixelSize((int)(Width * screen.Scaling), (int)(Height * screen.Scaling));
            target = new PixelPoint(Math.Clamp(target.X, area.X, Math.Max(area.X, area.Right - size.Width)), Math.Clamp(target.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
        }
        Position = target;
    }

    void Remember()
    {
        try { Motion.SetIslandPosition((Position.X, Position.Y)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // The window is larger than the pill. Only over the pill does it take the mouse; everywhere else clicks go to what is underneath.
    void HitTest()
    {
        if (handle == 0 || !GetCursorPos(out var cursor)) return;
        var local = this.PointToClient(new PixelPoint(cursor.X, cursor.Y));
        SetThrough(!(view.Interactive && view.PillRect.Inflate(5).Contains(local)));
    }

    void SetThrough(bool value)
    {
        if (through == value || handle == 0) return;
        through = value; var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, (nint)(value ? style | Transparent : style & ~Transparent));
    }

    // ---- what is going on ------------------------------------------------------------------------------------------
    void Watch(string peer)
    {
        if (!watched.Add(peer)) return;
        engine.ChatFor(peer).MessageAdded += message => Dispatcher.UIThread.Post(() =>
        {
            if (message.Mine || chatIsOpen(peer)) return;
            var preview = message.Kind == ChatKind.Text ? message.Text : (message.Kind == ChatKind.Image ? "[图片] " : "[文件] ") + message.FileName;
            model.MessageArrived(peer, labelOf(peer), preview.Length > 120 ? preview[..120] + "…" : preview);
        });
    }

    void Tick()
    {
        if (closing) return;
        live = engine.Outgoing.Values.Concat(engine.Incoming.Values).Where(s => s.Live).Select(s => s.PeerId).Distinct().ToArray();
        var unread = 0; var active = new List<ChatTransfer>();
        foreach (var peer in live)
        {
            Watch(peer);
            var chat = engine.ChatFor(peer); unread += chat.Unread;
            foreach (var t in chat.ActiveTransfers) { active.Add(t); owners[t] = peer; seen.Add(t); }
        }
        // a finished file keeps its place a moment, with a check, before the island goes back to rest
        foreach (var t in seen.ToArray())
        {
            if (active.Contains(t)) continue;
            seen.Remove(t);
            if (t.State == TransferState.Done) lingering[t] = DateTimeOffset.Now.AddMilliseconds(1600);
        }
        foreach (var pair in lingering.ToArray()) if (pair.Value <= DateTimeOffset.Now) { lingering.Remove(pair.Key); owners.Remove(pair.Key); }
        active.AddRange(lingering.Keys);
        model.SetConnected(live.Length); model.SetUnread(unread); model.SetTransfers(active);
        if (ticks++ % 8 == 0) model.SetSuppressed(!Motion.IslandInFullscreen && FullScreenApp());
        model.Tick();
        if (model.Visible && IsVisible) view.Apply();   // progress moves without a change of state
    }

    // A full-screen app, a game or a presentation is in front: stay out of the way.
    static bool FullScreenApp()
    {
        try { return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    void OnPillClicked()
    {
        var peer = model.UnreadPeer is { } p && live.Contains(p) ? p : model.Transfer is { } t && owners.TryGetValue(t, out var owner) ? owner : live.FirstOrDefault();
        if (peer == null) return;
        model.DismissBubble(); openChat(peer);
    }
}
