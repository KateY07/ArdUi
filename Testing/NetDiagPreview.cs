namespace ArdUi;

// Preview only (`--render-netdiag <png>`): the diagnosis cards for a real run on this machine.
static class NetDiagPreview
{
    public static int Render(string path, string? dataRoot = null)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        var report = Task.Run(() => NetDiag.Run(new NetDiagOptions
        {
            UseSsdp = false,
            Probe = dataRoot == null ? null : _ => NetProbeLive.Ticket(dataRoot, new Settings().Server), ProbeFallbackHost = new Uri(new Settings().Server).Host,
            DirectoryUrl = new Settings().Server, RelayHostPort = new Uri(new Settings().Relay).Authority,
        })).GetAwaiter().GetResult();   // off the UI thread: its context would deadlock the wait
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(14), Background = Look.Bg };
        foreach (var section in report.Sections) stack.Children.Add(MainWindow.DiagCard(section));
        var window = new Window { Width = 760, SizeToContent = SizeToContent.Height, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontSize = 13, Background = Look.Bg, Content = new ScrollViewer { Content = stack } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            window.Height = Math.Min(2600, stack.DesiredSize.Height + 40); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using var bitmap = window.CaptureRenderedFrame() ?? throw new IOException("无法渲染预览。");
            bitmap.Save(path);
        }
        finally { window.Close(); }
        return 0;
    }
}
