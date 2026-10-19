using Avalonia.Animation;
using Avalonia.Media.Transformation;

namespace ArdUi;

// Attention and intent: the window reacts to what the user is doing. Hovering the connect panel lifts it; typing or focusing
// the field dims everything else so the panel is the light of the page; while connecting the rest dims further. The connect
// button wakes when a full code is typed, and typing a few characters brings up the matching recent device.
sealed partial class MainWindow
{
    enum Attention { Idle, Hover, Typing, Busy }

    readonly Border scrim = new() { Background = Brush.Parse("#1F1E1D"), Opacity = 0, IsHitTestVisible = false };
    Grid? titleBar;
    Border? stripHost;
    Attention attention = Attention.Idle;
    bool cardHover, goArmed;
    int hoverTicket;

    // Every dimmed region fades with the same short transition.
    Transitions Fades() => new() { new DoubleTransition { Property = OpacityProperty, Duration = Motion.Ms(240), Easing = new Avalonia.Animation.Easings.CubicEaseInOut() } };

    void SetupAttention(bool animated)
    {
        if (animated)
        {
            scrim.Transitions = Fades();
            if (titleBar != null) titleBar.Transitions = Fades();
            if (stripHost != null) stripHost.Transitions = Fades();
            if (homeLower != null) homeLower.Transitions = Fades();
            if (homeCard != null)
            {
                homeCard.RenderTransformOrigin = RelativePoint.Center; homeCard.RenderTransform = TransformOperations.Parse("translate(0px,0px) scale(1)");
                homeCard.Transitions = new Transitions
                {
                    new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Motion.Ms(220), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
                    new BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = Motion.Ms(220) },
                };
            }
        }
        if (homeCard == null) return;
        // hover lifts the connect panel (with a short dwell so passing over it does not flicker)
        homeCard.PointerEntered += (_, _) => { var ticket = ++hoverTicket; DispatcherTimer.RunOnce(() => { if (ticket == hoverTicket) { cardHover = true; UpdateAttention(); } }, TimeSpan.FromMilliseconds(120)); };
        homeCard.PointerExited += (_, _) => { var ticket = ++hoverTicket; DispatcherTimer.RunOnce(() => { if (ticket == hoverTicket) { cardHover = false; UpdateAttention(); } }, TimeSpan.FromMilliseconds(300)); };
        // Clicking anywhere else (an empty part of the window included) leaves the field and ends the focused mode.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!remote.IsFocused) return;
            for (var v = e.Source as Avalonia.Visual; v != null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (ReferenceEquals(v, fieldBorder)) return;
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            Dispatcher.UIThread.Post(UpdateAttention);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        remote.GotFocus += (_, _) => Dispatcher.UIThread.Post(UpdateAttention);
        remote.LostFocus += (_, _) => Dispatcher.UIThread.Post(UpdateAttention);
    }

    void UpdateAttention()
    {
        var next = settingsOpen ? Attention.Idle
            : pendingCards.Count != 0 ? Attention.Busy
            : remote.IsFocused ? Attention.Typing
            : cardHover ? Attention.Hover : Attention.Idle;
        ApplyAttention(next);
        UpdateGo();
    }

    void ApplyAttention(Attention level)
    {
        attention = level;
        var dimmed = level is Attention.Typing or Attention.Busy;
        scrim.Opacity = level switch { Attention.Typing => 0.20, Attention.Busy => 0.30, _ => 0 };
        if (titleBar != null) titleBar.Opacity = dimmed ? 0.55 : 1;
        if (stripHost != null) stripHost.Opacity = dimmed ? 0.55 : 1;
        if (homeLower != null) homeLower.Opacity = level == Attention.Typing ? 0.55 : 1;   // while connecting, the placeholder lives here and stays bright
        if (homeCard != null)
        {
            homeCard.RenderTransform = TransformOperations.Parse(level switch
            {
                Attention.Hover => "translate(0px,-2px) scale(1)",
                Attention.Typing => "translate(0px,0px) scale(1.012)",
                _ => "translate(0px,0px) scale(1)",
            });
            homeCard.BoxShadow = BoxShadows.Parse(level switch
            {
                Attention.Hover => "0 26 58 0 #2E301E14, 0 2 6 0 #14000000",
                Attention.Typing => "0 30 70 0 #40301E14, 0 2 6 0 #14000000",
                _ => "0 20 50 0 #26301E14, 0 2 6 0 #14000000",
            });
        }
        // the field glows while it has the focus
        fieldBorder.BoxShadow = BoxShadows.Parse(level == Attention.Typing ? "0 0 0 4 #33C6613F" : "0 0 0 0 #00C6613F");
    }

    // ---- the connect button: asleep until the code is complete, a ring when it wakes, a spinner while connecting ---------------
    Button? goButton; Control? goArrow; Spinner? goSpinner; Ellipse? goRing;

