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
    public event Action? Changed;
    public event Action<string>? Notice;
    public DirectoryClient? DirectoryApi { get; set; }
    readonly OperationGate operations=new();
    readonly ConcurrentDictionary<string,ForwardHub> hubs = new();
    Engine(State state, Settings settings, string id) { State=state; Settings=settings; Id=id; }
    public static async Task<Engine> Create(string root, Settings settings)
    {
        settings.Validate();
        if (!File.Exists(Path.Combine(root,"device","identity"))) throw new IOException("本机身份尚未安装，请重新执行官方 install.ps1。运行客户端不会自动生成新身份。");
        var state=new State(root);
        try { return new Engine(state,settings,await Ard.Identity(state.IdentityDirectory,CancellationToken.None)); }
        catch { state.Dispose(); throw; }
    }
    void Update(string id,string message) { Status[id]=message; Changed?.Invoke(); }
    public void SetStatus(string id,string message)=>Update(id,message);
    string Describe(Session session, bool host)
    {
        var metric=session.UdpRtt is{} udp?$" · RTT {udp:F1} ms":" · RTT -- ms";
        metric+=session.BandwidthMbps is{} bandwidth?$" · 吞吐 ~{bandwidth:F1} Mbps":session.BandwidthLowerBoundMbps is{} minimum?$" · 吞吐 ≥{minimum:F1} Mbps":" · 吞吐 --";
        if(host)metric="";
        var recovering=session.Overlay is{} overlay&&overlay.Link(overlay.Selected)?.Live!=true;
        return (recovering?"正在恢复连接":host ? "正在访问本机" : "已连接") + " · " + session.Network + metric;
    }
    void Observe(Session session, bool host)
    {
        session.Process.Output += line =>
        {
            if (line.Contains("CONNECTED", StringComparison.Ordinal) || line.Contains("network path selected", StringComparison.Ordinal) || line.Contains("READY:", StringComparison.Ordinal))
            {
                Logs.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
                while (Logs.Count > 4) Logs.TryDequeue(out _);
            }
            Update(session.PeerId, Describe(session, host));
        };
        if (!host) session.StartMetrics(() => Update(session.PeerId, Describe(session, false)));
        Update(session.PeerId, Describe(session, host));
    }
    void ConfigureOverlay(Session session)
    {
        if(session.Overlay==null||DirectoryApi==null||session.Transit!=null)return;
        session.Transit=new TransitCoordinator(this,DirectoryApi,session);
        session.Notice+=message=>{Update(session.PeerId,message);Notice?.Invoke(message);};
        session.ConfigureFrd(Settings.FrdPath);
        session.Overlay.Changed+=()=>Update(session.PeerId,Describe(session,session.Host));
    }
    async Task Monitor(Session session,bool host)
    {
        while(!session.Token.IsCancellationRequested)
        {
            var child=session.Process;
            try{await Task.WhenAny(child.Exited,Task.Delay(1000,session.Token));session.Token.ThrowIfCancellationRequested();}catch(OperationCanceledException){break;}
            if(!session.Live)break;
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
        { await session.DisposeAsync(); Update(session.PeerId,"连接已断开"); }
    }
    public async Task Disconnect(string id)
    {
        operations.Cancel("out:"+id);operations.Cancel("in:"+id);
        if(Outgoing.TryRemove(id,out var outgoing))await outgoing.DisposeAsync();
        if(Incoming.TryRemove(id,out var incoming))await incoming.DisposeAsync();
        Update(id,"未连接");
    }
    public async Task DropOutgoing(string id)
    {
        operations.Cancel("out:"+id);
        if(Outgoing.TryRemove(id,out var session))await session.DisposeAsync();Update(id,"未连接");
    }
    public async Task RemoveOutgoing(string id)
    {
        await DropOutgoing(id);
        if(hubs.TryRemove(id,out var hub))await hub.DisposeAsync();
    }
    public async Task StopIncoming()
    {
        operations.CancelWhere(key=>key.StartsWith("in:",StringComparison.Ordinal));
        await Task.WhenAll(Incoming.ToArray().Select(async pair=>
        {if(Incoming.TryRemove(pair)){await pair.Value.DisposeAsync();Update(pair.Key,"被控访问已关闭");}}));
    }
    public async Task DropIncoming(string id)
    {
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
                child=Ard.Start(dir,host,port,remote,Settings);
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
    public async Task<string> AcceptServerSession(ServerTicket ticket, Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>> verifyPassword, CancellationToken ct)
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
            session = new Session(peer.Id, peer.Address, gateway.Port, capability, Settings.TcpPorts, Settings.UdpPorts, child, dir, gateway);
            session.OverlayReady+=()=>ConfigureOverlay(session);
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Attached += () => { attached.TrySetResult(); Update(peer.Id, "已连接 · 对方正在访问本机"); };
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
            operation.Commit(()=>peer=State.GetOrAdd(target.Endpoint,target.Code,admission.Grant));
            var hub=hubs.GetOrAdd(peer.Id,_=>new ForwardHub());
            session = new Session(peer.Id, peer.Address, port, Convert.FromHexString(admission.Token), admission.TcpPorts, admission.UdpPorts, child, dir, hub:hub);
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
        await operations.Close();
            foreach(var session in Outgoing.Values.Concat(Incoming.Values)) await session.DisposeAsync();
            Outgoing.Clear(); Incoming.Clear(); State.Dispose();
            foreach(var hub in hubs.Values)await hub.DisposeAsync();hubs.Clear();
    }
}
