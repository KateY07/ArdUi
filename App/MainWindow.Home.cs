namespace ArdUi;

// The home page (enter a device ID), the settings pages, and the connect flow overlays.
sealed partial class MainWindow
{
    // ---- fields --------------------------------------------------------------------------------------------------------
    readonly Border fieldBorder = new() { CornerRadius = new CornerRadius(22), Padding = new Thickness(10, 7, 8, 7), BorderThickness = new Thickness(1.2), Background = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)) };
    readonly TextBlock homeError = new() { FontSize = 12, Foreground = Brush.Parse("#B3432B"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly StackPanel recentsRow = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
    readonly StackPanel recentsBlock = new() { Spacing = 8, IsVisible = false };
    readonly StackPanel sessions = new() { Spacing = 6, IsVisible = false };
    readonly Dictionary<string, DeviceDisplay> homeViews = new();
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
        Avalonia.Input.InputMethod.SetIsInputMethodEnabled(remote, false);   // a device ID is A-Z and 0-9 only: no IME, no composition
        remote.FontFamily = new FontFamily("Consolas"); remote.FontSize = 22; remote.FontWeight = FontWeight.SemiBold; remote.LetterSpacing = 5;
        remote.Background = Brushes.Transparent; remote.BorderThickness = new Thickness(0); remote.HorizontalContentAlignment = HorizontalAlignment.Center;
        remote.VerticalContentAlignment = VerticalAlignment.Center; remote.MinHeight = 36; remote.Padding = new Thickness(0);
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

        var history = Chrome.IconButton(Chrome.ChevronPath, 14, 32); history.VerticalAlignment = VerticalAlignment.Center; ToolTip.SetTip(history, "最近连接过的设备");
        // The arrow turns over while the list is open (pointing up = collapse).
        var chevron = Look.Icon(Chrome.ChevronPath, 14);
        chevron.RenderTransformOrigin = RelativePoint.Center; chevron.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("rotate(0deg)"); chevron.Transitions = Look.Move(220);
        history.Content = chevron;
        var arrow = Look.Icon(Chrome.ArrowPath, 17, Brushes.White, 2.2);
        arrow.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,0px)"); arrow.Transitions = Look.Move(160);
        var go = new Button { Content = arrow, Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18) };
        go.Classes.Add("primary"); ToolTip.SetTip(go, "连接");
        go.Click += async (_, _) => await Guard(() => StartConnect(remote.Text ?? ""));
        // The arrow leans forward when the pointer is over the button.
        go.PointerEntered += (_, _) => arrow.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(3px,0px)");
        go.PointerExited += (_, _) => arrow.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,0px)");
        historyPopup = new Avalonia.Controls.Primitives.Popup { PlacementTarget = fieldBorder, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true, Width = 380 };
        // Pressing the arrow while the list is open first dismisses the list (light dismiss); that same click must not open it again.
        var closedAt = -100000L;
        historyPopup.Closed += (_, _) =>
        {
            closedAt = Environment.TickCount64;
            chevron.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("rotate(0deg)");
        };
        historyPopup.Opened += (_, _) =>
        {
            chevron.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("rotate(180deg)");
            if (historyPopup.Child is { } list) _ = RiseIn(list);
        };
        history.Click += (_, _) =>
        {
            if (Environment.TickCount64 - closedAt < 250) return;
            if (historyPopup.IsOpen) historyPopup.IsOpen = false; else { BuildHistory(); historyPopup.IsOpen = true; }
        };
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        field.Children.Add(new Border { Width = 72 });   // as wide as the buttons on the right, so the code sits in the middle of the card
        Grid.SetColumn(remote, 1); field.Children.Add(remote);
        Grid.SetColumn(history, 2); field.Children.Add(history);
        var ring = new Ellipse { Width = 36, Height = 36, Stroke = Look.Accent, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = RelativePoint.Center };
        SetupGo(go, arrow, ring);
        var goHost = new Grid { Margin = new Thickness(4, 0, 0, 0), Children = { ring, go } };
        Grid.SetColumn(goHost, 3); field.Children.Add(goHost);
        fieldBorder.Child = new Grid { Children = { field, progressLine, historyPopup } };

        var card = new Border
        {
            CornerRadius = new CornerRadius(24), Padding = new Thickness(18, 16), MaxWidth = 430, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Chrome.GlassFill, BorderBrush = Chrome.GlassEdge, BorderThickness = new Thickness(1.2), BoxShadow = BoxShadows.Parse("0 20 50 0 #26301E14, 0 2 6 0 #14000000"),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    (homeTitle = new TextBlock { Text = "连接到设备", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center }),
                    fieldBorder, hintText, homeError,
                    recentsBlock,
                }
            }
        };
        recentsBlock.Children.Add(new Border { Height = 1, Background = Chrome.Hairline });
        recentsBlock.Children.Add(new TextBlock { Text = "最近", FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
        recentsBlock.Children.Add(recentsRow);

        status.HorizontalAlignment = HorizontalAlignment.Center; status.TextAlignment = TextAlignment.Center; status.MaxWidth = 460;
        var sessionsHeading = new TextBlock { Text = "已连接", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(4, 6, 0, 0) };
        hostPanel.Children.Add(new TextBlock { Text = "正在访问本机", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(4, 6, 0, 0) }); hostPanel.Children.Add(hostList);
        sessions.Children.Add(sessionsHeading); sessions.Children.Add(pendingPanel); sessions.Children.Add(livePanel);
        // The connect card sits exactly in the middle of the page (equal space above and below) and never moves;
        // the status line and the connected devices live in the space below it and scroll there when there are many.
        card.Margin = new Thickness(14, 0); Grid.SetRow(card, 1);
        var lower = new ScrollViewer
        {
            VerticalAlignment = VerticalAlignment.Top,
            Content = new StackPanel { Spacing = 8, MaxWidth = 430, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(14, 8, 14, 8), Children = { status, auditWarning, hostPanel, sessions } },
        };
        Grid.SetRow(lower, 2);
        var scroll = new Grid { RowDefinitions = new RowDefinitions("*,Auto,*"), Children = { card, lower } };
        homeCard = card; homeGrid = scroll; homeLower = lower;

        // the machine's own role: a quiet glass strip at the bottom, away from the window controls
        var share = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Look.Icon(Look.SharePath, 15, Look.Accent), new TextBlock { Text = "邀请", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Accent, VerticalAlignment = VerticalAlignment.Center } } }, Padding = new Thickness(8, 4) };
        share.Classes.Add("quiet"); ToolTip.SetTip(share, "邀请设备：显示连接密码和设备 ID");
        share.Click += (_, _) => { if (!Person("邀请设备")) return; try { OpenInvite(); } catch (Exception ex) { Say(ex.Message); } };
        identityCard.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Chrome.Divider(18), share } };
        allow.OnContent = allow.OffContent = "允许被控";
        var strip = Chrome.Capsule(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(14, 2), Children = { new TextBlock { Text = "本机", FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }, allow, identityCard } });
        strip.HorizontalAlignment = HorizontalAlignment.Center; strip.Margin = new Thickness(0, 4, 0, 16); Grid.SetRow(strip, 1); stripHost = strip;
        SetupTyping();
        homePage.Child = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { scroll, strip } };
        return homePage;
    }

    // The list drops in: fades in while sliding down a few pixels.
    static async Task RiseIn(Control control)
    {
        if (!Motion.Enabled) return;
        var shift = control.RenderTransform as TranslateTransform ?? new TranslateTransform();
        control.RenderTransform = shift; control.Opacity = 0; shift.Y = -8;
        var rise = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(180), Easing = new Avalonia.Animation.Easings.CubicEaseOut(), FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0.0), new Setter(TranslateTransform.YProperty, -8.0) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } },
            }
        };
        try { await rise.RunAsync(control); } catch (InvalidOperationException) { }
        control.Opacity = 1; shift.Y = 0;
    }

    void BuildHistory()
    {
        if (engine == null || historyPopup == null) return;
        Peer[] peers; lock (engine.State.Peers) peers = engine.State.Peers.OrderByDescending(p => p.LastConnectedAt).Take(12).ToArray();
        var list = new StackPanel { Spacing = 0 };
        foreach (var peer in peers)
        {
            var online = directory?.IsOnline(peer.Id);
            var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            if (online == true) dot.Fill = Look.Online; else { dot.Fill = Brushes.Transparent; dot.Stroke = Look.Idle; dot.StrokeThickness = 1.5; }
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            FillTitle(name, peer);
            var state = new TextBlock { Text = online == true ? "在线" : online == false ? "离线" : "", FontSize = 12, Foreground = online == true ? Look.OnlineDeep : Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var row = new Button { Padding = new Thickness(12, 9), Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Opacity = online == false ? .7 : 1 };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(dot); Grid.SetColumn(name, 1); grid.Children.Add(name); Grid.SetColumn(state, 2); grid.Children.Add(state);
            row.Content = grid; row.Classes.Add("quiet");
            var code = peer.Code;
            row.Click += (_, _) => { historyPopup.IsOpen = false; remote.Text = code; remote.Focus(); remote.CaretIndex = 6; };
            list.Children.Add(row);
        }
        if (peers.Length == 0) list.Children.Add(new TextBlock { Text = "还没有连接过的设备。", Foreground = Muted, Margin = new Thickness(14, 10) });
        historyPopup.Width = Math.Max(240, fieldBorder.Bounds.Width);
        historyPopup.Child = new Border
        {
            Child = new ScrollViewer { Content = list, MaxHeight = 280 }, Background = Look.CardSolid, BorderBrush = Chrome.GlassEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(6), Margin = new Thickness(0, 6, 0, 0), BoxShadow = BoxShadows.Parse("0 14 36 0 #33301E14"),
        };
    }

    // The most recently used devices as round avatars; a green ring means the device is online now.
    void FillRecents(IReadOnlyList<(Peer Peer, bool? Online)> items)
    {
        recentsRow.Children.Clear(); recentItems.Clear();
        foreach (var (peer, online) in items)
        {
            var label = PeerLabel(peer).Split(" · ")[0];
            var ring = online == true ? Look.OnlineDeep : online == false ? Look.Line : Look.Idle;
            var avatar = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderBrush = ring, BorderThickness = new Thickness(online == true ? 2 : 1.2),
                Background = new SolidColorBrush(Color.FromArgb(online == false ? (byte)90 : (byte)170, 255, 255, 255)),
                Child = new TextBlock { Text = label.Length == 0 ? "?" : label[..1].ToUpperInvariant(), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = online == false ? Muted : Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var item = new StackPanel { Spacing = 3, Width = 56, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent, Children = { avatar, new TextBlock { Text = label, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 56 } } };
            ToolTip.SetTip(item, PeerLabel(peer) + (online == false ? "\n对方当前不在线" : "\n点击连接"));
            var code = peer.Code;
            // hover lifts the avatar, pressing sinks it
            var rest = BoxShadows.Parse("0 0 0 0 #00000000"); var lifted = BoxShadows.Parse("0 8 16 0 #2E301E14");
            avatar.BoxShadow = rest; avatar.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = Motion.Ms(160) } };
            item.RenderTransformOrigin = RelativePoint.Center; item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,0px) scale(1)"); item.Transitions = Look.Move(150);
            item.Transitions.Add(new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = Motion.Ms(160) });
            recentItems.Add((peer.Code, item, avatar, ring));
            item.PointerEntered += (_, _) => { item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,-3px) scale(1)"); avatar.BoxShadow = lifted; };
            item.PointerExited += (_, _) => { item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,0px) scale(1)"); avatar.BoxShadow = rest; };
            item.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,0px) scale(0.92)");
                await Guard(() => StartConnect(code));
                item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(0px,0px) scale(1)");
            };
            recentsRow.Children.Add(item);
        }
        hasRecents = items.Count != 0; recentsBlock.IsVisible = hasRecents && !compactHome;
    }

    // ---- this machine is being used ---------------------------------------------------------------------------------
    // Devices that are connecting to, or using, this machine: a breathing logo while they connect, a green dot once they are in.
    readonly StackPanel hostPanel = new() { Spacing = 6, IsVisible = false }, hostList = new() { Spacing = 6 };
    string hostSignature = "";
    readonly TextBlock auditWarning = new() { Text = "本机没有可用的 H.264 编码器（Windows 媒体功能包）：有设备连接时会被断开。请安装“媒体功能包”。", FontSize = 12, Foreground = Brush.Parse("#B3432B"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    public event Action<string>? ActivityChanged;
    string lastActivity = "";

    Control HostCard(string code, string detail, bool connecting, bool once, string? disconnectId, bool recording = false, bool unread = false)
    {
        Control icon;
        if (connecting) icon = new Logo(28) { Animate = Motion.Enabled && !previewMode, VerticalAlignment = VerticalAlignment.Center };
        else
        {
            var halo = new Ellipse { Width = 26, Height = 26, Fill = Look.OnlineHalo };
            var dot = new Ellipse { Width = 12, Height = 12, Fill = Look.OnlineDeep, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon = new Grid { Width = 28, Height = 28, Children = { halo, dot } };
            Pulse(halo, true);   // a slow breath: someone is here
        }
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = code, FontSize = 14, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Consolas") } } };
        if (once) title.Children.Add(new Border { Padding = new Thickness(8, 1), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(255, 252, 236, 228)), BorderBrush = Look.Accent, BorderThickness = new Thickness(1), Child = new TextBlock { Text = "仅此一次", FontSize = 11, Foreground = Look.Accent, FontWeight = FontWeight.SemiBold } });
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title, DetailText(detail, connecting, recording) } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        icon.Margin = new Thickness(0, 0, 12, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon); Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (disconnectId != null)
        {
            var drop = new Button { Content = Look.Icon(Look.CrossPath, 10, Brushes.White, 2.4), Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
            drop.Classes.Add("circlex"); ToolTip.SetTip(drop, "断开对方");
            drop.Click += async (_, _) => await Guard(async () => await engine!.Disconnect(disconnectId));
            Grid.SetColumn(drop, 3); grid.Children.Add(drop);
            if (!connecting)
            {
                var chat = new Button { Content = ChatBadge(unread), Width = 36, Height = 30, MinHeight = 30, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                ToolTip.SetTip(chat, "对话：文字、图片、文件"); chat.Click += (_, _) => OpenChat(disconnectId, code);
                Grid.SetColumn(chat, 2); grid.Children.Add(chat);
            }
        }
        var card = Card(grid); card.Padding = new Thickness(14, 10);
        card.BorderBrush = connecting ? Look.Relay : Look.OnlineDeep;
        return card;
    }

    static TextBlock DetailText(string detail, bool connecting, bool recording)
    {
        var text = new TextBlock { FontSize = 12, Foreground = connecting ? Look.Relay : Look.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(detail));
        if (recording) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  ● 录屏中") { Foreground = Brush.Parse("#D64545"), FontWeight = FontWeight.SemiBold });
        return text;
    }

    // Rebuilt only when something shown changed.
    void RefreshHost()
    {
        if (engine == null || directory == null) return;
        var items = new List<(string Key, string Code, string Detail, bool Connecting, bool Once, string? Id, bool Recording, bool Unread)>();
        foreach (var session in engine.Incoming.Values)
        {
            var status = engine.Status.GetValueOrDefault(session.PeerId, "");
            var connecting = status.Contains("等待", StringComparison.Ordinal) || !session.Live && status.Length == 0;
            var detail = connecting ? "等待密码与身份确认…" : Permissions.Describe(session.Perms) + " · " + status.Replace("正在访问本机 · ", "").Replace("已连接 · ", "");
            var code = directory.CodeOf(session.PeerId); var recording = !connecting && engine.Audit.IsRecording(session.PeerId); var unread = engine.ChatFor(session.PeerId).Unread > 0;
            if (!connecting) WatchChat(session.PeerId, code);
            items.Add((session.PeerId + detail + recording + unread, code, detail, connecting, session.OnceGranted, session.PeerId, recording, unread));
        }
        foreach (var (endpoint, code) in directory.Arriving.Where(a => !engine.Incoming.ContainsKey(a.Endpoint)))
            items.Add(("arriving" + endpoint, code, "对方正在连接…", true, false, null, false, false));
        var signature = string.Join("|", items.Select(i => i.Key + i.Once));
        if (signature != hostSignature)
        {
            hostSignature = signature;
            hostList.Children.Clear();
            foreach (var i in items) hostList.Children.Add(HostCard(i.Code, i.Detail, i.Connecting, i.Once, i.Id, i.Recording, i.Unread));
            hostPanel.IsVisible = items.Count != 0;
            UpdateSessionsVisible();
        }
        // without the encoder nothing can be recorded, and sessions would be ended: say so while control is allowed
        auditWarning.IsVisible = directory.Enabled && !engine.Audit.EncoderAvailable;
        // the title bar and the tray say it too, so a hidden window is not silent
        var inUse = items.Count(i => !i.Connecting); var connectingCount = items.Count(i => i.Connecting);
        var activity = inUse != 0 ? $"ArdUi — {inUse} 台设备正在访问本机" : connectingCount != 0 ? "ArdUi — 有设备正在连接本机" : "ArdUi " + Program.Version;
        if (activity != lastActivity) { lastActivity = activity; ActivityChanged?.Invoke(activity); }
        if (directory.Online) { netText.Text = inUse != 0 ? $"被控中 · {inUse}" : connectingCount != 0 ? "有设备正在连接" : "在线"; netDot.Fill = inUse != 0 ? Look.Accent : connectingCount != 0 ? Look.Relay : Online; }
    }

    // ---- connect flow ------------------------------------------------------------------------------------------------
    // Connecting happens in the "connected" area: a placeholder card flies in, shows progress, asks for the password or
    // reports a failure in place, and on success draws a check and makes way for the real session card.
    readonly Dictionary<string, ConnectCard> pendingCards = new();
    readonly StackPanel pendingPanel = new() { Spacing = 6 }, livePanel = new() { Spacing = 6 };

    void UpdateSessionsVisible()
    {
        sessions.IsVisible = pendingPanel.Children.Count != 0 || livePanel.Children.Count != 0;
        SetCompact(sessions.IsVisible || hostList.Children.Count != 0);
        UpdateAttention();
    }

    // While devices are connected, the "connect to device" panel shrinks to one floating row at the top and the
    // sessions use the rest of the page; with nothing connected it is the big centred card.
    Border? homeCard; Grid? homeGrid; ScrollViewer? homeLower; TextBlock? homeTitle;
    bool compactHome, hasRecents;
    void SetCompact(bool compact)
    {
        compactHome = compact;
        if (homeCard == null || homeGrid == null || homeLower == null || homeTitle == null) return;
        homeTitle.IsVisible = !compact;
        recentsBlock.IsVisible = hasRecents && !compact;
        homeCard.Padding = compact ? new Thickness(10, 8) : new Thickness(18, 16);
        homeCard.CornerRadius = new CornerRadius(compact ? 22 : 24);
        homeCard.Margin = compact ? new Thickness(14, 4, 14, 0) : new Thickness(14, 0);
        ((StackPanel)homeCard.Child!).Spacing = compact ? 4 : 14;
        homeGrid.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*,Auto,*");
        Grid.SetRow(homeCard, compact ? 0 : 1); Grid.SetRow(homeLower, compact ? 1 : 2);
        homeCard.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;
    }

    // The real session card is held back while its placeholder is on screen, so there is only ever one row; it then fades in.
    void RevealSession(string code)
    {
        if (engine == null) return;
        string? id; lock (engine.State.Peers) id = engine.State.Peers.FirstOrDefault(p => p.Code == code)?.Id;
        sessionSignature = ""; Refresh();
        if (id == null || previewMode || !Motion.Enabled || !homeViews.TryGetValue(id, out var view) || view.Card.Parent == null) return;
        var card = view.Card; card.Opacity = 0;
        var fade = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(260), FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } },
            }
        };
        _ = fade.RunAsync(card).ContinueWith(_ => Dispatcher.UIThread.Post(() => card.Opacity = 1));
    }

    // Enter a device code: ask for the connection password when the device is new, show progress, and explain failures.
    async Task StartConnect(string raw)
    {
        var code = ConnectFlow.NormaliseCode(raw);
        if (code == null) { homeError.Text = "请输入完整的 6 位设备 ID。"; homeError.IsVisible = true; remote.Focus(); Shake(fieldBorder); return; }
        if (directory == null || engine == null) throw new InvalidOperationException("设备尚未就绪。");
        if (pendingCards.ContainsKey(code)) return;
        Peer? existing; lock (engine.State.Peers) existing = engine.State.Peers.FirstOrDefault(p => p.Code == code);
        var card = new ConnectCard(existing == null ? code : PeerLabel(existing).Split(" · ")[0] + " · " + code, !previewMode && Motion.Enabled);
        pendingCards[code] = card; pendingPanel.Children.Insert(0, card); UpdateSessionsVisible();
        card.SetProgress(ConnectFlow.Text(ConnectStage.Lookup));
        card.BringIntoView();
        var cancel = new CancellationTokenSource();
        card.CancelRequested += () => cancel.Cancel();
        void OnStage(string machine, ConnectStage stage) { if (machine == code) Dispatcher.UIThread.Post(() => card.SetProgress(ConnectFlow.Text(stage))); }
        directory.Stage += OnStage;
        try
        {
            await card.Enter();
            string? password = null;
            if (existing == null)
            {
                password = await card.AskPassword(null);
                if (password == null) return;
            }
            while (true)
            {
                card.SetProgress(ConnectFlow.Text(ConnectStage.Lookup));
                try
                {
                    await directory.Connect(code, password ?? "", cancel.Token, enrolling: password != null);
                    remote.Text = ""; Say("已连接 " + code + "。");
                    await card.Succeed();
                    return;
                }
                catch (Exception ex)
                {
                    if (cancel.IsCancellationRequested) { Say("已取消。"); return; }
                    var problem = ConnectFlow.Classify(ex);
                    engine.Events.Add("ERROR", "连接", $"{code} 失败（{problem.Kind}）：{ex.Message}");
                    if (problem.AskPassword)
                    {
                        password = await card.AskPassword(problem.Title + "：" + problem.Hint);
                        if (password == null) return;
                        continue;
                    }
                    var canRetry = problem.Kind is ConnectFailure.Offline or ConnectFailure.Network or ConnectFailure.Other;
                    Shake(fieldBorder);
                    if (!await card.ShowFailure(problem, canRetry)) return;
                }
            }
        }
        finally
        {
            directory.Stage -= OnStage;
            await card.Leave();
            pendingPanel.Children.Remove(card); pendingCards.Remove(code); UpdateSessionsVisible();
            RevealSession(code);
        }
    }

    // ---- toast ---------------------------------------------------------------------------------------------------------
    // A short frosted note in the middle of the window ("copied"); it fades in, stays a moment, then fades out.
    readonly TextBlock toastText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center };
    Border? toastBox;
    int toastToken;
    Control toastHost
    {
        get
        {
            if (toastBox != null) return toastBox;
            var tick = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Look.OnlineDeep, Child = Look.Icon("M5,12.5 L10,17 L19,7.5", 12, Brushes.White, 2.4), Margin = new Thickness(0, 0, 10, 0) };
            ((Avalonia.Controls.Shapes.Path)tick.Child!).HorizontalAlignment = HorizontalAlignment.Center; ((Avalonia.Controls.Shapes.Path)tick.Child!).VerticalAlignment = VerticalAlignment.Center;
            toastBox = new Border
            {
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { tick, toastText } },
                Padding = new Thickness(16, 11), CornerRadius = new CornerRadius(18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(232, 255, 255, 255)), BorderBrush = Chrome.GlassEdge, BorderThickness = new Thickness(1.2),
                BoxShadow = BoxShadows.Parse("0 14 36 0 #33301E14"), Opacity = 0, IsHitTestVisible = false,
                Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = Motion.Ms(180) } },
            };
            return toastBox;
        }
    }
    async void Toast(string text)
    {
        var box = (Border)toastHost; var mine = ++toastToken;
        toastText.Text = text; box.Opacity = 1;
        if (previewMode) { box.Transitions = null; box.Opacity = 1; return; }
        await Task.Delay(1500);
        if (mine == toastToken) box.Opacity = 0;
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
        askEverySession.OnContent = "每次被控前都需要人工确认"; askEverySession.OffContent = "每次被控前都需要人工确认";
        askEverySession.IsCheckedChanged += (_, _) =>
        {
            if (syncingAsk || directory == null) return;
            if (askEverySession.IsChecked != true && !Person("关闭“每次被控前都需要人工确认”")) { syncingAsk = true; askEverySession.IsChecked = true; syncingAsk = false; return; }
            try { directory.SetAlwaysAsk(askEverySession.IsChecked == true); Say(askEverySession.IsChecked == true ? "已开启：每次有设备连接本机前都会先询问你。" : "已关闭：已信任的设备可以直接连接。"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
        };
        var askNote = new TextBlock { Text = "开启后，已信任的设备每次连接也要等你点“允许”；没有点允许就不会被控。", Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var sharedCard = BuildSharedFolder();
        var accessTab = new StackPanel { Spacing = 8, Children = { incomingArea, Section("确认方式", Card(new StackPanel { Spacing = 6, Children = { askEverySession, askNote } })), Section("共享文件夹", sharedCard), Section("可访问本机的设备", controllers), Section("审计记录", BuildAuditCard()) } };
        var logButton = Button("打开日志", OpenLogFile); var exportButton = Button("导出诊断包", () => { var path = Diagnostics.Export(Program.DataRoot, engine); Say("诊断包已导出：" + path); return Task.CompletedTask; });
        var copyEndpoint = Button("复制 EndpointId", async () => { await Clipboard!.SetTextAsync(engine?.Id ?? ""); Toast("已复制本机 EndpointId"); });
        logButton.Content = Look.IconText(Look.DocumentPath, "打开日志"); exportButton.Content = Look.IconText(Look.DownloadPath, "导出诊断包"); copyEndpoint.Content = Look.IconText(Look.CopyPath, "复制 EndpointId");
        foreach (var b in new[] { logButton, exportButton, copyEndpoint }) { b.HorizontalAlignment = HorizontalAlignment.Left; b.MinWidth = 140; }
        var aboutTab = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Section("ArdUi " + Program.Version, Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "本机 EndpointId（对方首次连接时用来核对身份）", Foreground = Muted, FontSize = 12 }, aboutEndpoint, copyEndpoint } })),
                Section("日志", Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "日志记录调试级信息，保存在本机，并用默认文本编辑器打开。", Foreground = Muted, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { logButton, exportButton } } } })),
            }
        };
        startupBox = new ToggleSwitch { OnContent = "随 Windows 启动（在托盘中静默运行）", OffContent = "随 Windows 启动（在托盘中静默运行）", IsChecked = Autostart.Enabled };
        startupBox.IsCheckedChanged += (_, _) =>
        {
            if (syncingStartup) return;
            try { Autostart.Set(startupBox.IsChecked == true); Say(startupBox.IsChecked == true ? "已设置随系统启动。" : "已取消随系统启动。"); }
            catch (Exception ex) { Say(ex.Message); }
        };
        var motionBox = new ToggleSwitch { OnContent = "减少动画（重新启动后生效）", OffContent = "减少动画（重新启动后生效）", IsChecked = Motion.UserReduced };
        motionBox.IsCheckedChanged += (_, _) =>
        {
            try { Motion.SetReduced(motionBox.IsChecked == true); Say(motionBox.IsChecked == true ? "已设置减少动画，重新启动后生效。" : "已恢复动画，重新启动后生效。"); }
            catch (Exception ex) { Say(ex.Message); }
        };
        var islandBox = new ToggleSwitch { OnContent = "显示灵动岛（屏幕右下角（托盘上方）的小胶囊，可拖动：新消息、未读数、文件进度）", OffContent = "显示灵动岛（屏幕顶部的小胶囊：新消息、未读数、文件进度）", IsChecked = !Motion.NoIsland };
        var islandFull = new ToggleSwitch { OnContent = "全屏或演示时也显示灵动岛", OffContent = "全屏或演示时也显示灵动岛", IsChecked = Motion.IslandInFullscreen };
        void SaveIsland()
        {
            try { Motion.SetIsland(islandBox.IsChecked == true, islandFull.IsChecked == true); island?.Enable(islandBox.IsChecked == true); islandFull.IsEnabled = islandBox.IsChecked == true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
        }
        islandBox.IsCheckedChanged += (_, _) => SaveIsland(); islandFull.IsCheckedChanged += (_, _) => SaveIsland(); islandFull.IsEnabled = islandBox.IsChecked == true;
        var voiceBox = new ToggleSwitch { OnContent = "允许语音通话（对方呼叫时响铃；建议戴耳机，避免回声）", OffContent = "允许语音通话（已关闭：自动拒绝来电）", IsChecked = !Motion.NoVoice };
        voiceBox.IsCheckedChanged += (_, _) => { try { Motion.SetVoice(voiceBox.IsChecked == true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); } };
        var receiptsBox = new ToggleSwitch { OnContent = "发送已读回执（对方能看到你已读了哪些消息）", OffContent = "发送已读回执（已关闭：对方只看到“已送达”）", IsChecked = !Motion.NoReadReceipts };
        receiptsBox.IsCheckedChanged += (_, _) => { try { Motion.SetReadReceipts(receiptsBox.IsChecked == true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); } };
        var languageBox = new ComboBox { ItemsSource = new[] { "自动（跟随 Windows）", "简体中文", "English" }, SelectedIndex = Motion.Language == "zh" ? 1 : Motion.Language == "en" ? 2 : 0, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        languageBox.SelectionChanged += (_, _) =>
        {
            var chosen = languageBox.SelectedIndex == 1 ? "zh" : languageBox.SelectedIndex == 2 ? "en" : "auto";
            if (chosen == Motion.Language) return;
            try { Motion.SetLanguage(chosen); Say("已更改语言，重新启动 ArdUi 后生效。"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(ex.Message); }
        };
        var languageNote = new TextBlock { Text = "界面语言（更改后重新启动 ArdUi 生效）", Foreground = Look.Muted, FontSize = 12 };
        var generalTab = new StackPanel { Spacing = 8, Children = { Section("语言 / Language", Card(new StackPanel { Spacing = 6, Children = { languageNote, languageBox } })), Section("启动", Card(startupBox)), Section("界面", Card(new StackPanel { Spacing = 6, Children = { motionBox, islandBox, islandFull } })), Section("语音", Card(voiceBox)), Section("对话", Card(receiptsBox)) } };
        var tabs = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var pages = new (string Name, Control Body)[] { ("通用", generalTab), ("设备管理", devicesTab), ("被控与授权", accessTab), ("网络诊断", BuildNetDiagTab()), ("麦克风诊断", BuildMicDiagTab()), ("日志与关于", aboutTab) };
        var tabButtons = new List<Button>();
        void Select(int index)
        {
            settingsBody.Content = pages[index].Body; if (index == 2) { RefreshAudit(); syncingAsk = true; askEverySession.IsChecked = directory?.AlwaysAsk == true; syncingAsk = false; }
            for (var i = 0; i < tabButtons.Count; i++) { if (i == index) tabButtons[i].Classes.Add("primary"); else tabButtons[i].Classes.Remove("primary"); }
        }
        for (var i = 0; i < pages.Length; i++)
        {
            var index = i; var tab = new Button { Content = pages[i].Name, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 5) };
            tab.Click += (_, _) => Select(index); tabButtons.Add(tab); tabs.Children.Add(tab);
        }
        selectSettingsTab = Select;
        Select(0);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { back, new TextBlock { Text = "设置", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(14, 0, 10, 10) };
        layout.Children.Add(head);
        Grid.SetRow(tabs, 1); tabs.Margin = new Thickness(0, 10, 0, 8); layout.Children.Add(tabs);
        var scroll = new ScrollViewer { Content = settingsBody }; Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        settingsPage.Child = layout; settingsPage.IsVisible = false;
        return settingsPage;
    }
    Action<int>? selectSettingsTab;
    ToggleSwitch? startupBox;
    bool syncingStartup;

    void ShowSettings(bool show)
    {
        settingsOpen = show; settingsPage.IsVisible = show; homePage.IsVisible = !show; UpdateAttention();
        if (show) { aboutEndpoint.Text = engine?.Id ?? aboutEndpoint.Text; RefreshAudit(); }
        if (show && startupBox != null) { syncingStartup = true; startupBox.IsChecked = Autostart.Enabled; syncingStartup = false; }
    }
}
