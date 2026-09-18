namespace ArdUi;

static class AppIcon
{
    public static WindowIcon Create()
    {
        using var bitmap=new SKBitmap(64,64,true);using var canvas=new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var blue=new SKPaint{Color=new SKColor(42,96,210),IsAntialias=true};
        using var white=new SKPaint{Color=SKColors.White,IsAntialias=true,StrokeWidth=5,StrokeCap=SKStrokeCap.Round};
        canvas.DrawRoundRect(new SKRect(3,3,61,61),13,13,blue);
        canvas.DrawLine(19,42,32,19,white);canvas.DrawLine(32,19,47,42,white);canvas.DrawLine(19,42,47,42,white);
        canvas.DrawCircle(19,42,5,white);canvas.DrawCircle(32,19,5,white);canvas.DrawCircle(47,42,5,white);
        using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);
        return new WindowIcon(new MemoryStream(data.ToArray(),false));
    }
}

sealed class App : Application
{
    public override void Initialize()
    { Styles.Add(new SimpleTheme()); RequestedThemeVariant = ThemeVariant.Light; }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
