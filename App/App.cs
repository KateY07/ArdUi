namespace ArdUi;

static class AppIcon
{
    public static WindowIcon Create()
    {
        using var bitmap=new SKBitmap(new SKImageInfo(64,64,SKColorType.Bgra8888,SKAlphaType.Premul));using var canvas=new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        // Logo A: a cursor sending out two signal arcs, white on a terracotta tile.
        const float S=64;
        using var tile=new SKPaint{Color=new SKColor(0xC6,0x61,0x3F),IsAntialias=true};
        using var fill=new SKPaint{Color=SKColors.White,IsAntialias=true};
        using var edge=new SKPaint{Color=SKColors.White,IsAntialias=true,Style=SKPaintStyle.Stroke,StrokeWidth=S*.03f,StrokeJoin=SKStrokeJoin.Round};
        using var wave=new SKPaint{Color=SKColors.White,IsAntialias=true,Style=SKPaintStyle.Stroke,StrokeWidth=S*.055f,StrokeCap=SKStrokeCap.Round};
        canvas.DrawRoundRect(new SKRect(0,0,S,S),S*.23f,S*.23f,tile);
        using var cursor=new SKPath();
        cursor.MoveTo(S*.30f,S*.24f);cursor.LineTo(S*.30f,S*.74f);cursor.LineTo(S*.43f,S*.62f);cursor.LineTo(S*.52f,S*.82f);
        cursor.LineTo(S*.61f,S*.78f);cursor.LineTo(S*.52f,S*.58f);cursor.LineTo(S*.70f,S*.58f);cursor.Close();
        canvas.DrawPath(cursor,fill);canvas.DrawPath(cursor,edge);
        foreach(var r in new[]{.20f,.27f})
            canvas.DrawArc(new SKRect(S*(.34f-r),S*(.28f-r),S*(.34f+r),S*(.28f+r)),-82,62,false,wave);
        using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);
        return new WindowIcon(new MemoryStream(data.ToArray(),false));
    }
}

sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme()); Look.Apply(Styles); RequestedThemeVariant = ThemeVariant.Light;
        // Switches, selection and focus follow the accent colour of the palette.
        foreach (var name in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            Resources[name] = Color.Parse("#C6613F");
        // The Simple theme draws switches and selections from its own "ThemeAccent*" resources.
        foreach (var name in new[] { "ThemeAccentColor", "ThemeAccentColor2", "ThemeAccentColor3", "ThemeAccentColor4" })
            Resources[name] = Color.Parse("#C6613F");
        foreach (var name in new[] { "ThemeAccentBrush", "ThemeAccentBrush2", "ThemeAccentBrush3", "ThemeAccentBrush4" })
            Resources[name] = new SolidColorBrush(Color.Parse("#C6613F"));
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace.Mark("framework ready; creating main window");
            var window = new MainWindow();
            StartupTrace.Mark("main window constructed");
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--tray") == true) window.WindowState = WindowState.Minimized;   // autostart: begin in the tray
            // Closing or minimising the window must not end the app: it lives in the tray until "退出" is chosen.
            var show = new NativeMenuItem("显示 ArdUi"); show.Click += (_, _) => window.ShowFromTray();
            var exit = new NativeMenuItem("退出 ArdUi"); exit.Click += (_, _) => window.ExitApplication();
            var tray = new TrayIcon { Icon = AppIcon.Create(), ToolTipText = "ArdUi " + Program.Version, Menu = new NativeMenu { Items = { show, exit } } };
            tray.Clicked += (_, _) => window.ShowFromTray();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            SingleInstance.Activated += () => Avalonia.Threading.Dispatcher.UIThread.Post(window.ShowFromTray);
            StartupTrace.Mark("tray icon created");
            desktop.Exit += (_, _) => { tray.IsVisible = false; tray.Dispose(); SingleInstance.Release(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
