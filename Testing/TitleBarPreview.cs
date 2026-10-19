namespace ArdUi;

// Preview only: three title-bar ideas on the glass-gradient backdrop (`--render-titlebars <folder>`).
static class TitleBarPreview
{
    const string Arrow = "M5,12 L19,12 M13,6 L19,12 L13,18", Copy = "M9,9 L19,9 L19,19 L9,19 Z M5,15 L5,5 L15,5";
    static readonly IBrush GlassFill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), GlassEdge = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255));

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        foreach (var (name, build) in new (string, Func<Control>)[] { ("T1-island", Island), ("T2-status-capsule", StatusCapsule), ("T3-ghost", Ghost) })
        {
            var window = new Window { Width = 720, Height = 190, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontSize = 13, Content = Stage(build()) };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var bitmap = window.CaptureRenderedFrame() ?? throw new IOException("无法渲染预览。");
                bitmap.Save(Path.Combine(folder, name + ".png"));
            }
            finally { window.Close(); }
        }
        return 0;
    }

    static Control Stage(Control bar)
    {
        Ellipse Orb(string color, double size, double x, double y) => new() { Width = size, Height = size, Fill = new SolidColorBrush(Color.Parse(color)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, y, 0, 0), Effect = new BlurEffect { Radius = 46 } };
        var backdrop = new Grid
        {
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#FCE9DC"), 0), new GradientStop(Color.Parse("#F1E6EE"), .55), new GradientStop(Color.Parse("#DDE8F3"), 1) } },
            Children = { Orb("#E88A63", 200, -40, 60), Orb("#F2B84B", 150, 520, -50), Orb("#8DB6E8", 190, 480, 90) }
        };
        // the top edge of the glass card below, for context
        var card = new Border { Height = 80, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(110, 0, 110, -30), CornerRadius = new CornerRadius(30), Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1.2) };
        return new Grid { Children = { backdrop, card, bar } };
    }

    // ---- pieces ------------------------------------------------------------------------------------------------------------
    internal static Control LogoMark(double size = 28)
    {
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .28), Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#E07A52"), 0), new GradientStop(Color.Parse("#B9532F"), 1) } },
            BoxShadow = BoxShadows.Parse("0 4 10 0 #44C6613F") };
        string P(double x, double y) => $"{x * size:F2},{y * size:F2}";
        var cursor = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse($"M{P(.30, .24)} L{P(.30, .74)} L{P(.43, .62)} L{P(.52, .82)} L{P(.61, .78)} L{P(.52, .58)} L{P(.70, .58)} Z"), Fill = Brushes.White, Stroke = Brushes.White, StrokeThickness = size * .03, StrokeJoin = PenLineJoin.Round };
        Avalonia.Controls.Shapes.Path Wave(double r) => new() { Data = Geometry.Parse($"M{P(.34 + r * .139, .28 - r * .990)} A{r * size:F2},{r * size:F2} 0 0 1 {P(.34 + r * .940, .28 - r * .342)}"), Stroke = Brushes.White, StrokeThickness = size * .055, StrokeLineCap = PenLineCap.Round };
        var glyph = new Grid { Width = size, Height = size, Children = { cursor, Wave(.20), Wave(.27) } };
        return new Grid { Width = size, Height = size, Children = { tile, glyph } };
    }

    internal static Border Capsule(Control child, double padH = 6, double padV = 3) => new()
    {
        Child = child, CornerRadius = new CornerRadius(20), Padding = new Thickness(padH, padV), Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1),
        BoxShadow = BoxShadows.Parse("0 6 18 0 #1A301E14"), VerticalAlignment = VerticalAlignment.Center,
    };

    internal static Button IconButton(string path, double size = 16, IBrush? tint = null, double box = 34)
    {
        var b = new Button { Content = Look.Icon(path, size, tint ?? Look.Muted), Width = box, Height = box, Padding = new Thickness(0), CornerRadius = new CornerRadius(box / 2) }; b.Classes.Add("quiet"); return b;
    }
    internal static Button Cross() => IconButton(Look.CrossPath, 12);
    internal static Button Minus() { var b = new Button { Content = Look.MinusIcon(), Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(17) }; b.Classes.Add("quiet"); return b; }
    internal static Control Divider() => new Border { Width = 1, Height = 18, Background = new SolidColorBrush(Color.FromArgb(70, 60, 40, 20)), Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
    internal static TextBlock Mono(string text, double size = 15) => new() { Text = text, FontFamily = new FontFamily("Consolas"), FontSize = size, FontWeight = FontWeight.SemiBold, LetterSpacing = 2, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center };
    internal static Ellipse StatusDot(bool connected = false) => connected
        ? new Ellipse { Width = 10, Height = 10, Fill = Look.OnlineDeep, Stroke = Look.OnlineHalo, StrokeThickness = 3, VerticalAlignment = VerticalAlignment.Center }
        : new Ellipse { Width = 9, Height = 9, Fill = Look.Online, VerticalAlignment = VerticalAlignment.Center };
    internal static ToggleSwitch Switch(string text = "允许被控") => new() { OnContent = text, OffContent = text, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    static Grid Bar(Control? left, Control? centre, Control? right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(18, 12, 12, 0), Height = 46, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        if (left != null) { left.HorizontalAlignment = HorizontalAlignment.Left; left.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(left); }
        if (centre != null) { Grid.SetColumn(centre, 1); grid.Children.Add(centre); }
        if (right != null) { right.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(right, 2); grid.Children.Add(right); }
        return grid;
    }

    // ---- T1: a floating "island" with the identity in the middle; controls grouped in a glass capsule on the right ----------
    static Control Island()
    {
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { LogoMark(30), new TextBlock { Text = "ArdUi", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center } } };
        var identity = Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(10, 0, 4, 0), Children = { StatusDot(), Mono("A7B2K9", 16), IconButton(Copy, 14, null, 28) } });
        var tools = Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children =
            { new Border { Padding = new Thickness(8, 0, 4, 0), Child = Switch() }, IconButton(Look.SharePath), IconButton("M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18"), Divider(), Minus(), Cross() } });
        return Bar(brand, identity, tools);
    }

    // ---- T2: the centre capsule speaks: connection quality + identity; window controls are individual glass discs --------
    static Control StatusCapsule()
    {
        var centre = Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 2), Children =
            { LogoMark(24), Mono("A7B2K9", 16), new Border { Width = 1, Height = 16, Background = new SolidColorBrush(Color.FromArgb(70, 60, 40, 20)), VerticalAlignment = VerticalAlignment.Center },
              StatusDot(true), new TextBlock { Text = "直连优先 · IPv6", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center } } });
        Border Disc(Control c) => new() { Child = c, Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 4 12 0 #14301E14"), VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Switch("被控"), Disc(IconButton(Look.SharePath, 15, null, 34)), Disc(IconButton("M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18", 15, null, 34)), Disc(Minus()), Disc(Cross()) } };
        return Bar(centre, null, right);
    }

    // ---- T3: ghost bar - nothing but the brand, a copyable code chip and quiet icons that gain glass on hover ---------------
    static Control Ghost()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
            { LogoMark(30), new TextBlock { Text = "ArdUi", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center },
              new Border { Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center,
                  Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { StatusDot(), Mono("A7B2K9", 15) } } } } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children =
            { new TextBlock { Text = "被控 · 开", FontSize = 12, Foreground = Look.OnlineDeep, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, IconButton(Look.SharePath), IconButton("M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18"), Minus(), Cross() } };
        return Bar(left, null, right);
    }
}
