namespace ArdUi;

sealed class Session : IAsyncDisposable
{
    public string PeerId { get; }
    public string Address { get; }
    readonly int basePort;
    public int LocalPort => !Host&&Overlay!=null?Overlay.Port:basePort;
    public byte[] Capability { get; }
    public int[] TcpPorts { get; }
    public int[] UdpPorts { get; }
    public bool Host { get; }
    public Child Process { get; set; }
    public OverlaySession? Overlay { get; set; }
    public event Action? OverlayReady;
    readonly string[] ardArguments;
    readonly TaskCompletionSource completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion=>completed.Task;
    public bool HasOverlay=>Overlay!=null;
    public CancellationToken Token => stop.Token;
    public bool Live => disposed == 0 && (Overlay!=null?!Overlay.Token.IsCancellationRequested&&(!Overlay.Expired||Process.ProgressAge<90000):!Process.Exited.IsCompleted);
    public string Network => Overlay?.Network??Process.Network;
    // The ArdFerry ard last named in its log (relay=https://s-….ard-relay.invalid/), for FerryMonitor's report.
    public string? RelayName { get; set; }
    public double? TcpRtt { get; set; }
    public double? UdpRtt { get; set; }
    // The latency shown for the session: what the end-to-end layer measures on the path in use (probed every 500 ms anyway, so
    // showing it costs nothing), or this side's own 5-second probe where there is no such layer.
    public double? LatencyMs => Overlay?.Link(Overlay.Selected)?.RecentRttMs ?? UdpRtt;
    public double? BandwidthMbps { get; set; }
    public double? BandwidthLowerBoundMbps { get; set; }
    public ForwardHub? Hub { get; }
    // Controller side: what the controlled machine allowed (from its admission reply). Host side: what this controller was granted.
    public Perm Perms { get => Host ? (gateway?.Perms ?? Perm.All) : perms; set => perms = value; }
    Perm perms = Perm.All;
    // The authorisation lasts only for this session.
    public bool Once { get; set; }
    public bool OnceGranted => gateway?.Once == true;
    readonly Gateway? gateway;
    // This PC's screen is in use through this session right now: an FRD host is up for it, or an RDP connection is open (the same fact that starts the audit recording).
    public bool ScreenInUse => gateway?.ScreenActive == true;
    readonly string directory;
    readonly CancellationTokenSource stop = new();
    Task metrics = Task.CompletedTask;
    readonly SemaphoreSlim frdGate=new(1);
    FrdHosted? frdHost;
    FrdOpened? frdClient;
    string frdPath="";
    int frdPort;
    internal bool FrdLive=>frdHost?.Live==true||frdClient?.Live==true;
    public event Action<string>? Notice;
    void Notify(string message)
    {try{Notice?.Invoke(message);}catch(Exception ex){Diagnostics.Log("session-notice",ex.Message);}}
    public void ReportFrdError(string message)
    {Diagnostics.Log("frd-error",message);Notify("FRD："+message);}
    public void CloseFrdPort(int port)=>Interlocked.CompareExchange(ref frdPort,0,port);
    public Task<TcpClient> OpenFrdTcp(int port,CancellationToken ct)
    {
        if(Host||port!=Volatile.Read(ref frdPort)||port==0)throw new IOException("FRD 连接未获授权。");
        return OpenTcpCore(port,true,ct);
    }
    public ChatChannel? Chat { get; private set; }
    // What the other side may ask of this one over the control channel, with the side that answers and the permission it needs. The
    // permissions are the ones the host granted for this session, on both sides: a host that does not allow files neither takes nor sends them.
    internal ControlRegistry? Controls { get; private set; }
    public void ConfigureControls(string frd, ChatChannel chat, CallChannel call, Func<string, (string Root, string Actor, Action<object> Audit)>? filesRoot = null)
    {
        frdPath = frd; Chat = chat; this.filesRoot = filesRoot;
        var link = new SessionCallLink(this);
        var registry = new ControlRegistry(Host, () => Perms)
            .Add("bye", ControlRole.Either, Perm.None, (_, data, _) => HandleBye(data))
            .Add("frd-start", ControlRole.Host, Perm.Frd, HandleFrd)
            .Add("frd-stop", ControlRole.Host, Perm.None, HandleFrd)
            .Add("chat-text", ControlRole.Either, Perm.Chat, chat.Handle)
            .Add("chat-nudge", ControlRole.Either, Perm.Chat, chat.Handle)
            .Add("chat-read", ControlRole.Either, Perm.Chat, chat.Handle)
            .Add("chat-file-begin", ControlRole.Either, Perm.Chat, chat.Handle)
            .Add("chat-file-chunk", ControlRole.Either, Perm.Chat, chat.Handle)
            .Add("chat-file-end", ControlRole.Either, Perm.Chat, chat.Handle)
            .Add("chat-file-cancel", ControlRole.Either, Perm.None, chat.Handle)   // stopping a transfer is always allowed
            .Add("call-offer", ControlRole.Either, Perm.Chat, (method, data, ct) => call.Handle(link, method, data, ct))
            .Add("call-answer", ControlRole.Either, Perm.Chat, (method, data, ct) => call.Handle(link, method, data, ct))
            .Add("files-open", ControlRole.Host, Perm.Files, HandleFiles)
            .Add("call-end", ControlRole.Either, Perm.None, (method, data, ct) => call.Handle(link, method, data, ct));
        Controls = registry;
        Overlay!.Control = registry.Dispatch;
    }
    Func<string, (string Root, string Actor, Action<object> Audit)>? filesRoot;
    FileShareServer? files;
    readonly object filesSync = new();
    // A relayed session runs over a volunteer ArdFerry node: the shared folder is for direct paths only, so its traffic does not use them.
    public bool Direct => !Network.Contains("中继", StringComparison.Ordinal);
    // The host's side of "文件": this session's own WebDAV server on the folder the host chose, or (none chosen) the controller's own
    // folder named after its computer name and device code; its loopback port is opened to this session's gateway, which also checks the path.
    Task<object> HandleFiles(string method, JsonElement data, CancellationToken ct)
    {
        if (!Host || gateway == null || filesRoot == null) throw new IOException("共享文件夹只能由已授权主控请求。");
        if (!Direct) throw new IOException("当前经 ArdFerry 中继连接：共享文件夹只在直连时可用。");
        var name = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var (root, actor, audit) = filesRoot(name);
        Directory.CreateDirectory(root);
        int port;
        lock (filesSync) { files ??= new FileShareServer(); files.Root = root; files.Actor = actor; files.Audit = audit; port = files.Port; }
        gateway.FilesPort = port;
        return Task.FromResult<object>(new { port });
    }
    // The controller's side: the host's shared folder as a local address (http://127.0.0.1:port/, the same port for this device every time).
    int filesPort;
    public async Task<int> OpenFiles(CancellationToken ct)
    {
        await FilesTarget(ct);
        return (Hub ?? throw new IOException("被控会话不提供本地转发入口。")).ForwardFiles(PeerId).Port;
    }
    // The host's port for this session's shared folder, asked for once per session (after a reconnect, by the first request).
    internal async Task<int> FilesTarget(CancellationToken ct)
    {
        if (Volatile.Read(ref filesPort) is var known and > 0) return known;
        if (Host || Overlay == null) throw new IOException("请先建立已授权连接。");
        if (!Perms.HasFlag(Perm.Files)) throw new IOException("被控端未允许访问共享文件夹。");
        if (!Direct) throw new IOException("当前经 ArdFerry 中继连接：共享文件夹只在直连时可用。");
        var reply = await Overlay.Request("files-open", new { name = Environment.MachineName }, ct);
        var port = reply.TryGetProperty("port", out var value) && value.TryGetInt32(out var number) && number is > 0 and <= 65535 ? number : throw new IOException("共享文件夹响应无效。");
        Volatile.Write(ref filesPort, port);
        return port;
    }
    internal CallMedia OpenVoiceHost()
    {
        if(!Host||gateway==null)throw new IOException("只有被控端可以接收通话的声音。");
        var transport=new HostVoiceTransport();var port=transport.Port;
        gateway.PermitVoiceUdp(port,true);
        return new CallMedia(transport,port,()=>gateway.PermitVoiceUdp(port,false));
    }
    internal CallMedia OpenVoiceController(int port)
    {
        if(Host||Overlay==null)throw new IOException("请先建立已授权连接。");
        return new CallMedia(new ControllerVoiceTransport(LocalPort,Capability,port),0,()=>{});
    }
    // The other side ends the session on purpose ("bye"): this side must not try to recover it.
    public event Action<string>? PeerEnded;
    Task<object> HandleBye(JsonElement data)
    {
        var reason=data.ValueKind==JsonValueKind.Object&&data.TryGetProperty("reason",out var value)?value.GetString()??"":"";
        PeerEnded?.Invoke(reason);
        return Task.FromResult<object>(new{ok=true});
    }
    // Tells the other side that this end is closing the session on purpose, so that it does not keep trying to bring it back.
    public async Task SayBye(string reason)
    {
        var overlay=Overlay; if(overlay==null||!Live)return;
        try{using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));await overlay.Request("bye",new{reason},timeout.Token);}
        catch(Exception ex){Diagnostics.Log("bye",ex.Message);}   // an older peer, or the link is already gone: nothing more to do
    }
    async Task<object> HandleFrd(string method,JsonElement data,CancellationToken ct)
    {
        if(!Host||gateway==null)throw new IOException("FRD 只能由已授权主控请求被控端启动。");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct,Token);ct=lifetime.Token;
        await frdGate.WaitAsync(ct);
        try
        {
            if(method=="frd-start")
            {
                if(!gateway.FrdAllowed)throw new IOException("被控端未允许 FRD。");   // (the registry checked it too; the gateway is the authority)
                if(frdHost!=null&&!frdHost.Live){await frdHost.DisposeAsync();frdHost=null;}
                frdHost??=await FrdHosted.Start(FrdRuntime.Resolve(frdPath),gateway,Token,ct,ReportFrdError);
                return frdHost.Offer;
            }
            if(method=="frd-stop")
            {
                if(frdHost!=null&&data.GetProperty("id").GetString()==frdHost.Offer.Id){await frdHost.DisposeAsync();frdHost=null;}
                return new{ok=true};
            }
            throw new IOException("对端不支持此 FRD 操作，请更新两端 ArdUi。");
        }
        finally{frdGate.Release();}
    }
    public async Task<FrdOpened> OpenFrd(CancellationToken ct,int testSeconds=0,string? report=null,string? title=null)
    {
        if(Host||Overlay==null)throw new IOException("请先建立已授权连接。");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct,Token);ct=lifetime.Token;
        await frdGate.WaitAsync(ct);
        try
        {
            if(frdClient?.Live==true)return frdClient;
            if(frdClient!=null){await frdClient.DisposeAsync();frdClient=null;}
            var executable=FrdRuntime.Resolve(frdPath);
            var reply=await Overlay.Request("frd-start",new{},ct);
            var offer=reply.Deserialize<FrdOffer>(Wire.Json)??throw new IOException("FRD 启动响应无效。");
            if(offer.Port is <1 or >65535||!Regex.IsMatch(offer.Id,@"\A[0-9a-f]{32}\z")||!Regex.IsMatch(offer.Token,@"\A[0-9a-f]{64}\z"))
                throw new IOException("FRD 会话参数无效。");
            Volatile.Write(ref frdPort,offer.Port);
            try{frdClient=await FrdOpened.Start(this,executable,offer,testSeconds,report,title);return frdClient;}
            catch
            {
                Volatile.Write(ref frdPort,0);
                try{await Overlay.Request("frd-stop",new{id=offer.Id},ct);}catch(Exception ex){Diagnostics.Log("frd-start-cleanup",ex.Message);}
                throw;
            }
        }
        finally{frdGate.Release();}
    }
    public LocalForwarder Forward(int target)=>Hub?.Forward(target)??throw new IOException("被控会话不提供本地转发入口。");
    int disposed;
    readonly OverlayIdentity identity;
    public Session(string peer, string address, int port, byte[] capability, int[] tcpPorts, int[] udpPorts,
        Child process, string directory, OverlayIdentity identity, Gateway? gateway = null, ForwardHub? hub = null)
    {
        if (identity.Peer != peer) throw new InvalidOperationException("会话签名身份与对端不一致。");
        PeerId = peer; Address = address; basePort = port; Capability = capability;
        TcpPorts = tcpPorts; UdpPorts = udpPorts; Process = process; this.directory = directory; this.identity = identity;
        this.gateway = gateway; Hub=hub; Host = gateway != null;
        ardArguments=process.Process.StartInfo.ArgumentList.ToArray();
        if(gateway!=null)gateway.AttachOverlay=AcceptOverlay;
    }
    public async Task EnableOverlay(CancellationToken ct)
    {Overlay=await OverlaySession.Connect(basePort,Capability,()=>Process.Network,ct,identity:identity);OverlayReady?.Invoke();}
    async Task AcceptOverlay(TcpClient tcp,byte command,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if(command==4)
        {
            if(Overlay!=null)throw new IOException("覆盖层已建立，必须使用恢复入口。");
            Overlay=await OverlaySession.AcceptHello(tcp,basePort,Capability,()=>Process.Network,timeout.Token,identity);gateway!.Overlay=Overlay;OverlayReady?.Invoke();
        }
        else
        {
            var resume=await Wire.ReadJson<JsonElement>(tcp.GetStream(),timeout.Token);
            if(Overlay==null||resume.GetProperty("session").GetString()!=Overlay.Id)throw new IOException("覆盖层恢复编号无效。");
            await tcp.GetStream().WriteAsync(new byte[]{0},timeout.Token);
        }
        var link=new OverlayLink("base",tcp,gateway!.SendOverlayUdp,()=>Process.Network);await Overlay!.Add(link);await link.Reader;
    }
    public async Task RestartArd(CancellationToken ct)
    {
        await Process.DisposeAsync();ct.ThrowIfCancellationRequested();Process=new Child(Process.LogTag,Ard.Exe,directory,ardArguments);
        Diagnostics.Log("ard-restart",PeerId);
    }
    public async Task<TcpClient> OpenTcp(int port, CancellationToken ct)
        =>await OpenTcpCore(port,false,ct);
    async Task<TcpClient> OpenTcpCore(int port,bool frd,CancellationToken ct)
    {
        if (!Live) throw new IOException("设备连接已断开。");
        if (!frd&&port != 0 && !TcpPorts.Contains(port) && port != Volatile.Read(ref filesPort)) throw new IOException("被控端未授权此 TCP 端口。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, LocalPort, timeout.Token);
            var head = new byte[23]; "AUI1"u8.CopyTo(head); Capability.CopyTo(head, 4);
            head[20] = port == 0 ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(21), checked((ushort)port));
            await tcp.GetStream().WriteAsync(head, timeout.Token);
            var status = (await Wire.Read(tcp.GetStream(), 1, timeout.Token))[0];
            if (status != 0) throw new IOException(status == 2 ? "被控端未授权此端口。" : "被控端本机服务不可用。");
            return tcp;
        }
        catch { tcp.Dispose(); throw; }
    }
    async Task<double?> MeasureBandwidth(CancellationToken ct)
    {
        if(Overlay?.Link(Overlay.Selected) is not {} link)return null;
        await Overlay.MeasureThroughput(link,ct);BandwidthLowerBoundMbps=link.BandwidthLowerBoundMbps;return link.BandwidthMbps;
    }
    public void StartMetrics(Action changed)
    {
        if (Host || metrics != Task.CompletedTask) return;
        // the latency on screen follows the path's own probes once a second; bandwidth stays on the slower loop below
        _ = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { await Task.Delay(1000, stop.Token); if (Overlay != null) changed(); } }
            catch (OperationCanceledException) { }
        });
        metrics = Task.Run(async () =>
        {
            var failures=0;var round=0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    using (await OpenTcp(0, stop.Token)) { }
                    watch.Stop(); TcpRtt = watch.Elapsed.TotalMilliseconds;
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    udp.Connect(IPAddress.Loopback, LocalPort);
                    var nonce = RandomNumberGenerator.GetBytes(12);
                    watch.Restart();
                    await udp.SendAsync(Wire.Packet(Capability, 0, nonce), stop.Token);
                    // one lost datagram on a lossy path only loses this UDP sample; the TCP round trip and bandwidth stay
                    try
                    {
                        var reply = await udp.ReceiveAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4), stop.Token);
                        watch.Stop();
                        UdpRtt = Wire.ValidPacket(reply.Buffer, Capability) && reply.Buffer.AsSpan(18).SequenceEqual(nonce)
                            ? watch.Elapsed.TotalMilliseconds : null;
                    }
                    catch (TimeoutException) { UdpRtt = null; }
                    changed();   // the latency is known now; a bandwidth measurement must not hold it back
                    if(round++%12==0)
                    {
                        try{BandwidthMbps=await MeasureBandwidth(stop.Token);}
                        catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}
                        catch(Exception ex){BandwidthMbps=null;BandwidthLowerBoundMbps=null;Diagnostics.Log("bandwidth",ex.Message);}
                    }
                    failures=0;
                    changed();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch
                {
                    TcpRtt=null;UdpRtt=null;BandwidthMbps=null;BandwidthLowerBoundMbps=null;changed();
                    if(++failures>=2&&Overlay==null){try{await Process.DisposeAsync();}catch(Exception ex){Diagnostics.Log("health-restart",ex.Message);}break;}
                    if(!Live){Diagnostics.Log("overlay-ended","Session closed or recovery made no progress within its budget.");break;}
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        FileShareServer? shared; lock (filesSync) { shared = files; files = null; }
        if (shared != null) try { await shared.DisposeAsync(); } catch (Exception ex) { Diagnostics.Log("files-close", ex.Message); }
        var errors=new List<string>();
        async ValueTask Cleanup(string name,Func<ValueTask> close)
        {
            try{await close();}
            catch(Exception ex){errors.Add(name);Diagnostics.Log("session-cleanup",name+": "+ex.Message);}
        }
        await Cleanup("停止会话",()=>{stop.Cancel();return ValueTask.CompletedTask;});
        await Cleanup("立即关闭被控入口",()=>{gateway?.Stop();return ValueTask.CompletedTask;});
        completed.TrySetResult();
        await Cleanup("解除转发绑定",()=>{Hub?.Detach(this);return ValueTask.CompletedTask;});
        Volatile.Write(ref frdPort,0);
        await Cleanup("FRD 会话",async()=>
        {
            await frdGate.WaitAsync();
            try
            {
                if(frdClient!=null)await Cleanup("FRD 主控",frdClient.DisposeAsync);
                if(frdHost!=null)await Cleanup("FRD 被控",frdHost.DisposeAsync);
                frdClient=null;frdHost=null;
            }
            finally{frdGate.Release();}
        });
        if(Overlay!=null)await Cleanup("加密覆盖层",Overlay.DisposeAsync);
        await Cleanup("网络测量",async()=>await metrics);
        await Cleanup("ARD 进程",Process.DisposeAsync);
        if(gateway!=null)await Cleanup("会话网关",gateway.DisposeAsync);
        await Cleanup("会话目录",()=>{Directory.Delete(directory,true);return ValueTask.CompletedTask;});
        if(errors.Count>0)Notify("连接已断开；部分资源清理遇到问题："+string.Join("、",errors)+"。详情见诊断日志。");
        // Keep the cancelled token source available to in-flight SOCKS handlers.
    }
}