    void SetupGo(Button go, Control arrow, Ellipse ring)
    {
        goButton = go; goArrow = arrow; goRing = ring;
        goSpinner = new Spinner(16) { Stroke = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        go.Opacity = 0.5;
    }

    void UpdateGo()
    {
        if (goButton == null) return;
        var busy = pendingCards.Count != 0;
        var armed = ConnectFlow.NormaliseCode(remote.Text) != null;
        goButton.Opacity = armed || busy ? 1 : 0.5;
        if (armed && !goArmed && !busy) PulseGo();
        goArmed = armed;
        var want = busy ? (Control)goSpinner! : goArrow!;
        if (!ReferenceEquals(goButton.Content, want)) goButton.Content = want;
        // the field is locked while connecting
        remote.IsReadOnly = busy; remote.Opacity = busy ? 0.65 : 1;
    }

    // One ring spreads from the button when the code becomes complete.
    void PulseGo()
    {
        if (goRing == null || previewMode || !Motion.Enabled) return;
        goRing.RenderTransform ??= new ScaleTransform();
        var pulse = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(520), Easing = new Avalonia.Animation.Easings.CubicEaseOut(), FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.65), new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(ScaleTransform.ScaleXProperty, 1.65), new Setter(ScaleTransform.ScaleYProperty, 1.65) } },
            }
        };
        _ = RunQuietly(pulse, goRing);
    }

    // A short sideways shake: the answer to a wrong or failed attempt.
    void Shake(Control target)
    {
        if (previewMode || !Motion.Enabled) return;
        if (target.RenderTransform is not TranslateTransform) target.RenderTransform = new TranslateTransform();
        var shake = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(340), FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(.2), Setters = { new Setter(TranslateTransform.XProperty, -8.0) } },
                new KeyFrame { Cue = new Cue(.4), Setters = { new Setter(TranslateTransform.XProperty, 7.0) } },
                new KeyFrame { Cue = new Cue(.6), Setters = { new Setter(TranslateTransform.XProperty, -4.0) } },
                new KeyFrame { Cue = new Cue(.8), Setters = { new Setter(TranslateTransform.XProperty, 3.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };
        _ = RunQuietly(shake, target);
    }

    static async Task RunQuietly(Animation animation, Animatable target)
    {
        try { await animation.RunAsync(target); } catch (InvalidOperationException) { }
    }

    // ---- typing: progress toward a full code, and the matching device -------------------------------------------------------
    readonly Border progressLine = new() { Height = 2.5, CornerRadius = new CornerRadius(2), Background = Look.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(26, 0, 0, -3), Width = 0, IsHitTestVisible = false };
    readonly TextBlock hintText = new() { FontSize = 12, Foreground = Look.Muted, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    readonly List<(string Code, Control Item, Border Avatar, IBrush Ring)> recentItems = new();
    string? hintCode;

    void SetupTyping()
    {
        progressLine.Transitions = new Transitions { new DoubleTransition { Property = WidthProperty, Duration = Motion.Ms(180), Easing = new Avalonia.Animation.Easings.CubicEaseOut() } };
        remote.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Dispatcher.UIThread.Post(OnCodeChanged); };
        remote.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Tab && hintCode != null && (remote.Text ?? "").Length < 6) { e.Handled = true; remote.Text = hintCode; remote.CaretIndex = hintCode.Length; }
            else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; (TopLevel.GetTopLevel(this)?.FocusManager)?.ClearFocus(); }
        };
    }

    Func<Peer[]>? previewPeers;   // render-only: stands in for the saved devices
    Peer[] KnownPeers()
    {
        if (previewPeers != null) return previewPeers();
        if (engine == null) return [];
        lock (engine.State.Peers) return engine.State.Peers.ToArray();
    }

    void OnCodeChanged()
    {
        var text = remote.Text ?? "";
        var track = Math.Max(0, fieldBorder.Bounds.Width - 60);
        progressLine.Width = track * Math.Min(text.Length, 6) / 6.0;
        UpdateGo();
        // a few characters are enough to know which device is meant
        Peer[] matches = [];
        if (text.Length is >= 2 and < 6) matches = KnownPeers().Where(p => p.Code.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
        hintCode = matches.Length == 1 ? matches[0].Code : null;
        hintText.IsVisible = hintCode != null;
        if (hintCode != null) hintText.Text = PeerLabel(matches[0]) + (PeerLabel(matches[0]).Contains(hintCode) ? "" : " · " + hintCode) + "　按 Tab 补全";
        foreach (var (code, item, avatar, ring) in recentItems)
        {
            var hit = matches.Any(p => p.Code == code);
            item.Opacity = matches.Length != 0 && !hit ? 0.35 : 1;
            avatar.BorderBrush = hit ? Look.Accent : ring;
        }
    }
}
