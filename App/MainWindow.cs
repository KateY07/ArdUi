namespace ArdUi;

sealed class MainWindow : Window
{
    static readonly IBrush Ink = Brush.Parse("#16243A"), Muted = Brush.Parse("#6A778A"),
        Online=Brush.Parse("#2E9D61"),Relay=Brush.Parse("#D09218"),Idle=Brush.Parse("#98A2B1");
    readonly TextBlock machine = new() { Text = "------", FontSize = 18, FontWeight = FontWeight.Bold, LetterSpacing = 2, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(8,0) };
    readonly TextBox endpoint = new() { IsReadOnly = true,FontFamily=new FontFamily("Consolas"),FontSize=10,Height=25,Width=210,VerticalContentAlignment=VerticalAlignment.Center };
    readonly TextBox remote = new() { Watermark = "ABC123", MaxLength = 6,Width=82 };
    readonly TextBox remotePassword = new() { Watermark = "123456",MaxLength=6,Width=82 };
    readonly CheckBox allow = new() { Content = "正在读取被控状态…", IsEnabled=false };
    readonly TextBox hostPassword = new() { Text="••••••",MaxLength=6,IsReadOnly=true,Width=70,FontFamily=new FontFamily("Consolas"),FontWeight=FontWeight.SemiBold };
    readonly StackPanel hostDetails = new() { IsVisible=false,Spacing=3 };
    readonly Expander identityExpander=new(){Header="ID",IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Left};
    readonly Border identityCard;
    readonly TextBlock status = new() { Text = "正在读取设备身份…",TextTrimming=TextTrimming.CharacterEllipsis,Foreground = Muted,FontSize=10 };
    readonly StackPanel activeIncoming = new() { Spacing = 6 };
    readonly StackPanel incomingArea = new() { Spacing = 6, IsVisible = false };
    readonly StackPanel pendingIncoming = new() { Spacing = 6 };
    readonly StackPanel controllers = new() { Spacing = 6 };
    readonly Expander controllersExpander = new() { Header = "可访问本机的设备管理", IsExpanded = false };
    readonly TextBlock networkLog = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontFamily = new FontFamily("Consolas"), FontSize = 9 };
    readonly TextBlock networkSummary = new() { Text="暂无网络事件",TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Muted,FontFamily=new FontFamily("Consolas"),FontSize=9,VerticalAlignment=VerticalAlignment.Center };
    readonly StackPanel devices = new() { Spacing = 4 };
    readonly TextBlock noDevices=new(){Text="尚未添加设备。",Foreground=Muted};
    readonly Dictionary<string,DeviceDisplay> deviceViews=new();
    readonly DispatcherTimer passwordTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly DispatcherTimer undoTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly ColumnDefinition dividerColumn,sideColumn;
    readonly GridSplitter splitter;
    readonly ScrollViewer sideScroll;
    readonly Border diagnostics,undoBar;
    readonly TextBlock undoText=new(){FontSize=10,VerticalAlignment=VerticalAlignment.Center};
    Engine? engine;
    DirectoryClient? directory;
    bool closing,closed,ready,passwordVisible;
    DateTimeOffset passwordVisibleUntil;
    DateTimeOffset undoUntil;
    Peer? undoPeer;
    double rememberedSideWidth=240;
    sealed record DeviceDisplay(Border Card,Ellipse Indicator,TextBlock Title,TextBlock Status);
    public MainWindow(bool preview = false)
    {
        Title = "ArdUi " + Program.Version; Width = 660; Height = 420; MinWidth = 600; MinHeight = 360;
        Icon=AppIcon.Create();
        Background = Brush.Parse("#F4F6FA"); FontFamily = new FontFamily("Microsoft YaHei UI"); Foreground = Ink; FontSize = 11;
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        header.Children.Add(new TextBlock { Text = "ArdUi " + Program.Version, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0,0,0,6) });
        Grid.SetColumn(allow,1);header.Children.Add(allow);root.Children.Add(header);
        var reveal=Button("查看 10 秒",RevealPassword);reveal.FontSize=10;reveal.Padding=new Thickness(7,3);
        var idDetails=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Children={endpoint,
            Button("复制码",async()=>await Clipboard!.SetTextAsync(directory?.Code??"")),Button("复制 ID",async()=>await Clipboard!.SetTextAsync(engine?.Id??""))}};
        identityExpander.Content=idDetails;identityExpander.FontSize=10;identityExpander.Padding=new Thickness(4,1);
        var access = new StackPanel { Orientation=Orientation.Horizontal,Spacing=6,Children={machine,new TextBlock{Text="首次密码",VerticalAlignment=VerticalAlignment.Center},hostPassword,reveal,identityExpander}};
        allow.PropertyChanged+=async(_,e)=>{if(e.Property==CheckBox.IsCheckedProperty && ready)await Guard(SaveAccess);};
        hostDetails.Children.Add(access);identityCard=Card(hostDetails);identityCard.IsVisible=false;Grid.SetRow(identityCard,1);root.Children.Add(identityCard);
        controllersExpander.Content=controllers;controllersExpander.IsVisible=false;
        var main = new Grid { Margin=new Thickness(0,7,0,0),ColumnDefinitions=new ColumnDefinitions("*,5,240") };
        dividerColumn=main.ColumnDefinitions[1];sideColumn=main.ColumnDefinitions[2];
        var addRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5,Children={new TextBlock{Text="6位ID:",VerticalAlignment=VerticalAlignment.Center},remote,
            new TextBlock{Text="6位密码:",VerticalAlignment=VerticalAlignment.Center},remotePassword,Button("连接",Connect)}};
        devices.Children.Add(noDevices);
        var undoButton=Button("撤销",UndoRemove);undoButton.FontSize=9;undoButton.Padding=new Thickness(5,1);
        undoBar=Card(new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,Children={undoText,undoButton}});
        undoBar.IsVisible=false;undoBar.Padding=new Thickness(6,3);undoBar.Background=Brush.Parse("#FFF8E8");
        var deviceArea=new StackPanel{Spacing=5,Children={addRow,Label("已获得授权连接（本机可主动控制）",13),undoBar,devices}};
        main.Children.Add(new ScrollViewer{Content=deviceArea});
        splitter=new GridSplitter{ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=Brush.Parse("#D8DEE8"),HorizontalAlignment=HorizontalAlignment.Stretch};
        splitter.PointerReleased+=(_,_)=>SaveSideWidth();
        Grid.SetColumn(splitter,1);main.Children.Add(splitter);
        incomingArea.Children.Add(Label("正在访问本机",14));incomingArea.Children.Add(activeIncoming);
        var side=new StackPanel{Spacing=6,Children={incomingArea,pendingIncoming,controllersExpander}};
        sideScroll=new ScrollViewer{Content=side};Grid.SetColumn(sideScroll,2);main.Children.Add(sideScroll);Grid.SetRow(main,2);root.Children.Add(main);
        var diagnosticButton=new Button{Content="诊断⌄",FontSize=9,Padding=new Thickness(5,1)};
        var exportDiagnostics=Button("导出诊断包",()=>
        {var path=Diagnostics.Export(Program.DataRoot,engine);Say("诊断包："+path);return Task.CompletedTask;});
        exportDiagnostics.FontSize=9;exportDiagnostics.Padding=new Thickness(5,1);
        var verboseDiagnostics=new CheckBox{Content="详细网络诊断（新连接生效）",FontSize=9};
        verboseDiagnostics.IsCheckedChanged+=(_,_)=>{if(engine!=null)engine.Settings.DetailedArdDiagnostics=verboseDiagnostics.IsChecked==true;};
        diagnostics=Card(new StackPanel{Spacing=3,Children={networkLog,verboseDiagnostics,exportDiagnostics}});diagnostics.IsVisible=false;diagnostics.Padding=new Thickness(5,3);
        diagnosticButton.Click+=(_,_)=>{diagnostics.IsVisible=!diagnostics.IsVisible;diagnosticButton.Content=diagnostics.IsVisible?"诊断⌃":"诊断⌄";};
        var networkRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};networkRow.Children.Add(networkSummary);
        Grid.SetColumn(diagnosticButton,1);networkRow.Children.Add(diagnosticButton);
        var footer=new StackPanel{Spacing=2,Margin=new Thickness(0,5,0,0),Children={status,networkRow,diagnostics}};
        Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;
        ToolTip.SetTip(endpoint,"本机永久 EndpointId，用于首次连接时核对身份。");
        Opened += async (_,_) =>
        {
            if(preview)return;
            await Guard(async () =>
            {
                var settings=JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Program.DataRoot,"config.json")),Wire.Json)!;
                engine=await Engine.Create(Program.DataRoot,settings);endpoint.Text=engine.Id;
                engine.Changed+=()=>Dispatcher.UIThread.Post(Refresh);
                engine.Notice+=message=>Dispatcher.UIThread.Post(()=>Say(message));
                directory=new DirectoryClient(engine);rememberedSideWidth=directory.SideWidth;
                directory.Confirm=async(pair,ct)=>await Dispatcher.UIThread.InvokeAsync(()=>ConfirmPair(pair,ct));
                directory.Changed+=()=>Dispatcher.UIThread.Post(Refresh);
                directory.Notice+=message=>Dispatcher.UIThread.Post(()=>Say(message));
                allow.IsChecked=directory.Enabled;hostDetails.IsVisible=directory.Enabled;identityCard.IsVisible=directory.Enabled;allow.Content="允许被控";allow.IsEnabled=true;ready=true;SetSideVisible(directory.Enabled);directory.Start();passwordTimer.Start();UpdatePassword();Say("正在连接可信服务器…");
            });
        };
        Closing+=async(_,e)=>
        {
            if(closed)return;e.Cancel=true;if(closing)return;closing=true;IsEnabled=false;
            try{passwordTimer.Stop();undoTimer.Stop();if(directory!=null)await directory.DisposeAsync();if(engine!=null)await engine.DisposeAsync();}
            finally{closed=true;Close();}
        };
        if(preview)
        {
            machine.Text="A7B2K9";endpoint.Text="5889f4c2e3c89af503a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1";
            allow.Content="允许被控";allow.IsEnabled=true;allow.IsChecked=false;Say("可信服务器 · https://f.visnova.cn/ · 本机被控功能已关闭");
            var sample=Device(new Peer{Id=new string('a',64),Code="C8M3P6",Name="办公电脑"});noDevices.IsVisible=false;devices.Children.Add(sample.Card);
            controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});SetSideVisible(false);
        }
        passwordTimer.Tick+=(_,_)=>UpdatePassword();
        undoTimer.Tick+=(_,_)=>UpdateUndo();
    }
    static TextBlock Label(string text,double size)=>new(){Text=text,FontSize=size,FontWeight=FontWeight.SemiBold};
    static Border Card(Control content)=>new(){Child=content,Padding=new Thickness(9),Background=Brushes.White,CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1),BorderBrush=Brush.Parse("#E4E9F0")};
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
    static string PeerLabel(Peer peer)=>string.IsNullOrWhiteSpace(peer.Name)||peer.Name==peer.Code?peer.Code:peer.Name+" · "+peer.Code;
    static IBrush ConnectionBrush(string text)
    {
        if(text.Contains("中继",StringComparison.OrdinalIgnoreCase)||text.Contains("relay",StringComparison.OrdinalIgnoreCase)||
           text.Contains("正在",StringComparison.Ordinal)||text.Contains("重连",StringComparison.Ordinal)||text.Contains("等待",StringComparison.Ordinal))return Relay;
        if(text.Contains("已连接",StringComparison.Ordinal)||text.Contains("直连",StringComparison.Ordinal))return Online;
        return Idle;
    }
    void SetSideVisible(bool visible)
    {
        sideScroll.IsVisible=visible;splitter.IsVisible=visible;
        dividerColumn.Width=new GridLength(visible?5:0);
        sideColumn.Width=new GridLength(visible?rememberedSideWidth:0);
    }
    void SaveSideWidth()
    {
        if(directory?.Enabled!=true||sideColumn.ActualWidth<180)return;
        rememberedSideWidth=Math.Clamp(sideColumn.ActualWidth,180,360);directory.SetSideWidth(rememberedSideWidth);
    }
    void Refresh()
    {
        if(engine==null)return;
        if(directory!=null && directory.Code.Length!=0 && machine.Text!=directory.Code)
        {machine.Text=directory.Code;Say("设备已注册 · "+engine.Settings.Server);}
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.ToArray();
        var structural=deviceViews.Count!=peers.Length||peers.Any(p=>!deviceViews.ContainsKey(p.Id));
        foreach(var stale in deviceViews.Keys.Except(peers.Select(p=>p.Id)).ToArray())deviceViews.Remove(stale);
        foreach(var peer in peers)if(!deviceViews.ContainsKey(peer.Id))deviceViews[peer.Id]=Device(peer);
        if(structural)
        {
            devices.Children.Clear();devices.Children.Add(noDevices);
            foreach(var peer in peers)devices.Children.Add(deviceViews[peer.Id].Card);
        }
        noDevices.IsVisible=peers.Length==0;
        foreach(var peer in peers)
        {
            var view=deviceViews[peer.Id];var title=PeerLabel(peer);
            var stateText=engine.Status.GetValueOrDefault(peer.Id,peer.AutoConnect?"已授权 · 正在连接":"已授权 · 已暂停");
            view.Title.Text=title;view.Status.Text=stateText;view.Indicator.Fill=ConnectionBrush(stateText);
            ToolTip.SetTip(view.Title,title);ToolTip.SetTip(view.Status,stateText);
        }
        activeIncoming.Children.Clear();
        foreach(var session in engine.Incoming.Values.Where(s=>s.Live))
        {
            var code=directory?.Controllers.FirstOrDefault(p=>p.Key==session.PeerId).Value?.Code??session.PeerId[..8];
            var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Children={new TextBlock{Text=code+" · "+engine.Status.GetValueOrDefault(session.PeerId,"正在控制"),VerticalAlignment=VerticalAlignment.Center},Button("断开",async()=>await engine.Disconnect(session.PeerId))}};
            activeIncoming.Children.Add(row);
        }
        incomingArea.IsVisible=activeIncoming.Children.Count!=0;
        controllers.Children.Clear();
        if(directory!=null)foreach(var pair in directory.Controllers)
        {
            var endpointId=pair.Key;var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            var controllerText=new TextBlock{Text=pair.Value.Code+" · "+endpointId[..8]+"…",VerticalAlignment=VerticalAlignment.Center};
            ToolTip.SetTip(controllerText,endpointId);row.Children.Add(controllerText);
            row.Children.Add(Button("撤销",async()=>await directory.Revoke(endpointId)));controllers.Children.Add(row);
        }
        if(controllers.Children.Count==0)controllers.Children.Add(new TextBlock{Text="尚无授权",Foreground=Muted});
        var logs=engine.Logs.TakeLast(3).ToArray();networkSummary.Text=logs.LastOrDefault()??"暂无网络事件";
        networkLog.Text=logs.Length==0?"暂无网络事件":string.Join(Environment.NewLine,logs);ToolTip.SetTip(networkSummary,networkSummary.Text);
        if(directory!=null)
        {
            if(allow.IsChecked!=directory.Enabled){ready=false;allow.IsChecked=directory.Enabled;ready=true;}
            hostDetails.IsVisible=directory.Enabled;
            identityCard.IsVisible=directory.Enabled;controllersExpander.IsVisible=directory.Enabled;SetSideVisible(directory.Enabled);
            if(!directory.Enabled){identityExpander.IsExpanded=false;passwordVisible=false;}UpdatePassword();
            Title="ArdUi "+Program.Version+(directory.Enabled&&directory.Code.Length!=0?" · "+directory.Code:"");
        }
    }
    async Task Connect()
    {
        if(directory==null)throw new InvalidOperationException("设备尚未就绪。");
        var password=remotePassword.Text??"";remotePassword.Text="";Say("正在核对对端身份并申请授权…");
        await directory.Connect(remote.Text??"",password);Say("已获授权，可以从设备列表打开远程桌面和文件共享。");
    }
    Task RevealPassword()
    {
        if(directory==null||!directory.Enabled)throw new InvalidOperationException("请先允许被控。");
        passwordVisible=true;passwordVisibleUntil=DateTimeOffset.UtcNow.AddSeconds(10);UpdatePassword();return Task.CompletedTask;
    }
    void UpdatePassword()
    {
        if(directory==null||!directory.Enabled){passwordVisible=false;hostPassword.Text="••••••";return;}
        if(passwordVisible&&DateTimeOffset.UtcNow>=passwordVisibleUntil)passwordVisible=false;
        var text=passwordVisible?directory.AccessPassword:"••••••";if(hostPassword.Text!=text)hostPassword.Text=text;
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
        var title=new TextBlock{Text=displayName,FontSize=11,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=110};
        var state=new TextBlock{Text=engine?.Status.GetValueOrDefault(peer.Id,"正在连接")??"已连接 · P2P 直连 / IPv4 · RTT 18.2 ms · ~86.4 Mbps",Foreground=Muted,FontSize=9,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(8,0,4,0)};
        var indicator=new Ellipse{Width=7,Height=7,Fill=ConnectionBrush(state.Text??""),Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};
        ToolTip.SetTip(title,displayName);ToolTip.SetTip(state,state.Text);
        var row=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,Auto,*,Auto"),MinHeight=26};
        row.Children.Add(indicator);Grid.SetColumn(title,1);row.Children.Add(title);Grid.SetColumn(state,2);row.Children.Add(state);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3,VerticalAlignment=VerticalAlignment.Center};
        Button Add(string caption,Func<Task> action)
        { var button=Button(caption,action);button.FontSize=9;button.Padding=new Thickness(5,1);button.MinHeight=22;actions.Children.Add(button);return button; }
        Add("桌面",async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var session=engine!.Outgoing[peer.Id];var bridge=session.Forward(3389);
            await Launch("mstsc.exe","/v:127.0.0.1:"+bridge.Port);
        });
        var frd=Add("FRD",async()=>
        {
            _=FrdRuntime.Resolve(engine!.Settings.FrdPath);
            await directory!.Connect(peer.Code,"",enrolling:false);
            await engine.Outgoing[peer.Id].OpenFrd(CancellationToken.None);
            Say("FRD 已启动，正在通过加密连接建立桌面。");
        });
        ToolTip.SetTip(frd,"打开 FRD 远程桌面；两端需安装支持 FRD 的新版 ArdUi。");
        Add("文件",async () =>
        {
            await directory!.Connect(peer.Code,"",enrolling:false);
            var share=await ShareDetails();
            var path=await WindowsShares.Map(engine!.Outgoing[peer.Id],share,CancellationToken.None);
            await Launch("explorer.exe",path);
        });
        var pause=Add("暂停",async()=>await directory!.Pause(peer));ToolTip.SetTip(pause,"断开当前会话并暂停自动重连；再次打开桌面或文件时恢复。");
        var moreActions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3};
        void More(string caption,Func<Task> action){var button=Button(caption,action);button.FontSize=9;button.Padding=new Thickness(5,1);button.MinHeight=22;moreActions.Children.Add(button);}
        More("EndpointId",async()=>{await Clipboard!.SetTextAsync(peer.Id);Say("完整 EndpointId 已复制："+peer.Id);});
        More("备注",async()=>{peer.Name=await AskText("设备备注",peer.Name);engine!.State.Save();});
        More("移除",async()=>await RemoveWithUndo(peer));
        var moreRow=new Border{Child=moreActions,IsVisible=false,Padding=new Thickness(0,3,0,0)};
        var more=new Button{Content="更多⌄",FontSize=9,Padding=new Thickness(5,1),MinHeight=22};
        more.Click+=(_,_)=>{moreRow.IsVisible=!moreRow.IsVisible;more.Content=moreRow.IsVisible?"收起⌃":"更多⌄";};
        actions.Children.Add(more);Grid.SetColumn(actions,3);row.Children.Add(actions);
        var stack=new StackPanel{Spacing=0,Children={row,moreRow}};var card=Card(stack);card.Padding=new Thickness(6,3);return new DeviceDisplay(card,indicator,title,state);
    }
    async Task RemoveWithUndo(Peer peer)
    {
        var snapshot=new Peer{Id=peer.Id,Address=peer.Address,Code=peer.Code,Name=peer.Name,Grant=peer.Grant,AutoConnect=peer.AutoConnect};
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
    async Task<bool> ConfirmPair(Pairing pair,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(pair.Incoming)
        {
            var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var row=new StackPanel{Spacing=6};
            row.Children.Add(new TextBlock{Text=$"{pair.Code} 请求获得本机控制权",FontWeight=FontWeight.SemiBold});
            row.Children.Add(new TextBox{Text=pair.Endpoint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=11});
            var pendingButtons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            var yes=new Button{Content="是，已核对 EndpointId"};var no=new Button{Content="否"};
            yes.Click+=(_,_)=>result.TrySetResult(true);no.Click+=(_,_)=>result.TrySetResult(false);
            pendingButtons.Children.Add(yes);pendingButtons.Children.Add(no);row.Children.Add(pendingButtons);
            var pendingCard=Card(row);pendingIncoming.Children.Add(pendingCard);
            using var pendingCancel=ct.Register(()=>result.TrySetCanceled(ct));
            try{return await result.Task;}finally{pendingIncoming.Children.Remove(pendingCard);}
        }
        var dialog=new Window { Title="首次连接：核对 EndpointId",Width=620,Height=420,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var content=new StackPanel { Margin=new Thickness(24),Spacing=14 };
        content.Children.Add(Label((pair.Incoming ? "设备请求访问本机：" : "即将连接设备：")+pair.Code,18));
        content.Children.Add(new TextBlock { Text="请通过电话、当面等独立渠道，与对方 ArdUi 中显示的完整 EndpointId 逐字核对。机器编号由服务器提供，不能代替公钥核对。",TextWrapping=TextWrapping.Wrap });
        content.Children.Add(new TextBox { Text=pair.Endpoint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=15 });
        content.Children.Add(new TextBlock { Text=pair.Incoming ? "访问密码已验证正确。只有确认后，才会允许这台设备访问 RDP / SMB。" : "确认后才会通过经过身份验证的加密连接发送访问密码。",TextWrapping=TextWrapping.Wrap });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
        var reject=new Button { Content="拒绝 / 未核对",IsDefault=true }; reject.Click+=(_,_)=>dialog.Close(false);
        var approved=false;
        var accept=new Button { Content="已独立核对一致，信任此设备" }; accept.Click+=(_,_)=>{approved=true;dialog.Close();};
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
    async Task<string> ShareDetails()
    {
        var dialog=new Window{Title="打开 SMB 文件共享",Width=460,Height=220,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var share=new TextBox{Watermark="共享名，例如 Documents",MaxLength=80};
        var open=new Button{Content="打开共享"};open.Click+=(_,_)=>dialog.Close(share.Text??"");
        dialog.Content=new StackPanel{Margin=new Thickness(24),Spacing=14,Children={share,new TextBlock{Text="ArdUi 仅透明转发 TCP 445。Windows 会使用当前账户、已有 SMB 凭据或其原生凭据机制完成访问。",TextWrapping=TextWrapping.Wrap},open}};
        return await dialog.ShowDialog<string?>(this) ?? throw new OperationCanceledException();
    }
    static Task Launch(string file,string argument)
    { var start=new ProcessStartInfo(file) { UseShellExecute=true };start.ArgumentList.Add(argument);Process.Start(start);return Task.CompletedTask; }
}
