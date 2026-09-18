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
    readonly SemaphoreSlim operation = new(1);
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
        metric+=session.BandwidthMbps is{} bandwidth?$" · ~{bandwidth:F1} Mbps":" · ~-- Mbps";
        if(host)metric="";
        return (host ? "正在访问本机" : "已连接") + " · " + session.Network + metric;
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
        long baseLost=0;
        while(!session.Token.IsCancellationRequested)
        {
            var child=session.Process;
            try{await Task.WhenAny(child.Exited,Task.Delay(1000,session.Token));session.Token.ThrowIfCancellationRequested();}catch(OperationCanceledException){break;}
            if(!session.Live)break;
            if(session.Overlay?.Link("base") is{Live:false})
            {if(baseLost==0)baseLost=Environment.TickCount64;}
            else baseLost=0;
            var stalled=baseLost!=0&&Environment.TickCount64-baseLost>=10000;
            if(!child.Exited.IsCompleted&&!stalled)continue;
            if(session.Overlay==null||!session.Live)break;
            try
            {
                if(stalled)Diagnostics.Log("ard-stalled","Base transport remained unavailable for 10 seconds; restarting the same identity.");
                await Task.Delay(1000,session.Token);await session.RestartArd(session.Token);baseLost=0;Observe(session,host);
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
        await operation.WaitAsync();
        try
        {
            if(Outgoing.TryRemove(id,out var outgoing)) await outgoing.DisposeAsync();
            if(Incoming.TryRemove(id,out var incoming)) await incoming.DisposeAsync();
            Update(id,"未连接");
        }
        finally { operation.Release(); }
    }
    public async Task DropOutgoing(string id)
    {
        await operation.WaitAsync();
        try { if(Outgoing.TryRemove(id,out var session))await session.DisposeAsync();Update(id,"未连接"); }
        finally { operation.Release(); }
    }
    public async Task RemoveOutgoing(string id)
    {
        await DropOutgoing(id);
        if(hubs.TryRemove(id,out var hub))await hub.DisposeAsync();
    }
    public async Task StopIncoming()
    {
        await operation.WaitAsync();
        try { foreach(var pair in Incoming.ToArray()) if(Incoming.TryRemove(pair.Key,out var session)) { await session.DisposeAsync(); Update(pair.Key,"被控访问已关闭"); } }
        finally { operation.Release(); }
    }
    public async Task<string> AcceptServerSession(ServerTicket ticket, Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>> verifyPassword, CancellationToken ct)
    {
        await operation.WaitAsync(ct);
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
            child = Ard.Start(dir, true, gateway.Port, ticket.ClientSessionId, Settings);
            await child.WaitFor("relay online", ct);
            session = new Session(peer.Id, peer.Address, gateway.Port, capability, Settings.TcpPorts, Settings.UdpPorts, child, dir, gateway);
            session.OverlayReady+=()=>ConfigureOverlay(session);
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Attached += () => { attached.TrySetResult(); Update(peer.Id, "已连接 · 对方正在访问本机"); };
            if (!Incoming.TryAdd(peer.Id, session)) throw new IOException("已有同设备会话。");
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
        finally { operation.Release(); }
    }
    public async Task ConnectServerSession(MachineInfo target, string dir, ServerOffer offer, bool enrolling, string password, CancellationToken ct)
    {
        await operation.WaitAsync(ct);
        Child? child = null; Session? session = null;
        var peer = new Peer { Id = target.Endpoint, Code = target.Code };
        try
        {
            var port = Wire.Port();
            child = Ard.Start(dir, false, port, offer.SessionId, Settings);
            Update(peer.Id, "正在建立经过身份签名的连接…");
            await child.WaitFor("READY:", ct);
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
            peer = State.GetOrAdd(target.Endpoint, target.Code, admission.Grant);
            var hub=hubs.GetOrAdd(peer.Id,_=>new ForwardHub());
            session = new Session(peer.Id, peer.Address, port, Convert.FromHexString(admission.Token), admission.TcpPorts, admission.UdpPorts, child, dir, hub:hub);
            await session.EnableOverlay(ct);ConfigureOverlay(session);
            using (await session.OpenTcp(0, ct)) { }
            if (!Outgoing.TryAdd(peer.Id, session)) throw new IOException("该设备已有连接。");
            hub.Attach(session);
            Observe(session, false); _ = Monitor(session, false);
        }
        catch
        {
            if (session != null) await session.DisposeAsync();
            else { if (child != null) await child.DisposeAsync(); try { Directory.Delete(dir, true); } catch (IOException) { } }
            Update(peer.Id, "连接未获批准或已失败"); throw;
        }
        finally { operation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await operation.WaitAsync();
        try
        {
            foreach(var session in Outgoing.Values.Concat(Incoming.Values)) await session.DisposeAsync();
            Outgoing.Clear(); Incoming.Clear(); State.Dispose();
            foreach(var hub in hubs.Values)await hub.DisposeAsync();hubs.Clear();
        }
        finally { operation.Release(); }
    }
}
