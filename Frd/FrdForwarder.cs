namespace ArdUi;

// FRD owns the bytes and datagram format; this listener forwards one authorized TCP/UDP port.
sealed class FrdForwarder : IAsyncDisposable
{
    public const int MaxTcpConnections=8;
    readonly Session session;
    readonly int target;
    readonly TcpListener listener=new(IPAddress.Loopback,0);
    readonly UdpClient udp;
    readonly CancellationTokenSource stop;
    readonly ConcurrentDictionary<int,TcpClient> clients=new();
    readonly ConcurrentDictionary<int,Task> tasks=new();
    readonly ConcurrentDictionary<IPEndPoint,LocalUdpFlow> flows=new();
    readonly SemaphoreSlim slots=new(MaxTcpConnections);
    readonly Task accept,receive;
    int serial,stopped,disposed;
    long sentDatagrams,receivedDatagrams;
    public int Port{get;}
    public bool Live=>!stop.IsCancellationRequested;
    public Task Completion{get;}
    public Action<string>? Failed{get;set;}

    public FrdForwarder(Session session,int target,CancellationToken ct)
    {
        this.session=session;this.target=target;stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;
            udp=new(new IPEndPoint(IPAddress.Loopback,Port));Wire.ConfigureUdp(udp);
        }
        catch{listener.Stop();udp?.Dispose();stop.Dispose();slots.Dispose();throw;}
        accept=Accept();receive=Receive();Completion=Task.WhenAll(accept,receive);
        Diagnostics.Log("frd-proxy",$"Transparent TCP/UDP forwarding ready: 127.0.0.1:{Port} -> authorized port {target}.");
    }
    void Report(string category,Exception error)
    {
        Diagnostics.Log(category,error.Message);
        if(!stop.IsCancellationRequested)
            try{Failed?.Invoke(error.Message);}catch(Exception ex){Diagnostics.Log("frd-error-handler",ex.Message);}
    }
    void Stop()
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        stop.Cancel();listener.Stop();udp.Dispose();
        foreach(var client in clients.Values)client.Dispose();
        foreach(var flow in flows.Values)flow.Dispose();
    }
    async Task Accept()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                if(!IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint!).Address)){client.Dispose();continue;}
                if(!slots.Wait(0))
                {Diagnostics.Log("frd-proxy",$"TCP connection limit reached ({MaxTcpConnections}); new connection rejected.");client.Dispose();continue;}
                client.NoDelay=true;var id=Interlocked.Increment(ref serial);clients[id]=client;
                var task=Forward(id,client);tasks[id]=task;
                _=task.ContinueWith(_=>tasks.TryRemove(id,out var removed),TaskScheduler.Default);
            }
        }
        catch(Exception ex){Report("frd-proxy",ex);}
        finally{Stop();}
    }
    async Task Forward(int id,TcpClient client)
    {
        try
        {
            using(client)
            using(var upstream=await session.OpenFrdTcp(target,stop.Token))
                await Wire.Bridge(client,upstream,stop.Token);
        }
        catch(Exception ex){Report("frd-proxy",ex);}
        finally{client.Dispose();clients.TryRemove(id,out _);slots.Release();}
    }
    async Task Reply(byte[] data,IPEndPoint remote)
    {await udp.SendAsync(data,remote,stop.Token);Interlocked.Increment(ref receivedDatagrams);}
    async Task Receive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await udp.ReceiveAsync(stop.Token);
                if(!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))continue;
                if(packet.Buffer.Length>Wire.MaxUdp)
                {Diagnostics.Log("frd-client-udp",$"Datagram exceeds tunnel MTU: {packet.Buffer.Length} > {Wire.MaxUdp}.");continue;}
                if(!flows.TryGetValue(packet.RemoteEndPoint,out var flow))
                {
                    if(flows.Count>=64){Diagnostics.Log("frd-client-udp","UDP source limit reached.");continue;}
                    flow=new(session,target,packet.RemoteEndPoint,Reply,stop.Token);flows[packet.RemoteEndPoint]=flow;
                    var captured=flow;var source=packet.RemoteEndPoint;
                    _=flow.Completion.ContinueWith(task=>
                    {
                        if(task.Exception is{} error)Report("frd-client-udp",error);
                        flows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(source,captured));captured.Dispose();
                    },TaskScheduler.Default);
                    Diagnostics.Log("frd-udp","Transparent UDP source mapping opened.");
                }
                try{await flow.Send(packet.Buffer);Interlocked.Increment(ref sentDatagrams);}
                catch(Exception ex)
                {
                    flows.TryRemove(new KeyValuePair<IPEndPoint,LocalUdpFlow>(packet.RemoteEndPoint,flow));flow.Dispose();
                    Report("frd-client-udp",ex);
                }
            }
        }
        catch(Exception ex){Report("frd-client-udp",ex);}
        finally{Stop();}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;Stop();
        try{await Completion;await Task.WhenAll(tasks.Values);await Task.WhenAll(flows.Values.Select(flow=>flow.Completion));}
        catch(Exception ex){Diagnostics.Log("frd-proxy-close",ex.Message);}
        Diagnostics.Log("frd-udp",$"Transparent UDP forwarding stopped: sent={Interlocked.Read(ref sentDatagrams)} received={Interlocked.Read(ref receivedDatagrams)}.");
        slots.Dispose();stop.Dispose();
    }
}
