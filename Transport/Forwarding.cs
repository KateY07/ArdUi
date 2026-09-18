namespace ArdUi;

sealed class ForwardHub : IAsyncDisposable
{
    readonly object sync=new();
    readonly Dictionary<int,LocalForwarder> forwards=new();
    Session? current;
    int disposed;
    public Dictionary<string,string> Mappings { get; }=new();
    public Session? Current { get { lock(sync)return current; } }
    public void Attach(Session session)
    {
        LocalForwarder[] bridges;
        lock(sync){if(disposed!=0)throw new ObjectDisposedException(nameof(ForwardHub));current=session;bridges=forwards.Values.ToArray();}
        foreach(var bridge in bridges)bridge.Reset();
    }
    public void Detach(Session session)
    {
        LocalForwarder[] bridges;
        lock(sync)
        {
            if(!ReferenceEquals(current,session))return;
            current=null;bridges=forwards.Values.ToArray();
        }
        foreach(var bridge in bridges)bridge.Reset();
    }
    public LocalForwarder Forward(int target)
    {
        lock(sync)
        {
            if(disposed!=0)throw new ObjectDisposedException(nameof(ForwardHub));
            var session=current;
            if(session==null||!session.Live)throw new IOException("设备正在自动重连。");
            if(!session.TcpPorts.Contains(target))throw new IOException("被控端未授权此 TCP 端口。");
            if(!forwards.TryGetValue(target,out var bridge))forwards[target]=bridge=new LocalForwarder(this,target,session.UdpPorts.Contains(target));
            return bridge;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        LocalForwarder[] bridges;lock(sync){current=null;bridges=forwards.Values.ToArray();forwards.Clear();}
        KeyValuePair<string,string>[] mappings;lock(Mappings){mappings=Mappings.ToArray();Mappings.Clear();}
        foreach(var mapping in mappings)await WindowsShares.Remove(mapping.Key,mapping.Value);
        foreach(var bridge in bridges)await bridge.DisposeAsync();
    }
}

sealed class LocalForwarder : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly UdpClient? udp;
    readonly ForwardHub hub;
    readonly int target;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int,Task> tasks = new();
    readonly SemaphoreSlim slots = new(128);
    readonly Task accept;
    readonly Task receive;
    readonly ConcurrentDictionary<IPEndPoint,LocalUdpFlow> udpFlows = new();
    int serial;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public LocalForwarder(ForwardHub hub,int target,bool udpEnabled)
    {
        this.hub=hub;this.target=target;
        listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        if(udpEnabled)udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,Port));
        if(udp!=null)Wire.ConfigureUdp(udp);
        accept=Accept();receive=udp == null ? Task.CompletedTask : ReceiveUdp();
    }
    async Task Accept()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                if(!slots.Wait(0)){client.Dispose();continue;}
                var id=Interlocked.Increment(ref serial);var task=Forward(client);tasks[id]=task;
                _=task.ContinueWith(t=>{slots.Release();tasks.TryRemove(id,out _);},TaskScheduler.Default);
            }
        }
        catch(Exception) when(stop.IsCancellationRequested) { }
    }
    async Task Forward(TcpClient client)
    {
        using(client)
        try
        {
            var session=hub.Current??throw new IOException("设备正在自动重连。");
            using var upstream=await session.OpenTcp(target,stop.Token);await Wire.Bridge(client,upstream,stop.Token);
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    async Task ReceiveUdp()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await udp!.ReceiveAsync(stop.Token);
                if(packet.Buffer.Length > Wire.MaxUdp) continue;
                if(!udpFlows.TryGetValue(packet.RemoteEndPoint,out var flow))
                {
                    if(udpFlows.Count >= 64) continue;
                    var session=hub.Current;
                    if(session==null||!session.Live||!session.UdpPorts.Contains(target))continue;
                    flow=new LocalUdpFlow(session,target,packet.RemoteEndPoint,async (data,remote) =>
                        await udp.SendAsync(data,remote,stop.Token),stop.Token);
                    udpFlows[packet.RemoteEndPoint]=flow;
                    var captured=flow;
                    _=flow.Completion.ContinueWith(t =>
                    { udpFlows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,captured)); captured.Dispose(); },TaskScheduler.Default);
                }
                await flow.Send(packet.Buffer);
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public void Reset()
    {
        foreach(var pair in udpFlows.ToArray())if(udpFlows.TryRemove(pair.Key,out var flow))flow.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();udp?.Dispose();foreach(var flow in udpFlows.Values)flow.Dispose();
        try{await Task.WhenAll(accept,receive);await Task.WhenAll(tasks.Values);}catch{}stop.Dispose();
    }
}

sealed class LocalUdpFlow : IDisposable
{
    readonly UdpClient upstream=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly Session session;
    readonly int target;
    readonly IPEndPoint remote;
    readonly Func<byte[],IPEndPoint,Task> reply;
    readonly CancellationTokenSource stop;
    public Task Completion { get; }
    public LocalUdpFlow(Session session,int target,IPEndPoint remote,Func<byte[],IPEndPoint,Task> reply,CancellationToken ct)
    {
        this.session=session;this.target=target;this.remote=remote;this.reply=reply;
        stop=CancellationTokenSource.CreateLinkedTokenSource(ct);upstream.Connect(IPAddress.Loopback,session.LocalPort);
        Wire.ConfigureUdp(upstream);
        stop.CancelAfter(TimeSpan.FromMinutes(2));Completion=Receive();
    }
    public async Task Send(byte[] data)
    {
        stop.CancelAfter(TimeSpan.FromMinutes(2));
        await upstream.SendAsync(Wire.Packet(session.Capability,target,data),stop.Token);
    }
    async Task Receive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await upstream.ReceiveAsync(stop.Token);stop.CancelAfter(TimeSpan.FromMinutes(2));
                if(Wire.ValidPacket(packet.Buffer,session.Capability) && BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16)) == target)
                    await reply(packet.Buffer[18..],remote);
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    int disposed;
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){stop.Cancel();upstream.Dispose();}}
}
