namespace ArdUi;

// Preview only: the floating "island" (`--render-island <png>`). Nothing here is wired to the engine.
static class IslandPreview
{
    static readonly IBrush Pill = new SolidColorBrush(Color.FromArgb(246, 22, 19, 17));
    static readonly IBrush Soft = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly IBrush Red = Brush.Parse("#F0504A");

    public static int Render(string path)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        ChatTransfer Send(long done, bool finish = false)
        {
            var t = new ChatTransfer("f", "tcs-manual-2026.09.26.7.exe", ChatKind.File, 100_000_000, false, () => { });
            Thread.Sleep(300); t.Report(done); if (finish) t.Finish(TransferState.Done); return t;
        }
        var rows = new (string Title, Action<IslandModel> Set)[]
        {
            ("1 待命（已连接，没有消息）", m => m.SetConnected(1)),
            ("2 收到消息：透明气泡 4 秒，鼠标可穿透", m => { m.SetConnected(1); m.SetUnread(1, "p"); m.MessageArrived("p", "K4DD0B", "你好，我这边看到远程桌面卡了一下，能帮我看看吗？"); }),
            ("3 气泡消失后：未读红点和数量", m => { m.SetConnected(1); m.SetUnread(3, "p"); }),
            ("4 发送文件：进度环 + 速度 + 取消", m => { m.SetConnected(1); m.SetTransfers([Send(42_000_000)]); }),
            ("5 发送中又有新消息", m => { m.SetConnected(1); m.SetUnread(2, "p"); m.SetTransfers([Send(42_000_000)]); }),
            ("7 被控中：红点和文字", m => { m.SetConnected(1); m.SetControlled(["K4DD0B"]); }),
            ("6 发送完成：对勾", m => { m.SetConnected(1); m.SetTransfers([Send(100_000_000, true)]); }),
        };
        var stack = new StackPanel { Spacing = 0 };
        foreach (var (title, set) in rows)
        {
            var model = new IslandModel(); set(model);
            var view = new IslandView(model, animated: false);
            view.Apply();
            var screen = new Grid
            {
                Height = 150, ClipToBounds = true, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#2E5E92"), 0), new GradientStop(Color.Parse("#6FA5D6"), .55), new GradientStop(Color.Parse("#E1B58E"), 1) } },
                Children = { view },
            };
            view.HorizontalAlignment = HorizontalAlignment.Right; view.VerticalAlignment = VerticalAlignment.Bottom;
            stack.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(14, 6), Child = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = Look.Ink } });
            stack.Children.Add(screen);
        }
        var window = new Window { Width = 560, Height = 7 * (150 + 30), FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontSize = 13, Content = stack };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var bitmap = window.CaptureRenderedFrame() ?? throw new IOException("无法渲染预览。");
            bitmap.Save(path);
        }
        finally { window.Close(); }
        return 0;
    }
}
