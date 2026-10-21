namespace ArdUi;

// Voice calls in the main window: a banner at the top for the call that is ringing or running (answer, decline, mute, hang up), and the
// phone button in the conversation. The sound itself is handled by CallChannel; this only shows it and passes the user's choices on.
sealed partial class MainWindow
{
    internal const string PhonePath = "M5,4 L9,4 L11,9 L8.5,10.5 A11,11 0 0 0 13.5,15.5 L15,13 L20,15 L20,19 A2,2 0 0 1 18,21 A16,16 0 0 1 3,6 A2,2 0 0 1 5,4 Z";
    internal const string MicPath = "M12,3 A3,3 0 0 1 15,6 L15,11 A3,3 0 0 1 9,11 L9,6 A3,3 0 0 1 12,3 Z M6,11 A6,6 0 0 0 18,11 M12,17 L12,21";

    readonly Border callBanner = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 54, 0, 0), Background = new SolidColorBrush(Color.FromArgb(246, 22, 19, 17)), CornerRadius = new CornerRadius(30), Padding = new Thickness(14, 8, 10, 8), BoxShadow = BoxShadows.Parse("0 12 30 0 #66000000, 0 0 0 1 #26FFFFFF"), MinWidth = 300 };
    readonly TextBlock callTitle = new() { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 14 };
    readonly TextBlock callSub = new() { Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), FontSize = 12 };
    readonly Border callLevel = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Brush.Parse("#5FD38D"), Opacity = .35, VerticalAlignment = VerticalAlignment.Center };
    readonly Button callAccept = DiscButton("#2F7D4F", PhonePath, "接听");
    readonly Button callHang = DiscButton("#D64545", Look.CrossPath, "挂断");
    readonly Button callMute = DiscButton("#44FFFFFF", MicPath, "静音");
    readonly Button chatCall = Chrome.IconButton(PhonePath, 17, 30);
    readonly HashSet<string> callsWatched = new();
    string? bannerPeer; CallChannel? lastRing;
    DispatcherTimer? callTimer;

    static Button DiscButton(string color, string path, string tip)
    {
        var button = new Button { Content = Look.Icon(path, 15, Brushes.White, 2), Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18), Background = Brush.Parse(color), BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(button, tip); return button;
    }

    Border BuildCallBanner()
    {
        callAccept.Click += async (_, _) => { if (CurrentCall() is { } call) await Guard(() => call.Accept()); };
        callHang.Click += async (_, _) => { if (CurrentCall() is { } call) await Guard(async () => { if (call.State == CallState.Ringing) await call.Decline(); else await call.Hangup(); }); };
        callMute.Click += (_, _) => { if (CurrentCall() is { } call) call.Muted = !call.Muted; UpdateCalls(); };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0), Children = { callTitle, callSub } };
        var phone = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Look.Accent, Child = Look.Icon(PhonePath, 16, Brushes.White, 2) };
        callBanner.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { phone, text, callLevel, new Border { Width = 10 }, callMute, callAccept, callHang } };
        // a phone button in the conversation: call, or hang up
        ToolTip.SetTip(chatCall, "语音通话");
        chatCall.Click += async (_, _) =>
        {
            if (engine == null || openChatPeer == null) return;
            var call = engine.CallFor(openChatPeer);
            if (call.State == CallState.Idle) await Guard(() => call.Start());
            else await Guard(async () => { if (call.State == CallState.Ringing) await call.Decline(); else await call.Hangup(); });
        };
        return callBanner;
    }

    CallChannel? CurrentCall()
    {
        if (engine == null) return null;
        return bannerPeer != null && engine.CallFor(bannerPeer) is { State: not CallState.Idle } kept ? kept : engine.Calls.Select(c => c.Value).FirstOrDefault(c => c.State != CallState.Idle);
    }

    // Called when anything about a call may have changed, and twice a second while one runs (the timer and the level).
    void UpdateCalls()
    {
        if (engine == null) return;
        foreach (var peerId in engine.Outgoing.Keys.Concat(engine.Incoming.Keys).Distinct()) WatchCall(peerId);
        var pair = engine.Calls.FirstOrDefault(c => c.Value.State != CallState.Idle);
        var call = pair.Value; bannerPeer = pair.Key;
        if (call == null)
        {
            callBanner.IsVisible = false; bannerPeer = null; callTimer?.Stop();
            SetChatCall(null); return;
        }
        callBanner.IsVisible = true; callTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; if (callTimer.Tag == null) { callTimer.Tag = true; callTimer.Tick += (_, _) => UpdateCalls(); }
        callTimer.Start();
        var label = LabelOf(pair.Key);
        if (call.State == CallState.Ringing && lastRing != call && (island == null || Motion.NoIsland) && !IsVisible) ShowFromTray();   // nobody would see the ring otherwise
        lastRing = call.State == CallState.Ringing ? call : null;
        switch (call.State)
        {
            case CallState.Ringing:
                callTitle.Text = label + " 来电"; callSub.Text = "语音通话 · 点击接听或拒绝";
                callAccept.IsVisible = true; callMute.IsVisible = false; ToolTip.SetTip(callHang, "拒绝"); break;
            case CallState.Calling:
                callTitle.Text = "正在呼叫 " + label; callSub.Text = "等待对方接听…";
                callAccept.IsVisible = false; callMute.IsVisible = false; ToolTip.SetTip(callHang, "取消"); break;
            default:
                var elapsed = call.Elapsed;
                callTitle.Text = "通话中 · " + label; callSub.Text = (elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss")) + (call.Muted ? " · 已静音" : "") + (call.Note is { } note ? " · " + note : "");
                callAccept.IsVisible = false; callMute.IsVisible = true; ToolTip.SetTip(callHang, "挂断");
                callMute.Background = Brush.Parse(call.Muted ? "#D79A3B" : "#44FFFFFF"); break;
        }
        callLevel.IsVisible = call.State == CallState.Active; callLevel.Opacity = .3 + Math.Min(.7, call.OutputLevel * 3);
        SetChatCall(call.State == CallState.Idle ? null : call);
    }

    void SetChatCall(CallChannel? call)
    {
        chatCall.Content = Look.Icon(PhonePath, 17, call == null ? Look.Muted : call.State == CallState.Active ? Brush.Parse("#2F7D4F") : Look.Accent, 1.8);
        ToolTip.SetTip(chatCall, call == null ? "语音通话" : call.State == CallState.Active ? "挂断" : call.State == CallState.Ringing ? "拒绝来电" : "取消呼叫");
    }

    // What the user should hear about when a call is over: missed, refused, failed and ordinary ends with their length.
    void WatchCall(string peerId)
    {
        if (engine == null || !callsWatched.Add(peerId)) return;
        var call = engine.CallFor(peerId);
        call.Ended += end => Dispatcher.UIThread.Post(() =>
        {
            var label = LabelOf(peerId);
            var length = end.Answered ? " · " + (end.Duration.TotalHours >= 1 ? end.Duration.ToString(@"h\:mm\:ss") : end.Duration.ToString(@"mm\:ss")) : "";
            if (end.Reason is "已拒绝" or "已取消") return;   // the user did that
            Toast(label + "：" + end.Reason + length);
            UpdateCalls();
        });
        call.Changed += () => Dispatcher.UIThread.Post(UpdateCalls);
    }
}
