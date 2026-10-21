namespace ArdUi;

static class AppIcon
{
    // Logo A: a cursor sending out two signal arcs, white on a terracotta tile; drawn at any size.
    public static byte[] Png(int size)
    {
        using var bitmap=new SKBitmap(new SKImageInfo(size,size,SKColorType.Bgra8888,SKAlphaType.Premul));using var canvas=new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        float S=size;
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
        return data.ToArray();
    }
    public static WindowIcon Create()=>new(new MemoryStream(Png(64),false));

    // A Windows .ico with several sizes (PNG frames), used as the program's icon in Explorer, the taskbar and the Start menu.
    public static void WriteIco(string path)
    {
        var sizes=new[]{16,24,32,48,64,128,256};
        var frames=sizes.Select(Png).ToArray();
        using var file=File.Create(path);using var writer=new BinaryWriter(file);
        writer.Write((short)0);writer.Write((short)1);writer.Write((short)sizes.Length);
        var offset=6+16*sizes.Length;
        for(var i=0;i<sizes.Length;i++)
        {
            writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);
            writer.Write((short)1);writer.Write((short)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;
        }
        foreach(var frame in frames)writer.Write(frame);
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
        foreach (var name in new[] { "SystemControlHighlightAccentBrush", "SystemControlBackgroundAccentBrush", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver" })
            Resources[name] = new SolidColorBrush(Color.Parse("#C6613F"));
        foreach (var name in new[] { "ThemeAccentBrush", "ThemeAccentBrush2", "ThemeAccentBrush3", "ThemeAccentBrush4" })
            Resources[name] = new SolidColorBrush(Color.Parse("#C6613F"));
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Loc.Init(Motion.Language); Loc.Install();
            StartupTrace.Mark("framework ready; creating main window");
            var window = new MainWindow();
            StartupTrace.Mark("main window constructed");
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--tray") == true) window.WindowState = WindowState.Minimized;   // autostart: begin in the tray
            // Closing or minimising the window must not end the app: it lives in the tray until "退出" is chosen.
            var show = new NativeMenuItem(Loc.T("显示 ArdUi")); show.Click += (_, _) => window.ShowFromTray();
            var exit = new NativeMenuItem(Loc.T("退出 ArdUi")); exit.Click += (_, _) => window.ExitApplication();
            var tray = new TrayIcon { Icon = AppIcon.Create(), ToolTipText = "ArdUi " + Program.Version, Menu = new NativeMenu { Items = { show, exit } } };
            tray.Clicked += (_, _) => window.ShowFromTray();
            window.ActivityChanged += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => tray.ToolTipText = Loc.T(text));
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            SingleInstance.Activated += () => Avalonia.Threading.Dispatcher.UIThread.Post(window.ShowFromTray);
            StartupTrace.Mark("tray icon created");
            desktop.Exit += (_, _) => { tray.IsVisible = false; tray.Dispose(); SingleInstance.Release(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
