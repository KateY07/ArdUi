using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace ArdUi;

// The conversation with a connected device: text, pictures and files, both ways, over the encrypted session.
// Pictures and files are staged under the input box first and go out when "send" is pressed; each one then travels on its own, with a
// progress bar, a speed and a cancel button, while the conversation carries on.
sealed partial class MainWindow
{
    public const string ChatPath = "M4,5 L20,5 L20,15 L11,15 L7,19 L7,15 L4,15 Z";
    // a phone shaking: the "抖一抖" button
    public const string NudgePath = "M9,4 L15,4 L15,20 L9,20 Z M5,8 L5,16 M19,8 L19,16 M2,10 L2,14 M22,10 L22,14";
    public const string ClipPath = "M20,11 L12,19 a4.5,4.5 0 0 1 -6.4,-6.4 L13.5,4.7 a3,3 0 0 1 4.3,4.3 L9.9,16.9 a1.5,1.5 0 0 1 -2.1,-2.1 L15,7.6";
    static readonly string PasteFolder = Path.Combine(Path.GetTempPath(), "ArdUi-paste");

    readonly Border chatOverlay = new() { Background = Brush.Parse("#8C1F1E1D"), IsVisible = false };
    readonly StackPanel chatList = new() { Spacing = 8, Margin = new Thickness(2, 6) };
    readonly ScrollViewer chatScroll = new() { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    readonly TextBox chatInput = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = ChatChannel.MaxText, Watermark = "输入消息，Enter 发送，Shift+Enter 换行", MinHeight = 38, MaxHeight = 110, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock chatTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly WrapPanel attachTray = new() { IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    readonly List<string> staged = new();
    readonly Dictionary<string, ContentControl> transferViews = new();
    ChatChannel? openChat;
    string? openChatPeer;

    Border BuildChat()
    {
        var close = Chrome.IconButton(Look.CrossPath, 12, 30); ToolTip.SetTip(close, "关闭"); close.Click += (_, _) => CloseChat();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Look.Icon(ChatPath, 18, Look.Accent)); ((Control)head.Children[0]).Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(chatTitle, 1); head.Children.Add(chatTitle); SetChatCall(null); Grid.SetColumn(chatCall, 2); head.Children.Add(chatCall); Grid.SetColumn(close, 3); head.Children.Add(close);
        chatScroll.Content = chatList; chatScroll.Padding = new Thickness(2);
        var listBox = new Border { Child = chatScroll, Background = new SolidColorBrush(Color.FromArgb(120, 245, 244, 238)), CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Height = 330 };
        var attach = Chrome.IconButton(ClipPath, 17, 36); ToolTip.SetTip(attach, "添加文件、图片或视频（也可以拖进来或 Ctrl+V 粘贴），按发送才会发出");
        attach.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "选择要发送的文件" });
            foreach (var file in files) if (file.TryGetLocalPath() is { } path) Stage(path);
        };
        var send = new Button { Content = Look.Icon(Chrome.ArrowPath, 16, Brushes.White, 2.2), Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Bottom };
        send.Classes.Add("primary"); ToolTip.SetTip(send, "发送");
        send.Click += async (_, _) => await Guard(SendChat);
        // Tunnelling, so these keys are seen before the text box acts on them: Enter sends, Shift+Enter is a new line,
        // Ctrl+V adds a picture or files from the clipboard (text is pasted as usual).
        chatInput.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { e.Handled = true; await Guard(SendChat); }
            else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await Guard(PasteIntoChat); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        attach.VerticalAlignment = VerticalAlignment.Bottom;
        var nudge = Chrome.IconButton(NudgePath, 17, 36); ToolTip.SetTip(nudge, $"抖一抖：提醒对方看对话（{ChatChannel.NudgeSeconds} 秒内只能一次）"); nudge.VerticalAlignment = VerticalAlignment.Bottom;
        nudge.Click += (_, _) =>
        {
            if (openChat is not { } chat) return;
            if (chat.NudgeWait > TimeSpan.Zero) { Toast($"抖一抖太频繁了，请 {Math.Ceiling(chat.NudgeWait.TotalSeconds)} 秒后再试。"); return; }
            _ = Task.Run(async () => { try { await chat.SendNudge(); } catch (Exception ex) { Diagnostics.Log("chat-nudge", ex.Message); } });
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Children = { attach, nudge } };
        var composer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        composer.Children.Add(tools); Grid.SetColumn(chatInput, 1); chatInput.Margin = new Thickness(4, 0); composer.Children.Add(chatInput); Grid.SetColumn(send, 2); composer.Children.Add(send);
        var card = Card(new StackPanel { Children = { head, listBox, attachTray, composer } });
        card.Background = Look.CardSolid; card.BorderBrush = Look.Line; card.Padding = new Thickness(16); card.MaxWidth = 460; card.Margin = new Thickness(14); card.VerticalAlignment = VerticalAlignment.Center;
        // files dropped on the window are staged
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            foreach (var item in e.Data.GetFiles() ?? []) if (item.TryGetLocalPath() is { } path) Stage(path);
        });
        chatOverlay.Child = new ScrollViewer { VerticalAlignment = VerticalAlignment.Center, Content = card };
        return chatOverlay;
    }

    // ---- opening and closing ---------------------------------------------------------------------------------------
    void OpenChat(string peerId, string label)
    {
        if (engine == null) return;
        CloseChat();
        openChatPeer = peerId; openChat = engine.ChatFor(peerId);
        chatTitle.Text = "与 " + label + " 对话";
        chatList.Children.Clear(); transferViews.Clear(); messageViews.Clear();
        foreach (var message in openChat.Messages.ToArray()) AddMessageView(message);
        foreach (var transfer in openChat.ActiveTransfers.Where(t => t.Incoming)) AddTransferView(transfer);   // what this side sends is in its message
        openChat.MessageAdded += OnChatMessage; openChat.TransferStarted += OnTransferStarted; openChat.MessageChanged += OnMessageChanged;
        openChat.MarkRead(); chatOverlay.IsVisible = true; chatInput.Focus(); ScrollChatToEnd(); ChatSeen();
    }

    void CloseChat()
    {
        if (openChat != null) { openChat.MessageAdded -= OnChatMessage; openChat.TransferStarted -= OnTransferStarted; openChat.MessageChanged -= OnMessageChanged; }
        openChat = null; openChatPeer = null; chatOverlay.IsVisible = false;
    }

    // A received file replaces its progress bubble where it stood; anything else goes to the end. Each message keeps its place, so a
    // change of its delivery (delivered, failed, sent again) redraws it there.
    readonly Dictionary<string, ContentControl> messageViews = new();
    void AddMessageView(ChatMessage message)
    {
        var host = new ContentControl { Content = Bubble(message), HorizontalAlignment = message.Kind == ChatKind.Nudge ? HorizontalAlignment.Center : message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        messageViews[message.Id] = host;
        if (transferViews.Remove(message.Id, out var view)) { var at = chatList.Children.IndexOf(view); chatList.Children[at] = host; }
        else chatList.Children.Add(host);
    }
    void OnChatMessage(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (openChat == null) return;
        AddMessageView(message);
        if (message.Kind == ChatKind.Nudge && !message.Mine) ShakeWindow();   // the conversation is open: the window itself shakes
        if (!message.Mine) { openChat.MarkRead(); ChatSeen(); } ScrollChatToEnd();
    });
    // the conversation is on screen: the other side may be told what was read (unless receipts are off)
    void ChatSeen()
    {
        if (openChat != null && chatOverlay.IsVisible && IsVisible && WindowState != WindowState.Minimized) openChat.Seen(!Motion.NoReadReceipts);
    }
    void OnMessageChanged(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (openChat != null && messageViews.TryGetValue(message.Id, out var host)) host.Content = Bubble(message);
    });

    void OnTransferStarted(ChatTransfer transfer) => Dispatcher.UIThread.Post(() => { if (openChat != null && transfer.Incoming) { AddTransferView(transfer); ScrollChatToEnd(); } });

    void ScrollChatToEnd() => Dispatcher.UIThread.Post(() => chatScroll.ScrollToEnd(), DispatcherPriority.Background);

    // ---- staging and sending -------------------------------------------------------------------------------------------
    void Stage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("找不到文件。");
            if (info.Length == 0) throw new InvalidOperationException("文件是空的。");
            if (info.Length > ChatChannel.MaxFile) throw new InvalidOperationException("文件超过 2 GB。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Say(ex.Message); return; }
        if (staged.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        staged.Add(path); RebuildTray();
    }

    void RebuildTray()
    {
        attachTray.Children.Clear();
        foreach (var path in staged.ToArray())
        {
            var info = new FileInfo(path);
            Control thumb = ChatChannel.LooksLikeImage(path) && TryBitmap(path) is { } bitmap
                ? new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill } }
                : new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Look.Ghost, Child = Look.Icon(Look.FolderPath, 18, Look.Muted) };
            var remove = new Button { Content = Look.Icon(Look.CrossPath, 9, Brushes.White, 2.4), Width = 20, Height = 20 }; remove.Classes.Add("circlex"); ToolTip.SetTip(remove, "不发送这个文件");
            var captured = path;
            remove.Click += (_, _) => { staged.Remove(captured); DeleteIfPasted(captured); RebuildTray(); };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = info.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 }, new TextBlock { Text = SizeText(info.Exists ? info.Length : 0), FontSize = 11, Foreground = Look.Muted } } };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { thumb, text, remove } };
            attachTray.Children.Add(new Border { Child = row, Padding = new Thickness(6, 5, 8, 5), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(12), Background = Look.CardSolid, BorderBrush = Look.Line, BorderThickness = new Thickness(1) });
        }
        attachTray.IsVisible = staged.Count != 0;
    }

    static void DeleteIfPasted(string path)
    {
        try { if (path.StartsWith(PasteFolder, StringComparison.OrdinalIgnoreCase)) File.Delete(path); } catch (IOException) { }
    }

    // Text goes first (it is quick), then every staged file starts its own transfer; this returns without waiting for them.
    async Task SendChat()
    {
        if (openChat == null) return;
        var text = chatInput.Text ?? ""; var files = staged.ToArray();
        if (text.Trim().Length == 0 && files.Length == 0) return;
        if (text.Trim().Length > ChatChannel.MaxText) { Say($"一条消息最多 {ChatChannel.MaxText} 个字符。"); return; }
        chatInput.Text = ""; staged.Clear(); RebuildTray();
        // the message shows at once; whether it got through is shown on it (a failed one can be sent again from there)
        var chat = openChat;
        if (text.Trim().Length != 0) _ = Task.Run(async () => { try { await chat.SendText(text); } catch (Exception ex) { Diagnostics.Log("chat-send", ex.Message); } });
        foreach (var path in files)
        {
            try
            {
                var transfer = openChat.StartSend(path);
                // a pasted picture is kept until it got through (a failed one may be sent again)
                if (path.StartsWith(PasteFolder, StringComparison.OrdinalIgnoreCase)) _ = transfer.Completion.ContinueWith(_ => { if (transfer.State == TransferState.Done) DeleteIfPasted(path); });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { Say(ex.Message); }
        }
        await Task.CompletedTask;
    }

    // Files copied in Explorer and pictures copied from anywhere are staged; anything else is pasted as text.
    async Task PasteIntoChat()
    {
        var clipboard = Clipboard;
        if (clipboard != null && openChat != null)
        {
            var files = await Avalonia.Input.Platform.ClipboardExtensions.TryGetFilesAsync(clipboard);
            var paths = files?.Select(f => f.TryGetLocalPath()).Where(path => path != null).Cast<string>().ToArray() ?? [];
            if (paths.Length != 0) { foreach (var path in paths) Stage(path); return; }
            var bitmap = await Avalonia.Input.Platform.ClipboardExtensions.TryGetBitmapAsync(clipboard);
            if (bitmap != null)
            {
                Directory.CreateDirectory(PasteFolder);
                var picture = Path.Combine(PasteFolder, "粘贴的图片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                using (var stream = File.Create(picture)) bitmap.Save(stream);
                Stage(picture); return;
            }
        }
        chatInput.Paste();
    }

    // ---- progress bubbles ------------------------------------------------------------------------------------------------
    internal static string SizeText(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:F2} GB" : bytes >= 1 << 20 ? $"{bytes / 1048576.0:F1} MB" : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB" : bytes + " B";
    internal static string SpeedText(double bytesPerSecond) => bytesPerSecond >= 1 << 20 ? $"{bytesPerSecond / 1048576.0:F1} MB/s" : $"{bytesPerSecond / 1024.0:F0} KB/s";
    internal static string TimeLeft(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒" : $"{Math.Max(1, (int)span.TotalSeconds)} 秒";

    void AddTransferView(ChatTransfer transfer)
    {
        var host = new ContentControl { HorizontalAlignment = transfer.Incoming ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        transferViews[transfer.Id] = host; chatList.Children.Add(host);
        var name = new TextBlock { Text = transfer.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 };
        var detail = new TextBlock { FontSize = 11, Foreground = Look.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
        var done = new ColumnDefinition(new GridLength(0, GridUnitType.Star)); var rest = new ColumnDefinition(new GridLength(1, GridUnitType.Star));
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 4) };
        bar.ColumnDefinitions.Add(done); bar.ColumnDefinitions.Add(rest);
        var fill = new Border { Background = Look.Accent, CornerRadius = new CornerRadius(3) };
        bar.Children.Add(new Border { Background = Look.Ghost, CornerRadius = new CornerRadius(3) }); Grid.SetColumnSpan((Control)bar.Children[0], 2); bar.Children.Add(fill);
        var stop = new Button { Content = Look.Icon(Look.CrossPath, 10, Brushes.White, 2.4), Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center }; stop.Classes.Add("circlex");
        ToolTip.SetTip(stop, transfer.Incoming ? "不接收了" : "取消发送"); stop.Click += (_, _) => transfer.Cancel();
        var text = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Look.Icon(Look.FolderPath, 16, Look.Accent), name } }, bar, detail } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Width = 270 };
        grid.Children.Add(text); stop.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(stop, 1); grid.Children.Add(stop);
        host.Content = new Border { Child = grid, Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(14), Background = Look.CardSolid, BorderBrush = Look.Line, BorderThickness = new Thickness(1) };

        void Update()
        {
            var fraction = Math.Clamp(transfer.Fraction, 0, 1);
            done.Width = new GridLength(fraction, GridUnitType.Star); rest.Width = new GridLength(1 - fraction, GridUnitType.Star);
            Grid.SetColumn(fill, 0);
            if (transfer.State == TransferState.Active)
            {
                var left = transfer.Remaining; var speed = transfer.BytesPerSecond;
                detail.Text = (transfer.Incoming ? "正在接收 " : "正在发送 ") + $"{fraction * 100:F0}%  ·  {SizeText(transfer.Transferred)} / {SizeText(transfer.Size)}" + (speed > 0 ? "  ·  " + SpeedText(speed) : "") + (left != null ? "  ·  剩余 " + TimeLeft(left.Value) : "");
            }
            else if (transfer.State is TransferState.Cancelled or TransferState.Failed)
            {
                stop.IsVisible = false; fill.Background = Look.Idle; detail.Foreground = transfer.State == TransferState.Failed ? Brush.Parse("#B3432B") : Look.Muted;
                detail.Text = (transfer.State == TransferState.Failed ? "失败：" : "") + (transfer.Error ?? "已取消");
            }
        }
        Update();
        transfer.Changed += () => Dispatcher.UIThread.Post(Update);
    }

    // ---- the bubbles -------------------------------------------------------------------------------------------------
    // "抖一抖" in the conversation: a quiet line in the middle, with its delivery when this side sent it.
    Control NudgeLine(ChatMessage message)
    {
        var who = message.Mine ? "你" : openChatPeer != null ? LabelOf(openChatPeer) : "对方";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(Look.Icon(NudgePath, 13, Look.Accent));
        row.Children.Add(new TextBlock { [Loc.KeepProperty] = !message.Mine, Text = who + " 发送了抖一抖", FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = message.At.ToLocalTime().ToString("HH:mm"), FontSize = 10, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center });
        if (message.Mine && message.Delivery == Delivery.Sending) row.Children.Add(new TextBlock { Text = "发送中…", FontSize = 10, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center });
        if (message.Mine && message.Delivery == Delivery.Delivered) row.Children.Add(new TextBlock { Text = message.Read ? "✓✓ 已读" : "✓ 已送达", FontSize = 10, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center });
        if (message.Mine && message.Delivery == Delivery.Failed)
        {
            var failed = new TextBlock { Text = "未送达", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#B3432B"), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(failed, message.Error);
            var retry = new Button { Content = "重试", FontSize = 11, MinHeight = 22, Padding = new Thickness(10, 1), VerticalAlignment = VerticalAlignment.Center };
            retry.Click += (_, _) => openChat?.Retry(message.Id);
            row.Children.Add(failed); row.Children.Add(retry);
        }
        return new Border { Child = row, Padding = new Thickness(12, 5), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center };
    }

    // the main window shakes side to side for a moment (a nudge arrived while its conversation is open)
    void ShakeWindow()
    {
        if (!Motion.Enabled || WindowState != WindowState.Normal) return;
        var origin = Position; int[] steps = [-10, 10, -8, 8, -6, 6, -3, 3, 0]; var at = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { Position = new PixelPoint(origin.X + steps[at++], origin.Y); if (at >= steps.Length) { timer.Stop(); Position = origin; } };
        timer.Start();
    }

    Control Bubble(ChatMessage message)
    {
        if (message.Kind == ChatKind.Nudge) return NudgeLine(message);
        var mine = message.Mine;
        var ink = mine ? Brushes.White : Look.Ink;
        var content = new StackPanel { Spacing = 4 };
        switch (message.Kind)
        {
            case ChatKind.Text:
                content.Children.Add(new TextBlock { [Loc.KeepProperty] = true, Text = message.Text, Foreground = ink, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
                break;
            case ChatKind.Image when TryBitmap(message.Path) is { } bitmap:
                var picture = new Image { Source = bitmap, MaxWidth = 240, MaxHeight = 220, Stretch = Stretch.Uniform, Cursor = new Cursor(StandardCursorType.Hand) };
                ToolTip.SetTip(picture, message.FileName + "（点击打开）");
                picture.PointerPressed += (_, e) => { e.Handled = true; OpenStored(message.Path); };
                content.Children.Add(new Border { Child = picture, CornerRadius = new CornerRadius(10), ClipToBounds = true });
                break;
            default:
                var open = new Button { Content = "打开", MinHeight = 26, Padding = new Thickness(10, 3) };
                open.Click += (_, _) => OpenStored(message.Path);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                row.Children.Add(Look.Icon(Look.FolderPath, 20, ink)); ((Control)row.Children[0]).Margin = new Thickness(0, 0, 10, 0);
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { [Loc.KeepProperty] = true, Text = message.FileName, Foreground = ink, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 }, new TextBlock { Text = SizeText(message.Size), FontSize = 11, Foreground = mine ? Brush.Parse("#F3D4C8") : Look.Muted } } };
                Grid.SetColumn(info, 1); row.Children.Add(info); Grid.SetColumn(open, 2); open.Margin = new Thickness(10, 0, 0, 0); row.Children.Add(open);
                content.Children.Add(row);
                break;
        }
        if (mine && message.Transfer is { State: TransferState.Active } sending) content.Children.Add(SendingProgress(sending));
        content.Children.Add(Footer(message));
        return new Border
        {
            Child = content, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(14), MaxWidth = 330,
            Background = mine ? Look.Accent : Look.CardSolid, BorderBrush = mine ? Brushes.Transparent : Look.Line, BorderThickness = new Thickness(1),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
    }

    // Time, and for what this side sends: sending… / delivered / not delivered with "重试".
    Control Footer(ChatMessage message)
    {
        var soft = message.Mine ? Brush.Parse("#F3D4C8") : Look.Muted;
        var time = new TextBlock { Text = message.At.ToLocalTime().ToString("HH:mm"), FontSize = 10, Foreground = soft, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { time } };
        if (!message.Mine) return row;
        switch (message.Delivery)
        {
            case Delivery.Sending:
                row.Children.Add(new TextBlock { Text = "发送中…", FontSize = 10, Foreground = soft, VerticalAlignment = VerticalAlignment.Center });
                break;
            case Delivery.Delivered:
                row.Children.Add(new TextBlock { Text = message.Read ? "✓✓ 已读" : "✓ 已送达", FontSize = 10, FontWeight = message.Read ? FontWeight.SemiBold : FontWeight.Normal, Foreground = message.Read ? Brushes.White : soft, VerticalAlignment = VerticalAlignment.Center });
                break;
            case Delivery.Failed:
                var failed = new TextBlock { Text = "未送达", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(failed, message.Error);
                var retry = new Button { Content = "重试", FontSize = 11, MinHeight = 22, Padding = new Thickness(10, 1), Background = Brushes.White, Foreground = Look.Accent, VerticalAlignment = VerticalAlignment.Center };
                retry.Click += (_, _) => openChat?.Retry(message.Id);
                row.Children.Add(failed); row.Children.Add(retry);
                break;
        }
        return row;
    }

    // A file this side is sending: its progress inside its own message, and a way to stop it.
    Control SendingProgress(ChatTransfer transfer)
    {
        var done = new ColumnDefinition(new GridLength(0, GridUnitType.Star)); var rest = new ColumnDefinition(new GridLength(1, GridUnitType.Star));
        var bar = new Grid { Height = 5, Margin = new Thickness(0, 4, 0, 2) };
        bar.ColumnDefinitions.Add(done); bar.ColumnDefinitions.Add(rest);
        var track = new Border { Background = Brush.Parse("#55FFFFFF"), CornerRadius = new CornerRadius(3) }; Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track); bar.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(3) });
        var detail = new TextBlock { FontSize = 11, Foreground = Brush.Parse("#F3D4C8"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var stop = new Button { Content = "取消", FontSize = 11, MinHeight = 22, Padding = new Thickness(8, 1), Background = Brush.Parse("#33FFFFFF"), Foreground = Brushes.White };
        stop.Click += (_, _) => transfer.Cancel();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(detail); Grid.SetColumn(stop, 1); stop.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(stop);
        void Update()
        {
            var fraction = Math.Clamp(transfer.Fraction, 0, 1);
            done.Width = new GridLength(fraction, GridUnitType.Star); rest.Width = new GridLength(1 - fraction, GridUnitType.Star);
            var left = transfer.Remaining; var speed = transfer.BytesPerSecond;
            detail.Text = $"{fraction * 100:F0}%  ·  {SizeText(transfer.Transferred)} / {SizeText(transfer.Size)}" + (speed > 0 ? "  ·  " + SpeedText(speed) : "") + (left != null ? "  ·  剩余 " + TimeLeft(left.Value) : "");
        }
        Update();
        transfer.Changed += () => Dispatcher.UIThread.Post(Update);
        return new StackPanel { Width = 250, Children = { bar, row } };
    }

    static Bitmap? TryBitmap(string? path)
    {
        try { return path != null && File.Exists(path) ? new Bitmap(path) : null; }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
    }

    // Received files are never run: "open" hands them to the program Windows associates (pictures) or shows the file in its folder.
    void OpenStored(string? path)
    {
        if (path == null || !File.Exists(path)) { Say("文件已不存在。"); return; }
        try
        {
            if (ChatChannel.LooksLikeImage(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say(ex.Message); }
    }

    // ---- the floating island ---------------------------------------------------------------------------------------
    IslandWindow? island;

    void StartIsland()
    {
        if (engine == null || directory == null || island != null) return;
        island = new IslandWindow(engine, LabelOf, id => openChatPeer == id && IsVisible && WindowState != WindowState.Minimized, OpenFromIsland, ShowFromTray);
        island.Start();
    }

    string LabelOf(string peerId)
    {
        Peer? peer; lock (engine!.State.Peers) peer = engine.State.Peers.FirstOrDefault(p => p.Id == peerId);
        return peer != null ? PeerLabel(peer).Split(" · ")[0] : directory?.CodeOf(peerId) ?? peerId[..Math.Min(6, peerId.Length)];
    }

    void OpenFromIsland(string peerId) { ShowFromTray(); OpenChat(peerId, LabelOf(peerId)); }

    // ---- unread ----------------------------------------------------------------------------------------------------
    // A message arriving while its conversation is closed: a note on screen, and the badge on the device's card.
    readonly HashSet<string> chatWatched = new();
    void WatchChat(string peerId, string label)
    {
        if (engine == null || !chatWatched.Add(peerId)) return;
        var chat = engine.ChatFor(peerId);
        chat.MessageAdded += message => Dispatcher.UIThread.Post(() =>
        {
            if (message.Mine || openChatPeer == peerId || island != null && !Motion.NoIsland) return;   // the island announces it
            if (message.Kind == ChatKind.Nudge) { Toast(label + " 发送了抖一抖"); return; }
            var preview = message.Kind == ChatKind.Text ? message.Text : (message.Kind == ChatKind.Image ? "[图片] " : "[文件] ") + message.FileName;
            Toast(label + "：" + (preview.Length > 30 ? preview[..30] + "…" : preview));
        });
    }

    static Control ChatBadge(bool unread)
    {
        var icon = Look.Icon(ChatPath, 16, Look.Ink, 1.8);
        var host = new Grid { Width = 20, Height = 20, Children = { icon } };
        if (unread) host.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Brush.Parse("#D64545"), Stroke = Brushes.White, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0) });
        return host;
    }
}
