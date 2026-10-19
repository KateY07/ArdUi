namespace ArdUi;

// Preview only: static mock-ups of the planned minimal home page and settings page.
// Nothing here is connected to the real connect flow; `--render-home-preview <folder>` writes one PNG per state.
static class HomePreview
{
    static readonly IBrush Ink = Look.Ink, Muted = Look.Muted, Danger = Brush.Parse("#B4472F");
    const string Chevron = "M6,9 L12,15 L18,9", Arrow = "M5,12 L19,12 M13,6 L19,12 L13,18", Check = "M5,13 L10,18 L19,7", Back = "M19,12 L5,12 M11,6 L5,12 L11,18",
        Settings = "M4,6 L12,6 M16,6 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M16,6 L20,6 M4,12 L6,12 M10,12 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M10,12 L20,12 M4,18 L13,18 M17,18 a2,2 0 1 1 -4,0 a2,2 0 1 1 4,0 z M17,18 L20,18";

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        var states = new (string Name, Func<Control> Build)[]
        {
            ("00-startup", Startup),
            ("01-home", () => Home(Input("", true, false, true), Hint("输入对方的 6 位设备 ID，或从历史中选择"))),
            ("02-history", () => Home(Input("", true, false, true), Hint("输入对方的 6 位设备 ID，或从历史中选择"), History())),
            ("03-first-time-password", FirstTime),
            ("04-verify-fingerprint", Fingerprint),
            ("05-progress", Progress),
            ("06-no-permission", NoPermission),
            ("07-network-failure", NetworkFailure),
            ("08-connected", Connected),
            ("09-settings-devices", SettingsDevices),
            ("10-menu", MenuOpen),
            ("11-logs", Logs),
            ("12-network-diagnostics", NetDiag),
        };
        foreach (var (name, build) in states)
        {
            var window = new Window { Width = 720, Height = 520, Background = Look.Bg, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontSize = 13, Foreground = Ink, Content = build() };
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

    // ---- building blocks -------------------------------------------------------------------------------------------
    static TextBlock T(string text, double size = 13, bool bold = false, IBrush? color = null, TextAlignment align = TextAlignment.Left) =>
        new() { Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = color ?? Ink, TextAlignment = align, TextWrapping = TextWrapping.Wrap };
    static TextBlock Hint(string text) => T(text, 12, false, Muted, TextAlignment.Center);
    static Border Card(Control child, double padding = 16, double radius = 16) =>
        new() { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius), Background = Look.Card, BorderBrush = Look.Line, BorderThickness = new Thickness(1), BoxShadow = Look.CardShadow };
    static Button Pill(string text, bool primary = false) { var b = new Button { Content = text }; if (primary) b.Classes.Add("primary"); return b; }
    static Button Quiet(string path, double size = 16, double box = 34) { var b = new Button { Content = Look.Icon(path, size), Width = box, Height = box, Padding = new Thickness(0) }; b.Classes.Add("quiet"); return b; }
    static Button Go(bool enabled = true)
    {
        var b = new Button { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Padding = new Thickness(0), Content = Look.Icon(Arrow, 18, Brushes.White, 2.2), Opacity = enabled ? 1 : .45 };
        b.Classes.Add("primary"); return b;
    }
    static Ellipse Dot(string kind) => kind switch
    {
        "connected" => new Ellipse { Width = 12, Height = 12, Fill = Look.OnlineDeep, Stroke = Look.OnlineHalo, StrokeThickness = 3 },
        "online" => new Ellipse { Width = 11, Height = 11, Fill = Brushes.Transparent, Stroke = Look.Online, StrokeThickness = 2 },
        "busy" => new Ellipse { Width = 10, Height = 10, Fill = Look.Relay },
        _ => new Ellipse { Width = 8, Height = 8, Fill = Look.Idle },
    };

