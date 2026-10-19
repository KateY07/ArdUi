namespace ArdUi;

// The home page (enter a device ID), the settings pages, and the connect flow overlays.
sealed partial class MainWindow
{
    // ---- fields --------------------------------------------------------------------------------------------------------
    readonly Border fieldBorder = new() { CornerRadius = new CornerRadius(22), Padding = new Thickness(10, 7, 8, 7), BorderThickness = new Thickness(1.2), Background = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)) };
    readonly TextBlock homeError = new() { FontSize = 12, Foreground = Brush.Parse("#B3432B"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly StackPanel recentsRow = new() { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
    readonly StackPanel recentsBlock = new() { Spacing = 10, IsVisible = false };
    readonly StackPanel sessions = new() { Spacing = 6, IsVisible = false };
    readonly Dictionary<string, DeviceDisplay> homeViews = new();
    readonly Border flowOverlay = new() { Background = Brush.Parse("#8C1F1E1D"), IsVisible = false };
    readonly Logo titleLogo = new(24);
    readonly Ellipse netDot = new() { Width = 9, Height = 9, Fill = Look.Idle, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock netText = new() { Text = "正在启动…", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly Border homePage = new(), settingsPage = new();
    readonly ContentControl settingsBody = new();
    Avalonia.Controls.Primitives.Popup? historyPopup;
    string recentSignature = "", sessionSignature = "";
    bool settingsOpen;

    // ---- home ----------------------------------------------------------------------------------------------------------
    Control BuildHome()
    {
        remote.Watermark = "ABC123"; remote.MaxLength = 6; remote.Width = double.NaN;
        remote.FontFamily = new FontFamily("Consolas"); remote.FontSize = 28; remote.FontWeight = FontWeight.SemiBold; remote.LetterSpacing = 8;
        remote.Background = Brushes.Transparent; remote.BorderThickness = new Thickness(0); remote.HorizontalContentAlignment = HorizontalAlignment.Center;
        remote.VerticalContentAlignment = VerticalAlignment.Center; remote.MinHeight = 46; remote.Padding = new Thickness(0);
        remote.Resources["TextControlBackgroundFocused"] = Brushes.Transparent; remote.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent;
        remote.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent; remote.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
        remote.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            var text = remote.Text ?? "";
            var clean = new string(text.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray()).ToUpperInvariant();
            if (clean != text) { remote.Text = clean; remote.CaretIndex = clean.Length; }
            if (homeError.IsVisible) homeError.IsVisible = false;
        };
        remote.GotFocus += (_, _) => fieldBorder.BorderBrush = Look.Accent;
        remote.LostFocus += (_, _) => fieldBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        remote.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = Guard(() => StartConnect(remote.Text ?? "")); } };

        var history = Chrome.IconButton(Chrome.ChevronPath, 14, 32); ToolTip.SetTip(history, "最近连接过的设备");
        var go = new Button { Content = Look.Icon(Chrome.ArrowPath, 20, Brushes.White, 2.2), Width = 46, Height = 46, MinHeight = 46, Padding = new Thickness(0), CornerRadius = new CornerRadius(23) };
        go.Classes.Add("primary"); ToolTip.SetTip(go, "连接");
        go.Click += async (_, _) => await Guard(() => StartConnect(remote.Text ?? ""));
        historyPopup = new Avalonia.Controls.Primitives.Popup { PlacementTarget = fieldBorder, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true, Width = 380 };
        history.Click += (_, _) => { if (historyPopup.IsOpen) historyPopup.IsOpen = false; else { BuildHistory(); historyPopup.IsOpen = true; } };
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        field.Children.Add(new Border { Width = 4 });
        Grid.SetColumn(remote, 1); field.Children.Add(remote);
        Grid.SetColumn(history, 2); field.Children.Add(history);
        go.Margin = new Thickness(4, 0, 0, 0); Grid.SetColumn(go, 3); field.Children.Add(go);
        fieldBorder.Child = new Grid { Children = { field, historyPopup } };

        var card = new Border
        {
            CornerRadius = new CornerRadius(30), Padding = new Thickness(30, 26), Width = 500, HorizontalAlignment = HorizontalAlignment.Center,
            Background = Chrome.GlassFill, BorderBrush = Chrome.GlassEdge, BorderThickness = new Thickness(1.2), BoxShadow = BoxShadows.Parse("0 20 50 0 #26301E14, 0 2 6 0 #14000000"),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = "连接到设备", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center },
                    fieldBorder, homeError,
                    recentsBlock,
                }
            }
        };
        recentsBlock.Children.Add(new Border { Height = 1, Background = Chrome.Hairline });
        recentsBlock.Children.Add(new TextBlock { Text = "最近", FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        recentsBlock.Children.Add(recentsRow);

        status.HorizontalAlignment = HorizontalAlignment.Center; status.TextAlignment = TextAlignment.Center; status.MaxWidth = 460;
        var sessionsHeading = new TextBlock { Text = "已连接", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(4, 6, 0, 0) };
        sessions.Children.Add(sessionsHeading);
        var scroll = new ScrollViewer
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Spacing = 12, Width = 500, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 10), Children = { card, status, sessions } },
        };

        // the machine's own role: a quiet glass strip at the bottom, away from the window controls
        var share = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Look.Icon(Look.SharePath, 15, Look.Accent), new TextBlock { Text = "邀请", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Accent, VerticalAlignment = VerticalAlignment.Center } } }, Padding = new Thickness(8, 4) };
        share.Classes.Add("quiet"); ToolTip.SetTip(share, "邀请设备：显示连接密码和设备 ID");
        share.Click += (_, _) => { try { OpenInvite(); } catch (Exception ex) { Say(ex.Message); } };
        identityCard.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Chrome.Divider(18), share } };
        allow.OnContent = allow.OffContent = "允许被控";
        var strip = Chrome.Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(14, 2), Children = { new TextBlock { Text = "本机", FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }, allow, identityCard } });
        strip.HorizontalAlignment = HorizontalAlignment.Center; strip.Margin = new Thickness(0, 4, 0, 16); Grid.SetRow(strip, 1);
        homePage.Child = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { scroll, strip } };
        return homePage;
    }

    void BuildHistory()
    {
        if (engine == null || historyPopup == null) return;
        Peer[] peers; lock (engine.State.Peers) peers = engine.State.Peers.OrderByDescending(p => p.LastConnectedAt).Take(8).ToArray();
        var list = new StackPanel { Spacing = 0 };
        foreach (var peer in peers)
        {
            var row = new Button { Content = PeerLabel(peer), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 8), Foreground = Ink };
            row.Classes.Add("quiet");
            var code = peer.Code;
            row.Click += (_, _) => { historyPopup.IsOpen = false; remote.Text = code; remote.Focus(); remote.CaretIndex = 6; };
            list.Children.Add(row);
        }
        if (peers.Length == 0) list.Children.Add(new TextBlock { Text = "还没有连接过的设备。", Foreground = Muted, Margin = new Thickness(14, 10) });
        historyPopup.Child = new Border { Child = list, Background = Chrome.GlassStrong, BorderBrush = Chrome.GlassEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(6), Margin = new Thickness(0, 6, 0, 0), BoxShadow = BoxShadows.Parse("0 14 36 0 #33301E14") };
    }

    // The most recently used devices as round avatars; a green ring means the device is online now.
    void FillRecents(IReadOnlyList<(Peer Peer, bool? Online)> items)
    {
        recentsRow.Children.Clear();
        foreach (var (peer, online) in items)
        {
            var label = PeerLabel(peer).Split(" · ")[0];
            var ring = online == true ? Look.OnlineDeep : online == false ? Look.Line : Look.Idle;
            var avatar = new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(23), BorderBrush = ring, BorderThickness = new Thickness(online == true ? 2 : 1.2),
                Background = new SolidColorBrush(Color.FromArgb(online == false ? (byte)90 : (byte)170, 255, 255, 255)),
                Child = new TextBlock { Text = label.Length == 0 ? "?" : label[..1].ToUpperInvariant(), FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = online == false ? Muted : Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var item = new StackPanel { Spacing = 4, Width = 74, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent, Children = { avatar, new TextBlock { Text = label, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 74 } } };
            ToolTip.SetTip(item, PeerLabel(peer) + (online == false ? "\n对方当前不在线" : "\n点击连接"));
            var code = peer.Code;
            item.PointerPressed += async (_, e) => { e.Handled = true; await Guard(() => StartConnect(code)); };
            recentsRow.Children.Add(item);
        }
        recentsBlock.IsVisible = items.Count != 0;
    }

    // ---- connect flow ------------------------------------------------------------------------------------------------
    CancellationTokenSource? flowCancel;
    string flowText = "";

    void ShowFlow(Control content)
    {
        var card = Card(content); card.Padding = new Thickness(26); card.Width = 400; card.CornerRadius = new CornerRadius(26);
        card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
        flowOverlay.Child = card; flowOverlay.IsVisible = true;
    }
    void HideFlow() { flowOverlay.IsVisible = false; flowOverlay.Child = null; }

    TextBlock flowStage = new();
    void ShowProgress(string code)
    {
        // a fresh text block each time: the previous card may still hold the old one
        flowStage = new TextBlock { Text = ConnectFlow.Text(ConnectStage.Lookup), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, HorizontalAlignment = HorizontalAlignment.Center };
        var cancel = Button("取消", () => { flowCancel?.Cancel(); return Task.CompletedTask; }); cancel.HorizontalAlignment = HorizontalAlignment.Center; cancel.Width = 96;
        ShowFlow(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new Logo(64) { Animate = true, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "连接 " + code, FontSize = 13, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Consolas"), LetterSpacing = 2 },
                flowStage, cancel,
            }
        });
    }

    Task<string?> AskFirstPassword(string code, string? problem)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var box = new TextBox { MaxLength = 6, Watermark = "6 位连接密码", FontFamily = new FontFamily("Consolas"), FontSize = 24, LetterSpacing = 6, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 50 };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            var clean = new string((box.Text ?? "").Where(char.IsDigit).ToArray());
            if (clean != box.Text) box.Text = clean;
        };
        var ok = new Button { Content = "连接", Width = 110 }; ok.Classes.Add("primary");
        var no = new Button { Content = "取消", Width = 90 };
        void Submit() { if ((box.Text ?? "").Length == 6) result.TrySetResult(box.Text); }
        ok.Click += (_, _) => Submit(); no.Click += (_, _) => result.TrySetResult(null);
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Submit(); } };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = problem == null ? "首次连接 " + code : "没有权限", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = problem == null ? Ink : Brush.Parse("#B3432B") });
        body.Children.Add(new TextBlock { Text = problem ?? "请输入对方提供的 6 位连接密码。通过后，以后连接这台设备不再需要密码。", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, ok } });
        ShowFlow(body);
        box.Focus();
        return result.Task;
    }

    Task<bool> ShowFailure(ConnectProblem problem)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new Button { Content = "关闭", Width = 90 }; close.Click += (_, _) => result.TrySetResult(false);
        var retry = new Button { Content = "重试", Width = 90 }; retry.Classes.Add("primary"); retry.Click += (_, _) => result.TrySetResult(true);
        var warn = problem.Kind == ConnectFailure.SecurityWarning;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } };
        if (!warn && problem.Kind != ConnectFailure.Yourself && problem.Kind != ConnectFailure.NotFound) buttons.Children.Add(retry);
        ShowFlow(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = problem.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = warn ? Brush.Parse("#B3432B") : Ink },
                new TextBlock { Text = problem.Hint, Foreground = Muted, TextWrapping = TextWrapping.Wrap },
                buttons,
            }
        });
        return result.Task;
    }

    // Enter a device code: ask for the first-time password when the device is new, show progress, and explain failures.
    async Task StartConnect(string raw)
    {
        var code = ConnectFlow.NormaliseCode(raw);
        if (code == null) { homeError.Text = "请输入完整的 6 位设备 ID。"; homeError.IsVisible = true; remote.Focus(); return; }
        if (directory == null || engine == null) throw new InvalidOperationException("设备尚未就绪。");
        bool known; lock (engine.State.Peers) known = engine.State.Peers.Any(p => p.Code == code);
        string? password = null;
        if (!known)
        {
            password = await AskFirstPassword(code, null);
            if (password == null) { HideFlow(); return; }
        }
        void OnStage(string machine, ConnectStage stage) { if (machine == code) Dispatcher.UIThread.Post(() => flowStage.Text = ConnectFlow.Text(stage)); }
        directory.Stage += OnStage;
        try
        {
            while (true)
            {
                flowCancel = new CancellationTokenSource(); ShowProgress(code);
                try
                {
                    await directory.Connect(code, password ?? "", flowCancel.Token, enrolling: password != null);
                    HideFlow(); remote.Text = ""; Say("已连接 " + code + "。"); return;
                }
                catch (Exception ex)
                {
                    if (flowCancel.IsCancellationRequested) { HideFlow(); Say("已取消。"); return; }
                    var problem = ConnectFlow.Classify(ex);
                    engine.Events.Add("ERROR", "连接", $"{code} 失败（{problem.Kind}）：{ex.Message}");
                    if (problem.AskPassword)
                    {
                        password = await AskFirstPassword(code, problem.Hint);
                        if (password == null) { HideFlow(); return; }
                        continue;
                    }
                    if (!await ShowFailure(problem)) { HideFlow(); return; }
                }
            }
        }
        finally { directory.Stage -= OnStage; flowCancel = null; }
    }

    // ---- settings ---------------------------------------------------------------------------------------------------
    readonly TextBlock aboutEndpoint = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Ink };

    Control BuildSettings()
    {
        var back = Chrome.IconButton(Chrome.BackPath, 16, 36); ToolTip.SetTip(back, "返回"); back.Click += (_, _) => ShowSettings(false);
        Control Section(string title, params Control[] children)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(4, 8, 0, 0) });
            foreach (var child in children) panel.Children.Add(child);
            return panel;
        }
        var devicesTab = new StackPanel { Spacing = 8, Children = { undoBar, devices } };
        var accessTab = new StackPanel { Spacing = 8, Children = { incomingArea, Section("可访问本机的设备", Card(controllers)) } };
        var logButton = Button("打开日志", OpenLogFile); var exportButton = Button("导出诊断包", () => { var path = Diagnostics.Export(Program.DataRoot, engine); Say("诊断包已导出：" + path); return Task.CompletedTask; });
        var copyEndpoint = Button("复制 EndpointId", async () => { await Clipboard!.SetTextAsync(engine?.Id ?? ""); Say("已复制本机 EndpointId。"); });
        foreach (var b in new[] { logButton, exportButton, copyEndpoint }) { b.HorizontalAlignment = HorizontalAlignment.Left; b.MinWidth = 140; }
        var aboutTab = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Section("ArdUi " + Program.Version, Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "本机 EndpointId（对方首次连接时用来核对身份）", Foreground = Muted, FontSize = 12 }, aboutEndpoint, copyEndpoint } })),
                Section("日志", Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "日志默认记录详细信息，保存在本机，并用默认文本编辑器打开。", Foreground = Muted, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { logButton, exportButton } } } })),
            }
        };
        var startup = new CheckBox { Content = "随 Windows 启动（在托盘中静默运行）", IsChecked = Autostart.Enabled };
        startup.IsCheckedChanged += (_, _) =>
        {
            try { Autostart.Set(startup.IsChecked == true); Say(startup.IsChecked == true ? "已设置随系统启动。" : "已取消随系统启动。"); }
            catch (Exception ex) { Say(ex.Message); }
        };
        aboutTab.Children.Insert(0, Section("启动", Card(startup)));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pages = new (string Name, Control Body)[] { ("设备管理", devicesTab), ("被控与授权", accessTab), ("日志与关于", aboutTab) };
        var tabButtons = new List<Button>();
        void Select(int index)
        {
            settingsBody.Content = pages[index].Body;
            for (var i = 0; i < tabButtons.Count; i++) { if (i == index) tabButtons[i].Classes.Add("primary"); else tabButtons[i].Classes.Remove("primary"); }
        }
        for (var i = 0; i < pages.Length; i++)
        {
            var index = i; var tab = new Button { Content = pages[i].Name, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 5) };
            tab.Click += (_, _) => Select(index); tabButtons.Add(tab); tabs.Children.Add(tab);
        }
        selectSettingsTab = Select;
        Select(0);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { back, new TextBlock { Text = "设置", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(22, 0, 16, 14) };
        layout.Children.Add(head);
        Grid.SetRow(tabs, 1); tabs.Margin = new Thickness(0, 10, 0, 8); layout.Children.Add(tabs);
        var scroll = new ScrollViewer { Content = settingsBody }; Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        settingsPage.Child = layout; settingsPage.IsVisible = false;
        return settingsPage;
    }
    Action<int>? selectSettingsTab;

    void ShowSettings(bool show)
    {
        settingsOpen = show; settingsPage.IsVisible = show; homePage.IsVisible = !show;
        if (show) aboutEndpoint.Text = engine?.Id ?? aboutEndpoint.Text;
    }
}
