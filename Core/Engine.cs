namespace ArdUi;

sealed class Engine : IAsyncDisposable
{
    public State State { get; }
    public Settings Settings { get; }
    public string Id { get; }
    public ConcurrentDictionary<string, Session> Outgoing { get; } = new();
    public ConcurrentDictionary<string, Session> Incoming { get; } = new();
    public ConcurrentDictionary<string, string> Status { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();
    public EventLog Events { get; } = new();
    public event Action? Changed;
    public event Action<string>? Notice;
    public DirectoryClient? DirectoryApi { get; set; }
    readonly OperationGate operations=new();
    readonly ConcurrentDictionary<string,ForwardHub> hubs = new();
    // The audit of controllers (screen + conversation). It cannot be switched off: if it cannot record, the session is ended.
    public AuditService Audit { get; }
    Engine(State state, Settings settings, string id)
    {
        State=state; Settings=settings; Id=id;
        Audit=new AuditService(Path.Combine(state.Root,"audit"));
        Audit.Failure+=(endpoint,reason)=>
        {
            Events.Add("ERROR","审计","审计录屏失败，已结束该会话："+reason);
            Notice?.Invoke("审计录屏失败，已断开对方：" + reason);
            _=DropIncoming(endpoint,"audit");
        };
        Audit.Changed+=()=>Changed?.Invoke();
    }
    public static async Task<Engine> Create(string root, Settings settings)
    {
        settings.Validate();
        if (!File.Exists(Path.Combine(root,"device","identity"))) throw new IOException("本机身份尚未安装，请重新执行官方 install.ps1。运行客户端不会自动生成新身份。");
        var state=new State(root);
        StartupTrace.Mark("state opened; asking ard for the identity");
        try { return new Engine(state,settings,await Ard.Identity(state.IdentityDirectory,CancellationToken.None)); }
        catch { state.Dispose(); throw; }
    }
    // The ArdFerry a session's traffic runs over right now: ard's "network path selected … remote=relay:https://s-….ard-relay.invalid/"
    // (a direct path names an address instead). Other lines mentioning relays are about this machine's own relay registrations.
    internal static readonly Regex RelayName = new(@"network path selected .*?\bremote=(?:relay:https://(s-[0-9a-f]{40}\.ard-relay\.invalid)/|\S)", RegexOptions.Compiled);
    // A status that did not change must not wake the UI: ard prints thousands of lines per second at trace level.
    void Update(string id,string message) { if(Status.TryGetValue(id,out var old)&&old==message)return; Status[id]=message; Changed?.Invoke(); }
    public void SetStatus(string id,string message)=>Update(id,message);
    static string Speed(double mbps)=>mbps>=10?$"{mbps:F0} Mbps":$"{mbps:F1} Mbps";
    // "P2P 直连 / IPV6" → "IPv6", "ArdRelay 中继" → "Relay"; other texts (recovering, connecting) stay as they are.
    internal static string PathLabel(string network)
    {
        if(network.Contains("IPV6",StringComparison.OrdinalIgnoreCase))return "IPv6";
        if(network.Contains("IPV4",StringComparison.OrdinalIgnoreCase))return "IPv4";
        if(network.Contains("中继",StringComparison.Ordinal))return "Relay";
        if(network.Contains("直连",StringComparison.Ordinal))return "直连";
        return network;
    }
    // True for the status text of an established session (it starts with the path kind).
    internal static bool IsLiveText(string text)=>text.StartsWith("IPv4",StringComparison.Ordinal)||text.StartsWith("IPv6",StringComparison.Ordinal)||text.StartsWith("Relay",StringComparison.Ordinal)||text.StartsWith("直连",StringComparison.Ordinal)||text.StartsWith("已连接",StringComparison.Ordinal);
    string Describe(Session session, bool host)
    {
        var recovering=session.Overlay is{} overlay&&overlay.Link(overlay.Selected)?.Live!=true;
        var path=PathLabel(session.Network);
        if(host)return (recovering?"正在恢复连接":"正在访问本机")+" · "+path;
        if(recovering)return "正在恢复连接 · "+path;
        // Connected: the path kind, then the measured speed and latency (whatever is known so far).
        var parts=new List<string>{path};
        if(session.BandwidthMbps is{} bandwidth)parts.Add(Speed(bandwidth));
        else if(session.BandwidthLowerBoundMbps is{} minimum)parts.Add("≥"+Speed(minimum));
        if(session.UdpRtt is{} udp)parts.Add($"{udp:F0} ms");
        return string.Join(" · ",parts);
    }
    void Observe(Session session, bool host)
    {
        session.Process.Output += line =>
        {
            if (RelayName.Match(line) is { Success: true } relay) session.RelayName = relay.Groups[1].Success ? relay.Groups[1].Value : null;
            if (line.Contains(" WARN ", StringComparison.Ordinal) || line.Contains(" ERROR ", StringComparison.Ordinal)) Events.AddFromArd(line);
            if (line.Contains("CONNECTED", StringComparison.Ordinal) || line.Contains("network path selected", StringComparison.Ordinal) || line.Contains("READY:", StringComparison.Ordinal))
            {
                Events.AddFromArd(line);
                Logs.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
                while (Logs.Count > 4) Logs.TryDequeue(out _);
            }
            Update(session.PeerId, Describe(session, host));
        };
        if (!host) session.StartMetrics(() => Update(session.PeerId, Describe(session, false)));
        Update(session.PeerId, Describe(session, host));
    }
    // The audit of this session opens when the controller is in; the screen is recorded whenever it is being used (FRD or RDP).
    async Task BeginAudit(Peer peer, string code, Gateway gateway, Session session)
    {
        void Screen(bool active)
        {
            try { Audit.ScreenActivity(peer.Id, active); }
            catch (Exception ex)
            {
                Diagnostics.Log("audit-start", ex.ToString());
                Events.Add("ERROR", "审计", $"无法录屏，已断开 {code}：{ex.Message}");
                Notice?.Invoke("审计录屏无法启动，已断开对方：" + ex.Message);
                _ = DropIncoming(peer.Id);
            }
        }
        Audit.Open(peer.Id, code, gateway.Once, gateway.Perms, ChatFor(peer.Id), CallFor(peer.Id));
        gateway.ScreenActivity += Screen;
        if (!session.Live) { await Audit.End(peer.Id); return; }
        _ = session.Completion.ContinueWith(_ => Audit.End(peer.Id), TaskScheduler.Default);
        if (gateway.ScreenActive) Screen(true);   // the desktop was already in use
    }
    // One conversation per device, kept for as long as ArdUi runs; files received are stored under data\chat\<device>.
    readonly ConcurrentDictionary<string,ChatChannel> chats=new();
    public ChatChannel ChatFor(string peerId)=>chats.GetOrAdd(peerId,id=>
    {
        var chat=new ChatChannel(Path.Combine(State.Root,"chat",id[..16]),()=>
        {
            var overlay=(Outgoing.TryGetValue(id,out var outgoing)&&outgoing.Live?outgoing:Incoming.TryGetValue(id,out var incoming)&&incoming.Live?incoming:null)?.Overlay;
            return overlay!=null&&!overlay.Token.IsCancellationRequested?overlay.Request:null;
        });
        chat.Changed+=()=>Changed?.Invoke();
        return chat;
    });
    readonly ConcurrentDictionary<string,CallChannel> calls=new();
    public IEnumerable<KeyValuePair<string,CallChannel>> Calls=>calls;
    // The voice call with one device: on the session that is live (the controlling one first), with the sound card of this machine.
    public CallChannel CallFor(string peerId)=>calls.GetOrAdd(peerId,id=>
    {
        var call=new CallChannel(()=>
        {
            if(Outgoing.TryGetValue(id,out var outgoing)&&outgoing.Live&&outgoing.Overlay!=null)return new SessionCallLink(outgoing);
            if(Incoming.TryGetValue(id,out var incoming)&&incoming.Live&&incoming.Overlay!=null)return new SessionCallLink(incoming);
            return null;
        },()=>new MicrophoneInput(),()=>new SpeakerOutput(),()=>!Motion.NoVoice);
        call.Changed+=()=>Changed?.Invoke();
        return call;
    });
    void ConfigureOverlay(Session session)
    {
        if(session.Overlay==null||DirectoryApi==null||session.Controls!=null)return;   // once per session
        session.Notice+=message=>{Update(session.PeerId,message);Notice?.Invoke(message);};
        var call=CallFor(session.PeerId);
        session.ConfigureControls(Settings.FrdPath,ChatFor(session.PeerId),call);
        _=session.Completion.ContinueWith(_=>call.Close(session),TaskScheduler.Default);
        session.PeerEnded+=reason=>OnPeerEnded(session,reason);
        var conversation = ChatFor(session.PeerId); _ = session.Completion.ContinueWith(_ => conversation.Close(), TaskScheduler.Default);
        session.Overlay.Changed+=()=>Update(session.PeerId,Describe(session,session.Host));
    }
    // How long an incoming session waits for a controller that has gone without a goodbye (its machine went offline, its ArdUi
    // was killed). The controller came with a one-time identity and will not come back to this session; a new connection makes a
    // new one. The controller's own recovery has its budget in Session.StartMetrics.
    internal static TimeSpan IncomingRecoveryBudget=TimeSpan.FromMinutes(2);
    async Task Monitor(Session session,bool host)
    {
        long? pathless=null;
        while(!session.Token.IsCancellationRequested)
        {
            var child=session.Process;
            try{await Task.WhenAny(child.Exited,Task.Delay(1000,session.Token));session.Token.ThrowIfCancellationRequested();}catch(OperationCanceledException){break;}
            if(!session.Live)break;
            if(host&&session.Overlay is{} overlay)
            {
                if(overlay.Link(overlay.Selected)?.Live==true)pathless=null;
                else if(Environment.TickCount64-(pathless??=Environment.TickCount64)>IncomingRecoveryBudget.TotalMilliseconds)
                {Diagnostics.Log("incoming-abandoned",$"{session.PeerId[..8]}: no path for {IncomingRecoveryBudget.TotalSeconds:F0} s; the controller is gone");break;}
            }
            if(!child.Exited.IsCompleted)continue;
            if(session.Overlay==null||!session.Live)break;
            try
            {
                Diagnostics.Log("ard-exited","Restarting an exited process with the same identity; live ARD recovery is not interrupted.");
                await Task.Delay(1000,session.Token);await session.RestartArd(session.Token);Observe(session,host);
            }
            catch(OperationCanceledException){break;}
            catch(Exception ex){Diagnostics.Log("ard-restart-failed",ex.Message);break;}
        }
        var sessions=host ? Incoming : Outgoing;
        if(sessions.TryRemove(new KeyValuePair<string,Session>(session.PeerId,session)))
        { await session.DisposeAsync(); if(!host)ForgetTemporary(session.PeerId); Update(session.PeerId,"连接已断开"); }
    }
    // A device accepted only once disappears with its session.
    void ForgetTemporary(string id)
    {
        bool temporary;lock(State.Peers)temporary=State.Peers.Find(p=>p.Id==id)?.Temporary==true;
        if(temporary)State.Remove(id);
    }
    // The other side hung up on purpose: this side ends the session and, as the controller, stops trying to recover it.
    void OnPeerEnded(Session session,string reason)
    {
        var id=session.PeerId;
        var text=reason switch
        {
            "revoked"=>"被控端已撤销授权","stopped"=>"被控端已关闭被控访问","audit"=>"被控端因审计录屏失败断开了连接",
            _=>session.Host?"控制端已断开连接":"被控端已断开连接",
        };
        _=Task.Run(async()=>
        {
            try
            {
                if(session.Host){operations.Cancel("in:"+id);if(Incoming.TryRemove(new KeyValuePair<string,Session>(id,session)))await session.DisposeAsync();Update(id,text);}
                else if(DirectoryApi!=null)await DirectoryApi.PeerHungUp(id,text);
                Notice?.Invoke(text+"。");
            }
            catch(Exception ex){Diagnostics.Log("peer-ended",ex.Message);}
        });
    }
    // Tells the device on the other end that this side is closing the session on purpose, so that it ends too instead of "recovering".
    // Every way a person ends a session (disconnect, pause, remove, quit ArdUi) goes through here first.
    public async Task Goodbye(string id)
    {
        var goodbyes=new List<Task>();
        if(Incoming.TryGetValue(id,out var hosted))goodbyes.Add(hosted.SayBye("host"));
        if(Outgoing.TryGetValue(id,out var controlling))goodbyes.Add(controlling.SayBye("controller"));
        await Task.WhenAll(goodbyes);
    }
    public Task GoodbyeAll()=>Task.WhenAll(Outgoing.Values.Select(s=>s.SayBye("controller")).Concat(Incoming.Values.Select(s=>s.SayBye("host"))));
    public async Task Disconnect(string id)
    {
        await Goodbye(id);
        operations.Cancel("out:"+id);operations.Cancel("in:"+id);
        if(Outgoing.TryRemove(id,out var outgoing))await outgoing.DisposeAsync();
        if(Incoming.TryRemove(id,out var incoming))await incoming.DisposeAsync();
        ForgetTemporary(id);Update(id,"未连接");
    }
    public async Task DropOutgoing(string id)
    {
        operations.Cancel("out:"+id);
        if(Outgoing.TryRemove(id,out var session))await session.DisposeAsync();
        ForgetTemporary(id);Update(id,"未连接");
    }
    public async Task RemoveOutgoing(string id)
    {
        await Goodbye(id);
        await DropOutgoing(id);
        if(hubs.TryRemove(id,out var hub))await hub.DisposeAsync();
    }
    public async Task StopIncoming()
    {
        await Task.WhenAll(Incoming.ToArray().Select(pair=>pair.Value.SayBye("stopped")));
        operations.CancelWhere(key=>key.StartsWith("in:",StringComparison.Ordinal));
        await Task.WhenAll(Incoming.ToArray().Select(async pair=>
        {if(Incoming.TryRemove(pair)){await pair.Value.DisposeAsync();Update(pair.Key,"被控访问已关闭");}}));
    }
    public async Task DropIncoming(string id,string reason="revoked")
    {
        if(Incoming.TryGetValue(id,out var live))await live.SayBye(reason);
        operations.Cancel("in:"+id);
        if(Incoming.TryRemove(id,out var session))await session.DisposeAsync();
        Update(id,"被控访问已撤销");
    }
    static bool IsTransientArdStartupFailure(Exception error)
    {
        var text=error.ToString();
        return text.Contains("ArdRelay TLS preflight could not complete",StringComparison.Ordinal)||
            text.Contains("timed out",StringComparison.OrdinalIgnoreCase)||
            Regex.IsMatch(text,@"os error \d+");
    }
    async Task<Child> StartArdWithRetry(string peer,string dir,bool host,int port,string remote,string ready,CancellationToken ct)
    {
        Exception? last=null;
        for(var attempt=1;attempt<=3;attempt++)
        {
            Child? child=null;
            try
            {
                child=Ard.Start(dir,host,port,remote,Settings,"ard "+peer[..8]);
                await child.WaitFor(ready,ct);
                return child;
            }
            catch(Exception error) when(IsTransientArdStartupFailure(error)&&attempt<3)
            {
                last=error;
                if(child!=null)await child.DisposeAsync();
                Update(peer,$"网络预检暂时失败，正在重试（{attempt}/3）…");
                Diagnostics.Log("ard-start-retry",$"peer={peer} attempt={attempt} {error.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt),ct);
            }
            catch
            {
                if(child!=null)await child.DisposeAsync();
                throw;
            }
        }
        throw new IOException("ARD 网络预检连续失败。",last);
    }
    // Every session's key exchange is signed with this device's key and must be signed by the peer's (see OverlayIdentity).
    OverlayIdentity Identity(string peer)=>DirectoryApi?.OverlayIdentity(peer)??throw new InvalidOperationException("尚未连接可信服务器，无法建立会话。");
    public async Task<string> AcceptServerSession(ServerTicket ticket, Func<PasswordRequest, CancellationToken, Task<Authorization?>> verifyPassword, CancellationToken ct)
    {
        using var operation=await operations.Enter("in:"+ticket.ControllerEndpoint,ct);ct=operation.Token;
        string? dir = null; Gateway? gateway = null; Child? child = null; Session? session = null;
        try
        {
            if(Incoming.TryRemove(ticket.ControllerEndpoint,out var previous))await previous.DisposeAsync();
            var peer = new Peer { Id = ticket.ControllerEndpoint, Code = ticket.ControllerCode };
            dir = State.NewSessionDirectory();
            var local = await Ard.Identity(dir, ct);
            var capability = RandomNumberGenerator.GetBytes(16);
            gateway = new Gateway(Settings, capability, async (password, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                return await verifyPassword(password, linked.Token);
            });
            child = await StartArdWithRetry(peer.Id,dir,true,gateway.Port,ticket.ClientSessionId,"relay online",ct);
            session = new Session(peer.Id, peer.Address, gateway.Port, capability, Settings.TcpPorts, Settings.UdpPorts, child, dir, Identity(peer.Id), gateway);
            session.OverlayReady+=()=>ConfigureOverlay(session);
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var auditStarted = 0;
            gateway.Attached += () =>
            {
                attached.TrySetResult(); Update(peer.Id, "已连接 · 对方正在访问本机");
                // from the moment someone is in, the screen is recorded
                if (Interlocked.Exchange(ref auditStarted, 1) == 0) _ = Task.Run(() => BeginAudit(peer, ticket.ControllerCode, gateway!, session!));
            };
            operation.Commit(()=>{if(!Incoming.TryAdd(peer.Id,session))throw new IOException("已有同设备会话。");});
            Observe(session, true);
            var owned = session;
            var registration = ct.Register(() => { _ = owned.DisposeAsync(); });
            _ = child.Exited.ContinueWith(t => registration.Dispose(), TaskScheduler.Default);
            _ = Monitor(session, true);
            _ = Task.Run(async () =>
            {
                try { await attached.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, ticket.Expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds())), owned.Token); }
                catch { await owned.DisposeAsync(); }
            });
            Update(peer.Id, "等待密码与首次身份确认");
            return local;
        }
        catch
        {
            if (session != null) { Incoming.TryRemove(ticket.ControllerEndpoint, out _); await session.DisposeAsync(); }
            else
            {
                if (child != null) await child.DisposeAsync();
                if (gateway != null) await gateway.DisposeAsync();
                if (dir != null) try { Directory.Delete(dir, true); } catch (IOException) { }
            }
            throw;
        }
    }
    public async Task ConnectServerSession(MachineInfo target, string dir, ServerOffer offer, bool enrolling, string password, CancellationToken ct)
    {
        using var operation=await operations.Enter("out:"+target.Endpoint,ct);ct=operation.Token;
        Child? child = null; Session? session = null;
        var peer = new Peer { Id = target.Endpoint, Code = target.Code };
        try
        {
            var port = Wire.Port();
            Update(peer.Id, "正在建立经过身份签名的连接…");
            child = await StartArdWithRetry(peer.Id,dir,false,port,offer.SessionId,"READY:",ct);
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            var header = new byte[23]; "AUI1"u8.CopyTo(header); header[20] = 2;
            await tcp.GetStream().WriteAsync(header, ct);
            await Wire.WriteJson(tcp.GetStream(), new PasswordRequest(enrolling, password), ct);
            Update(peer.Id, "等待对端验证密码和确认 EndpointId…");
            var admission = await Wire.ReadJson<AdmissionReply>(tcp.GetStream(), ct);
            if (!admission.Accepted) throw new IOException(admission.Error ?? "访问未获批准。");
            if (admission.Token == null || !Regex.IsMatch(admission.Token, @"\A[0-9a-fA-F]{32}\z") ||
                admission.TcpPorts == null || admission.UdpPorts == null || admission.TcpPorts.Concat(admission.UdpPorts).Any(p => p is < 1 or > 65535))
                throw new InvalidDataException("对端授权数据无效。");
            if (admission.Grant == null) throw new IOException("缺少被控端签名的授权。");
            var grant = DirectoryClient.Verify<GrantReceipt>(admission.Grant, "/grant/v1", target.Endpoint, false);
            if (grant.ControllerEndpoint != Id || grant.TargetEndpoint != target.Endpoint) throw new IOException("授权未绑定当前设备。");
            operation.Commit(()=>peer=State.GetOrAdd(target.Endpoint,target.Code,admission.Grant,admission.Once));
            var hub=hubs.GetOrAdd(peer.Id,_=>new ForwardHub());
            session = new Session(peer.Id, peer.Address, port, Convert.FromHexString(admission.Token), admission.TcpPorts, admission.UdpPorts, child, dir, Identity(peer.Id), hub:hub);
            session.Perms=Permissions.Parse(admission.Perms);session.Once=admission.Once;
            await session.EnableOverlay(ct);ConfigureOverlay(session);
            using (await session.OpenTcp(0, ct)) { }
            operation.Commit(()=>
            {
                if(!Outgoing.TryAdd(peer.Id,session))throw new IOException("该设备已有连接。");
                hub.Attach(session);
            });
            Observe(session, false); _ = Monitor(session, false);
        }
        catch
        {
            if (session != null) await session.DisposeAsync();
            else { if (child != null) await child.DisposeAsync(); try { Directory.Delete(dir, true); } catch (IOException) { } }
            Update(peer.Id, "连接未获批准或已失败"); throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await GoodbyeAll();   // quitting ArdUi is on purpose too: the other side must not wait for this one to come back
        await operations.Close();
        await Audit.DisposeAsync();
            foreach(var session in Outgoing.Values.Concat(Incoming.Values)) await session.DisposeAsync();
            Outgoing.Clear(); Incoming.Clear(); State.Dispose();
            foreach(var hub in hubs.Values)await hub.DisposeAsync();hubs.Clear();
    }
}
