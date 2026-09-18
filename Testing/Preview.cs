namespace ArdUi;

static class Preview
{
    public static int Render(string path)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().SetupWithoutStarting();
        var window = new MainWindow(true); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            if (window.Title?.Contains(Program.Version, StringComparison.Ordinal) != true || window.Content is not Grid)
                throw new IOException("无头界面结构不完整。");
            using var bitmap = window.CaptureRenderedFrame() ?? throw new IOException("无法渲染界面。");
            if (bitmap.PixelSize.Width < 600 || bitmap.PixelSize.Height < 360) throw new IOException("无头界面尺寸异常。");
            bitmap.Save(path); return 0;
        }
        finally { window.Close(); }
    }
}
