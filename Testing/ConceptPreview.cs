using Avalonia.Media.Imaging;

namespace ArdUi;

// Preview only: three different visual directions for the home page (`--render-concepts <folder>`).
static class ConceptPreview
{
    const string Monitor = "M3,4 L21,4 L21,16 L3,16 Z M8,20 L16,20 M12,16 L12,20", Arrow = "M5,12 L19,12 M13,6 L19,12 L13,18", Chevron = "M6,9 L12,15 L18,9";
    static readonly (string Name, string Kind, Color Tint)[] Recent =
    {
        ("SY0812A", "online", Color.Parse("#F3D9CC")), ("办公电脑", "connected", Color.Parse("#D9E6D5")), ("展厅内1", "online", Color.Parse("#D8E3F0")),
        ("家里 NAS", "off", Color.Parse("#EBDDF0")), ("展厅内2", "off", Color.Parse("#F2E8C9")),
    };

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        foreach (var (name, build) in new (string, Func<Control>)[] { ("A-hero-link", HeroLink), ("B-glass-gradient", GlassGradient), ("C-dark", Dark) })
        {
            var window = new Window { Width = 720, Height = 520, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontSize = 13, Content = build() };
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

    // ---- shared pieces -----------------------------------------------------------------------------------------------
    internal static TextBlock T(string text, double size, IBrush color, bool bold = false, TextAlignment align = TextAlignment.Left) =>
        new() { Text = text, FontSize = size, Foreground = color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextAlignment = align, TextWrapping = TextWrapping.Wrap };

    static Control Avatar(string name, string kind, Color tint, bool dark, double size = 54)
    {
        var ring = kind switch
        {
            "connected" => new Ellipse { Stroke = Look.OnlineDeep, StrokeThickness = 3 },
            "online" => new Ellipse { Stroke = Look.Online, StrokeThickness = 2 },
            _ => new Ellipse { Stroke = dark ? new SolidColorBrush(Color.Parse("#4A4843")) : Look.Line, StrokeThickness = 1.5 },
        };
        var face = new Ellipse { Margin = new Thickness(5), Fill = new SolidColorBrush(dark ? Color.FromArgb(70, tint.R, tint.G, tint.B) : tint), Opacity = kind == "off" ? .6 : 1 };
        var initial = new TextBlock { Text = name[..1], FontSize = size * 0.34, FontWeight = FontWeight.SemiBold, Foreground = dark ? Brushes.White : Look.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = kind == "off" ? .6 : 1 };
        var disc = new Grid { Width = size, Height = size, Children = { ring, face, initial } };
        var label = T(name, 11, dark ? new SolidColorBrush(Color.Parse("#B9B5AB")) : Look.Muted, false, TextAlignment.Center);
        label.MaxWidth = size + 22;
        return new StackPanel { Spacing = 6, Width = size + 22, Children = { disc, label } };
    }

    internal static Control RecentRow(bool dark)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (name, kind, tint) in Recent) row.Children.Add(Avatar(name, kind, tint, dark));
        return row;
    }

    internal static Button Go(IBrush fill, bool glow)
    {
        var b = new Button { Width = 50, Height = 50, CornerRadius = new CornerRadius(25), Padding = new Thickness(0), Content = Look.Icon(Arrow, 20, Brushes.White, 2.4), Background = fill };
        if (glow) b.Effect = new DropShadowEffect { Color = Color.Parse("#C6613F"), BlurRadius = 24, OffsetX = 0, OffsetY = 6, Opacity = .55 };
        b.Classes.Add("primary"); return b;
    }

    static Control Bar(bool dark)
    {
        var ink = dark ? Brushes.White : Look.Ink;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children =
            { T("ArdUi", 20, ink, true), new TextBlock { Text = "A7B2K9", FontSize = 17, FontWeight = FontWeight.SemiBold, LetterSpacing = 2, Foreground = dark ? new SolidColorBrush(Color.Parse("#7FD19A")) : Look.Online, VerticalAlignment = VerticalAlignment.Center } } };
        var tint = dark ? new SolidColorBrush(Color.Parse("#B9B5AB")) : Look.Muted;
        Button Icon(string path, double size = 16) { var b = new Button { Content = Look.Icon(path, size, tint), Width = 34, Height = 34, Padding = new Thickness(0) }; b.Classes.Add("quiet"); return b; }
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children =
            { new ToggleSwitch { OnContent = "允许被控", OffContent = "允许被控", IsChecked = true, Foreground = ink }, Icon(Look.SharePath), Icon("M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18"), Icon(Look.CrossPath, 12) } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 8, 12, 0), Height = 46, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }

    internal static TextBlock IdText(string text, bool placeholder, bool dark) => new()
    {
        Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 32, FontWeight = FontWeight.SemiBold, LetterSpacing = 9, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Foreground = placeholder ? (dark ? new SolidColorBrush(Color.Parse("#6F6C65")) : Look.Idle) : (dark ? Brushes.White : Look.Ink),
    };

    // ---- A: a hero graphic of the two ends being linked ----------------------------------------------------------------
    static Control HeroLink()
    {
        Control Node(string label, bool filled)
        {
            var disc = new Grid { Width = 64, Height = 64, Children =
            {
                new Ellipse { Fill = filled ? Look.Card : Brushes.Transparent, Stroke = filled ? Look.Line : Look.Accent, StrokeThickness = filled ? 1 : 2, StrokeDashArray = filled ? null : new Avalonia.Collections.AvaloniaList<double> { 3, 3 },
                    Effect = filled ? new DropShadowEffect { Color = Color.Parse("#1F1E1D"), BlurRadius = 18, OffsetY = 5, Opacity = .12 } : null },
                new Viewbox { Width = 26, Height = 26, Child = Look.Icon(Monitor, 26, filled ? Look.Ink : Look.Accent, 1.8) } } };
            return new StackPanel { Spacing = 8, Children = { disc, T(label, 11, Look.Muted, false, TextAlignment.Center) } };
        }
        var link = new Grid { Width = 150, Height = 64, VerticalAlignment = VerticalAlignment.Top, Children =
        {
            new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0,32 C40,8 110,56 150,32"), Stroke = Look.Accent, StrokeThickness = 2.2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 4 }, StrokeLineCap = PenLineCap.Round },
            new Ellipse { Width = 9, Height = 9, Fill = Look.Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(62, 28, 0, 0), VerticalAlignment = VerticalAlignment.Top } } };
        var graphic = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { Node("本机", true), link, Node("远端设备", false) } };
        var input = new Border { CornerRadius = new CornerRadius(30), Background = Look.Card, BorderBrush = Look.Line, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), Width = 430,
            Effect = new DropShadowEffect { Color = Color.Parse("#1F1E1D"), BlurRadius = 26, OffsetY = 8, Opacity = .10 } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(IdText("ABC123", true, false));
        var chev = new Button { Content = Look.Icon(Chevron, 16), Width = 38, Height = 38, Padding = new Thickness(0) }; chev.Classes.Add("quiet"); Grid.SetColumn(chev, 1); row.Children.Add(chev);
        var go = Go(Look.Accent, true); go.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(go, 2); row.Children.Add(go); input.Child = row;
        var stack = new StackPanel { Spacing = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 24, 0, 0), Children =
            { graphic, new StackPanel { Spacing = 4, Children = { T("连接到另一台电脑", 24, Look.Ink, true, TextAlignment.Center), T("输入设备 ID，或点选最近使用的设备", 13, Look.Muted, false, TextAlignment.Center) } }, input, RecentRow(false) } };
        var backdrop = new Border
        {
            Background = new RadialGradientBrush { Center = new RelativePoint(.5, .15, RelativeUnit.Relative), RadiusX = RelativeScalar.Parse("90%"), RadiusY = RelativeScalar.Parse("90%"),
                GradientStops = { new GradientStop(Color.Parse("#FBEADF"), 0), new GradientStop(Color.Parse("#F5F4EE"), .7) } }
        };
        return new Grid { Children = { backdrop, Bar(false), stack } };
    }

    // ---- B: warm gradient with floating glass ---------------------------------------------------------------------------
    static Control GlassGradient()
    {
        Ellipse Orb(string color, double size, double x, double y) => new() { Width = size, Height = size, Fill = new SolidColorBrush(Color.Parse(color)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, y, 0, 0), Effect = new BlurEffect { Radius = 46 } };
        var backdrop = new Grid
        {
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#FCE9DC"), 0), new GradientStop(Color.Parse("#F1E6EE"), .55), new GradientStop(Color.Parse("#DDE8F3"), 1) } },
            Children = { Orb("#E88A63", 260, -40, 220), Orb("#F2B84B", 190, 470, -50), Orb("#8DB6E8", 240, 430, 280) }
        };
        var glass = new Border
        {
            CornerRadius = new CornerRadius(30), Padding = new Thickness(30, 26), Width = 500, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 30, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), BorderThickness = new Thickness(1.2),
            BoxShadow = BoxShadows.Parse("0 20 50 0 #26301E14, 0 2 6 0 #14000000"),
        };
        var field = new Border { CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), Padding = new Thickness(10, 7), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), BorderThickness = new Thickness(1) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(IdText("ABC123", true, false));
        var chev = new Button { Content = Look.Icon(Chevron, 16), Width = 38, Height = 38, Padding = new Thickness(0) }; chev.Classes.Add("quiet"); Grid.SetColumn(chev, 1); row.Children.Add(chev);
        var go = Go(new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#E07A52"), 0), new GradientStop(Color.Parse("#B9532F"), 1) } }, true);
        go.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(go, 2); row.Children.Add(go); field.Child = row;
        glass.Child = new StackPanel { Spacing = 20, Children = { T("连接到设备", 22, Look.Ink, true, TextAlignment.Center), field, new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(90, 60, 40, 20)) }, T("最近", 12, Look.Muted, false, TextAlignment.Center), RecentRow(false) } };
        return new Grid { Children = { backdrop, Bar(false), glass } };
    }

    // ---- C: dark, premium --------------------------------------------------------------------------------------------------
    static Control Dark()
    {
        var bg = Color.Parse("#1B1A18");
        var backdrop = new Grid
        {
            Background = new SolidColorBrush(bg),
            Children = { new Ellipse { Width = 420, Height = 420, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0),
                Fill = new SolidColorBrush(Color.Parse("#C6613F")), Opacity = .20, Effect = new BlurEffect { Radius = 90 } } }
        };
        var field = new Border { CornerRadius = new CornerRadius(30), Padding = new Thickness(10, 8), Width = 440, Background = new SolidColorBrush(Color.Parse("#262421")), BorderBrush = new SolidColorBrush(Color.Parse("#3C3935")), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 0 0 1 #1AC6613F, 0 14 40 0 #66000000") };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(IdText("ABC123", true, true));
        var chev = new Button { Content = Look.Icon(Chevron, 16, new SolidColorBrush(Color.Parse("#B9B5AB"))), Width = 38, Height = 38, Padding = new Thickness(0) }; chev.Classes.Add("quiet"); Grid.SetColumn(chev, 1); row.Children.Add(chev);
        var go = Go(Look.Accent, true); go.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(go, 2); row.Children.Add(go); field.Child = row;
        var stack = new StackPanel { Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Children =
            { new StackPanel { Spacing = 4, Children = { T("连接到设备", 26, Brushes.White, true, TextAlignment.Center), T("端到端加密 · 直连优先", 12, new SolidColorBrush(Color.Parse("#8F8B82")), false, TextAlignment.Center) } }, field, RecentRow(true) } };
        return new Grid { Children = { backdrop, Bar(true), stack } };
    }
}
