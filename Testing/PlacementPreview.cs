namespace ArdUi;

// Preview only: two places for the "allow control" switch (`--render-placement <folder>`).
static class PlacementPreview
{
    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        foreach (var (name, build) in new (string, Func<Control>)[] { ("P1-bottom-strip", () => Page(false)), ("P2-identity-popover", () => Page(true)) })
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

    const string Settings = "M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18";
    static readonly IBrush GlassFill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), GlassEdge = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255));

    static Control Page(bool popover)
    {
        Ellipse Orb(string color, double size, double x, double y) => new() { Width = size, Height = size, Fill = new SolidColorBrush(Color.Parse(color)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, y, 0, 0), Effect = new BlurEffect { Radius = 46 } };
        var backdrop = new Grid
        {
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#FCE9DC"), 0), new GradientStop(Color.Parse("#F1E6EE"), .55), new GradientStop(Color.Parse("#DDE8F3"), 1) } },
            Children = { Orb("#E88A63", 260, -40, 220), Orb("#F2B84B", 190, 470, -50), Orb("#8DB6E8", 240, 430, 280) }
        };

        // title bar: brand on the left, identity capsule in the middle, only window-level controls on the right
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children =
            { TitleBarPreview.LogoMark(28), new TextBlock { Text = "ArdUi", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center } } };
        var identity = TitleBarPreview.Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(10, 0, 4, 0), Children =
            { TitleBarPreview.StatusDot(), TitleBarPreview.Mono("A7B2K9", 16), popover ? Look.Icon(Chevron, 12) : TitleBarPreview.IconButton(Copy, 14, null, 28) } });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
            { TitleBarPreview.IconButton(Settings), TitleBarPreview.Minus(), TitleBarPreview.Cross() } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(18, 12, 12, 0), Height = 46, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        brand.HorizontalAlignment = HorizontalAlignment.Left; bar.Children.Add(brand);
        Grid.SetColumn(identity, 1); bar.Children.Add(identity);
        controls.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(controls, 2); bar.Children.Add(controls);

        // the glass card from direction B
        var field = new Border { CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), Padding = new Thickness(10, 7), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), BorderThickness = new Thickness(1) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(ConceptPreview.IdText("ABC123", true, false));
        var go = ConceptPreview.Go(new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#E07A52"), 0), new GradientStop(Color.Parse("#B9532F"), 1) } }, true);
        go.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(go, 2); row.Children.Add(go); field.Child = row;
        var card = new Border
        {
            CornerRadius = new CornerRadius(30), Padding = new Thickness(30, 26), Width = 500, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, popover ? 30 : 10, 0, 0),
            Background = GlassFill, BorderBrush = GlassEdge, BorderThickness = new Thickness(1.2), BoxShadow = BoxShadows.Parse("0 20 50 0 #26301E14, 0 2 6 0 #14000000"),
            Child = new StackPanel { Spacing = 20, Children = { ConceptPreview.T("连接到设备", 22, Look.Ink, true, TextAlignment.Center), field, new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(90, 60, 40, 20)) }, ConceptPreview.T("最近", 12, Look.Muted, false, TextAlignment.Center), ConceptPreview.RecentRow(false) } }
        };

        var layers = new Grid { Children = { backdrop, card, bar } };
        if (!popover)
        {
            // P1: a quiet strip at the bottom: this machine's own role, kept away from the window controls
            var strip = TitleBarPreview.Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(14, 2), Children =
            {
                new TextBlock { Text = "本机", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center },
                TitleBarPreview.Switch("允许被控"),
                TitleBarPreview.Divider(),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { Look.Icon(Look.SharePath, 15, Look.Accent), new TextBlock { Text = "邀请", FontSize = 13, Foreground = Look.Accent, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } },
            } });
            strip.HorizontalAlignment = HorizontalAlignment.Center; strip.VerticalAlignment = VerticalAlignment.Bottom; strip.Margin = new Thickness(0, 0, 0, 18);
            layers.Children.Add(strip);
        }
        else
        {
            // P2: the identity capsule opens a small panel with everything about this machine
            Control Line(string title, string note, Control trailing)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
                g.Children.Add(new StackPanel { Children = { ConceptPreview.T(title, 13, Look.Ink, true), ConceptPreview.T(note, 11, Look.Muted) } });
                trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 1); g.Children.Add(trailing); return g;
            }
            var share = new Button { Content = "邀请…", Padding = new Thickness(12, 4), MinHeight = 28 }; share.Classes.Add("primary");
            var panel = new Border
            {
                Width = 300, CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 14), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 62, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), BorderBrush = GlassEdge, BorderThickness = new Thickness(1.2), BoxShadow = BoxShadows.Parse("0 18 44 0 #33301E14"),
                Child = new StackPanel { Spacing = 2, Children =
                {
                    Line("设备 ID", "A7B2K9 · 在线 · 直连优先", new Button { Content = Look.Icon(Copy, 14), Width = 30, Height = 30, Padding = new Thickness(0), Classes = { "quiet" } }),
                    new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(70, 60, 40, 20)) },
                    Line("允许被控", "他人可凭设备 ID 申请连接本机", TitleBarPreview.Switch("")),
                    new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(70, 60, 40, 20)) },
                    Line("邀请设备", "生成首次密码，发给对方", share),
                } }
            };
            layers.Children.Add(panel);
        }
        return layers;
    }

    const string Chevron = "M6,9 L12,15 L18,9", Copy = "M9,9 L19,9 L19,19 L9,19 Z M5,15 L5,5 L15,5";
}
