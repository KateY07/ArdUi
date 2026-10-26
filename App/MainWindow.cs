using Avalonia.Animation;

namespace ArdUi;

sealed partial class MainWindow : Window
{
    static readonly IBrush Ink = Look.Ink, Muted = Look.Muted, Online = Look.Online, Relay = Look.Relay, Idle = Look.Idle;
    readonly TextBlock machine = new() { Text = "······", FontSize = 17, FontWeight = FontWeight.SemiBold, LetterSpacing = 2, VerticalAlignment=VerticalAlignment.Center, Foreground = Look.Idle };
    readonly TextBox endpoint = new() { IsReadOnly = true,FontFamily=new FontFamily("Consolas"),FontSize=10,Height=25,Width=210,VerticalContentAlignment=VerticalAlignment.Center };
    readonly TextBox remote = new() { Watermark = "ABC123", MaxLength = 6,Width=82 };
    readonly TextBox remotePassword = new() { Watermark = "123456",MaxLength=6,Width=82 };
    readonly ToggleSwitch allow = new() { IsEnabled=false, OnContent = "正在读取被控状态…", OffContent = "正在读取被控状态…", VerticalAlignment = VerticalAlignment.Center };
    readonly Border identityCard;
    readonly TextBlock status = new() { Text = "正在启动…",TextTrimming=TextTrimming.CharacterEllipsis,Foreground = Muted,FontSize=12 };
    readonly StackPanel activeIncoming = new() { Spacing = 6 };
    readonly StackPanel incomingArea = new() { Spacing = 6, IsVisible = false };
    readonly StackPanel pendingIncoming = new() { Spacing = 6 };
    readonly StackPanel controllers = new() { Spacing = 6 };
    readonly TextBlock networkSummary = new() { Text="暂无网络事件",TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Muted,FontFamily=new FontFamily("Consolas"),FontSize=11,VerticalAlignment=VerticalAlignment.Center };
    readonly StackPanel devices = new() { Spacing = 4 };
    readonly TextBlock noDevices=new(){Text="尚未添加设备。",Foreground=Muted};
    readonly Dictionary<string,DeviceDisplay> deviceViews=new();
    readonly DispatcherTimer passwordTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    readonly DispatcherTimer undoTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly Border undoBar;
    readonly TextBlock undoText=new(){FontSize=10,VerticalAlignment=VerticalAlignment.Center};
    Engine? engine;
    DirectoryClient? directory;
    bool closing,closed,ready;
    DateTimeOffset undoUntil;
    Peer? undoPeer;
    sealed record DeviceDisplay(Border Card,Ellipse Indicator,TextBlock Title,TextBlock Status,Button Connect,Button[] Launchers,Button Pause,StackPanel Actions,Button Chat);
    readonly Dictionary<Ellipse,CancellationTokenSource> pulses=new();
    // A slow breathing dot while something is being established; static otherwise.
    void Pulse(Ellipse dot,bool on)
    {
        if(previewMode)return;
        if(on&&!pulses.ContainsKey(dot))
        {
            var stop=new CancellationTokenSource();pulses[dot]=stop;
            var animation=new Animation{Duration=TimeSpan.FromMilliseconds(1100),IterationCount=IterationCount.Infinite,Children=
            {
                new KeyFrame{Cue=new Cue(0),Setters={new Setter(OpacityProperty,1.0)}},
                new KeyFrame{Cue=new Cue(.5),Setters={new Setter(OpacityProperty,.3)}},
                new KeyFrame{Cue=new Cue(1),Setters={new Setter(OpacityProperty,1.0)}}
            }};
            _=animation.RunAsync(dot,stop.Token);
        }
        else if(!on&&pulses.Remove(dot,out var running)){running.Cancel();dot.Opacity=1;}
    }
    readonly bool previewMode;
    // Shown while the very first connection to the trusted server is being made (gives up after 12 s; the footer spinner continues).
    readonly Border connectingOverlay=new(){Background=Brush.Parse("#FCF7EFE8"),IsVisible=false,Transitions=new Avalonia.Animation.Transitions{new Avalonia.Animation.DoubleTransition{Property=OpacityProperty,Duration=TimeSpan.FromMilliseconds(500),Easing=new Avalonia.Animation.Easings.CubicEaseInOut()}}};
    readonly DateTime appStarted=DateTime.UtcNow;
    bool everOnline;
    void UpdateConnecting()
    {
        if(previewMode)return;
        if(directory?.Online==true)everOnline=true;
        titleLogo.Animate=directory==null||!directory.Online;
        var show=!everOnline&&DateTime.UtcNow-appStarted<TimeSpan.FromSeconds(12)&&!approvalOverlay.IsVisible;
        // Leaving the start-up screen is a slow fade into the home page, not a cut.
        if(show){connectingOverlay.IsVisible=true;connectingOverlay.Opacity=1;connectingOverlay.IsHitTestVisible=true;}
        else if(connectingOverlay.IsVisible&&connectingOverlay.Opacity>0)
        {
            connectingOverlay.IsHitTestVisible=false;connectingOverlay.Opacity=0;
            DispatcherTimer.RunOnce(()=>{if(connectingOverlay.Opacity==0)connectingOverlay.IsVisible=false;},TimeSpan.FromMilliseconds(600));
        }
    }
    // Incoming approval requests sit in the middle of the whole window on a dimmed backdrop and block the page behind.
    readonly Border approvalOverlay=new(){Background=Brush.Parse("#8C1F1E1D"),IsVisible=false};
    void UpdateApproval()=>approvalOverlay.IsVisible=pendingIncoming.Children.Count!=0;
    bool exiting,started;
    string listSignature="";
    readonly HashSet<string> connecting=new();
    // Closing or minimising only hides the window; the tray icon (or a second launch) brings it back.
    public void ShowFromTray()
    {
        if(!IsVisible)Show();
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Activate();
    }
    public void ExitApplication(){exiting=true;Close();}
    void HideToTray()
    {
        Hide();
        Say("ArdUi 仍在托盘中运行；点击托盘图标显示窗口，右键可退出。");
    }
    public MainWindow(bool preview = false)
    {
        previewMode=preview;
        if(!preview)LocalInput.Start();   // tells the person at this machine from a remote controller (see MainWindow.Person.cs)
        PropertyChanged+=(_,e)=>
        {
            if(e.Property==WindowStateProperty&&WindowState==WindowState.Minimized&&!previewMode&&!exiting)Dispatcher.UIThread.Post(HideToTray);
        };
        Title = "ArdUi " + Program.Version; Width = 400; Height = 650; MinWidth = 360; MinHeight = 340;
        if(preview&&int.TryParse(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_WIDTH"),out var previewWidth)){Width=previewWidth;MinWidth=previewWidth;}
        if(preview&&int.TryParse(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_HEIGHT"),out var previewHeight)){Height=previewHeight;MinHeight=previewHeight;}
        Icon=AppIcon.Create();
        // Borderless: the client area covers the title bar; the capsule bar below drags the window and carries settings / minimise / close.
        ExtendClientAreaToDecorationsHint=true;ExtendClientAreaChromeHints=Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint=-1;
        Background = Look.Bg; FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"); Foreground = Ink; FontSize = 12;
        // Real windows let the desktop show through (Mica, else acrylic); where that is unavailable the window stays solid.
        if(!previewMode)
        {
            TransparencyLevelHint=[WindowTransparencyLevel.Mica,WindowTransparencyLevel.AcrylicBlur,WindowTransparencyLevel.Blur];
            Background=Brushes.Transparent;
            PropertyChanged+=(_,e)=>{if(e.Property==ActualTransparencyLevelProperty)Background=ActualTransparencyLevel==WindowTransparencyLevel.None?Look.Bg:Brushes.Transparent;};
        }
        // Title bar: one status capsule (logo, device code, connection state) on the left; window controls as glass discs on the right.
        machine.FontFamily=new FontFamily("Consolas");machine.FontSize=14;machine.Foreground=Ink;machine.LetterSpacing=2;
        var capsule=Chrome.Capsule(new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,Margin=new Thickness(12,2),Children={titleLogo,machine,Chrome.Divider(),netDot,netText}});
        capsule.HorizontalAlignment=HorizontalAlignment.Left;capsule.Cursor=new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        ToolTip.SetTip(capsule,"本机设备 ID；点击复制");
        capsule.PointerPressed+=async(_,e)=>
        {
            e.Handled=true;if(machine.Text.Length!=6||!machine.Text.All(char.IsLetterOrDigit))return;
            await Clipboard!.SetTextAsync(machine.Text);Toast("已复制本机设备 ID");
        };
        var settingsButton=Chrome.IconButton(Chrome.SettingsPath,15);ToolTip.SetTip(settingsButton,"设置");settingsButton.Click+=(_,_)=>ShowSettings(!settingsOpen);
        var closeWindow=Chrome.IconButton(Look.CrossPath,12);ToolTip.SetTip(closeWindow,"关闭到托盘（程序继续在托盘中运行）");closeWindow.Click+=(_,_)=>Close();
        var windowButtons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Children={Chrome.Disc(settingsButton),Chrome.Disc(closeWindow)}};
        var bar=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(14,8,10,0),Height=40,VerticalAlignment=VerticalAlignment.Top,Background=Brushes.Transparent};
        bar.Children.Add(capsule);windowButtons.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(windowButtons,1);bar.Children.Add(windowButtons);
        bar.PointerPressed+=(_,e)=>{if(!e.Handled&&e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)BeginMoveDrag(e);};
        bar.DoubleTapped+=(_,e)=>{if(!e.Handled)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;};
        // Share (invite) sits next to the "allow control" switch and only exists while control is allowed.
        identityCard=new Border{IsVisible=false};
        allow.PropertyChanged+=async(_,e)=>{if(e.Property==Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty && ready){if(allow.IsChecked==true&&!Person("开启被控")){ready=false;allow.IsChecked=false;ready=true;return;}await Guard(SaveAccess);}};
        controllers.Margin=new Thickness(2,2,2,2);
        devices.Children.Add(noDevices);
        var undoButton=Button("撤销",UndoRemove);undoButton.Content=Look.IconText(Look.UndoPath,"撤销");
        undoBar=Card(new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,Children={undoText,undoButton}});
        undoBar.IsVisible=false;undoBar.Padding=new Thickness(6,3);undoBar.Background=Brush.Parse("#FFF8E8");
        incomingArea.Children.Add(Label("正在访问本机",12,true));incomingArea.Children.Add(activeIncoming);
        var pageArea=new Grid{Margin=new Thickness(0,48,0,0),Children={BuildHome(),BuildSettings()}};
        pendingIncoming.HorizontalAlignment=HorizontalAlignment.Center;pendingIncoming.VerticalAlignment=VerticalAlignment.Center;
        pendingIncoming.MaxWidth=500;pendingIncoming.Margin=new Thickness(28);approvalOverlay.Child=pendingIncoming;
        BuildInvite();
        // First start: the logo breathes while the network comes up.
        connectingOverlay.Child=new StackPanel{Spacing=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
            Children={new Logo(72){Animate=true,HorizontalAlignment=HorizontalAlignment.Center},T_("正在连接网络…")}};
        connectingOverlay.IsVisible=!previewMode;
        titleBar=bar;SetupAttention(!previewMode&&Motion.Enabled);
        Content=new Grid{Children={Chrome.Backdrop(!previewMode),scrim,pageArea,connectingOverlay,bar,inviteOverlay,BuildChat(),BuildCallBanner(),BuildUpdate(),approvalOverlay,toastHost}};
        ToolTip.SetTip(endpoint,"本机永久 EndpointId，用于首次连接时核对身份。");
        Opened += async (_,_) =>
        {
            // Showing the window again after it was hidden in the tray raises Opened again; start the engine only once.
            StartupTrace.Mark("window opened");StartupTrace.Watch();
            if(preview||started)return;started=true;
            if(Autostart.Enabled){try{Autostart.Set(true);}catch{}}   // keep the entry pointing at the running copy
            await Guard(async () =>
            {
                var settings=JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Program.DataRoot,"config.json")),Wire.Json)!;
                FileLog.Start(Program.DataRoot);
                StartupTrace.Mark("config read; creating engine");
                engine=await Task.Run(()=>Engine.Create(Program.DataRoot,settings));endpoint.Text=engine.Id;
                StartupTrace.Mark("engine ready");
                engine.Changed+=RequestRefresh;
                engine.Notice+=message=>Dispatcher.UIThread.Post(()=>Say(message));
                directory=await Task.Run(()=>new DirectoryClient(engine!));
                StartupTrace.Mark("directory client constructed");
                directory.Confirm=async(pair,ct)=>await Dispatcher.UIThread.InvokeAsync(()=>ConfirmPair(pair,ct));
                directory.ConfirmIncoming=async(pair,ct)=>await Dispatcher.UIThread.InvokeAsync(()=>ConfirmIncoming(pair,ct));
                directory.ConfirmSession=async(pair,ct)=>await Dispatcher.UIThread.InvokeAsync(()=>ConfirmSession(pair,ct));
                directory.Changed+=RequestRefresh;
                directory.Notice+=message=>Dispatcher.UIThread.Post(()=>Say(message));
                StartIsland();StartUpdater(settings);
                allow.IsChecked=directory.Enabled;identityCard.IsVisible=directory.Enabled;allow.OnContent=allow.OffContent="允许被控";allow.IsEnabled=true;ready=true;directory.Start();passwordTimer.Start();UpdatePassword();Say("正在连接网络…");
            });
        };
        Closing+=async(_,e)=>
        {
            if(closed)return;
            if(!previewMode&&!exiting&&!closing&&e.CloseReason==WindowCloseReason.WindowClosing)
            {e.Cancel=true;HideToTray();return;}
            e.Cancel=true;if(closing)return;closing=true;IsEnabled=false;
            try{island?.Shutdown();LocalInput.Stop();passwordTimer.Stop();undoTimer.Stop();if(updater!=null)await updater.DisposeAsync();if(directory!=null)await directory.DisposeAsync();if(engine!=null)await engine.DisposeAsync();}
            finally{closed=true;Close();}
        };
        if(preview)
        {
            machine.Text="A7B2K9";netDot.Fill=Online;netText.Text="在线";endpoint.Text="5889f4c2e3c89af503a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1";
            allow.OnContent=allow.OffContent="允许被控";allow.IsEnabled=true;allow.IsChecked=false;Say("就绪");
            noDevices.IsVisible=false;
            // Representative rows for the headless preview: connected, online, connecting, offline, and a group.
            var recents=new List<(Peer,bool?)>();var samples=new List<Peer>();previewPeers=()=>samples.ToArray();
            void Sample(string id,string code,string name,string computer,string text,bool live,bool reachable)
            {
                var sample=new Peer{Id=new string(id[0],64),Code=code,Name=name,ComputerName=computer};
                if(recents.Count<5)recents.Add((sample,reachable));
                samples.Add(sample);
                if(live)
                {
                    var card=Device(sample);SetStatus(card.Status,text);Dot(card.Indicator,text);card.Connect.IsVisible=false;card.Pause.IsVisible=true;
                    foreach(var launcher in card.Launchers)launcher.IsVisible=true;card.Chat.IsVisible=true;
                    livePanel.Children.Add(card.Card);UpdateSessionsVisible();
                    if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_MORE")=="1")card.Actions.Children.OfType<Button>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                }
                var row=Device(sample);
                SetStatus(row.Status,text);Dot(row.Indicator,text);row.Pause.IsVisible=live;
                ApplyDensity(row,!reachable&&!live);row.Connect.IsVisible=!live&&reachable;row.Connect.Opacity=reachable?1:.4;
                if(text.StartsWith("正在",StringComparison.Ordinal))row.Connect.Content="连接中…";
                foreach(var launcher in row.Launchers)launcher.IsVisible=live;
                devices.Children.Add(row.Card);
            }
            Sample("a","C8M3P6","办公电脑","DESKTOP-OFFICE","IPv6 · ≥167 Mbps · 6 ms",true,true);
            Sample("b","K4DD0B","","SY0812A","在线 · 最近连接 3 小时前",false,true);
            Sample("c","T7Q2LM","家里 NAS","HOME-NAS","正在连接…",false,true);
            devices.Children.Add(GroupHeader("展厅"));
            Sample("d","3MKXWS","展厅内1","EXHIBIT-01","在线 · 最近连接 2 天前",false,true);
            offlineExpanded=Environment.GetEnvironmentVariable("ARDUI_PREVIEW_EXPAND")=="1";
            devices.Children.Add(OfflineHeader("离线设备 · 2"));
            if(offlineExpanded)
            {
                Sample("e","12F0JT","展厅内2","EXHIBIT-02","离线 · 最近连接 2 天前",false,false);
                Sample("f","N861VC","","","离线 · 从未连接",false,false);
            }
            controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});
            FillRecents(recents);
            if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_CHAT")=="1")
            {
                // render-only: a conversation with text, a picture and a file in both directions
                var folder=Path.Combine(Path.GetTempPath(),"ardui-chat-preview");Directory.CreateDirectory(folder);
                var picture=Path.Combine(folder,"截图.png");
                using(var bitmap=new SkiaSharp.SKBitmap(240,140)){using var canvas=new SkiaSharp.SKCanvas(bitmap);
                    using var paint=new SkiaSharp.SKPaint{Shader=SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(0,0),new SkiaSharp.SKPoint(240,140),[new SkiaSharp.SKColor(0xE0,0x7A,0x52),new SkiaSharp.SKColor(0x8D,0xB6,0xE8)],SkiaSharp.SKShaderTileMode.Clamp)};
                    canvas.DrawRect(0,0,240,140,paint);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var data=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);File.WriteAllBytes(picture,data.ToArray());}
                var sample=new ChatChannel(folder,()=>null);
                var now=DateTimeOffset.Now;
                sample.Messages.Add(new ChatMessage("1",false,ChatKind.Text,"你好，我这边看到远程桌面卡了一下，能帮我看看吗？",null,0,null,now.AddMinutes(-6)));
                sample.Messages.Add(new ChatMessage("2",true,ChatKind.Text,"好的，我先看一下网络状况。",null,0,null,now.AddMinutes(-5)));
                sample.Messages.Add(new ChatMessage("3",false,ChatKind.Image,"","截图.png",1,picture,now.AddMinutes(-4)));
                sample.Messages.Add(new ChatMessage("4",true,ChatKind.File,"","诊断结果.zip",3_400_000,null,now.AddMinutes(-2)));
                openChat=sample;chatTitle.Text="与 K4DD0B 对话";
                foreach(var message in sample.Messages)chatList.Children.Add(Bubble(message));
                chatOverlay.IsVisible=true;
            }
            if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_USE")=="1")
            {
                hostList.Children.Add(HostCard("Q8X2ND","对方正在连接…",true,false,null));
                hostList.Children.Add(HostCard("7TLM4P","FRD · IPv6 · 18 Mbps · 9 ms",false,true,"x",true,true));
                hostPanel.IsVisible=true;UpdateSessionsVisible();netText.Text="被控中 · 1";netDot.Fill=Look.Accent;
            }
            switch(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_FOCUS"))
            {
                case "typing":remote.Text="K4";OnCodeChanged();ApplyAttention(Attention.Typing);break;
                case "armed":remote.Text="K4DD0B";OnCodeChanged();ApplyAttention(Attention.Typing);break;
                case "hover":ApplyAttention(Attention.Hover);break;
            }
            switch(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_FLOW"))
            {
                case "startup":connectingOverlay.IsVisible=true;break;
                case var flow when flow!=null:
                {
                    var card=new ConnectCard("办公电脑 · C8M3P6",false);pendingPanel.Children.Add(card);UpdateSessionsVisible();
                    switch(flow)
                    {
                        case "progress":card.SetProgress(ConnectFlow.Text(ConnectStage.Signal));break;
                        case "password":_=card.AskPassword(null);break;
                        case "nopermission":_=card.AskPassword(ConnectFlow.Classify(new IOException(ConnectFlow.RejectedText)).Title+"："+ConnectFlow.Classify(new IOException(ConnectFlow.RejectedText)).Hint);break;
                        case "failure":_=card.ShowFailure(ConnectFlow.Classify(new IOException("对端离线或未开放远程访问。")),true);break;
                        case "security":_=card.ShowFailure(ConnectFlow.Classify(new InvalidDataException("该机器的 EndpointId 已改变！已阻止连接")),false);break;
                        case "success":_=card.Succeed();break;
                    }
                    break;
                }
            }
            if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_TOAST")=="1")Toast("已复制本机设备 ID");
            var settingsTab=Environment.GetEnvironmentVariable("ARDUI_PREVIEW_PAGE");
            if(settingsTab!=null&&int.TryParse(settingsTab,out var tabIndex)){ShowSettings(true);selectSettingsTab?.Invoke(tabIndex);}
            if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_HOST")=="1")
            {
                // Render-only: the window as it looks while this machine accepts remote control.
                allow.IsChecked=true;identityCard.IsVisible=true;
                if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_INVITE")=="1")OpenInvite();
                controllers.Children.Clear();
                foreach(var (code,perms) in new[]{("7TLM4P",Perm.Frd),("Q8X2ND",Perm.All),("K4DD0B",Perm.Frd|Perm.Chat)})
                {
                    var revoke=new Button{Content="撤销",MinHeight=26,Padding=new Thickness(10,3),VerticalAlignment=VerticalAlignment.Center};
                    var head=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
                    head.Children.Add(new StackPanel{Children={new TextBlock{Text=code,FontWeight=FontWeight.SemiBold,FontSize=14},new TextBlock{Text="5889f4c2…",Foreground=Muted,FontSize=11,FontFamily=new FontFamily("Consolas")}}});
                    Grid.SetColumn(revoke,1);head.Children.Add(revoke);
                    controllers.Children.Add(Card(new StackPanel{Spacing=8,Children={head,new PermissionPicker(perms,false)}}));
                }
                var liveRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
                liveRow.Children.Add(new StackPanel{VerticalAlignment=VerticalAlignment.Center,Children={new TextBlock{Text="7TLM4P  · 仅此一次",FontWeight=FontWeight.SemiBold,FontSize=14},new TextBlock{Text="FRD · IPv6 · 18 Mbps · 9 ms",Foreground=Muted,FontSize=12}}});
                var dropPreview=new Button{Content=Look.Icon(Look.CrossPath,10,Brushes.White,2.4),Width=26,Height=26};dropPreview.Classes.Add("circlex");Grid.SetColumn(dropPreview,1);liveRow.Children.Add(dropPreview);
                activeIncoming.Children.Add(Card(liveRow));incomingArea.IsVisible=true;
                if(Environment.GetEnvironmentVariable("ARDUI_PREVIEW_REQUEST")=="1")
                    _=ConfirmIncoming(new Pairing("Q8X2ND",new string('a',64),true),CancellationToken.None);   // the real approval card
            }
        }
        passwordTimer.Tick+=(_,_)=>{UpdatePassword();UpdateConnecting();};
        undoTimer.Tick+=(_,_)=>UpdateUndo();
    }
    // The log is a plain text file; open it in whatever editor Windows associates with .txt.
    Task OpenLogFile()
    {
        var path=FileLog.EnsureCurrent();
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
        Say("已在默认编辑器中打开日志。");
        return Task.CompletedTask;
    }
    static TextBlock T_(string text)=>new(){Text=text,FontSize=14,Foreground=Muted,HorizontalAlignment=HorizontalAlignment.Center};
    static TextBlock DeviceListLabel()
    {
        var label=Label("设备列表",12,true);
        ToolTip.SetTip(label,"已获得授权、可以由本机主动控制的设备");
        return label;
    }
    static TextBlock Label(string text,double size,bool section=false)=>new(){Text=text,FontSize=size,FontWeight=FontWeight.SemiBold,
        Foreground=section?Muted:Ink,Margin=section?new Thickness(2,8,0,0):new Thickness()};
    // Layered white cards on a pale background: a soft shadow instead of a hard outline.
    static Border Card(Control content)=>new(){Child=content,Padding=new Thickness(12),Background=Look.Card,CornerRadius=new CornerRadius(12),
        BoxShadow=Look.CardShadow,BorderThickness=new Thickness(1),BorderBrush=Look.CardEdge,
        Transitions=Look.BrushFade(Border.BackgroundProperty)};
    Button Button(string text,Func<Task> action)
    {
        var button=new Button{Content=text,HorizontalAlignment=HorizontalAlignment.Stretch};
        button.Click+=async(_,_)=>
        {button.IsEnabled=false;try{await Guard(action);}finally{button.IsEnabled=true;}};return button;
    }
    async Task Guard(Func<Task> action)
    {
        if(closing)return;
        try{await action();}catch(OperationCanceledException){Say("操作已取消或等待确认超时。");}catch(Exception ex){Say(ex.Message);}
        finally{Refresh();}
    }
    void Say(string text){status.Text=text;ToolTip.SetTip(status,text);}
    // Own note first, then the name the controlled computer reported, then the machine code.
    static string PeerLabel(Peer peer)
    {
        var hasNote=!string.IsNullOrWhiteSpace(peer.Name)&&peer.Name!=peer.Code;
        var primary=hasNote?peer.Name:peer.ComputerName.Length!=0?peer.ComputerName:peer.Code;
        var parts=new List<string>{primary};
        if(hasNote&&peer.ComputerName.Length!=0&&peer.ComputerName!=peer.Name)parts.Add(peer.ComputerName);
        if(primary!=peer.Code)parts.Add(peer.Code);
        return string.Join(" · ",parts);
    }
    // Own note or computer name in bold; the other name and the machine code follow in a lighter tone.
    static void FillTitle(TextBlock title,Peer peer)
    {
        var hasNote=!string.IsNullOrWhiteSpace(peer.Name)&&peer.Name!=peer.Code;
        var primary=hasNote?peer.Name:peer.ComputerName.Length!=0?peer.ComputerName:peer.Code;
        var key=primary+"|"+(hasNote&&peer.ComputerName.Length!=0&&peer.ComputerName!=peer.Name?peer.ComputerName:"")+"|"+peer.Code;
        if(title.Tag as string==key)return;
        title.Tag=key;title.Inlines!.Clear();
        title.Inlines.Add(new Avalonia.Controls.Documents.Run{[Loc.KeepRunProperty]=true,Text=primary});
        void Quiet(string text)=>title.Inlines.Add(new Avalonia.Controls.Documents.Run{[Loc.KeepRunProperty]=true,Text="  "+text,FontWeight=FontWeight.Normal,FontSize=12,Foreground=Muted});
        if(hasNote&&peer.ComputerName.Length!=0&&peer.ComputerName!=peer.Name)Quiet(peer.ComputerName);
        if(primary!=peer.Code)Quiet(peer.Code);
    }
    void UpdateChatButton(DeviceDisplay view,Peer peer,bool live)
    {
        // the conversation needs the host's permission for messages or files (the same permissions apply to both sides of the session)
        var session=engine==null?null:engine.Outgoing.TryGetValue(peer.Id,out var outgoing)?outgoing:engine.Incoming.GetValueOrDefault(peer.Id);
        view.Chat.IsVisible=live&&(session==null||(session.Perms&(Perm.Chat|Perm.Files))!=Perm.None);
        if(!view.Chat.IsVisible||engine==null)return;
        var unread=engine.ChatFor(peer.Id).Unread>0;
        if((bool)(view.Chat.Tag??false)!=unread){view.Chat.Tag=unread;view.Chat.Content=ChatBadge(unread);}
        WatchChat(peer.Id,PeerLabel(peer).Split(" · ")[0]);
    }
    // FRD appears only when the controlled machine allowed it.
    static void ShowLaunchers(DeviceDisplay view,bool live,Perm perms)
    {
        view.Launchers[0].IsVisible=live&&perms.HasFlag(Perm.Frd);
    }
    // Status text of a device row. For a live session the path kind (IPv6 / IPv4 / Relay) is highlighted, then speed and latency.
    static string StatusOf(TextBlock t)=>t.Tag as string??t.Text??"";
    static void SetStatus(TextBlock t,string text)
    {
        if(t.Tag as string==text)return;
        t.Tag=text;t.Text=null;t.Inlines!.Clear();   // drop the runs of the previous state
        var split=text.IndexOf(" · ",StringComparison.Ordinal);
        if(Engine.IsLiveText(text)&&split>0&&!text.StartsWith("已连接",StringComparison.Ordinal))
        {
            var kind=text[..split];
            t.Inlines.Add(new Avalonia.Controls.Documents.Run(kind){FontWeight=FontWeight.SemiBold,Foreground=kind=="Relay"?Brush.Parse("#B87A1F"):Look.OnlineDeep});
            t.Inlines.Add(new Avalonia.Controls.Documents.Run(text[split..]));
        }
        else t.Text=text;
    }
    sealed record Slot(string? Header,Peer? Peer,bool Toggle=false);
    bool offlineExpanded;
    // Most recently used first; groups keep their members together, ordered by their most recent member.
    // Devices known to be offline (and not in use) are tucked into a collapsed section at the end of the list.
    bool IsParked(Peer peer)=>directory?.IsOnline(peer.Id)==false&&!connecting.Contains(peer.Id)
        &&!(engine?.Outgoing.TryGetValue(peer.Id,out var session)==true&&session.Live);
    List<Slot> Arrange(Peer[] all)
    {
        var result=new List<Slot>();
        var parked=all.Where(IsParked).ToArray();
        var peers=all.Except(parked).ToArray();
        var groups=peers.GroupBy(p=>(p.Group??"").Trim()).ToList();
        var hasGroups=groups.Any(g=>g.Key.Length!=0);
        foreach(var group in groups.OrderBy(g=>g.Key.Length==0?1:0).ThenByDescending(g=>g.Max(p=>p.LastConnectedAt)).ThenBy(g=>g.Key,StringComparer.CurrentCultureIgnoreCase))
        {
            if(hasGroups)result.Add(new Slot(group.Key.Length==0?"未分组":group.Key,null));
            foreach(var peer in group.OrderByDescending(p=>p.LastConnectedAt).ThenBy(p=>string.IsNullOrWhiteSpace(p.Name)?p.Code:p.Name,StringComparer.CurrentCultureIgnoreCase))
                result.Add(new Slot(null,peer));
        }
        if(parked.Length!=0)
        {
            result.Add(new Slot($"离线设备 · {parked.Length}",null,true));
            if(offlineExpanded)
                foreach(var peer in parked.OrderByDescending(p=>p.LastConnectedAt).ThenBy(p=>string.IsNullOrWhiteSpace(p.Name)?p.Code:p.Name,StringComparer.CurrentCultureIgnoreCase))
                    result.Add(new Slot(null,peer));
        }
        return result;
    }
    Control OfflineHeader(string text)
    {
        var label=new TextBlock{Text=(offlineExpanded?"▾ ":"▸ ")+text,FontSize=12,FontWeight=FontWeight.SemiBold,Foreground=Muted,Background=Brushes.Transparent,
            Margin=new Thickness(2,12,0,2),Cursor=new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)};
        ToolTip.SetTip(label,"点击展开或折叠离线设备");
        label.PointerPressed+=(_,e)=>{offlineExpanded=!offlineExpanded;Refresh();e.Handled=true;};
        return label;
    }
    // Offline devices (in the collapsed section) use a compact single-line style.
    static void ApplyDensity(DeviceDisplay view,bool compact)
    {
        view.Title.FontSize=compact?13:14;view.Status.FontSize=compact?11:12;
        view.Card.Padding=compact?new Thickness(14,3):new Thickness(14,6);
        var row=(Grid)((StackPanel)view.Card.Child!).Children[0];row.MinHeight=compact?34:52;
    }
    static TextBlock GroupHeader(string name)=>new(){[Loc.KeepProperty]=name!="未分组",Text=name,FontSize=12,FontWeight=FontWeight.SemiBold,Foreground=Muted,Margin=new Thickness(2,10,0,0)};
    static string Ago(long unixSeconds)
    {
        var span=DateTimeOffset.UtcNow-DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if(span<TimeSpan.FromMinutes(1))return "刚刚";
        if(span<TimeSpan.FromHours(1))return $"{(int)span.TotalMinutes} 分钟前";
        if(span<TimeSpan.FromDays(1))return $"{(int)span.TotalHours} 小时前";
        if(span<TimeSpan.FromDays(30))return $"{(int)span.TotalDays} 天前";
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd");
    }
    static string PresenceText(Peer peer,bool? online)
    {
        var state=online switch{true=>"在线",false=>"离线",_=>"状态未知"};
        return peer.LastConnectedAt>0?$"{state} · 最近连接 {Ago(peer.LastConnectedAt)}":$"{state} · 从未连接";
    }
    async Task Connecting(Peer peer,Func<Task> work)
    {
        connecting.Add(peer.Id);Refresh();
        try{await work();}finally{connecting.Remove(peer.Id);}
    }
    // Four distinct states by colour, size and shape, not just a hue.
    static void Dot(Ellipse dot,string text)
    {
        if(Engine.IsLiveText(text)){dot.Width=dot.Height=12;dot.Fill=Look.OnlineDeep;dot.Stroke=Look.OnlineHalo;dot.StrokeThickness=3;}
        else if(text.StartsWith("在线",StringComparison.Ordinal)){dot.Width=dot.Height=11;dot.Fill=Brushes.Transparent;dot.Stroke=Online;dot.StrokeThickness=2;}
        else if(text.StartsWith("正在",StringComparison.Ordinal)||text.Contains("重连")||text.Contains("等待")||text.Contains("中继")){dot.Width=dot.Height=10;dot.Fill=Relay;dot.Stroke=Brushes.Transparent;dot.StrokeThickness=0;}
        else{dot.Width=dot.Height=8;dot.Fill=Idle;dot.Stroke=Brushes.Transparent;dot.StrokeThickness=0;}
    }
    static IBrush ConnectionBrush(string text)
    {
        if(text.StartsWith("在线",StringComparison.Ordinal))return Online;
        if(text.Contains("中继",StringComparison.OrdinalIgnoreCase)||text.Contains("relay",StringComparison.OrdinalIgnoreCase)||
           text.Contains("正在",StringComparison.Ordinal)||text.Contains("重连",StringComparison.Ordinal)||text.Contains("等待",StringComparison.Ordinal))return Relay;
        if(Engine.IsLiveText(text))return Online;
        return Idle;
    }
    // Many events in a row cost one refresh: the UI thread is never flooded.
    int refreshPending;
    void RequestRefresh()
    {
        if(Interlocked.Exchange(ref refreshPending,1)!=0)return;
        Dispatcher.UIThread.Post(()=>{Volatile.Write(ref refreshPending,0);Refresh();},DispatcherPriority.Background);
    }
    void Refresh()
    {
        if(engine==null)return;
        UpdateCalls();
        
        if(directory!=null && directory.Code.Length!=0 && machine.Text!=directory.Code)
        {machine.Text=directory.Code;Say("就绪");engine.Events.Add("INFO","系统","设备已注册，编号 "+directory.Code+"，服务器 "+engine.Settings.Server);}
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.ToArray();
        foreach(var stale in deviceViews.Keys.Except(peers.Select(p=>p.Id)).ToArray()){deviceViews.Remove(stale);homeViews.Remove(stale);}
        foreach(var peer in peers){if(!deviceViews.ContainsKey(peer.Id))deviceViews[peer.Id]=Device(peer);if(!homeViews.ContainsKey(peer.Id))homeViews[peer.Id]=Device(peer);}
        var layout=Arrange(peers);
        var signature=string.Join("|",layout.Select(slot=>slot.Header!=null?"#"+slot.Header+(slot.Toggle?(offlineExpanded?"^":"v"):""):slot.Peer!.Id));
        if(signature!=listSignature)
        {
            listSignature=signature;
            devices.Children.Clear();devices.Children.Add(noDevices);
            foreach(var slot in layout)
                devices.Children.Add(slot.Header==null?deviceViews[slot.Peer!.Id].Card:slot.Toggle?OfflineHeader(slot.Header):GroupHeader(slot.Header));
        }
        noDevices.IsVisible=peers.Length==0;
        foreach(var peer in peers)
        {
            var view=deviceViews[peer.Id];var title=PeerLabel(peer);
            var live=engine.Outgoing.TryGetValue(peer.Id,out var session)&&session.Live;
            var online=directory?.IsOnline(peer.Id);
            // A device is only contacted when the user opens it; otherwise the row shows what the server knows.
            var stateText=connecting.Contains(peer.Id)?"正在连接…"
                :live||directory?.IsReconnecting(peer.Id)==true?engine.Status.GetValueOrDefault(peer.Id,"已连接")
                :PresenceText(peer,online);
            FillTitle(view.Title,peer);SetStatus(view.Status,stateText);Dot(view.Indicator,stateText);
            var reachable=online!=false||live;
            var busy=connecting.Contains(peer.Id);
            var parked=IsParked(peer);ApplyDensity(view,parked);
            view.Connect.IsVisible=!live&&!parked;view.Connect.Content=busy?"连接中…":"连接";
            view.Connect.Opacity=reachable?1:.4;view.Connect.IsHitTestVisible=reachable&&!busy;
            ShowLaunchers(view,live,engine.Outgoing.TryGetValue(peer.Id,out var allowedSession)?allowedSession.Perms:Perm.All);UpdateChatButton(view,peer,live);
            view.Pause.IsVisible=live||directory?.IsReconnecting(peer.Id)==true;
            Pulse(view.Indicator,stateText.StartsWith("正在",StringComparison.Ordinal));
            ToolTip.SetTip(view.Title,title+"\n"+peer.Id);ToolTip.SetTip(view.Status,reachable?stateText:stateText+" · 对方在线后才能连接");
        }
        // Home: the session cards of connected devices mirror their rows in the device list; recents are the latest used devices.
        var liveIds=new List<string>();
        foreach(var peer in peers)
        {
            var dv=deviceViews[peer.Id];var hv=homeViews[peer.Id];
            var live=engine.Outgoing.TryGetValue(peer.Id,out var liveSession)&&liveSession.Live;
            if(live&&!pendingCards.ContainsKey(peer.Code))liveIds.Add(peer.Id);   // its placeholder is still on screen
            FillTitle(hv.Title,peer);SetStatus(hv.Status,StatusOf(dv.Status));Dot(hv.Indicator,StatusOf(dv.Status));
            hv.Connect.IsVisible=false;
            ShowLaunchers(hv,live,liveSession?.Perms??Perm.All);UpdateChatButton(hv,peer,live);
            hv.Pause.IsVisible=dv.Pause.IsVisible;
        }
        var sessionKey=string.Join("|",liveIds);
        if(sessionKey!=sessionSignature)
        {
            sessionSignature=sessionKey;
            livePanel.Children.Clear();
            foreach(var id in liveIds)livePanel.Children.Add(homeViews[id].Card);
            UpdateSessionsVisible();
        }
        var recent=peers.OrderByDescending(p=>p.LastConnectedAt).Take(5).Select(p=>(Peer:p,Online:directory?.IsOnline(p.Id))).ToList();
        var recentKey=string.Join("|",recent.Select(r=>r.Peer.Id+r.Online+PeerLabel(r.Peer)));
        if(recentKey!=recentSignature){recentSignature=recentKey;FillRecents(recent);}
        activeIncoming.Children.Clear();
        foreach(var session in engine.Incoming.Values.Where(s=>s.Live))
        {
            var code=directory?.Controllers.FirstOrDefault(p=>p.Key==session.PeerId).Value?.Code??session.PeerId[..8];
            var info=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Children=
            {
                new TextBlock{Text=code+(session.OnceGranted?"  · 仅此一次":""),FontWeight=FontWeight.SemiBold,FontSize=14},
                new TextBlock{Text=Permissions.Describe(session.Perms)+" · "+engine.Status.GetValueOrDefault(session.PeerId,"正在控制"),Foreground=Muted,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis},
            }};
            var drop=new Button{Content=Look.Icon(Look.CrossPath,10,Brushes.White,2.4),Width=26,Height=26,VerticalAlignment=VerticalAlignment.Center};drop.Classes.Add("circlex");
            ToolTip.SetTip(drop,"断开对方");var sessionPeer=session.PeerId;drop.Click+=async(_,_)=>await Guard(async()=>await engine.Disconnect(sessionPeer));
            var line=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};line.Children.Add(info);Grid.SetColumn(drop,1);line.Children.Add(drop);
            activeIncoming.Children.Add(Card(line));
        }
        incomingArea.IsVisible=activeIncoming.Children.Count!=0;
        controllers.Children.Clear();
        if(directory!=null)foreach(var pair in directory.Controllers)
        {
            var endpointId=pair.Key;
            var revoke=new Button{Content="撤销",MinHeight=26,Padding=new Thickness(10,3),VerticalAlignment=VerticalAlignment.Center};
            revoke.Click+=async(_,_)=>await Guard(async()=>await directory.Revoke(endpointId));
            var head=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
            var headText=new StackPanel{Children={new TextBlock{Text=pair.Value.Code,FontWeight=FontWeight.SemiBold,FontSize=14},new TextBlock{Text=endpointId[..8]+"…",Foreground=Muted,FontSize=11,FontFamily=new FontFamily("Consolas")}}};
            ToolTip.SetTip(headText,endpointId);head.Children.Add(headText);Grid.SetColumn(revoke,1);head.Children.Add(revoke);
            var picker=new PermissionPicker(Permissions.Parse(pair.Value.Perms),false);
            picker.Changed+=async value=>{if(!Person("修改权限")){RequestRefresh();return;}await Guard(()=>directory.SetControllerPerms(endpointId,value));};
            controllers.Children.Add(Card(new StackPanel{Spacing=8,Children={head,picker}}));
        }
        if(controllers.Children.Count==0)controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});
        if(directory!=null)
        {
            if(allow.IsChecked!=directory.Enabled){ready=false;allow.IsChecked=directory.Enabled;ready=true;}
            identityCard.IsVisible=directory.Enabled;
            if(!directory.Enabled)CloseInvite();
            // The invitation worked: a new device was authorised while the overlay was open.
            if(inviteOverlay.IsVisible&&(directory.Controllers.Length>inviteControllers||engine.Incoming.Count>inviteIncoming)){CloseInvite();Say("邀请成功：新设备已获得授权。");}
            netDot.Fill=directory.Online?Online:directory.Outdated!=null?Look.Accent:Relay;
            netText.Text=directory.Online?"在线":directory.Outdated!=null?"版本过旧，请升级 ArdUi":"正在连接网络…";
            ToolTip.SetTip(netText,directory.Outdated);
            UpdateUpdate();
            RefreshHost();
            UpdatePassword();UpdateConnecting();
            Title="ArdUi "+Program.Version+(directory.Enabled&&directory.Code.Length!=0?" · "+directory.Code:"");
        }
    }
    async Task Connect()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        var password=remotePassword.Text??"";remotePassword.Text="";Say("正在核对对端身份并申请授权…");
        await directory.Connect(remote.Text??"",password);Say("已获授权，可以从设备列表打开远程桌面和文件共享。");
    }
    // The invitation overlay: first-time password with an authenticator-style ring, the device ID always visible for checking.
    readonly Border inviteOverlay=new(){Background=Brush.Parse("#8C1F1E1D"),IsVisible=false};
    readonly TextBlock invitePassword=new(){Text="••••••",FontFamily=new FontFamily("Consolas"),FontSize=28,FontWeight=FontWeight.SemiBold,LetterSpacing=4,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock inviteSeconds=new(){FontSize=15,FontWeight=FontWeight.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    readonly Avalonia.Controls.Shapes.Arc inviteArc=new(){StrokeThickness=6,StartAngle=-90,SweepAngle=360,Margin=new Thickness(3),StrokeLineCap=PenLineCap.Round};
    readonly Button inviteRefresh=new(){Content="显示新密码",IsVisible=false,HorizontalAlignment=HorizontalAlignment.Left};
    readonly TextBlock inviteCode=new(){FontSize=24,FontWeight=FontWeight.SemiBold,LetterSpacing=3,FontFamily=new FontFamily("Consolas"),VerticalAlignment=VerticalAlignment.Center};
    long inviteMinute;int inviteControllers,inviteIncoming;
    Border BuildInvite()
    {
        inviteArc.Stroke=Look.Accent;
        var ring=new Grid{Width=64,Height=64,Children={new Ellipse{Stroke=Look.Ghost,StrokeThickness=6,Margin=new Thickness(3)},inviteArc,inviteSeconds}};
        inviteRefresh.Classes.Add("primary");
        inviteRefresh.Click+=(_,_)=>{if(!Person("显示新密码"))return;inviteMinute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;UpdatePassword();};
        var passwordBlock=new StackPanel{Spacing=4,VerticalAlignment=VerticalAlignment.Center,Children={new TextBlock{Text="连接密码",Foreground=Muted,FontSize=12},invitePassword,inviteRefresh}};
        var passwordRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=14,Children={ring,passwordBlock}};
        endpoint.Width=double.NaN;endpoint.Height=double.NaN;endpoint.FontSize=12;endpoint.TextWrapping=TextWrapping.Wrap;endpoint.MinHeight=0;
        var copy=Button("一键复制",async()=>{if(!Person("复制邀请"))return;await Clipboard!.SetTextAsync(InviteText());Toast("已复制邀请信息");});copy.Content=Look.IconText(Look.CopyPath,"一键复制");copy.HorizontalAlignment=HorizontalAlignment.Left;
        var card=Card(WithCloseIcon(new StackPanel{Spacing=14,Children=
        {
            new TextBlock{Text="邀请设备",FontSize=17,FontWeight=FontWeight.SemiBold},
            new TextBlock{Text="请对方在 ArdUi 中填入下面的设备 ID 和连接密码，并在连接时核对 EndpointId。密码到期会自动隐藏，邀请成功后本窗口自动关闭。",Foreground=Muted,TextWrapping=TextWrapping.Wrap},
            passwordRow,
            new StackPanel{Spacing=4,Children={new TextBlock{Text="设备 ID",Foreground=Muted,FontSize=12},inviteCode}},
            new StackPanel{Spacing=4,Children={new TextBlock{Text="EndpointId（供对方核对）",Foreground=Muted,FontSize=12},endpoint}},
            copy
        }},CloseInvite));
        // The card follows the window width (down to the minimum) and scrolls when the window is short.
        card.Background=Look.CardSolid;card.BorderBrush=Look.Line;card.Padding=new Thickness(20);card.MaxWidth=460;card.Margin=new Thickness(14);card.HorizontalAlignment=HorizontalAlignment.Stretch;card.VerticalAlignment=VerticalAlignment.Center;
        inviteOverlay.Child=new ScrollViewer{VerticalAlignment=VerticalAlignment.Center,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,Content=card};return card;
    }
    void OpenInvite()
    {
        if(!previewMode&&(directory==null||!directory.Enabled||directory.Code.Length==0))throw new InvalidOperationException("请先允许被控。");
        inviteMinute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;inviteControllers=directory?.Controllers.Length??0;inviteIncoming=engine?.Incoming.Count??0;
        inviteCode.Text=directory?.Code??machine.Text;inviteOverlay.IsVisible=true;UpdatePassword();
    }
    // Every overlay closes the same way: a quiet cross in its top-right corner.
    static Grid WithCloseIcon(Control content,Action close)
    {
        var button=new Button{Content=Look.Icon(Look.CrossPath,12),Width=30,Height=30,MinHeight=30,Padding=new Thickness(0),
            HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,-6,-6,0)};
        button.Classes.Add("quiet");ToolTip.SetTip(button,"关闭");button.Click+=(_,_)=>close();
        return new Grid{Children={content,button}};
    }
    // One button copies the whole invitation: device ID, the password while it is still shown, and the EndpointId.
    string InviteText()
    {
        var lines=new List<string>{"ArdUi 邀请","设备 ID："+inviteCode.Text};
        if(!inviteRefresh.IsVisible&&invitePassword.Text!.All(char.IsDigit))lines.Add("连接密码："+invitePassword.Text);
        lines.Add("EndpointId："+(engine?.Id??endpoint.Text));
        return string.Join("\n",lines);
    }
    void CloseInvite(){inviteOverlay.IsVisible=false;invitePassword.Text="••••••";}
    // Shows the password of the minute the invitation was opened in; the ring runs down to the end of that minute
    // (the server also accepts the previous minute, so the recipient keeps a short grace period).
    void UpdatePassword()
    {
        if(!inviteOverlay.IsVisible)return;
        if(!previewMode&&(directory==null||!directory.Enabled)){CloseInvite();return;}
        var remaining=previewMode?37.0:(inviteMinute+1)*60-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
        inviteSeconds.Text=Math.Max(0,(int)Math.Ceiling(remaining)).ToString();
        if(remaining<=0)
        {
            invitePassword.Text="••••••";inviteArc.SweepAngle=0.01;inviteRefresh.IsVisible=true;return;
        }
        invitePassword.Text=directory?.AccessPasswordFor(inviteMinute)??"384291";
        inviteArc.SweepAngle=Math.Max(0.01,360*remaining/60);inviteRefresh.IsVisible=false;
    }
    async Task SaveAccess()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        await directory.SetAccess(allow.IsChecked==true);
        Say(directory.Enabled?"已自动保存：允许远程访问。":"已自动保存：关闭远程访问并断开被控会话。");
    }
    DeviceDisplay Device(Peer peer)
    {
        var displayName=PeerLabel(peer);
        var title=new TextBlock{HorizontalAlignment=HorizontalAlignment.Left,FontSize=14,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=420};
        var state=new TextBlock{Text=engine?.Status.GetValueOrDefault(peer.Id,"正在连接")??"IPv4 · 86 Mbps · 18 ms",Foreground=Muted,FontSize=12,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0)};
        var indicator=new Ellipse{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Transitions=Look.BrushFade(Ellipse.FillProperty)};Dot(indicator,state.Text??"");
        FillTitle(title,peer);ToolTip.SetTip(title,displayName);ToolTip.SetTip(state,state.Text);
        var row=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto"),RowDefinitions=new RowDefinitions("*,Auto"),MinHeight=52};
        row.Children.Add(new Grid{Width=18,Height=18,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center,Children={indicator}});var text=new StackPanel{Spacing=1,VerticalAlignment=VerticalAlignment.Center,Children={title,state}};Grid.SetColumn(text,1);row.Children.Add(text);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,VerticalAlignment=VerticalAlignment.Center};
        Button Add(string caption,Func<Task> action)
        { var button=Button(caption,action);actions.Children.Add(button);return button; }
        var connect=Add("连接",()=>StartConnect(peer.Code));
        connect.Classes.Add("primary");
        var launchers=new List<Button>();
        var frd=Add("远程桌面",()=>Connecting(peer,async()=>
        {
            _=FrdRuntime.Resolve(engine!.Settings.FrdPath);
            await directory!.Connect(peer.Code,"",enrolling:false);
            if(!engine.Outgoing[peer.Id].Perms.HasFlag(Perm.Frd))throw new IOException("被控端未允许 FRD。");
            await engine.Outgoing[peer.Id].OpenFrd(CancellationToken.None);
            Say("FRD 已启动，正在通过加密连接建立桌面。");
        }));
        launchers.Add(frd);
        // the conversation with this device (only while connected); a red dot marks unread messages
        var chat=new Button{Width=36,Height=30,MinHeight=30,Padding=new Thickness(0),IsVisible=false,Tag=false};chat.Content=ChatBadge(false);ToolTip.SetTip(chat,"对话：文字、图片、文件");
        chat.Click+=(_,_)=>OpenChat(peer.Id,PeerLabel(peer).Split(" · ")[0]);actions.Children.Add(chat);
        var pause=Add("断开",async()=>await directory!.Pause(peer));pause.IsVisible=false;ToolTip.SetTip(pause,"断开连接（停止自动恢复；点击“连接”可重新连接）");
        pause.Classes.Add("circlex");pause.Content=Look.Icon(Look.CrossPath,10,Brushes.White,2.4);pause.Width=26;pause.Height=26;pause.HorizontalAlignment=HorizontalAlignment.Center;pause.VerticalAlignment=VerticalAlignment.Center;
        var moreActions=new WrapPanel{Orientation=Orientation.Horizontal};
        void More(string caption,Func<Task> action){var button=Button(caption,action);button.Margin=new Thickness(0,0,6,6);moreActions.Children.Add(button);}
        More("远程桌面（RDP）",()=>Connecting(peer,async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var session=engine!.Outgoing[peer.Id];
            if(!session.Perms.HasFlag(Perm.Rdp))throw new IOException("被控端未允许 RDP。");
            var bridge=session.Forward(3389);
            await Launch("mstsc.exe","/v:127.0.0.1:"+bridge.Port);
        }));
        More("EndpointId",async()=>{await Clipboard!.SetTextAsync(peer.Id);Toast("已复制 EndpointId");});
        More("备注",async()=>{peer.Name=await AskText("设备备注",peer.Name);engine!.State.Save();});
        More("分组",async()=>{peer.Group=(await AskText("分组（留空为未分组）",peer.Group)).Trim();engine!.State.Save();});
        More("移除",async()=>await RemoveWithUndo(peer));
        var moreRow=new Border{Child=moreActions,IsVisible=false,Padding=new Thickness(22,8,0,2)};
        var more=new Button{Content="⋯",FontSize=16,Padding=new Thickness(10,0)};more.Classes.Add("quiet");
        ToolTip.SetTip(more,"更多：RDP、备注、分组、复制 EndpointId、移除");
        more.Click+=(_,_)=>
        {
            moreRow.IsVisible=!moreRow.IsVisible;
            // expanded: an up arrow collapses the extra actions again
            if(moreRow.IsVisible){more.Content=Look.Icon(Look.ChevronUpPath,14);ToolTip.SetTip(more,"收起");}
            else{more.Content="⋯";more.FontSize=16;ToolTip.SetTip(more,"更多：RDP、备注、分组、复制 EndpointId、移除");}
        };
        actions.Children.Add(more);Grid.SetColumn(actions,2);row.Children.Add(actions);
        var stack=new StackPanel{Spacing=0,Children={row,moreRow}};var card=Card(stack);card.Padding=new Thickness(14,6);
        card.PointerEntered+=(_,_)=>card.Background=Look.CardHover;card.PointerExited+=(_,_)=>card.Background=Look.Card;
        Look.IconOnly(launchers[0],Look.MonitorPath,"远程桌面（FRD）；两端需安装支持 FRD 的新版 ArdUi");
        foreach(var launcher in launchers)launcher.IsVisible=false;
        return new DeviceDisplay(card,indicator,title,state,connect,launchers.ToArray(),pause,actions,chat);
    }
    async Task RemoveWithUndo(Peer peer)
    {
        var snapshot=new Peer{Id=peer.Id,Address=peer.Address,Code=peer.Code,Name=peer.Name,Grant=peer.Grant,AutoConnect=peer.AutoConnect,Group=peer.Group,ComputerName=peer.ComputerName,LastConnectedAt=peer.LastConnectedAt};
        await directory!.RemoveLocal(peer);undoPeer=snapshot;undoUntil=DateTimeOffset.UtcNow.AddSeconds(8);undoBar.IsVisible=true;undoTimer.Start();UpdateUndo();
        Say($"已移除 {peer.Code}，可在 8 秒内撤销。");
    }
    Task UndoRemove()
    {
        var snapshot=undoPeer??throw new InvalidOperationException("撤销期限已结束。");
        undoPeer=null;undoTimer.Stop();undoBar.IsVisible=false;directory!.RestoreLocal(snapshot);Say("已撤销移除："+snapshot.Code);return Task.CompletedTask;
    }
    void UpdateUndo()
    {
        if(undoPeer==null)return;
        var seconds=(int)Math.Ceiling((undoUntil-DateTimeOffset.UtcNow).TotalSeconds);
        if(seconds<=0){undoPeer=null;undoTimer.Stop();undoBar.IsVisible=false;return;}
        undoText.Text=$"已移除 {undoPeer.Code} · {seconds} 秒内可撤销";
    }
    // The approval card for a device that asks for control of this machine: only this once (the main choice), accept and
    // trust, or refuse - with the permissions to give (FRD by default).
    async Task<Decision> ConfirmIncoming(Pairing pair,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result=new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!previewMode)ShowFromTray();   // the approval prompt lives in this window, which may be hidden in the tray
        var picker=new PermissionPicker(Permissions.Default,true);
        var once=new Button{Content="仅接受这一次"};once.Classes.Add("primary");
        var trust=new Button{Content="接受并信任"};
        var no=new Button{Content="拒绝"};
        once.Click+=(_,_)=>{if(Person("仅接受这一次"))result.TrySetResult(new Decision(true,true,picker.Value));};
        trust.Click+=(_,_)=>{if(Person("接受并信任"))result.TrySetResult(new Decision(true,false,picker.Value));};
        no.Click+=(_,_)=>result.TrySetResult(Decision.Rejected);
        var row=new StackPanel{Spacing=10};
        row.Children.Add(new TextBlock{Text=$"{pair.Code} 请求获得本机控制权",FontSize=17,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,24,0)});
        row.Children.Add(new TextBlock{Text="请通过电话或当面，与对方 ArdUi 中显示的完整 EndpointId 逐字核对。",Foreground=Muted,TextWrapping=TextWrapping.Wrap});
        row.Children.Add(new TextBox{Text=pair.Endpoint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=11});
        row.Children.Add(new TextBlock{Text="允许对方使用",Foreground=Muted,FontSize=12,FontWeight=FontWeight.SemiBold});
        row.Children.Add(picker);
        row.Children.Add(new TextBlock{Text="默认只允许 FRD。“仅接受这一次”不保存授权，断开后对方需要重新申请；“接受并信任”会保存，之后可在 设置 → 被控与授权 中修改。",Foreground=Muted,FontSize=11,TextWrapping=TextWrapping.Wrap});
        row.Children.Add(new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Children={once,trust,no}});
        var pendingCard=Card(WithCloseIcon(row,()=>result.TrySetResult(Decision.Rejected)));
        pendingCard.Background=Look.CardSolid;pendingCard.BorderBrush=Look.Line;pendingCard.Padding=new Thickness(20);pendingIncoming.Children.Add(pendingCard);UpdateApproval();
        using var pendingCancel=ct.Register(()=>result.TrySetCanceled(ct));
        try{return await result.Task;}finally{pendingIncoming.Children.Remove(pendingCard);UpdateApproval();}
    }
    // "Ask me before every session": even a trusted device is let in only after a yes from the person at this machine.
    async Task<bool> ConfirmSession(Pairing pair,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!previewMode)ShowFromTray();   // the prompt lives in this window, which may be hidden in the tray
        var allow=new Button{Content="允许连接"};allow.Classes.Add("primary");
        var deny=new Button{Content="拒绝"};
        allow.Click+=(_,_)=>{if(Person("允许连接"))result.TrySetResult(true);};
        deny.Click+=(_,_)=>result.TrySetResult(false);
        var row=new StackPanel{Spacing=10};
        row.Children.Add(new TextBlock{Text=$"{pair.Code} 请求连接本机",FontSize=17,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,24,0)});
        row.Children.Add(new TextBlock{Text="已信任的设备也需要你点“允许”后才能连接（可在 设置 → 被控与授权 中关闭）。",Foreground=Muted,TextWrapping=TextWrapping.Wrap});
        row.Children.Add(new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Children={allow,deny}});
        var pendingCard=Card(WithCloseIcon(row,()=>result.TrySetResult(false)));
        pendingCard.Background=Look.CardSolid;pendingCard.BorderBrush=Look.Line;pendingCard.Padding=new Thickness(20);pendingIncoming.Children.Add(pendingCard);UpdateApproval();
        using var pendingCancel=ct.Register(()=>result.TrySetCanceled(ct));
        try{return await result.Task;}finally{pendingIncoming.Children.Remove(pendingCard);UpdateApproval();}
    }
    async Task<bool> ConfirmPair(Pairing pair,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dialog=new Window { Title="首次连接：核对 EndpointId",Width=620,Height=420,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var content=new StackPanel { Margin=new Thickness(24),Spacing=14 };
        content.Children.Add(Label((pair.Incoming ? "设备请求访问本机：" : "即将连接设备：")+pair.Code,18));
        content.Children.Add(new TextBlock { Text="请通过电话、当面等独立渠道，与对方 ArdUi 中显示的完整 EndpointId 逐字核对。机器编号由服务器提供，不能代替公钥核对。",TextWrapping=TextWrapping.Wrap });
        content.Children.Add(new TextBox { Text=pair.Endpoint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=15 });
        content.Children.Add(new TextBlock { Text=pair.Incoming ? "访问密码已验证正确。只有确认后，才会允许这台设备访问本机。" : "确认后才会通过经过身份验证的加密连接发送访问密码。",TextWrapping=TextWrapping.Wrap });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
        var reject=new Button { Content="拒绝 / 未核对",IsDefault=true }; reject.Click+=(_,_)=>dialog.Close(false);
        var approved=false;
        var accept=new Button { Content="已独立核对一致，信任此设备" }; accept.Click+=(_,_)=>{if(!Person("信任此设备"))return;approved=true;dialog.Close();};
        buttons.Children.Add(reject);buttons.Children.Add(accept);content.Children.Add(buttons);dialog.Content=content;
        using var cancel=ct.Register(()=>Dispatcher.UIThread.Post(()=>dialog.Close(false)));
        var decision=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed+=(_,_)=>decision.TrySetResult(approved&&!ct.IsCancellationRequested&&!closing);
        dialog.Show(this);
        return await decision.Task;
    }
    async Task<string> AskPassword(string title)
    {
        var dialog=new Window { Title=title,Width=430,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var input=new TextBox { PasswordChar='●',MaxLength=128,Watermark=title };
        var button=new Button { Content="确认" };button.Click+=(_,_)=>dialog.Close(input.Text ?? "");
        dialog.Content=new StackPanel { Margin=new Thickness(24),Spacing=16,Children={input,button} };
        return await dialog.ShowDialog<string?>(this) ?? throw new OperationCanceledException();
    }
    async Task<string> AskText(string title,string initial="")
    {
        var dialog=new Window {Title=title,Width=430,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var input=new TextBox {Text=initial,MaxLength=80};var button=new Button{Content="保存"};button.Click+=(_,_)=>dialog.Close(input.Text ?? "");
        dialog.Content=new StackPanel{Margin=new Thickness(24),Spacing=16,Children={input,button}};
        return await dialog.ShowDialog<string?>(this) ?? throw new OperationCanceledException();
    }
    static Task Launch(string file,string argument)
    { var start=new ProcessStartInfo(file) { UseShellExecute=true };start.ArgumentList.Add(argument);Process.Start(start);return Task.CompletedTask; }
}