    static Control Header(bool settings = false, string title = "设置")
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        if (settings) { left.Children.Add(Quiet(Back)); left.Children.Add(T(title, 20, true)); }
        else
        {
            left.Children.Add(T("ArdUi", 20, true));
            left.Children.Add(new TextBlock { Text = "A7B2K9", FontSize = 17, FontWeight = FontWeight.SemiBold, LetterSpacing = 2, Foreground = Look.Online, VerticalAlignment = VerticalAlignment.Center });
        }
        var minus = new Button { Content = Look.MinusIcon(), Width = 34, Height = 34, Padding = new Thickness(0) }; minus.Classes.Add("quiet");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!settings)
        {
            right.Children.Add(new ToggleSwitch { OnContent = "允许被控", OffContent = "允许被控", IsChecked = true, VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(Quiet(Look.SharePath));
            right.Children.Add(Quiet(Settings));
        }
        right.Children.Add(minus); right.Children.Add(Quiet(Look.CrossPath, 12));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 46, Margin = new Thickness(0, 0, 0, 6) };
        grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right);
        return grid;
    }

    static Control Shell(Control body, bool settings = false, string footer = "可信服务器 · https://f.visnova.cn/ · 已连接", string title = "设置")
    {
        var root = new Grid { Margin = new Thickness(20, 8, 12, 14), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(Header(settings, title));
        Grid.SetRow(body, 1); root.Children.Add(body);
        var status = T(footer, 11, false, Muted); status.VerticalAlignment = VerticalAlignment.Center;
        var more = new Button { Content = Dots(), Width = 34, Height = 28, Padding = new Thickness(0) }; more.Classes.Add("quiet");
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottom.Children.Add(status); Grid.SetColumn(more, 1); bottom.Children.Add(more);
        Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        return root;
    }

    // The big six-character field with a history chevron and the round "go" button.
    static Border Input(string text, bool placeholder, bool readOnly, bool chevron, bool goEnabled = false)
    {
        var field = new TextBlock
        {
            Text = placeholder && text.Length == 0 ? "ABC123" : text, FontFamily = new FontFamily("Consolas"), FontSize = 30, FontWeight = FontWeight.SemiBold, LetterSpacing = 8,
            Foreground = placeholder && text.Length == 0 ? Look.Idle : readOnly ? Muted : Ink, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Width = 420 };
        row.Children.Add(field);
        if (chevron) { var c = Quiet(Chevron, 16, 38); Grid.SetColumn(c, 1); row.Children.Add(c); }
        var go = Go(goEnabled || text.Length == 6); go.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(go, 2); row.Children.Add(go);
        return Card(row, 9, 28);
    }

    static Control Home(Control input, Control hint, Control? extra = null)
    {
        var stack = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(T("连接到设备", 15, false, Muted, TextAlignment.Center));
        stack.Children.Add(input); stack.Children.Add(hint);
        if (extra != null) stack.Children.Add(extra);
        // keep the input at the optical centre even when a list drops below it
        var host = new Grid { Children = { stack } };
        stack.Margin = new Thickness(0, extra == null ? 0 : 80, 0, 0);
        stack.VerticalAlignment = extra == null ? VerticalAlignment.Center : VerticalAlignment.Top;
        if (extra != null) stack.Margin = new Thickness(0, 90, 0, 0);
        return Shell(host);
    }

    static Control History()
    {
        Control Row(string kind, string name, string id, string detail)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8, 7), Background = Brushes.Transparent };
            var dot = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), Children = { Dot(kind) } };
            var names = new StackPanel { Children = { T(name, 14, true), T(detail, 11, false, Muted) } };
            Grid.SetColumn(names, 1);
            var code = new TextBlock { Text = id, FontFamily = new FontFamily("Consolas"), FontSize = 14, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 1 };
            Grid.SetColumn(code, 2);
            g.Children.Add(dot); g.Children.Add(names); g.Children.Add(code); return g;
        }
        var list = new StackPanel { Width = 396, Spacing = 0 };
        list.Children.Add(Row("online", "SY0812A", "K4DD0B", "在线 · 最近连接 3 小时前"));
        list.Children.Add(Row("connected", "办公电脑", "C8M3P6", "已连接 · P2P 直连"));
        list.Children.Add(Row("online", "展厅内1", "3MKXWS", "在线 · 最近连接 2 天前"));
        list.Children.Add(Row("off", "家里 NAS", "T7Q2LM", "离线 · 最近连接 5 天前"));
        list.Children.Add(new Border { Height = 1, Background = Look.Line, Margin = new Thickness(8, 4) });
        list.Children.Add(new TextBlock { Text = "管理设备…", FontSize = 12, Foreground = Look.Accent, Margin = new Thickness(10, 6, 0, 6) });
        var card = Card(list, 6, 16); card.HorizontalAlignment = HorizontalAlignment.Center; return card;
    }

    static Control Centered(Control card, double width = 460) { card.Width = width; card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center; return Shell(card); }

    static Control PasswordBox(string value, bool error)
    {
        var field = new TextBlock { Text = value.Length == 0 ? "首次密码（6 位数字）" : value, FontFamily = new FontFamily("Consolas"), FontSize = value.Length == 0 ? 15 : 24, FontWeight = FontWeight.SemiBold,
            LetterSpacing = value.Length == 0 ? 0 : 8, Foreground = value.Length == 0 ? Look.Idle : Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(field); var go = Go(value.Length == 6); Grid.SetColumn(go, 1); row.Children.Add(go);
        var box = Card(row, 6, 24); box.BorderBrush = error ? Danger : Look.Line; box.Height = 58; return box;
    }

    static Control Identity(string name, string id, string state, string kind) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children =
            { new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Children = { Dot(kind) } }, T(name, 16, true),
              new TextBlock { Text = id, FontFamily = new FontFamily("Consolas"), FontSize = 15, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 2 }, T(state, 12, false, Muted) } };


    // ---- logs, menu, start-up and network diagnostics ---------------------------------------------------------------
    static Control Dots()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++) row.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Muted });
        return row;
    }

    static Control Startup()
    {
        var stack = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Spinner(38) { HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(T("正在连接可信服务器…", 14, false, Muted, TextAlignment.Center));
        return Shell(stack, false, "正在初始化网络…");
    }

    static Control MenuOpen()
    {
        Control Item(string text, string? hint = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 8), Background = Brushes.Transparent };
            g.Children.Add(T(text, 13));
            if (hint != null) { var h = T(hint, 11, false, Muted); Grid.SetColumn(h, 1); g.Children.Add(h); }
            return g;
        }
        var list = new StackPanel { Width = 190 };
        foreach (var (text, hint) in new[] { ("设置", ""), ("日志", ""), ("网络诊断", ""), ("导出诊断包", ""), ("关于 ArdUi", "v4.pre9") })
            list.Children.Add(Item(text, hint.Length == 0 ? null : hint));
        var menu = Card(list, 6, 14); menu.HorizontalAlignment = HorizontalAlignment.Right; menu.VerticalAlignment = VerticalAlignment.Bottom; menu.Margin = new Thickness(0, 0, 8, 44);
        var home = Home(Input("", true, false, true), Hint("输入对方的 6 位设备 ID，或从历史中选择"));
        return new Grid { Children = { home, menu } };
    }

    static Control Chip(string text, string kind)
    {
        var (bg, fg) = kind switch
        {
            "ok" => (Brush.Parse("#E4F1E8"), Look.OnlineDeep), "warn" => (Brush.Parse("#FBEFD6"), Brush.Parse("#9A6A12")), "bad" => (Brush.Parse("#F7E0DA"), Danger), _ => (Look.Ghost, Muted),
        };
        return new Border { Background = bg, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2), VerticalAlignment = VerticalAlignment.Center, Child = T(text, 11, true, fg) };
    }

    static Control Logs()
    {
        Control Chipbtn(string text, bool on) { var b = new Button { Content = text, Padding = new Thickness(12, 4), MinHeight = 28 }; b.Classes.Add(on ? "primary" : "quiet"); return b; }
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Chipbtn("全部", true), Chipbtn("连接", false), Chipbtn("网络", false), Chipbtn("错误", false) } };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Pill("复制"), Pill("导出诊断包") } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 10) };
        bar.Children.Add(filters); Grid.SetColumn(tools, 1); bar.Children.Add(tools);
        Control Line(string time, string level, string group, string text)
        {
            var color = level == "ERROR" ? Danger : level == "WARN" ? Brush.Parse("#9A6A12") : Muted;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("62,52,44,*"), Margin = new Thickness(0, 3) };
            var mono = new FontFamily("Consolas");
            g.Children.Add(new TextBlock { Text = time, FontFamily = mono, FontSize = 12, Foreground = Muted });
            var lv = new TextBlock { Text = level, FontFamily = mono, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = color }; Grid.SetColumn(lv, 1); g.Children.Add(lv);
            var gr = T(group, 12, false, Muted); Grid.SetColumn(gr, 2); g.Children.Add(gr);
            var msg = T(text, 12, false, level == "ERROR" ? Danger : Ink); Grid.SetColumn(msg, 3); g.Children.Add(msg);
            return g;
        }
        var lines = new StackPanel();
        lines.Children.Add(Line("18:27:20", "INFO", "连接", "已选择网络路径：P2P 直连 · IPv6 · RTT 6.2 ms"));
        lines.Children.Add(Line("18:27:18", "INFO", "连接", "对方已批准，正在建立加密连接"));
        lines.Children.Add(Line("18:27:12", "INFO", "连接", "正在连接 SY0812A（K4DD0B）"));
        lines.Children.Add(Line("18:26:41", "WARN", "网络", "候选中继 s-3621… 验证超时，改用回退中继"));
        lines.Children.Add(Line("18:25:03", "INFO", "服务器", "可信服务器连接已恢复"));
        lines.Children.Add(Line("18:25:00", "ERROR", "服务器", "无法连接可信服务器：请求超时（20 秒）"));
        lines.Children.Add(Line("18:24:58", "INFO", "系统", "ArdUi v4.pre9 已启动"));
        var card = Card(lines, 14, 14);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(T("显示最近 500 条；主界面只显示面向用户的提示。", 11, false, Muted));
        var verbose = new CheckBox { Content = "详细网络诊断（新连接生效）", FontSize = 11 }; Grid.SetColumn(verbose, 1); footer.Children.Add(verbose);
        var page = new StackPanel { Children = { bar, card, footer } };
        return Shell(page, true, "日志", "日志");
    }

    static Control NetDiag()
    {
        Control Row(string key, string value, string? chip = null, string kind = "ok")
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), Margin = new Thickness(0, 3) };
            g.Children.Add(T(key, 12, false, Muted));
            var v = T(value, 12); Grid.SetColumn(v, 1); g.Children.Add(v);
            if (chip != null) { var c = Chip(chip, kind); Grid.SetColumn(c, 2); g.Children.Add(c); }
            return g;
        }
        Control Section(string title, string chip, string kind, params Control[] rows)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            head.Children.Add(T(title, 14, true)); var c = Chip(chip, kind); Grid.SetColumn(c, 1); head.Children.Add(c);
            var stack = new StackPanel { Spacing = 4 }; stack.Children.Add(head);
            foreach (var r in rows) stack.Children.Add(r);
            return Card(stack, 14, 14);
        }
        var summary = Card(new StackPanel { Spacing = 6, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { T("P2P 友好度", 14, true), Chip("一般", "warn") } },
            T("IPv6 直连可用；IPv4 入站不通，且存在多个公网出口。与同样有 IPv6 的设备可直连，其余情况会走中继，延迟略高。", 12, false, Muted),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Pill("重新检测", true), T("上次检测 18:30 · 用时 7.4 秒", 11, false, Muted) } },
        } }, 16, 14);
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        list.Children.Add(summary);
        list.Children.Add(Section("本机网络", "正常", "ok", Row("活动网卡", "以太网 2 · 1 Gbps  ·  无线网络 (已断开)"), Row("IPv4", "192.168.2.184（私网）"), Row("IPv6", "240e:3b1:d082:c080::1044（全局）", "可用", "ok"), Row("DNS", "202.96.128.86、114.114.114.114")));
        list.Children.Add(Section("NAT 与映射（STUN）", "注意", "warn", Row("NAT 类型", "端点无关映射（锥形）· 过滤：地址受限", "利于 P2P", "ok"), Row("映射随目的地变化", "否"), Row("回环（Hairpin）", "不支持", "注意", "warn"), Row("UDP 出站", "正常")));
        list.Children.Add(Section("出口地址", "异常", "bad", Row("检测 8 次", "113.102.233.233 × 4   ·   116.18.118.3 × 4", "2 个出口", "bad"), Row("说明", "同一设备的连接在两个公网地址间轮换（多线或运营商地址池），IPv4 入站不会稳定。")));
        list.Children.Add(Section("端口映射（UPnP / NAT-PMP / PCP）", "注意", "warn", Row("网关", "192.168.2.1 · UPnP 可用"), Row("映射测试", "已建立并清除 TCP 18443", "成功", "ok"), Row("外网地址", "113.102.233.233（公网）"), Row("入站可达（服务器回连）", "超时：映射存在，但运营商未放行入站", "不可达", "bad")));
        list.Children.Add(Section("ICE 候选（等价 WebRTC 收集）", "正常", "ok", Row("host", "2 个"), Row("srflx（公网映射）", "IPv4 ×1 · IPv6 ×1"), Row("relay（中继）", "可用 · NJ 回退 5 ms · SY0812A 候选 57 ms")));
        list.Children.Add(Section("可信服务器与中继", "正常", "ok", Row("可信服务器", "94 ms · HTTPS 正常"), Row("M 目录 / QAD", "8080 / 7842 可达"), Row("建议", "优先使用 IPv6；如需 IPv4 直连，请联系运营商开放入站或使用公网 IP。")));
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        return Shell(scroller, true, "网络诊断", "网络诊断");
    }

    // ---- states ----------------------------------------------------------------------------------------------------
    static Control FirstTime()
    {
        var s = new StackPanel { Spacing = 14 };
        s.Children.Add(T("首次连接", 17, true, Ink, TextAlignment.Center));
        s.Children.Add(Identity("SY0812A", "K4DD0B", "在线", "online"));
        s.Children.Add(Hint("这台设备还没有授权。请向对方索取首次密码。"));
        s.Children.Add(PasswordBox("", false));
        var back = new Button { Content = "← 更换设备", HorizontalAlignment = HorizontalAlignment.Center }; back.Classes.Add("quiet"); s.Children.Add(back);
        return Centered(Card(s, 24, 20));
    }

    static Control Fingerprint()
    {
        var s = new StackPanel { Spacing = 14 };
        s.Children.Add(T("核对对方身份", 17, true));
        s.Children.Add(T("请通过电话或当面，与对方 ArdUi 邀请页显示的 EndpointId 逐字核对。核对一致后才会发送密码。", 12, false, Muted));
        s.Children.Add(new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), Background = Look.Ghost,
            Child = new TextBlock { Text = "5889f4c2e3c89af503a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1", FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap } });
        s.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { Pill("取消"), Pill("一致，继续", true) } });
        return Centered(Card(s, 24, 20));
    }

    static Control Progress()
    {
        Control Step(string icon, string text, bool done, bool active)
        {
            var mark = done ? (Control)Look.Icon(Check, 14, Look.OnlineDeep, 2.2)
                : new Ellipse { Width = 9, Height = 9, Fill = active ? Look.Accent : Brushes.Transparent, Stroke = active ? Look.Accent : Look.Idle, StrokeThickness = 1.5 };
            var host = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), Children = { mark } };
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { host, T(text, 13, active, active ? Ink : done ? Muted : Look.Idle) } };
        }
        var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Look.Ghost, Child = new Border { Width = 190, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = Look.Accent } };
        var s = new StackPanel { Spacing = 14 };
        s.Children.Add(T("正在连接", 17, true, Ink, TextAlignment.Center));
        s.Children.Add(Identity("SY0812A", "K4DD0B", "", "busy"));
        s.Children.Add(bar);
        s.Children.Add(new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children =
            { Step("", "查询设备", true, false), Step("", "核对身份指纹", true, false), Step("", "等待对方批准…", false, true), Step("", "建立加密连接", false, false) } });
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Center }; cancel.Classes.Add("quiet"); s.Children.Add(cancel);
        return Centered(Card(s, 24, 20));
    }

    static Control NoPermission()
    {
        var s = new StackPanel { Spacing = 14 };
        s.Children.Add(T("没有权限", 17, true, Danger, TextAlignment.Center));
        s.Children.Add(Identity("SY0812A", "K4DD0B", "在线", "online"));
        s.Children.Add(Hint("密码不正确，或对方未批准本次连接。请重新输入首次密码。"));
        s.Children.Add(PasswordBox("48", true));
        var back = new Button { Content = "← 更换设备", HorizontalAlignment = HorizontalAlignment.Center }; back.Classes.Add("quiet"); s.Children.Add(back);
        return Centered(Card(s, 24, 20));
    }

    static Control NetworkFailure()
    {
        var s = new StackPanel { Spacing = 14 };
        s.Children.Add(T("无法连接", 17, true, Ink, TextAlignment.Center));
        s.Children.Add(Identity("SY0812A", "K4DD0B", "", "off"));
        s.Children.Add(Hint("网络不可用，或可信服务器暂时无法访问。这不是权限问题，请检查网络后重试。"));
        s.Children.Add(new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(10), Background = Look.Ghost, Child = T("详情：请求可信服务器超时（20 秒）", 11, false, Muted) });
        s.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { Pill("返回"), Pill("重试", true) } });
        return Centered(Card(s, 24, 20));
    }

    static Control Connected()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Dot("connected") } });
        var names = new StackPanel { Children = { T("SY0812A", 20, true), T("K4DD0B · P2P 直连 / IPv6 · RTT 6.2 ms · 吞吐 ≥166.5 Mbps", 12, false, Muted) } };
        Grid.SetColumn(names, 1); head.Children.Add(names);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var desktop = Pill("桌面", true); desktop.MinHeight = 38; actions.Children.Add(desktop);
        foreach (var t in new[] { "FRD", "文件" }) { var b = Pill(t); b.MinHeight = 38; actions.Children.Add(b); }
        var drop = Pill("断开"); drop.MinHeight = 38; drop.Margin = new Thickness(16, 0, 0, 0); actions.Children.Add(drop);
        var session = Card(new StackPanel { Spacing = 18, Children = { head, actions } }, 22, 20);
        var another = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        another.Children.Add(T("连接另一台设备", 12, false, Muted, TextAlignment.Center)); another.Children.Add(Input("", true, false, true));
        var stack = new StackPanel { Width = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { session, another } };
        return Shell(stack);
    }

    static Control SettingsDevices()
    {
        Control Tab(string text, bool active) { var b = new Button { Content = text, Padding = new Thickness(14, 6) }; b.Classes.Add(active ? "primary" : "quiet"); return b; }
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Tab("被控与授权", false), Tab("设备管理", true), Tab("网络诊断", false), Tab("关于", false) } };
        Control Row(string kind, string title, string quiet, string status)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 40 };
            g.Children.Add(new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Dot(kind) } });
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T(title, 13, true), T(quiet, 12, false, Muted) } };
            names.Children.Add(line); names.Children.Add(T(status, 11, false, Muted)); Grid.SetColumn(names, 1); g.Children.Add(names);
            var more = Quiet("M5,12 L5.01,12 M12,12 L12.01,12 M19,12 L19.01,12", 16, 32); Grid.SetColumn(more, 2); g.Children.Add(more);
            return Card(g, 10, 12);
        }
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        list.Children.Add(T("全部设备 · 6", 12, true, Muted));
        list.Children.Add(Row("online", "SY0812A", "K4DD0B", "在线 · 最近连接 3 小时前"));
        list.Children.Add(Row("connected", "办公电脑", "DESKTOP-OFFICE  C8M3P6", "已连接 · P2P 直连"));
        list.Children.Add(Row("online", "展厅内1", "EXHIBIT-01  3MKXWS", "分组 展厅 · 在线 · 最近连接 2 天前"));
        list.Children.Add(Row("off", "家里 NAS", "HOME-NAS  T7Q2LM", "离线 · 最近连接 5 天前"));
        list.Children.Add(Row("off", "展厅内2", "EXHIBIT-02  12F0JT", "分组 展厅 · 离线 · 从未连接"));
        var page = new StackPanel { Children = { tabs, list } };
        return Shell(page, true);
    }
}
