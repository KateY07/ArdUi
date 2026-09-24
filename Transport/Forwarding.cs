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
        catch(Exception ex) when(stop.IsCancellationRequested){Diagnostics.Log("forward-listener-ended",ex.Message);}
    }
    async Task Forward(TcpClient client)
    {
        using(client)
        try
        {
            var session=hub.Current??throw new IOException("设备正在自动重连。");
            using var upstream=await session.OpenTcp(target,stop.Token);await Wire.Bridge(client,upstream,stop.Token);
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){Diagnostics.Log("forward-tcp",ex.Message);}
    }
    async Task ReceiveUdp()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await udp!.ReceiveAsync(stop.Token);
                if(packet.Buffer.Length > Wire.MaxUdp) continue;
                LocalUdpFlow? flow=null;
                try
                {
                    if(udpFlows.TryGetValue(packet.RemoteEndPoint,out flow)&&!ReferenceEquals(flow.Session,hub.Current))
                    {if(udpFlows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,flow)))flow.Dispose();flow=null;}
                    if(!udpFlows.TryGetValue(packet.RemoteEndPoint,out flow))
                    {
                        if(udpFlows.Count >= 64) continue;
                        var session=hub.Current;
                        if(session==null||!session.Live||!session.UdpPorts.Contains(target))continue;
                        flow=new LocalUdpFlow(session,target,packet.RemoteEndPoint,async (data,remote) =>
                            await udp.SendAsync(data,remote,stop.Token),stop.Token);
                        udpFlows[packet.RemoteEndPoint]=flow;
                        if(!ReferenceEquals(session,hub.Current))
                        {if(udpFlows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,flow)))flow.Dispose();continue;}
                        var captured=flow;
                        _=flow.Completion.ContinueWith(t =>
                        { udpFlows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,captured)); captured.Dispose(); },TaskScheduler.Default);
                    }
                    await flow.Send(packet.Buffer);
                }
                catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    Diagnostics.Log("forward-udp-flow",ex.Message);
                    if(flow!=null&&udpFlows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,flow)))flow.Dispose();
                }
            }
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {Diagnostics.Log("forward-udp-ended",$"Stopping={stop.IsCancellationRequested}: {ex.Message}");}
    }
    public void Reset()
    {
        foreach(var pair in udpFlows.ToArray())if(udpFlows.TryRemove(pair.Key,out var flow))flow.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();udp?.Dispose();foreach(var flow in udpFlows.Values)flow.Dispose();
        try{await Task.WhenAll(accept,receive);await Task.WhenAll(tasks.Values);}catch(Exception ex){Diagnostics.Log("forward-close",ex.Message);}stop.Dispose();
    }
}
