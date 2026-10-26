namespace ArdUi;

// Guided updates (see Updater): a floating card that stays hidden while the new version downloads in the background. It comes up when
// the download is ready (install now, or later), and at once, with the progress, when M refuses this version: then nothing works
// until the new one is installed. "Later" hides it for this run until there is something new to say.
sealed partial class MainWindow
{
    Updater? updater;
    readonly Border updateOverlay = new() { Background = Brush.Parse("#8C1F1E1D"), IsVisible = false };
    readonly TextBlock updateTitle = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock updateText = new() { TextWrapping = TextWrapping.Wrap };
    // the progress: a filled part and the rest of a track, as two proportional columns
    readonly Grid updateProgress = new() { Height = 6, IsVisible = false, ColumnDefinitions = new ColumnDefinitions("0*,1*") };
    readonly TextBlock updateBytes = new() { FontSize = 12, IsVisible = false };
    readonly Button updateInstall = new() { Content = "立即安装", IsVisible = false };
    readonly Button updateLater = new() { Content = "稍后" };
    (string? Version, Updater.Phase Phase, bool Urgent)? updateDismissed;

    Border BuildUpdate()
    {
        updateInstall.Classes.Add("primary");
        updateBytes.Foreground = Muted;
        var track = new Border { Background = Look.Ghost, CornerRadius = new CornerRadius(3) }; Grid.SetColumnSpan(track, 2);
        updateProgress.Children.Add(track); updateProgress.Children.Add(new Border { Background = Look.Accent, CornerRadius = new CornerRadius(3) });
        updateInstall.Click += async (_, _) =>
        {
            if (!Person("安装新版本")) return;
            await Guard(() => { updater?.Install(); ExitApplication(); return Task.CompletedTask; });
        };
        updateLater.Click += (_, _) => { if (updater != null) updateDismissed = (updater.Latest?.Version, updater.State, directory?.Outdated != null); UpdateUpdate(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { updateLater, updateInstall } };
        var card = Card(new StackPanel { Spacing = 12, Children = { updateTitle, updateText, updateProgress, updateBytes, buttons } });
        card.Background = Look.CardSolid; card.BorderBrush = Look.Line; card.Padding = new Thickness(20); card.MaxWidth = 440; card.Margin = new Thickness(14);
        card.HorizontalAlignment = HorizontalAlignment.Stretch; card.VerticalAlignment = VerticalAlignment.Center;
        updateOverlay.Child = new ScrollViewer { VerticalAlignment = VerticalAlignment.Center, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = card };
        return updateOverlay;
    }

    void StartUpdater(Settings settings)
    {
        try { updater = new Updater(settings.Server, Program.DataRoot, Program.WireVersion); }
        catch (Exception ex) { Diagnostics.Log("update", ex.Message); return; }
        updater.Changed += () => Dispatcher.UIThread.Post(UpdateUpdate);
        updater.Start();
    }

    bool outdatedSeen;
    void UpdateUpdate()
    {
        if (updater == null) { updateOverlay.IsVisible = false; return; }
        var urgent = directory?.Outdated != null;
        if (urgent && !outdatedSeen) updater.CheckNow();   // refused just now: look for the new version without waiting for the next planned look
        outdatedSeen = urgent;
        var offer = updater.Latest; var phase = updater.State;
        var show = urgent ? offer != null || phase == Updater.Phase.Failed : offer != null && phase == Updater.Phase.Ready;
        if (updateDismissed is { } dismissed && dismissed.Version == offer?.Version && dismissed.Phase == phase && dismissed.Urgent == urgent) show = false;
        updateOverlay.IsVisible = show;
        if (show) ShowUpdate(urgent, phase, offer?.Version ?? "", updater.Received, offer?.Size ?? 0, updater.Error);
    }

    // What the card says; also used by the headless preview (--render-update-preview).
    internal void ShowUpdate(bool urgent, Updater.Phase phase, string version, long received, long size, string? error)
    {
        updateOverlay.IsVisible = true;
        updateTitle.Text = urgent ? "需要升级 ArdUi" : $"ArdUi {version} 已可安装";
        updateText.Text = phase switch
        {
            Updater.Phase.Ready => urgent ? $"这个版本已不能连接服务器。ArdUi {version} 已下载好，安装后即可继续使用。"
                                          : $"新版本 ArdUi {version} 已在后台下载好。安装时 ArdUi 会关闭，当前的连接会断开。",
            Updater.Phase.Downloading => $"这个版本已不能连接服务器，正在下载 ArdUi {version}。下载完成后即可安装。",
            Updater.Phase.Failed => $"这个版本已不能连接服务器。下载新版本暂时没有成功（{error}），稍后会自动再试。",
            _ => "这个版本已不能连接服务器，正在查找新版本。",
        };
        var downloading = phase == Updater.Phase.Downloading && size > 0;
        updateProgress.IsVisible = updateBytes.IsVisible = downloading;
        if (downloading)
        {
            var done = Math.Clamp((double)received / size, 0, 1);
            updateProgress.ColumnDefinitions[0].Width = new GridLength(done, GridUnitType.Star);
            updateProgress.ColumnDefinitions[1].Width = new GridLength(1 - done, GridUnitType.Star);
            updateBytes.Text = $"{received / 1048576.0:F1} / {size / 1048576.0:F1} MB";
        }
        updateInstall.IsVisible = phase == Updater.Phase.Ready;
    }
}
