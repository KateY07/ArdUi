using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Animation.Easings;
using Avalonia.Media.Transformation;

namespace ArdUi;

// The drawing of the floating island: a small dark pill (logo + dot, unread badge, or a file's progress) with a bubble under it for a
// new message. It only reads the model; the window around it feeds the model and moves things on the screen.
sealed class IslandView : Grid
{
    static readonly IBrush Ink = new SolidColorBrush(Color.FromArgb(246, 22, 19, 17)), BubbleInk = new SolidColorBrush(Color.FromArgb(228, 22, 19, 17));
    static readonly IBrush Soft = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), Dim = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly IBrush Red = Brush.Parse("#F0504A"), Green = Brush.Parse("#5FD38D");
    const string CheckPath = "M5,12.5 L10,17.5 L19,7";
    const double Pad = 18, InfoWidth = 190;

    readonly IslandModel model;
    readonly bool animated;
    readonly Border pill = new(), host = new(), bubble = new();
    readonly TextBlock bubbleSender = Label(11, Dim, true), bubbleText = Label(13, Brushes.White, false);
    string signature = "", bubbleKey = "";
    bool shown, hover;
    StackPanel? row;
    Avalonia.Controls.Shapes.Arc? arc;
    TextBlock? percent, line1, line2, countText;
    Border? badge;
    int shownCount;

    bool pressed;
    public event Action? PillDown, PillMove, PillUp;
    public event Action<ChatTransfer>? CancelClicked;
    public event Action? CallAccept, CallHang, CallMute;

    public IslandView(IslandModel model, bool animated = true)
    {
        this.model = model; this.animated = animated && Motion.Enabled;
        Width = 480; Height = 150; Background = Brushes.Transparent;
        host.CornerRadius = new CornerRadius(26); host.ClipToBounds = true; host.Padding = new Thickness(8, 5, 10, 5); host.MinHeight = 40;
        pill.Child = host; pill.CornerRadius = new CornerRadius(26); pill.Background = Ink; pill.HorizontalAlignment = HorizontalAlignment.Right;
        pill.BoxShadow = BoxShadows.Parse("0 10 26 0 #5C000000, 0 0 0 1 #26FFFFFF");
        pill.RenderTransformOrigin = new RelativePoint(.5, 1, RelativeUnit.Relative);
        pill.PointerEntered += (_, _) => { hover = true; Pose(); };
        pill.PointerExited += (_, _) => { hover = false; Pose(); };
        pill.PointerPressed += (_, e) => { if (e.GetCurrentPoint(pill).Properties.IsLeftButtonPressed) { pressed = true; e.Pointer.Capture(pill); PillDown?.Invoke(); } };
        pill.PointerMoved += (_, _) => { if (pressed) PillMove?.Invoke(); };
        pill.PointerReleased += (_, e) => { if (!pressed) return; pressed = false; e.Pointer.Capture(null); PillUp?.Invoke(); };

        bubble.Child = new StackPanel { Spacing = 2, Children = { bubbleSender, bubbleText } };
        bubble.CornerRadius = new CornerRadius(20); bubble.Background = BubbleInk; bubble.Padding = new Thickness(15, 9, 15, 10); bubble.MaxWidth = 400;
        bubble.HorizontalAlignment = HorizontalAlignment.Right; bubble.Margin = new Thickness(0, 0, 0, 9); bubble.IsHitTestVisible = false;
        bubble.BoxShadow = BoxShadows.Parse("0 8 22 0 #4A000000, 0 0 0 1 #1FFFFFFF");
        bubble.RenderTransformOrigin = new RelativePoint(.5, 1, RelativeUnit.Relative);
        bubbleText.TextWrapping = TextWrapping.Wrap; bubbleText.MaxLines = 2; bubbleText.TextTrimming = TextTrimming.CharacterEllipsis;
        Hidden(pill); Hidden(bubble);
        Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 12), Children = { bubble, pill } });
        model.Changed += Apply;
    }

    public bool Interactive => shown;
    public Rect PillRect => pill.TranslatePoint(default, this) is { } p ? new Rect(p, pill.Bounds.Size) : default;

    static TextBlock Label(double size, IBrush brush, bool bold) => new() { FontSize = size, Foreground = brush, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };

    // ---- poses and motion -------------------------------------------------------------------------------------------
    static TransformOperations Ops(double y, double scale) => TransformOperations.Parse($"translate(0px, {y:F1}px) scale({scale:F3})");

    TransformOperationsTransition Slide(int ms, Easing ease) => new() { Property = Visual.RenderTransformProperty, Duration = animated ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero, Easing = ease };
    DoubleTransition Fade(int ms, Easing ease) => new() { Property = OpacityProperty, Duration = animated ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero, Easing = ease };

    void Hidden(Control c) { c.Transitions = null; c.Opacity = 0; c.RenderTransform = Ops(10, .72); }

    // Coming in overshoots a little and settles (a spring); leaving is quick and does not bounce.
    void Appear(Control c, double opacity = 1)
    {
        c.Transitions = new Transitions { Fade(240, new CubicEaseOut()), Slide(520, new BackEaseOut()) };
        c.Opacity = opacity; c.RenderTransform = Ops(0, 1);
    }

    void Vanish(Control c)
    {
        c.Transitions = new Transitions { Fade(190, new CubicEaseIn()), Slide(240, new CubicEaseIn()) };
        c.Opacity = 0; c.RenderTransform = Ops(8, .82);
    }

    void Pose()
    {
        if (!shown) return;
        pill.Transitions = new Transitions { Fade(180, new CubicEaseOut()), Slide(170, new CubicEaseOut()), WidthMotion() };
        pill.RenderTransform = Ops(0, hover ? 1.045 : 1); pill.Opacity = Calm ? (hover ? 1 : .8) : 1;
    }

    DoubleTransition WidthMotion() => new() { Property = WidthProperty, Duration = animated ? TimeSpan.FromMilliseconds(420) : TimeSpan.Zero, Easing = new QuinticEaseOut() };

    // Nothing to report: the island rests half-faded.
    bool Calm => model.Call == CallState.Idle && model.Transfer == null && model.Unread == 0 && !model.BubbleVisible;

    // ---- content ------------------------------------------------------------------------------------------------
    public void Apply()
    {
        if (!model.Visible)
        {
            if (shown) { shown = false; hover = false; Vanish(pill); }
            if (bubbleKey != "") { bubbleKey = ""; Vanish(bubble); }
            return;
        }
        var transfer = model.Transfer;
        var sig = (model.Call == CallState.Idle ? "" : "call" + model.Call + model.CallMuted + model.CallLabel + model.CallNote) + (transfer == null ? "idle" : transfer.Id + transfer.State) + "|" + (model.Unread > 0) + "|" + (model.Connected > 0);
        if (!shown)
        {
            signature = sig; Build(transfer, instant: true);
            shown = true; Pose(); Appear(pill, Calm ? .8 : 1);
        }
        else if (sig != signature) { signature = sig; Build(transfer, instant: false); Pose(); }
        else Live(transfer);
        Message();
    }

    void Message()
    {
        if (!model.BubbleVisible) { if (bubbleKey != "") { bubbleKey = ""; Vanish(bubble); } return; }
        var key = model.BubblePeer + "\n" + model.BubbleText + "\n" + model.BubbleSender;
        if (key == bubbleKey) return;
        var again = bubbleKey != "";
        bubbleKey = key;
        bubbleSender.Text = model.BubbleSender; bubbleText.Text = model.BubbleText;
        if (again) Hidden(bubble);   // a newer message: the bubble pops in again from the top
        Dispatcher.UIThread.Post(() => { if (bubbleKey == key) Appear(bubble); }, DispatcherPriority.Render);
    }

    void Build(ChatTransfer? t, bool instant)
    {
        arc = null; percent = line1 = line2 = countText = null; badge = null;
        row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        if (model.Call != CallState.Idle)
        {
            var ringing = model.Call == CallState.Ringing;
            row.Children.Add(new Border { Width = 30, Height = 30, Margin = new Thickness(2, 0, 0, 0), CornerRadius = new CornerRadius(15), Background = ringing ? Look.Accent : model.Call == CallState.Active ? Green : Soft, Child = Look.Icon(MainWindow.PhonePath, 15, Brushes.White, 2) });
            line1 = Label(12.5, Brushes.White, true); line1.TextTrimming = TextTrimming.CharacterEllipsis; line2 = Label(11, Dim, false);
            row.Children.Add(new StackPanel { Width = 150, VerticalAlignment = VerticalAlignment.Center, Children = { line1, line2 } });
            callLine = line2;
            if (model.Call == CallState.Active) row.Children.Add(Disc(Soft, MainWindow.MicPath, model.CallMuted ? Brush.Parse("#D79A3B") : Soft, () => CallMute?.Invoke()));
            if (ringing) row.Children.Add(Disc(Green, MainWindow.PhonePath, Green, () => CallAccept?.Invoke()));
            row.Children.Add(Disc(Red, Look.CrossPath, Red, () => CallHang?.Invoke()));
            LiveCall();
        }
        else if (t != null)
        {
            row.Children.Add(t.State == TransferState.Done ? Check() : Ring(t));
            line1 = Label(12.5, Brushes.White, true); line1.TextTrimming = TextTrimming.CharacterEllipsis; line2 = Label(11, Dim, false);
            row.Children.Add(new StackPanel { Width = InfoWidth, VerticalAlignment = VerticalAlignment.Center, Children = { line1, line2 } });
            if (t.State == TransferState.Active) row.Children.Add(CancelDisc(t));
            Live(t);
        }
        else
        {
            row.Children.Add(new Logo(24));
            if (model.Connected > 0) row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Green, VerticalAlignment = VerticalAlignment.Center });
        }
        if (model.Unread > 0)
        {
            row.Children.Add(Look.Icon(MainWindow.ChatPath, 16, Brushes.White, 1.8));
            countText = new TextBlock { FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            badge = new Border { Background = Red, CornerRadius = new CornerRadius(10), MinWidth = 20, Height = 20, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, Child = countText, RenderTransformOrigin = RelativePoint.Center };
            row.Children.Add(badge); shownCount = 0; SetCount(pop: !instant);
        }
        host.Child = row;
        // the content fades in while the pill grows or shrinks around it
        row.Transitions = new Transitions { Fade(260, new CubicEaseOut()) };
        if (!instant && animated) { row.Opacity = 0; Dispatcher.UIThread.Post(() => { if (host.Child == row) row.Opacity = 1; }, DispatcherPriority.Render); }
        Fit(instant);
    }

    void Fit(bool instant)
    {
        if (row == null) return;
        row.Measure(Size.Infinity);
        var width = Math.Ceiling(row.DesiredSize.Width + Pad);
        if (instant) { pill.Transitions = null; pill.Width = width; }
        else pill.Width = width;
    }

    TextBlock? callLine;
    void LiveCall()
    {
        if (line1 == null || callLine == null) return;
        var e = model.CallElapsed;
        line1.Text = model.CallLabel ?? "";
        callLine.Text = model.Call switch { CallState.Ringing => "来电 · 语音通话", CallState.Calling => "正在呼叫…", _ => (e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss")) + (model.CallMuted ? " · 已静音" : " · 通话中") };
        if (model.Call == CallState.Active && model.CallNote != null) callLine.Text = model.CallNote;
    }

    Control Disc(IBrush color, string path, IBrush hoverBase, Action click)
    {
        var disc = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = color, Child = Look.Icon(path, 12, Brushes.White, 2.2), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = RelativePoint.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        disc.Transitions = new Transitions { Slide(140, new CubicEaseOut()) };
        disc.PointerEntered += (_, _) => disc.RenderTransform = Ops(0, 1.12);
        disc.PointerExited += (_, _) => disc.RenderTransform = Ops(0, 1);
        disc.PointerPressed += (_, e) => { e.Handled = true; click(); };
        return disc;
    }

    void Live(ChatTransfer? t)
    {
        if (model.Call != CallState.Idle) { LiveCall(); return; }
        if (t != null && arc != null && line1 != null && line2 != null)
        {
            arc.SweepAngle = Math.Max(2, 360 * t.Fraction);
            if (percent != null) percent.Text = ((int)(t.Fraction * 100)).ToString();
            line1.Text = t.Name;
            var left = t.Remaining;
            line2.Text = (t.Incoming ? "接收 " : "发送 ") + $"{(int)(t.Fraction * 100)}%" + (t.BytesPerSecond > 0 ? " · " + MainWindow.SpeedText(t.BytesPerSecond) : "") + (left != null ? " · 剩余 " + MainWindow.TimeLeft(left.Value) : "");
        }
        else if (t != null && line1 != null && line2 != null) { line1.Text = t.Name; line2.Text = "已完成 · " + MainWindow.SizeText(t.Size); }
        if (countText != null && model.Unread != shownCount) { SetCount(pop: true); Fit(false); }
    }

    void SetCount(bool pop)
    {
        shownCount = model.Unread; countText!.Text = shownCount > 99 ? "99+" : shownCount.ToString();
        if (!pop || badge == null || !animated) return;
        var target = badge; target.Transitions = null; target.RenderTransform = Ops(0, 1.55);
        Dispatcher.UIThread.Post(() =>
        {
            target.Transitions = new Transitions { Slide(460, new BackEaseOut()) };
            target.RenderTransform = Ops(0, 1);
        }, DispatcherPriority.Render);
    }

    Control Ring(ChatTransfer t)
    {
        var track = new Ellipse { Stroke = Soft, StrokeThickness = 3.5 };
        arc = new Avalonia.Controls.Shapes.Arc { Stroke = Look.Accent, StrokeThickness = 3.5, StartAngle = -90, SweepAngle = 2, StrokeLineCap = PenLineCap.Round };
        arc.Transitions = new Transitions { new DoubleTransition { Property = Avalonia.Controls.Shapes.Arc.SweepAngleProperty, Duration = animated ? TimeSpan.FromMilliseconds(260) : TimeSpan.Zero, Easing = new CubicEaseOut() } };
        percent = Label(10, Brushes.White, true); percent.HorizontalAlignment = HorizontalAlignment.Center;
        return new Grid { Width = 30, Height = 30, Margin = new Thickness(2, 0, 0, 0), Children = { track, arc, percent } };
    }

    // The ring has closed: the disc turns green and the check springs in.
    Control Check()
    {
        var mark = Look.Icon(CheckPath, 15, Brushes.White, 2.6);
        mark.RenderTransformOrigin = RelativePoint.Center; mark.RenderTransform = Ops(0, animated ? .3 : 1); mark.Opacity = animated ? 0 : 1;
        var disc = new Border { Width = 30, Height = 30, Margin = new Thickness(2, 0, 0, 0), CornerRadius = new CornerRadius(15), Background = Green, Child = mark, RenderTransformOrigin = RelativePoint.Center, RenderTransform = Ops(0, animated ? .7 : 1) };
        if (animated)
            Dispatcher.UIThread.Post(() =>
            {
                disc.Transitions = new Transitions { Slide(420, new BackEaseOut()) }; disc.RenderTransform = Ops(0, 1);
                mark.Transitions = new Transitions { Fade(200, new CubicEaseOut()), Slide(480, new BackEaseOut()) }; mark.Opacity = 1; mark.RenderTransform = Ops(0, 1);
            }, DispatcherPriority.Render);
        return disc;
    }

    Control CancelDisc(ChatTransfer t)
    {
        var disc = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Soft, Child = Look.Icon(Look.CrossPath, 9, Brushes.White, 2.2), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = RelativePoint.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        disc.Transitions = new Transitions { Slide(140, new CubicEaseOut()) };
        disc.PointerEntered += (_, _) => { disc.Background = Red; disc.RenderTransform = Ops(0, 1.12); };
        disc.PointerExited += (_, _) => { disc.Background = Soft; disc.RenderTransform = Ops(0, 1); };
        disc.PointerPressed += (_, e) => { e.Handled = true; CancelClicked?.Invoke(t); };
        return disc;
    }
}
