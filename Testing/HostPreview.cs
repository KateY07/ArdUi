namespace ArdUi;

// Preview only: the controlled side with per-device permissions (`--render-host-preview <folder>`). Nothing here is wired to the engine.
static class HostPreview
{
    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        foreach (var (name, build) in new (string, Func<Control>)[] { ("H1-approval", Approval), ("H2-invite", Invite), ("H3-access", Access) })
        {
            var window = new Window { Width = 400, Height = 650, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontSize = 12, Content = build() };
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

    static TextBlock T(string text, double size, IBrush? brush = null, bool bold = false) => new() { Text = text, FontSize = size, Foreground = brush ?? Look.Ink, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };

    const string Tick = "M5,12.5 L10,17 L19,7.5";

    // A permission switch drawn as a chip: filled with a tick when allowed, outlined when not.
    static Control Chip(string title, string note, bool on)
    {
        var tick = on ? new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Look.Accent, Child = Look.Icon(Tick, 11, Brushes.White, 2.4) }
                      : new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderBrush = Look.Idle, BorderThickness = new Thickness(1.4) };
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { T(title, 13, on ? Look.Ink : Look.Muted, true) } };
        if (note.Length != 0) text.Children.Add(T(note, 11, Look.Muted));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { tick, text } };
        return new Border
        {
            Child = row, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(12),
            Background = on ? new SolidColorBrush(Color.FromArgb(255, 252, 236, 228)) : new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            BorderBrush = on ? Look.Accent : Look.Line, BorderThickness = new Thickness(on ? 1.2 : 1),
        };
    }

    static Border Card(Control content, bool solid = false) => new()
    {
        Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(16), Background = solid ? Look.CardSolid : Look.Card, BorderBrush = solid ? Look.Line : Look.CardEdge,
        BorderThickness = new Thickness(1), BoxShadow = Look.CardShadow,
    };

    static Control Page(Control body, bool dim)
    {
        var grid = new Grid { Children = { Chrome.Backdrop(false) } };
        if (dim) grid.Children.Add(new Border { Background = Brush.Parse("#8C1F1E1D"), IsHitTestVisible = false });
        grid.Children.Add(body);
        return grid;
    }

    static Control Permissions() => new StackPanel
    {
        Spacing = 6,
        Children = { Chip("远程桌面（FRD）", "推荐：加密、可调画质", true), Chip("RDP 远程桌面", "Windows 原生，端口 3389", false), Chip("文件共享", "SMB，端口 445", false) },
    };

    static Border Modal(Control content)
    {
        var card = Card(content, true);
        card.Width = 360; card.Padding = new Thickness(20); card.CornerRadius = new CornerRadius(22);
        card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
        return card;
    }

    static Control Approval()
    {
        var yes = new Button { Content = "是，已核对 EndpointId" }; yes.Classes.Add("primary");
        var id = new Border { Background = Brushes.White, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
            Child = new TextBlock { Text = "5889f4c2e3c89af503a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1", FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap } };
        return Page(Modal(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                T("Q8X2ND 请求获得本机控制权", 17, bold: true),
                T("请通过电话或当面，与对方 ArdUi 中显示的完整 EndpointId 逐字核对。", 12, Look.Muted),
                id,
                T("允许对方使用", 12, Look.Muted, true),
                Permissions(),
                T("默认只允许 FRD。以后可在 设置 → 被控与授权 中随时修改。", 11, Look.Muted),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yes, new Button { Content = "否" } } },
            }
        }), true);
    }

    static Control Invite()
    {
        var copy = new Button { Content = Look.IconText(Look.CopyPath, "一键复制"), HorizontalAlignment = HorizontalAlignment.Left };
        var ring = new Grid
        {
            Width = 64, Height = 64,
            Children =
            {
                new Ellipse { Stroke = Look.Ghost, StrokeThickness = 6, Margin = new Thickness(3) },
                new Avalonia.Controls.Shapes.Arc { Stroke = Look.Accent, StrokeThickness = 6, StartAngle = -90, SweepAngle = 230, Margin = new Thickness(3), StrokeLineCap = PenLineCap.Round },
                new TextBlock { Text = "37", FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            }
        };
        var password = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T("连接密码", 12, Look.Muted), new TextBlock { Text = "384291", FontFamily = new FontFamily("Consolas"), FontSize = 30, FontWeight = FontWeight.SemiBold, LetterSpacing = 5 } } };
        return Page(Modal(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                T("邀请设备", 17, bold: true),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { ring, password } },
                new StackPanel { Children = { T("设备 ID", 12, Look.Muted), new TextBlock { Text = "A7B2K9", FontFamily = new FontFamily("Consolas"), FontSize = 22, FontWeight = FontWeight.SemiBold, LetterSpacing = 3 } } },
                T("对方可使用", 12, Look.Muted, true),
                Permissions(),
                copy,
            }
        }), true);
    }

    static Control Small(string text, bool on) => new Border
    {
        Padding = new Thickness(11, 4), CornerRadius = new CornerRadius(14),
        Background = on ? new SolidColorBrush(Color.FromArgb(255, 252, 236, 228)) : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
        BorderBrush = on ? Look.Accent : Look.Line, BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            Children = { on ? Look.Icon(Tick, 11, Look.Accent, 2.2) : new Border { Width = 1 }, new TextBlock { Text = text, FontSize = 12, Foreground = on ? Look.Accent : Look.Muted, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal } },
        },
    };

    static Control DeviceCard(string code, string note, bool frd, bool rdp, bool files)
    {
        var revoke = new Button { Content = "撤销", MinHeight = 26, Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Right };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new StackPanel { Children = { T(code, 14, bold: true), T(note, 11, Look.Muted) } });
        Grid.SetColumn(revoke, 1); head.Children.Add(revoke);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Small("FRD", frd), Small("RDP", rdp), Small("文件", files) } };
        return Card(new StackPanel { Spacing = 8, Children = { head, chips } });
    }

    static Control Access()
    {
        var cross = new Button { Content = Look.Icon(Look.CrossPath, 10, Brushes.White, 2.4), Width = 26, Height = 26 }; cross.Classes.Add("circlex");
        var live = new TextBlock { FontSize = 12, Foreground = Look.Muted };
        live.Inlines!.Add(new Avalonia.Controls.Documents.Run("IPv6") { FontWeight = FontWeight.SemiBold, Foreground = Look.OnlineDeep });
        live.Inlines.Add(new Avalonia.Controls.Documents.Run(" · 18 Mbps · 9 ms"));
        var active = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        active.Children.Add(new StackPanel { Children = { T("7TLM4P", 14, bold: true), live } });
        Grid.SetColumn(cross, 1); active.Children.Add(cross);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (name, on) in new[] { ("通用", false), ("设备管理", false), ("被控与授权", true), ("日志与关于", false) })
        {
            var tab = new Button { Content = name, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 5) };
            if (on) tab.Classes.Add("primary");
            tabs.Children.Add(tab);
        }
        var body = new StackPanel
        {
            Margin = new Thickness(14, 56, 10, 0), Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Look.Icon(Chrome.BackPath, 16), T("设置", 18, bold: true) } },
                tabs,
                T("正在访问本机", 12, Look.Muted, true), Card(active),
                T("可访问本机的设备（点击标签修改权限）", 12, Look.Muted, true),
                DeviceCard("7TLM4P", "5889f4c2… · 刚刚授权", true, false, false),
                DeviceCard("Q8X2ND", "9a31b7e0… · 3 天前", true, true, true),
                DeviceCard("K4DD0B", "c04e12aa… · 2 周前", true, false, true),
                T("新授权的设备默认只允许 FRD；升级前已授权的设备保持原有的全部权限。", 11, Look.Muted),
            }
        };
        var capsule = Chrome.Capsule(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 2),
            Children =
            {
                new Logo(24), new TextBlock { Text = "A7B2K9", FontFamily = new FontFamily("Consolas"), FontSize = 14, FontWeight = FontWeight.SemiBold, LetterSpacing = 2, VerticalAlignment = VerticalAlignment.Center },
                Chrome.Divider(), new Ellipse { Width = 9, Height = 9, Fill = Look.Online }, new TextBlock { Text = "在线", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center },
            }
        });
        capsule.HorizontalAlignment = HorizontalAlignment.Left; capsule.VerticalAlignment = VerticalAlignment.Top; capsule.Margin = new Thickness(14, 8, 10, 0);
        return Page(new Grid { Children = { body, capsule } }, false);
    }
}
