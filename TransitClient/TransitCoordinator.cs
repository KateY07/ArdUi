namespace ArdUi;

sealed class TransitCoordinator : IAsyncDisposable
{
    readonly Engine engine;
    readonly DirectoryClient directory;
    readonly Session session;
    readonly OverlaySession overlay;
    readonly ConcurrentDictionary<string,Leg> legs=new();
    readonly CancellationTokenSource stop;
    readonly SemaphoreSlim selecting=new(1),keyGate=new(1),switching=new(1),wake=new(0,1);
    readonly object wakeGate=new();
    readonly Task loop;
    string? directoryKey;
    long testSelectionUntil;
    long nextSelection;
    bool awaitingReplacement;
    Task selectionTask=Task.CompletedTask;
    sealed class Leg(string route,string candidate,string folder,string endpoint)
    {
        public string Route{get;}=route;
        public string Candidate{get;}=candidate;
        public string Folder{get;}=folder;
        public string Endpoint{get;}=endpoint;
        public long Created{get;}=Environment.TickCount64;
        public Child? Child;
        public OverlayLink? Link;
        public UdpClient? Udp;
        public Task Reader=Task.CompletedTask;
        public bool Activated,PeerDirect;
        public PathQuality? Quality;
        public int Degraded;
        public long DirectLostAt;
    }
    public TransitCoordinator(Engine engine,DirectoryClient directory,Session session)
    {
        this.engine=engine;this.directory=directory;this.session=session;overlay=session.Overlay!;
        stop=CancellationTokenSource.CreateLinkedTokenSource(session.Token);overlay.Control=Handle;
        overlay.Changed+=Wake;loop=Run();
    }
    void Wake(){lock(wakeGate)if(wake.CurrentCount==0)wake.Release();}
    async Task<string> ServerKey(CancellationToken ct)
    {
        await keyGate.WaitAsync(ct);
        try
        {
            if(directoryKey!=null)return directoryKey;
            var response=await directory.Call<JsonElement>("/api/v2/transit/key",new{},ct);
            var key=Wire.Endpoint(response.GetProperty("endpoint").GetString()!);
            var path=Path.Combine(engine.State.Root,"transit-directory-key");
            if(File.Exists(path)&&File.ReadAllText(path).Trim()!=key)throw new CryptographicException("NJ 中继票据公钥发生改变，已阻止候选中继。");
            File.WriteAllText(path,key);directoryKey=key;return key;
        }
        finally{keyGate.Release();}
    }
    async Task<Leg> Prepare(string route,string candidate,CancellationToken ct)
    {
        if(!Regex.IsMatch(route,@"\A[0-9a-f]{32}\z")||Wire.Endpoint(candidate)==engine.Id||candidate==session.PeerId||legs.Count>=3)
            throw new IOException("候选中继参数或数量无效。");
        if(legs.TryGetValue(route,out var existing))return existing;
        var folder=engine.State.NewSessionDirectory();var endpoint=await Ard.Identity(folder,ct);
        var leg=new Leg(route,candidate,folder,endpoint);
        if(!legs.TryAdd(route,leg)){Directory.Delete(folder,true);return legs[route];}
        return leg;
    }
    async Task<object> Handle(string method,JsonElement data,CancellationToken ct)
    {
        if(method=="transit-prepare")
        {
            if(!session.Host)throw new IOException("只有主控可以发起中继探测。");
            var leg=await Prepare(data.GetProperty("route").GetString()!,data.GetProperty("candidate").GetString()!,ct);
            var aSession=Wire.Endpoint(data.GetProperty("aSession").GetString()!);
            return new{endpoint=leg.Endpoint,approval=directory.ApproveTransit(leg.Route,session.PeerId,leg.Candidate,aSession,leg.Endpoint)};
        }
        if(method=="transit-activate")
        {
            if(!session.Host)throw new IOException("中继激活方向无效。");
            var activation=data.Deserialize<TransitActivation>(Wire.Json)!;
            var ticket=DirectoryClient.Verify<TransitTicket>(activation.Ticket,"/transit-ticket/v2",await ServerKey(ct));
            if(!legs.TryGetValue(ticket.Route,out var leg))throw new IOException("未知候选中继会话。");
            await Activate(leg,activation,ct);return new{ok=true};
        }
        var route=data.GetProperty("route").GetString()!;
        if(method=="transit-close"){await Close(route,false);return new{ok=true};}
        if(method=="transit-select")
        {
            if(!session.Host||!legs.TryGetValue(route,out var chosen)||!IsDirect(chosen)||!chosen.Link!.Responsive||
                !data.GetProperty("direct").GetBoolean())throw new IOException("候选选择需要双段 Direct 且本端响应正常。");
            chosen.PeerDirect=true;overlay.Select(route);return new{ok=true};
        }
        if(method=="transit-status")
        {
            if(!legs.TryGetValue(route,out var leg))return new{direct=false,network="closed",live=false};
            if(leg.Activated&&data.TryGetProperty("ready",out var ready)&&ready.GetBoolean())leg.Link!.HealthReady=true;
            leg.PeerDirect=data.TryGetProperty("direct",out var direct)&&direct.GetBoolean();
            return new{direct=IsDirect(leg),responsive=leg.Link?.Responsive??false,network=leg.Child?.Network,live=leg.Link?.Live??false,rttMs=leg.Child?.PathRtt};
        }
        throw new IOException("未知中继协调指令。");
    }
    static bool IsDirect(Leg leg)=>leg.Child!=null&&!leg.Child.Exited.IsCompleted&&leg.Child.Network.StartsWith("P2P",StringComparison.Ordinal)&&leg.Link?.Live==true;
    async Task Activate(Leg leg,TransitActivation activation,CancellationToken ct)
    {
        if(leg.Activated)throw new IOException("中继入口已激活。");
        var activationWatch=Stopwatch.StartNew();
        Diagnostics.Log("candidate-activation-start",leg.Route);
        var ticket=DirectoryClient.Verify<TransitTicket>(activation.Ticket,"/transit-ticket/v2",await ServerKey(ct));
        if(ticket.Route!=leg.Route||ticket.C!=leg.Candidate||ticket.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()||
            ticket.A!=(session.Host?session.PeerId:engine.Id)||ticket.B!=(session.Host?engine.Id:session.PeerId)||
            (session.Host?ticket.BSession:ticket.ASession)!=leg.Endpoint)throw new CryptographicException("中继票据没有绑定当前双方和临时身份。");
        var offer=DirectoryClient.Verify<TransitOffer>(activation.Offer,$"/api/v2/transit/routes/{leg.Route}/ready",leg.Candidate);
        if(offer.Route!=leg.Route)throw new CryptographicException("C 的签名绑定了错误会话。");
        var remote=Wire.Endpoint(session.Host?offer.BRelaySession:offer.ARelaySession);
        var token=Convert.FromHexString(session.Host?offer.BToken:offer.AToken);if(token.Length!=16)throw new IOException("中继能力令牌无效。");
        Diagnostics.Log("candidate-activation-verified",$"{leg.Route} elapsed_ms={activationWatch.Elapsed.TotalMilliseconds:F2}");
        var port=Wire.Port();leg.Child=Ard.Start(leg.Folder,false,port,remote,engine.Settings);
        Diagnostics.Log("candidate-process-started",$"{leg.Route} elapsed_ms={activationWatch.Elapsed.TotalMilliseconds:F2}");
        leg.Child.Output+=line=>Diagnostics.Log("transit-ard",line);
        await leg.Child.WaitFor("READY:",ct);
        var tcp=new TcpClient{NoDelay=true};
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback,port,ct);
            await Wire.WriteJson(tcp.GetStream(),new{route=leg.Route,token=Convert.ToHexString(token).ToLowerInvariant()},ct);
            if((await Wire.Read(tcp.GetStream(),1,ct))[0]!=0)throw new IOException("C 拒绝了转发入口。");
            var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(udp);leg.Udp=udp;udp.Connect(IPAddress.Loopback,port);
            var link=new OverlayLink(leg.Route,tcp,async(payload,cancel)=>
            {var packet=new byte[16+payload.Length];token.CopyTo(packet,0);payload.CopyTo(packet,16);await udp.SendAsync(packet,cancel);},()=>leg.Child.Network){HealthReady=false};
            link.Closed+=Wake;link.HealthChanged+=Wake;
            leg.Link=link;await overlay.Add(link);leg.Activated=true;
            leg.Reader=ReadDatagrams(leg,token);
            await overlay.RegisterDatagramPath(link);
            Diagnostics.Log("transit-active",leg.Route);
        }
        catch{tcp.Dispose();throw;}
    }
    async Task ReadDatagrams(Leg leg,byte[] token)
    {
        try
        {
            while(!stop.IsCancellationRequested&&leg.Link!.Live)
            {
                var packet=await leg.Udp!.ReceiveAsync(stop.Token);
                if(packet.Buffer.Length>=54&&CryptographicOperations.FixedTimeEquals(packet.Buffer.AsSpan(0,16),token))
                    await overlay.ReceiveUdp(leg.Link,packet.Buffer[16..]);
            }
        }
        catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("transit-udp",ex.Message);}
    }
    async Task<Leg?> EvaluateCandidate(TransitCandidate candidate,CancellationToken ct)
    {
        var route=Guid.NewGuid().ToString("N");var keep=false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(85));
        try
        {
            var leg=await Prepare(route,candidate.Endpoint,timeout.Token);
            var b=await overlay.Request("transit-prepare",new{route,candidate=candidate.Endpoint,aSession=leg.Endpoint},timeout.Token);
            Peer peer;lock(engine.State.Peers)peer=engine.State.Peers.Single(p=>p.Id==session.PeerId);
            await directory.Call<JsonElement>("/api/v2/transit/request",new{route,target=session.PeerId,candidate=candidate.Endpoint,aSession=leg.Endpoint,bSession=b.GetProperty("endpoint").GetString(),grant=peer.Grant,approval=b.GetProperty("approval")},timeout.Token);
            TransitReply? reply=null;
            while(reply?.Status!="ready")
            {
                await Task.Delay(500,timeout.Token);
                reply=await directory.Call<TransitReply>("/api/v2/transit/routes/"+route,new{},timeout.Token);
                if(reply.Status=="closed")throw new IOException("候选中继已关闭。");
            }
            var activation=new TransitActivation(reply.Ticket,reply.Offer??throw new IOException("缺少 C 签名。"));
            await Task.WhenAll(Activate(leg,activation,timeout.Token),overlay.Request("transit-activate",activation,timeout.Token));
            while(true)
            {
                // One half can activate seconds before the other; that is not a failed path.
                var bStatus=await overlay.Request("transit-status",new{route,direct=IsDirect(leg),ready=true},timeout.Token);
                leg.Link!.HealthReady=true;
                leg.PeerDirect=bStatus.GetProperty("direct").GetBoolean();
                if(IsDirect(leg)&&leg.PeerDirect)break;
                await Task.Delay(1000,timeout.Token);
            }
            leg.Quality=await overlay.Evaluate(leg.Link!,timeout.Token);
            Diagnostics.Log("candidate-quality",route+" "+JsonSerializer.Serialize(leg.Quality,Wire.Json));
            if(!leg.Quality.Eligible||!IsDirect(leg)||!leg.PeerDirect)return null;
            keep=true;return leg;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){Diagnostics.Log("candidate-failed",candidate.Endpoint+": "+ex);return null;}
        finally{if(!keep)await Close(route,true);}
    }
    async Task Choose(Leg leg,CancellationToken ct)
    {
        await switching.WaitAsync(ct);
        try
        {
            if(overlay.Selected==leg.Route)return;
            if(!IsDirect(leg)||!leg.Link!.Responsive)throw new IOException("切换前本端 Direct 或响应状态失败。");
            var selectionWatch=Stopwatch.StartNew();
            Diagnostics.Log("candidate-select-start",leg.Route+": from="+overlay.Selected);
            // Validate and select at the peer in one authenticated exchange. Separate
            // status/select requests add a relay RTT and use a stale validation result.
            await overlay.Request("transit-select",new{route=leg.Route,direct=true},ct);
            leg.PeerDirect=true;overlay.Select(leg.Route);
            Diagnostics.Log("candidate-select-complete",leg.Route+": coordination_ms="+selectionWatch.Elapsed.TotalMilliseconds.ToString("F2",System.Globalization.CultureInfo.InvariantCulture));
            awaitingReplacement=false;
            nextSelection=Environment.TickCount64+60000;
            Diagnostics.Log("candidate-chosen",leg.Route+" "+JsonSerializer.Serialize(leg.Quality,Wire.Json));
        }
        finally{switching.Release();}
    }
    public async Task<bool> ProbeCandidate(TransitCandidate candidate,bool testSelect,CancellationToken ct)
    {
        await selecting.WaitAsync(ct);Leg? leg=null;var chosen=false;
        try
        {
            leg=await EvaluateCandidate(candidate,ct);if(leg==null)return false;
            var baseline=await overlay.Evaluate(overlay.Link(overlay.Selected)!,ct);
            if(!testSelect&&(session.Process.Network.StartsWith("P2P",StringComparison.Ordinal)||!leg.Quality!.BetterThan(baseline)))return false;
            if(testSelect)testSelectionUntil=Environment.TickCount64+15000;
            await Choose(leg,ct);chosen=true;return true;
        }
        finally
        {
            if(leg!=null&&!chosen)await Close(leg.Route,true);selecting.Release();
        }
    }
    async Task SelectBest()
    {
        await selecting.WaitAsync(stop.Token);
        var evaluated=new List<Leg>();
        var pending=new List<Task<Leg?>>();
        try
        {
            var list=await directory.Call<TransitCandidates>("/api/v2/transit/candidates",new{target=session.PeerId},stop.Token);
            var current=overlay.Selected;
            var candidates=list.Candidates.Where(c=>legs.Values.All(l=>l.Candidate!=c.Endpoint)).Take(Math.Max(0,3-legs.Count)).ToArray();
            if(candidates.Length==0)return;
            var baselineTask=overlay.Evaluate(overlay.Link(current)??overlay.Link("base")!,stop.Token);
            pending.AddRange(candidates.Select(c=>EvaluateCandidate(c,stop.Token)));
            var baseline=await baselineTask;
            Diagnostics.Log("baseline-quality",JsonSerializer.Serialize(baseline,Wire.Json));
            // A slow or unreachable candidate must not hold an already verified improvement hostage.
            while(pending.Count!=0)
            {
                var completed=await Task.WhenAny(pending);pending.Remove(completed);
                var leg=await completed;if(leg==null)continue;evaluated.Add(leg);
                if(session.Process.Network.StartsWith("P2P",StringComparison.Ordinal))continue;
                var selected=overlay.Selected;
                var reference=selected==current?baseline:evaluated.FirstOrDefault(l=>l.Route==selected)?.Quality??overlay.Link(selected)?.Quality??baseline;
                if(!leg.Quality!.BetterThan(reference))continue;
                await Choose(leg,stop.Token);
                if(selected!="base"&&selected!=leg.Route)await Close(selected,true);
            }
        }
        catch(Exception ex){Diagnostics.Log("candidate-selection",ex.Message);}
        finally
        {
            foreach(var task in pending)
            {
                try{if(await task is{} leg)evaluated.Add(leg);}
                catch(Exception ex){Diagnostics.Log("candidate-evaluation-stop",ex.Message);}
            }
            // Keep one verified standby; rebuilding it after failure cannot provide a warm handover.
            var standby=legs.Values.Where(l=>l.Route!=overlay.Selected&&l.Quality?.Eligible==true&&IsDirect(l)&&l.PeerDirect&&l.Link!.Responsive)
                .OrderBy(l=>l.Quality!.Score).FirstOrDefault();
            if(standby!=null)Diagnostics.Log("candidate-standby",standby.Route);
            foreach(var leg in evaluated.Where(l=>l.Route!=overlay.Selected&&l!=standby))await Close(leg.Route,true);
            nextSelection=Environment.TickCount64+(standby==null?5000:60000);selecting.Release();
        }
    }
    async Task PromoteStandby()
    {
        if(session.Host||session.Process.Network.StartsWith("P2P",StringComparison.Ordinal))return;
        var current=overlay.Link(overlay.Selected);
        if(overlay.Selected!="base"&&current?.Live==true&&current.Responsive)return;
        foreach(var leg in legs.Values.Where(l=>l.Route!=overlay.Selected&&l.Quality?.Eligible==true&&IsDirect(l)&&l.PeerDirect&&l.Link!.Responsive&&l.Link.Quality.Eligible).OrderBy(l=>l.Link!.Quality.Score))
        {
            leg.Quality=leg.Link!.Quality;
            // Recovery is not permission to reuse a candidate's old good score.
            if(current?.Live==true&&current.Responsive&&(leg.Quality.DegradedFrom(current.Quality)||leg.Link.RecoverySlowerThan(current)))continue;
            // Failure replacement uses the already qualified standby; it is not a fresh
            // performance upgrade of the temporary relay fallback's incomplete measurements.
            if(!awaitingReplacement&&!overlay.RecoveringSelectedPath&&current?.Live==true&&current.Responsive&&!leg.Quality!.BetterThan(current.Quality))
            {Diagnostics.Log("candidate-standby-retained",leg.Route+": current path remains preferable");continue;}
            try{await Choose(leg,stop.Token);Diagnostics.Log("candidate-failover",leg.Route);return;}
            catch(Exception ex)when(!stop.IsCancellationRequested){Diagnostics.Log("candidate-failover-failed",ex.Message);}
        }
    }
    async Task PreferRecoveredCandidate()
    {
        if(session.Host||session.Process.Network.StartsWith("P2P",StringComparison.Ordinal)||overlay.Selected=="base")return;
        var current=overlay.Link(overlay.Selected);
        if(current?.Live!=true||!current.Responsive)return;
        // Existing standby routes are excluded from discovery. Recompare their live
        // measurements so a recovered better route is not permanently stranded.
        foreach(var leg in legs.Values.Where(l=>l.Route!=overlay.Selected&&IsDirect(l)&&l.PeerDirect&&l.Link!.Responsive)
            .OrderBy(l=>l.Link!.Quality.Score))
        {
            var quality=leg.Link!.Quality;
            if(!quality.BetterThan(current.Quality))continue;
            leg.Quality=quality;await Choose(leg,stop.Token);
            Diagnostics.Log("candidate-recovered-preferred",leg.Route);return;
        }
    }
    async Task Run()
    {
        var round=0;var initial=true;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                if(!initial)await wake.WaitAsync(TimeSpan.FromSeconds(3),stop.Token);initial=false;
                await PromoteStandby();
                var ended=legs.Values.Where(l=>l.Activated&&(l.Child!.Exited.IsCompleted||!l.Link!.Live)).ToArray();
                if(ended.Length!=0)
                {
                    // Failed transports invalidate the healthy-route discovery interval.
                    // Independent route teardown must not serialize peer notification timeouts.
                    nextSelection=0;
                    Diagnostics.Log("candidate-rebuild",$"ended={ended.Length}; discovery eligible immediately");
                    await Task.WhenAll(ended.Select(l=>Close(l.Route,!session.Host)));
                }
                foreach(var leg in legs.Values.ToArray())
                {
                    if(!leg.Activated)
                    {if(Environment.TickCount64-leg.Created>100000)await Close(leg.Route,false);continue;}
                    if(leg.Child!.Exited.IsCompleted||!leg.Link!.Live)
                    {await Close(leg.Route,!session.Host);continue;}
                    if(round%5==0)
                        await directory.Call<JsonElement>($"/api/v2/transit/routes/{leg.Route}/renew",new{},stop.Token);
                    if(!session.Host)
                    {
                        var status=await overlay.Request("transit-status",new{route=leg.Route,direct=IsDirect(leg)},stop.Token);
                        leg.PeerDirect=status.GetProperty("direct").GetBoolean();
                        if(overlay.Selected==leg.Route&&overlay.Link("base") is{} fallback)
                        {
                            var quality=leg.Link.Quality;var baseline=fallback.Quality;
                            var worse=quality.DegradedFrom(baseline);
                            leg.Degraded=worse?leg.Degraded+1:0;
                            if(leg.Degraded>=3){await Fallback("候选连续三轮退化");await Close(leg.Route,true);}
                        }
                    }
                    // A probe timeout is not a transport change. Refresh peer state before
                    // deciding, and let a live Iroh connection migrate instead of killing it.
                    if(!IsDirect(leg)||!leg.PeerDirect)
                    {
                        if(leg.DirectLostAt==0)leg.DirectLostAt=Environment.TickCount64;
                        if(overlay.Selected==leg.Route)
                        {awaitingReplacement=true;await Fallback("候选暂失双段 Direct，保留连接等待迁移");}
                        if(Environment.TickCount64-leg.DirectLostAt>=30000)await Close(leg.Route,!session.Host);
                    }
                    else leg.DirectLostAt=0;
                }
                if(!session.Host&&Environment.TickCount64>=testSelectionUntil&&session.Process.Network.StartsWith("P2P",StringComparison.Ordinal)&&overlay.Selected!="base")
                {
                    await Fallback("A–B 已达成 Direct");foreach(var route in legs.Keys)await Close(route,true);
                }
                await PromoteStandby();
                await PreferRecoveredCandidate();
                if(!session.Host&&selectionTask.IsCompleted&&selecting.CurrentCount!=0&&Environment.TickCount64>=nextSelection&&
                    !session.Process.Network.StartsWith("P2P",StringComparison.Ordinal))
                {
                    nextSelection=Environment.TickCount64+60000;selectionTask=SelectBest();
                }
                round++;
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){Diagnostics.Log("transit-coordinator",ex.Message);}
        }
    }
    async Task Fallback(string reason)
    {
        Diagnostics.Log("fallback",reason);
        nextSelection=Environment.TickCount64+5000;
        if(overlay.Link("base")?.Live==true)overlay.Select("base");
        try{await overlay.Request("select",new{path="base"},stop.Token);}
        catch(Exception ex){Diagnostics.Log("fallback-notify",ex.Message);}
    }
    async Task Close(string route,bool notify)
    {
        if(!legs.TryRemove(route,out var leg))return;
        if(overlay.Selected==route&&overlay.Link("base")?.Live==true)overlay.Select("base");
        await overlay.Remove(route);leg.Udp?.Dispose();
        if(leg.Child!=null)await leg.Child.DisposeAsync();
        try{await leg.Reader;}catch(Exception ex){Diagnostics.Log("transit-reader-close",ex.Message);}
        try{Directory.Delete(leg.Folder,true);}catch(IOException ex){Diagnostics.Log("transit-folder-close",ex.Message);}
        if(notify&&!stop.IsCancellationRequested)
        {
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await directory.Call<JsonElement>($"/api/v2/transit/routes/{route}/close",new{},timeout.Token);
                await overlay.Request("transit-close",new{route},timeout.Token);
            }
            catch(Exception ex){Diagnostics.Log("transit-close-notify",ex.Message);}
        }
    }
    public async ValueTask DisposeAsync()
    {
        overlay.Changed-=Wake;
        stop.Cancel();try{await Task.WhenAll(loop,selectionTask);}catch(Exception ex){Diagnostics.Log("transit-stop",ex.Message);}
        foreach(var route in legs.Keys)await Close(route,false);overlay.Control=null;
    }
}
