namespace ArdUi;

sealed class OverlaySession : IAsyncDisposable
{
    const int Chunk=16384,Window=512,Fragment=1100;
    readonly bool caller;
    readonly int basePort,targetPort;
    readonly byte[] capability;
    readonly Func<string> baseNetwork;
    readonly OverlayCipher cipher;
    readonly CancellationTokenSource stop=new();
    readonly ConcurrentDictionary<string,OverlayLink> links=new();
    readonly ConcurrentDictionary<uint,OverlayFlow> flows=new();
    readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> controls=new();
    readonly ConcurrentDictionary<long,TaskCompletionSource<bool>> probes=new();
    readonly SortedDictionary<long,byte[]> pending=new(),reorder=new();
    readonly SemaphoreSlim window=new(Window),incomingSignal=new(0,1),controlSlots=new(8),bandwidthGate=new(1);
    readonly Channel<(OverlayLink Link,byte[] Frame,bool Udp)> replies=Channel.CreateBounded<(OverlayLink,byte[],bool)>(128);
    readonly SemaphoreSlim ackSignal=new(0,1);
    readonly object incomingGate=new();
    readonly object sequenceGate=new(),udpGate=new();
    readonly TcpListener? listener;
    readonly UdpClient? facade;
    readonly Dictionary<IPEndPoint,uint> udpIds=new();
    readonly Dictionary<uint,long> udpTouched=new();
    readonly ConcurrentDictionary<uint,IPEndPoint> udpRemotes=new();
    readonly ConcurrentDictionary<uint,TargetUdp> targetUdp=new();
    readonly Dictionary<(uint,long),Fragments> fragments=new();
    readonly Task maintenance,orderedReceiver,replyWriter,ackWriter;
    Task accept=Task.CompletedTask,datagramReader=Task.CompletedTask,reconnect=Task.CompletedTask;
    long sendSequence,receiveSequence,probeId,udpSequence,missingSince,lastResend;
    int flowSerial,disposed;
    string selected="base";
    public string Id{get;}
    public int Port=>listener==null?0:((IPEndPoint)listener.LocalEndpoint).Port;
    public bool Expired=>stop.IsCancellationRequested||missingSince!=0&&Environment.TickCount64-missingSince>45000;
    public string Selected=>selected;
    public string Network=>selected=="base"?baseNetwork():"ArdTransit / "+selected[..Math.Min(6,selected.Length)];
    public Func<string,JsonElement,CancellationToken,Task<object>>? Control;
    public Action? Changed;
    public long Retransmits,Duplicates,UdpFragments,UdpDrops,Switches;
    public CancellationToken Token=>stop.Token;
    sealed class Fragments(int count,long time)
    {public byte[][] Parts{get;}=new byte[count][];public long Time{get;}=time;public int Bytes;}
    OverlaySession(string id,bool caller,int port,byte[] capability,byte[] secret,byte[] context,Func<string> network)
    {
        Id=id;this.caller=caller;this.capability=capability;this.baseNetwork=network;
        basePort=caller?port:0;targetPort=caller?0:port;cipher=new(secret,context,caller);
        if(caller)
        {
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start();facade=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port));Wire.ConfigureUdp(facade);
            accept=Accept();datagramReader=ReadFacade();
        }
        orderedReceiver=ReceiveOrdered();replyWriter=WriteReplies();ackWriter=WriteAcks();maintenance=Maintain();
    }
    static byte[] Derive(ECDiffieHellman key,string remote)
    {
        var blob=Convert.FromBase64String(remote);using var other=ECDiffieHellman.Create();other.ImportSubjectPublicKeyInfo(blob,out var consumed);
        if(consumed!=blob.Length||other.KeySize!=256||other.ExportParameters(false).Curve.Oid.Value!=ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("覆盖层密钥曲线无效。");
        return key.DeriveRawSecretAgreement(other.PublicKey);
    }
    static byte[] Context(OverlayHello a,OverlayHello b,byte[] cap)=>SHA256.HashData(Encoding.UTF8.GetBytes($"ArdUi/2\n{a.Session}\n{a.Key}\n{b.Key}\n{Convert.ToHexString(cap)}"));
    public static async Task<OverlaySession> Connect(int port,byte[] cap,Func<string> network,CancellationToken ct)
    {
        var tcp=await ConnectBase(port,cap,4,ct);
        try
        {
            using var key=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var hello=new OverlayHello(Guid.NewGuid().ToString("N"),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            await Wire.WriteJson(tcp.GetStream(),hello,ct);var reply=await Wire.ReadJson<OverlayHello>(tcp.GetStream(),ct);
            if(reply.Session!=hello.Session)throw new IOException("覆盖层会话编号不匹配。");
            var secret=Derive(key,reply.Key);OverlaySession session;
            try{session=new(hello.Session,true,port,cap,secret,Context(hello,reply,cap),network);}finally{CryptographicOperations.ZeroMemory(secret);}
            await session.AttachClientBase(tcp);session.reconnect=session.ReconnectBase();
            for(var attempt=0;attempt<3;attempt++)
            {
                try{await session.Probe(session.Link("base")!,true);break;}
                catch(Exception ex){Diagnostics.Log("udp-warmup",ex.Message);ct.ThrowIfCancellationRequested();}
            }
            return session;
        }
        catch{tcp.Dispose();throw;}
    }
    public static async Task<OverlaySession> AcceptHello(TcpClient tcp,int port,byte[] cap,Func<string> network,CancellationToken ct)
    {
        var hello=await Wire.ReadJson<OverlayHello>(tcp.GetStream(),ct);
        if(!Regex.IsMatch(hello.Session,@"\A[0-9a-f]{32}\z"))throw new IOException("覆盖层编号无效。");
        using var key=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var reply=new OverlayHello(hello.Session,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));var secret=Derive(key,hello.Key);
        try
        {
            var session=new OverlaySession(hello.Session,false,port,cap,secret,Context(hello,reply,cap),network);
            try{await Wire.WriteJson(tcp.GetStream(),reply,ct);return session;}catch{await session.DisposeAsync();throw;}
        }
        finally{CryptographicOperations.ZeroMemory(secret);}
    }
    static async Task<TcpClient> ConnectBase(int port,byte[] cap,byte command,CancellationToken ct)
    {
        var tcp=new TcpClient{NoDelay=true};
        try
        {await tcp.ConnectAsync(IPAddress.Loopback,port,ct);var header=new byte[23];"AUI1"u8.CopyTo(header);cap.CopyTo(header,4);header[20]=command;await tcp.GetStream().WriteAsync(header,ct);return tcp;}
        catch{tcp.Dispose();throw;}
    }
    async Task AttachClientBase(TcpClient tcp)
    {
        var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(udp);udp.Connect(IPAddress.Loopback,basePort);
        var link=new OverlayLink("base",tcp,async(data,ct)=>await udp.SendAsync(Wire.Packet(capability,65535,data),ct),baseNetwork);
        link.Closed+=()=>udp.Dispose();await Add(link);
        _=Task.Run(async()=>
        {
            try
            {
                while(!link.Token.IsCancellationRequested)
                {var p=await udp.ReceiveAsync(link.Token);if(Wire.ValidPacket(p.Buffer,capability)&&BinaryPrimitives.ReadUInt16BigEndian(p.Buffer.AsSpan(16))==65535)await Receive(link,p.Buffer[18..],true);}
            }
            catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("base-udp",ex.Message);}
        });
    }
    async Task ReconnectBase()
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000,stop.Token);if(links.TryGetValue("base",out var live)&&live.Live)continue;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var tcp=await ConnectBase(basePort,capability,5,timeout.Token);
                try
                {
                    await Wire.WriteJson(tcp.GetStream(),new{session=Id},timeout.Token);
                    if((await Wire.Read(tcp.GetStream(),1,timeout.Token))[0]!=0)throw new IOException("覆盖层恢复被拒绝。");
                    await AttachClientBase(tcp);
                }
                catch{tcp.Dispose();throw;}
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){Diagnostics.Log("base-reconnect",ex.Message);}
        }
    }
    public async Task Add(OverlayLink link)
    {
        if(links.TryGetValue(link.Name,out var previous))await previous.DisposeAsync();
        links[link.Name]=link;link.Reader=link.Read(Receive);Diagnostics.Log("path-added",link.Name);Changed?.Invoke();
    }
    public OverlayLink? Link(string name)=>links.TryGetValue(name,out var link)?link:null;
    public async Task Remove(string name)
    {if(links.TryRemove(name,out var link))await link.DisposeAsync();if(selected==name)Select("base");}
    public void Select(string name)
    {
        if(selected==name)return;if(!links.TryGetValue(name,out var link)||!link.Live)throw new IOException("目标路径未就绪。");
        selected=name;Interlocked.Increment(ref Switches);lastResend=0;Diagnostics.Log("path-selected",name);Changed?.Invoke();
    }
    static byte[] Frame(byte kind,long seq,uint flow,ReadOnlySpan<byte> body)
    {
        var data=new byte[13+body.Length];data[0]=kind;BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(1),seq);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(9),flow);body.CopyTo(data.AsSpan(13));return data;
    }
    async Task SendOn(OverlayLink link,byte[] plain,bool udp)
    {await link.Send(cipher.Encrypt(plain,udp),udp,stop.Token);}
    void Reply(OverlayLink link,byte[] frame,bool udp)
    {if(!replies.Writer.TryWrite((link,frame,udp)))Diagnostics.Log("probe-reply-dropped","Reply queue is full.");}
    void Ack()
    {lock(incomingGate){if(ackSignal.CurrentCount==0)ackSignal.Release();}}
    async Task WriteReplies()
    {
        try
        {
            await foreach(var reply in replies.Reader.ReadAllAsync(stop.Token))
                try{await SendOn(reply.Link,reply.Frame,reply.Udp);}
                catch(Exception ex){Diagnostics.Log("reply-deferred",ex.Message);}
        }
        catch(OperationCanceledException ex){Diagnostics.Log("reply-writer-stopped",ex.Message);}
    }
    async Task WriteAcks()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await ackSignal.WaitAsync(stop.Token);long acknowledged;
                lock(incomingGate)acknowledged=receiveSequence;
                if(Active() is{} link)
                    try{await SendOn(link,Frame(9,acknowledged,0,[]),false);}
                    catch(Exception ex){Diagnostics.Log("ack-deferred",ex.Message);}
            }
        }
        catch(OperationCanceledException ex){Diagnostics.Log("ack-writer-stopped",ex.Message);}
    }
    OverlayLink? Active()
    {
        if(links.TryGetValue(selected,out var active)&&active.Live)return active;
        if(links.TryGetValue("base",out var fallback)&&fallback.Live){Select("base");return fallback;}
        return null;
    }
    async Task SendReliable(byte kind,uint flow,byte[] body)
    {
        await window.WaitAsync(stop.Token);byte[] frame;
        lock(sequenceGate){frame=Frame(kind,++sendSequence,flow,body);pending.Add(sendSequence,frame);}
        if(Active() is{} link)
            try{await SendOn(link,frame,false);}catch(Exception ex)when(!stop.IsCancellationRequested){Diagnostics.Log("send-deferred",ex.Message);}
    }
    async Task Receive(OverlayLink link,byte[] packet,bool udp)
    {
        var frame=cipher.Decrypt(packet,udp);if(frame==null)return;
        var kind=frame[0];var seq=BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(1));var flow=BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9));
        if(udp)
        {
            if(kind==10){Reply(link,Frame(11,seq,flow,frame.AsSpan(13)),true);return;}
            if(kind==11){if(probes.TryRemove(seq,out var probe))probe.TrySetResult(true);return;}
            if(kind==12)await Datagram(flow,seq,frame[13..]);return;
        }
        if(kind==9)
        {
            lock(sequenceGate)
            {
                if(seq<0||seq>sendSequence)throw new IOException("覆盖层确认越界。");
                foreach(var id in pending.Keys.TakeWhile(n=>n<=seq).ToArray()){pending.Remove(id);window.Release();}
            }
            return;
        }
        if(kind==10){Reply(link,Frame(11,seq,flow,frame.AsSpan(13)),false);return;}
        if(kind==11){if(probes.TryRemove(seq,out var probe))probe.TrySetResult(true);return;}
        if(kind is <1 or >6||seq<=0)throw new IOException("覆盖层指令无效。");
        lock(incomingGate)
        {
            if(seq<=receiveSequence){Duplicates++;}
            else
            {
                if(seq>receiveSequence+Window)throw new IOException("覆盖层接收窗口超限。");
                reorder.TryAdd(seq,frame);
                if(incomingSignal.CurrentCount==0)incomingSignal.Release();
            }
        }
        Ack();
    }
    async Task ReceiveOrdered()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await incomingSignal.WaitAsync(stop.Token);
                while(true)
                {
                    byte[]? next;lock(incomingGate)reorder.TryGetValue(receiveSequence+1,out next);
                    if(next==null)break;
                    await Dispatch(next);
                    lock(incomingGate)reorder.Remove(++receiveSequence);
                    Ack();
                }
            }
        }
        catch(Exception ex){Diagnostics.Log("ordered-receiver",ex.Message);if(!stop.IsCancellationRequested)stop.Cancel();}
    }
    async Task Dispatch(byte[] frame)
    {
        var kind=frame[0];var id=BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9));var body=frame[13..];
        if(kind==5||kind==6)
        {
            var message=JsonSerializer.Deserialize<OverlayControl>(body,Wire.Json)??throw new IOException("空控制消息。");
            if(kind==6)
            {if(controls.TryRemove(message.Id,out var waiter)){if(message.Error!=null)waiter.TrySetException(new IOException(message.Error));else waiter.TrySetResult(message.Data);}return;}
            if(!controlSlots.Wait(0))throw new IOException("对端控制请求过多。");
            _=HandleControl(message);return;
        }
        if(kind==1)
        {
            if(caller||id==0||flows.Count>=128||flows.ContainsKey(id)){_=ResetFlow(id);return;}
            var flow=new OverlayFlow(this,id,null);if(flows.TryAdd(id,flow))flow.Start();return;
        }
        if(!flows.TryGetValue(id,out var existing))return;
        if(kind==4){existing.Close();return;}
        try{await existing.Enqueue(kind==3?[]:body);}
        catch(Exception ex){Diagnostics.Log("flow-queue",ex.Message);existing.Close();_=ResetFlow(id);}
    }
    async Task ResetFlow(uint id)
    {try{await SendReliable(4,id,[]);}catch(Exception ex){Diagnostics.Log("flow-reset",ex.Message);}}
    async Task HandleControl(OverlayControl message)
    {
        try
        {
            object result;
            if(message.Method=="select")
            {
                var name=message.Data.GetProperty("path").GetString()!;
                if(name!="base"&&(!(Link(name)?.Network.StartsWith("P2P",StringComparison.Ordinal)??false)))throw new IOException("本端候选未直连。");
                Select(name);result=new{ok=true};
            }
            else result=Control!=null?await Control(message.Method,message.Data,stop.Token):throw new IOException("中继协调尚未就绪。");
            await SendReliable(6,0,JsonSerializer.SerializeToUtf8Bytes(message with{Data=JsonSerializer.SerializeToElement(result,Wire.Json)},Wire.Json));
        }
        catch(Exception ex)
        {
            Diagnostics.Log("control",message.Method+": "+ex.Message);
            if(!stop.IsCancellationRequested)
                try{await SendReliable(6,0,JsonSerializer.SerializeToUtf8Bytes(message with{Data=JsonSerializer.SerializeToElement(new{}),Error=ex.Message},Wire.Json));}
                catch(Exception sendError){Diagnostics.Log("control-reply",sendError.Message);}
        }
        finally{controlSlots.Release();}
    }
    public async Task<JsonElement> Request(string method,object value,CancellationToken ct)
    {
        if(controls.Count>=16)throw new IOException("协调请求过多。");
        var id=Guid.NewGuid().ToString("N");var waiter=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);controls[id]=waiter;
        try
        {
            await SendReliable(5,0,JsonSerializer.SerializeToUtf8Bytes(new OverlayControl(id,method,JsonSerializer.SerializeToElement(value,Wire.Json)),Wire.Json));
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(75),ct);
        }
        finally{controls.TryRemove(id,out _);}
    }
    public async Task<double> Probe(OverlayLink link,bool udp,int bytes=32)
    {
        var id=Interlocked.Increment(ref probeId);var waiter=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);probes[id]=waiter;
        var watch=Stopwatch.StartNew();Interlocked.Increment(ref link.Probes);
        try
        {
            await SendOn(link,Frame(10,id,0,new byte[bytes]),udp);
            await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2),stop.Token);
            var rtt=watch.Elapsed.TotalMilliseconds;
            if(udp){link.JitterMs=link.RttMs is{} old?Math.Abs(old-rtt):0;link.RttMs=rtt;link.Sample(rtt);}
            else if(bytes>1024)link.BandwidthMbps=bytes*8d/Math.Max(.001,watch.Elapsed.TotalSeconds)/1e6;
            return rtt;
        }
        catch{Interlocked.Increment(ref link.Lost);if(udp)link.Sample(null);throw;}
        finally{probes.TryRemove(id,out _);}
    }
    async Task Maintain()
    {
        var round=0;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var active=Active();
                ExpireUdp(Environment.TickCount64);
                if(active==null){if(missingSince==0)missingSince=Environment.TickCount64;}
                else{missingSince=0;if(active.Name!=selected)Select(active.Name);}
                if(Environment.TickCount64-lastResend>=1000&&active!=null)
                {
                    lastResend=Environment.TickCount64;byte[][] frames;lock(sequenceGate)frames=pending.Values.Take(64).ToArray();
                    foreach(var frame in frames){await SendOn(active,frame,false);Retransmits++;}
                }
                foreach(var link in links.Values.Where(l=>l.Live).ToArray())
                {
                    try
                    {
                        await Probe(link,true);
                        if(round%15==0&&bandwidthGate.Wait(0))
                            try{await Probe(link,false,32768);}finally{bandwidthGate.Release();}
                    }
                    catch(Exception ex)when(!stop.IsCancellationRequested)
                    {
                        Diagnostics.Log("probe",link.Name+": "+ex.Message);
                        try{await Probe(link,false);}catch(Exception tcpError){Diagnostics.Log("path-unhealthy",tcpError.Message);await link.DisposeAsync();}
                    }
                }
                round++;await Task.Delay(500,stop.Token);
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){Diagnostics.Log("overlay-maintenance",ex.Message);await Task.Delay(300,stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);}
        }
    }
    public async Task<PathQuality> Evaluate(OverlayLink link,CancellationToken ct)
    {
        for(var warmup=0;warmup<3;warmup++)
        {try{await Probe(link,true);break;}catch(Exception ex){Diagnostics.Log("candidate-warmup",ex.Message);ct.ThrowIfCancellationRequested();}}
        var samples=new List<double?>();
        for(var i=0;i<8;i++)
        {
            ct.ThrowIfCancellationRequested();
            try{samples.Add(await Probe(link,true));}catch(Exception ex){Diagnostics.Log("quality-probe",ex.Message);samples.Add(null);}
            await Task.Delay(100,ct);
        }
        double? bandwidth=null;
        await bandwidthGate.WaitAsync(ct);
        try
        {
            var watch=Stopwatch.StartNew();await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Probe(link,false,32768)));
            bandwidth=8*32768*8d/Math.Max(.001,watch.Elapsed.TotalSeconds)/1e6;link.BandwidthMbps=bandwidth;
        }
        catch(Exception ex){Diagnostics.Log("quality-bandwidth",ex.Message);ct.ThrowIfCancellationRequested();}
        finally{bandwidthGate.Release();}
        return PathQuality.From(samples.ToArray(),bandwidth);
    }
    async Task Accept()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var tcp=await listener!.AcceptTcpClientAsync(stop.Token);
                if(flows.Count>=128){tcp.Dispose();continue;}
                var id=checked((uint)Interlocked.Increment(ref flowSerial));var flow=new OverlayFlow(this,id,tcp);
                if(!flows.TryAdd(id,flow)){tcp.Dispose();continue;}await SendReliable(1,id,[]);flow.Start();
            }
        }
        catch(Exception ex)when(stop.IsCancellationRequested){Diagnostics.Log("listener-stopped",ex.Message);}
    }
    internal void ExpireUdp(long now)
    {
        lock(udpGate)
            foreach(var flow in udpTouched.Where(p=>now-p.Value>120000).Select(p=>p.Key).ToArray())
            {
                udpTouched.Remove(flow);
                if(udpRemotes.TryRemove(flow,out var remote))udpIds.Remove(remote);
            }
    }
    internal int UdpFlowCount=>udpRemotes.Count;
    async Task ReadFacade()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await facade!.ReceiveAsync(stop.Token);if(packet.Buffer.Length>1500)continue;
                uint flow;lock(udpGate)
                {
                    if(!udpIds.TryGetValue(packet.RemoteEndPoint,out flow))
                    {if(udpIds.Count>=256){UdpDrops++;continue;}flow=checked((uint)Interlocked.Increment(ref flowSerial));udpIds[packet.RemoteEndPoint]=flow;udpRemotes[flow]=packet.RemoteEndPoint;}
                    udpTouched[flow]=Environment.TickCount64;
                }
                await SendDatagram(flow,packet.Buffer);
            }
        }
        catch(Exception ex)when(ex is SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("facade-udp",ex.Message);}
    }
    async Task SendDatagram(uint flow,byte[] data)
    {
        var link=Active();if(link==null){UdpDrops++;return;}
        var id=Interlocked.Increment(ref udpSequence);var count=(data.Length+Fragment-1)/Fragment;
        for(var i=0;i<count;i++)
        {
            var payload=new byte[2+Math.Min(Fragment,data.Length-i*Fragment)];payload[0]=(byte)i;payload[1]=(byte)count;data.AsSpan(i*Fragment,payload.Length-2).CopyTo(payload.AsSpan(2));
            try{await SendOn(link,Frame(12,id,flow,payload),true);UdpFragments++;}
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){UdpDrops++;Diagnostics.Log("udp-send",ex.Message);}
        }
    }
    async Task Datagram(uint flow,long id,byte[] payload)
    {
        if(payload.Length<3||payload[1] is <1 or >2||payload[0]>=payload[1]||payload.Length>Fragment+2){UdpDrops++;return;}
        byte[]? data=null;
        lock(udpGate)
        {
            foreach(var old in fragments.Where(p=>Environment.TickCount64-p.Value.Time>2000).Select(p=>p.Key).ToArray()){fragments.Remove(old);UdpDrops++;}
            var key=(flow,id);
            if(!fragments.TryGetValue(key,out var buffer))
            {if(fragments.Count>=256){UdpDrops++;return;}fragments[key]=buffer=new(payload[1],Environment.TickCount64);}
            if(buffer.Parts.Length!=payload[1]){UdpDrops++;return;}
            if(buffer.Parts[payload[0]]==null){buffer.Parts[payload[0]]=payload[2..];buffer.Bytes+=payload.Length-2;}
            if(buffer.Bytes>1500){fragments.Remove(key);UdpDrops++;return;}
            if(buffer.Parts.All(p=>p!=null)){data=buffer.Parts.SelectMany(p=>p).ToArray();fragments.Remove(key);}
        }
        if(data==null)return;
        if(caller)
        {
            IPEndPoint? remote;
            lock(udpGate){if(udpRemotes.TryGetValue(flow,out remote))udpTouched[flow]=Environment.TickCount64;}
            if(remote!=null)await facade!.SendAsync(data,remote,stop.Token);return;
        }
        if(!targetUdp.TryGetValue(flow,out var target))
        {
            if(targetUdp.Count>=256){UdpDrops++;return;}
            target=new TargetUdp(targetPort,reply=>SendDatagram(flow,reply),stop.Token,1500);targetUdp[flow]=target;
            var captured=target;_=target.Completion.ContinueWith(t=>{targetUdp.TryRemove(new KeyValuePair<uint,TargetUdp>(flow,captured));captured.Dispose();},TaskScheduler.Default);
        }
        await target.Send(data);
    }
    public async Task ReceiveBaseUdp(byte[] data)
    {if(Link("base") is{} link)await Receive(link,data,true);}
    public async Task ReceiveUdp(OverlayLink link,byte[] data)=>await Receive(link,data,true);
    public object Snapshot()
    {
        int buffered;lock(sequenceGate)buffered=pending.Values.Sum(p=>p.Length);
        return new{selected,flows=flows.Count,udpFlows=caller?udpRemotes.Count:targetUdp.Count,bufferedBytes=buffered,Retransmits,Duplicates,UdpFragments,UdpDrops,Switches,
            replayRejected=cipher.Replays,authenticationRejected=cipher.Rejected,
            paths=links.Values.Select(l=>new{l.Name,l.Live,l.Network,l.RttMs,l.JitterMs,l.BandwidthMbps,l.Probes,l.Lost,l.Sent,l.Received,quality=l.Quality}).ToArray()};
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();listener?.Stop();facade?.Dispose();
        foreach(var pendingControl in controls.Values)pendingControl.TrySetCanceled();
        foreach(var link in links.Values)await link.DisposeAsync();foreach(var flow in flows.Values)flow.Close();foreach(var udp in targetUdp.Values)udp.Dispose();
        try{await Task.WhenAll(links.Values.Select(l=>l.Reader).Concat([maintenance,orderedReceiver,replyWriter,ackWriter,accept,datagramReader,reconnect]));}catch(Exception ex){Diagnostics.Log("overlay-close",ex.Message);}
        cipher.Dispose();
    }
    sealed class OverlayFlow(OverlaySession owner,uint id,TcpClient? accepted)
    {
        readonly Channel<byte[]> received=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        readonly CancellationTokenSource stop=CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        TcpClient? socket=accepted;
        public ValueTask Enqueue(byte[] data)=>received.Writer.WriteAsync(data,stop.Token);
        public void Start()=>_=Run();
        public void Close(){stop.Cancel();socket?.Dispose();received.Writer.TryComplete();}
        async Task Run()
        {
            try
            {
                if(socket==null){socket=new TcpClient{NoDelay=true};await socket.ConnectAsync(IPAddress.Loopback,owner.targetPort,stop.Token);}
                socket.NoDelay=true;var stream=socket.GetStream();
                async Task Read()
                {
                    var buffer=new byte[Chunk];int size;
                    while((size=await stream.ReadAsync(buffer,stop.Token))>0)await owner.SendReliable(2,id,buffer[..size]);
                    await owner.SendReliable(3,id,[]);
                }
                async Task Write()
                {
                    await foreach(var data in received.Reader.ReadAllAsync(stop.Token))
                    {if(data.Length==0){socket.Client.Shutdown(SocketShutdown.Send);break;}await stream.WriteAsync(data,stop.Token);}
                }
                var read=Read();var write=Write();var first=await Task.WhenAny(read,write);if(first.IsFaulted||first.IsCanceled)Close();await Task.WhenAll(read,write);
            }
            catch(Exception ex)
            {
                Diagnostics.Log("tcp-flow",ex.Message);
                if(!owner.stop.IsCancellationRequested)try{await owner.SendReliable(4,id,[]);}catch(Exception sendError){Diagnostics.Log("tcp-reset",sendError.Message);}
            }
            finally{Close();owner.flows.TryRemove(new KeyValuePair<uint,OverlayFlow>(id,this));}
        }
    }
}
