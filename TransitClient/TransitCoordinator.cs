namespace ArdUi;

sealed class TransitCoordinator : IAsyncDisposable
{
    readonly Engine engine;
    readonly DirectoryClient directory;
    readonly Session session;
    readonly OverlaySession overlay;
    readonly ConcurrentDictionary<string,Leg> legs=new();
    readonly CancellationTokenSource stop;
    readonly SemaphoreSlim selecting=new(1),keyGate=new(1);
    readonly Task loop;
    string? directoryKey;
    long testSelectionUntil;
    long nextSelection;
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
    }
    public TransitCoordinator(Engine engine,DirectoryClient directory,Session session)
    {
        this.engine=engine;this.directory=directory;this.session=session;overlay=session.Overlay!;
        stop=CancellationTokenSource.CreateLinkedTokenSource(session.Token);overlay.Control=Handle;
        loop=Run();
    }
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
        if(method=="transit-status")
        {
            if(!legs.TryGetValue(route,out var leg))return new{direct=false,network="closed",live=false};
            leg.PeerDirect=data.TryGetProperty("direct",out var direct)&&direct.GetBoolean();
            return new{direct=IsDirect(leg),network=leg.Child?.Network,live=leg.Link?.Live??false,rttMs=leg.Child?.PathRtt};
        }
        throw new IOException("未知中继协调指令。");
    }
    static bool IsDirect(Leg leg)=>leg.Child!=null&&!leg.Child.Exited.IsCompleted&&leg.Child.Network.StartsWith("P2P",StringComparison.Ordinal)&&leg.Link?.Live==true;
    async Task Activate(Leg leg,TransitActivation activation,CancellationToken ct)
    {
        if(leg.Activated)throw new IOException("中继入口已激活。");
        var ticket=DirectoryClient.Verify<TransitTicket>(activation.Ticket,"/transit-ticket/v2",await ServerKey(ct));
        if(ticket.Route!=leg.Route||ticket.C!=leg.Candidate||ticket.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()||
            ticket.A!=(session.Host?session.PeerId:engine.Id)||ticket.B!=(session.Host?engine.Id:session.PeerId)||
            (session.Host?ticket.BSession:ticket.ASession)!=leg.Endpoint)throw new CryptographicException("中继票据没有绑定当前双方和临时身份。");
        var offer=DirectoryClient.Verify<TransitOffer>(activation.Offer,$"/api/v2/transit/routes/{leg.Route}/ready",leg.Candidate);
        if(offer.Route!=leg.Route)throw new CryptographicException("C 的签名绑定了错误会话。");
        var remote=Wire.Endpoint(session.Host?offer.BRelaySession:offer.ARelaySession);
        var token=Convert.FromHexString(session.Host?offer.BToken:offer.AToken);if(token.Length!=16)throw new IOException("中继能力令牌无效。");
        var port=Wire.Port();leg.Child=Ard.Start(leg.Folder,false,port,remote,engine.Settings);
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
            {var packet=new byte[16+payload.Length];token.CopyTo(packet,0);payload.CopyTo(packet,16);await udp.SendAsync(packet,cancel);},()=>leg.Child.Network);
            leg.Link=link;await overlay.Add(link);leg.Activated=true;
            leg.Reader=ReadDatagrams(leg,token);Diagnostics.Log("transit-active",leg.Route);
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
                var bStatus=await overlay.Request("transit-status",new{route,direct=IsDirect(leg)},timeout.Token);
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
        catch(Exception ex){Diagnostics.Log("candidate-failed",candidate.Endpoint+": "+ex.Message);return null;}
        finally{if(!keep)await Close(route,true);}
    }
    async Task Choose(Leg leg,CancellationToken ct)
    {
        var status=await overlay.Request("transit-status",new{route=leg.Route,direct=IsDirect(leg)},ct);
        leg.PeerDirect=status.GetProperty("direct").GetBoolean();
        if(!IsDirect(leg)||!leg.PeerDirect)throw new IOException("切换前复核双段 Direct 失败。");
        await overlay.Request("select",new{path=leg.Route},ct);overlay.Select(leg.Route);
        nextSelection=Environment.TickCount64+60000;
        Diagnostics.Log("candidate-chosen",leg.Route+" "+JsonSerializer.Serialize(leg.Quality,Wire.Json));
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
        try
        {
            var list=await directory.Call<TransitCandidates>("/api/v2/transit/candidates",new{target=session.PeerId},stop.Token);
            var current=overlay.Selected;
            var candidates=list.Candidates.Where(c=>legs.Values.All(l=>l.Candidate!=c.Endpoint)).Take(Math.Max(0,3-legs.Count)).ToArray();
            if(candidates.Length==0)return;
            var baselineTask=overlay.Evaluate(overlay.Link(current)??overlay.Link("base")!,stop.Token);
            var results=await Task.WhenAll(candidates.Select(c=>EvaluateCandidate(c,stop.Token)));
            evaluated.AddRange(results.OfType<Leg>());var baseline=await baselineTask;
            Diagnostics.Log("baseline-quality",JsonSerializer.Serialize(baseline,Wire.Json));
            if(session.Process.Network.StartsWith("P2P",StringComparison.Ordinal))return;
            var best=evaluated.Where(l=>l.Quality!.BetterThan(baseline)).OrderBy(l=>l.Quality!.Score).ThenByDescending(l=>l.Quality!.Mbps).FirstOrDefault();
            if(best==null)return;
            await Choose(best,stop.Token);
            if(current!="base"&&current!=best.Route)await Close(current,true);
        }
        catch(Exception ex){Diagnostics.Log("candidate-selection",ex.Message);}
        finally
        {
            foreach(var leg in evaluated.Where(l=>l.Route!=overlay.Selected))await Close(leg.Route,true);
            nextSelection=Environment.TickCount64+60000;selecting.Release();
        }
    }
    async Task Run()
    {
        var round=0;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000,stop.Token);
                foreach(var leg in legs.Values.ToArray())
                {
                    if(!leg.Activated)
                    {if(Environment.TickCount64-leg.Created>100000)await Close(leg.Route,false);continue;}
                    if(leg.Child!.Exited.IsCompleted||!leg.Link!.Live)
                    {await Close(leg.Route,!session.Host);continue;}
                    if(overlay.Selected==leg.Route&&(!IsDirect(leg)||!leg.PeerDirect))
                    {await Fallback("候选路径已不满足双段 Direct");await Close(leg.Route,!session.Host);continue;}
                    if(round%5==0)
                        await directory.Call<JsonElement>($"/api/v2/transit/routes/{leg.Route}/renew",new{},stop.Token);
                    if(!session.Host)
                    {
                        var status=await overlay.Request("transit-status",new{route=leg.Route,direct=IsDirect(leg)},stop.Token);
                        leg.PeerDirect=status.GetProperty("direct").GetBoolean();
                        if(overlay.Selected==leg.Route&&overlay.Link("base") is{} fallback)
                        {
                            var quality=leg.Link.Quality;var baseline=fallback.Quality;
                            var worse=quality.Samples>=8&&baseline.Samples>=8&&
                                (quality.Loss>.25&&quality.Loss>baseline.Loss||quality.Score>baseline.Score*1.3+5);
                            leg.Degraded=worse?leg.Degraded+1:0;
                            if(leg.Degraded>=3){await Fallback("候选连续三轮退化");await Close(leg.Route,true);}
                        }
                    }
                }
                if(!session.Host&&Environment.TickCount64>=testSelectionUntil&&session.Process.Network.StartsWith("P2P",StringComparison.Ordinal)&&overlay.Selected!="base")
                {
                    await Fallback("A–B 已达成 Direct");foreach(var route in legs.Keys)await Close(route,true);
                }
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
        stop.Cancel();try{await Task.WhenAll(loop,selectionTask);}catch(Exception ex){Diagnostics.Log("transit-stop",ex.Message);}
        foreach(var route in legs.Keys)await Close(route,false);overlay.Control=null;
    }
}
