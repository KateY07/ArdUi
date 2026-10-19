using Avalonia.Animation;
using Avalonia.VisualTree;

namespace ArdUi;

// Shared pieces of the window chrome: the logo mark (it can breathe while the network starts), glass capsules and discs.
static class Chrome
{
    public static readonly IBrush GlassFill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), GlassEdge = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)),
        GlassStrong = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), Hairline = new SolidColorBrush(Color.FromArgb(70, 60, 40, 20));
    public const string SettingsPath = "M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18",
        BackPath = "M19,12 L5,12 M11,6 L5,12 L11,18", ArrowPath = "M5,12 L19,12 M13,6 L19,12 L13,18", CopyPath = "M9,9 L19,9 L19,19 L9,19 Z M5,15 L5,5 L15,5", ChevronPath = "M6,9 L12,15 L18,9";

    public static LinearGradientBrush Accent() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#E07A52"), 0), new GradientStop(Color.Parse("#B9532F"), 1) },
    };

    public static Grid Backdrop(bool translucent = false)
    {
        string A(string hex) => translucent ? "#B8" + hex[1..] : hex;
        Ellipse Orb(string color, double size, double x, double y) => new() { Width = size, Height = size, Fill = new SolidColorBrush(Color.Parse(color)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, y, 0, 0), Effect = new BlurEffect { Radius = 46 }, IsHitTestVisible = false };
        return new Grid
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(A("#FCE9DC")), 0), new GradientStop(Color.Parse(A("#F1E6EE")), .55), new GradientStop(Color.Parse(A("#DDE8F3")), 1) } },
            Children = { Orb("#E88A63", 260, -40, 220), Orb("#F2B84B", 190, 470, -50), Orb("#8DB6E8", 240, 430, 280) },
        };
    }

    public static Border Capsule(Control child, double padH = 6, double padV = 3) => new()
    {
        Child = child, CornerRadius = new CornerRadius(20), Padding = new Thickness(padH, padV), Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1),
        BoxShadow = BoxShadows.Parse("0 6 18 0 #1A301E14"), VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Disc(Control child) => new()
    {
        Child = child, Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1),
        BoxShadow = BoxShadows.Parse("0 4 12 0 #14301E14"), VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button IconButton(string path, double size = 16, double box = 34)
    {
        var b = new Button { Content = Look.Icon(path, size, Look.Muted), Width = box, Height = box, MinHeight = box, Padding = new Thickness(0), CornerRadius = new CornerRadius(box / 2) };
        b.Classes.Add("quiet"); return b;
    }

    public static Control Divider(double height = 16) => new Border { Width = 1, Height = height, Background = Hairline, VerticalAlignment = VerticalAlignment.Center };
}

// The ArdUi mark: a cursor sending out two signal arcs. With Animate set the arcs pulse outwards in turn.
sealed class Logo : Grid
{
    readonly Avalonia.Controls.Shapes.Path inner, outer;
    CancellationTokenSource? run;
    bool animate;

    public Logo(double size = 28, bool tile = true)
    {
        Width = Height = size; IsHitTestVisible = false;
        string P(double x, double y) => $"{x * size:F2},{y * size:F2}";
        var ink = tile ? Brushes.White : Look.Accent;
        if (tile)
            Children.Add(new Border { CornerRadius = new CornerRadius(size * .28), Background = Chrome.Accent(), BoxShadow = BoxShadows.Parse($"0 {size * .14:F1} {size * .36:F1} 0 #44C6613F") });
        var cursor = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse($"M{P(.30, .24)} L{P(.30, .74)} L{P(.43, .62)} L{P(.52, .82)} L{P(.61, .78)} L{P(.52, .58)} L{P(.70, .58)} Z"), Fill = ink, Stroke = ink, StrokeThickness = size * .03, StrokeJoin = PenLineJoin.Round };
        Avalonia.Controls.Shapes.Path Wave(double r) => new() { Data = Geometry.Parse($"M{P(.34 + r * .139, .28 - r * .990)} A{r * size:F2},{r * size:F2} 0 0 1 {P(.34 + r * .940, .28 - r * .342)}"), Stroke = ink, StrokeThickness = size * .055, StrokeLineCap = PenLineCap.Round };
        inner = Wave(.20); outer = Wave(.27);
        Children.Add(new Grid { Children = { cursor, inner, outer } });
    }

    public bool Animate
    {
        get => animate;
        set { animate = value; Sync(); }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Sync(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Stop(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Sync();
    }

    void Sync() { if (animate && IsVisible && this.GetVisualRoot() != null) Start(); else Stop(); }
    void Start()
    {
        if (run != null) return;
        run = new CancellationTokenSource();
        Animation Pulse(double delayMs) => new()
        {
            Duration = TimeSpan.FromMilliseconds(1300), Delay = TimeSpan.FromMilliseconds(delayMs), IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.25) } },
                new KeyFrame { Cue = new Cue(.35), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.25) } },
            }
        };
        try { _ = Pulse(0).RunAsync(inner, run.Token); _ = Pulse(260).RunAsync(outer, run.Token); } catch (InvalidOperationException) { }
    }
    void Stop() { run?.Cancel(); run = null; inner.Opacity = outer.Opacity = 1; }
}
