namespace ArdUi;

sealed class OverlayLink : IAsyncDisposable
{
    readonly TcpClient tcp;
    readonly SemaphoreSlim write=new(1);
    readonly CancellationTokenSource stop=new();
    readonly Func<byte[],CancellationToken,Task> sendUdp;
    readonly Func<string> network;
    public string Name{get;}
    public CancellationToken Token=>stop.Token;
    public bool Live=>!stop.IsCancellationRequested;
    public string Network=>network();
    public double? RttMs, JitterMs, BandwidthMbps;
    readonly ConcurrentQueue<double?> samples=new();
    public PathQuality Quality=>PathQuality.From(samples.ToArray(),BandwidthMbps);
    public void Sample(double? ms){samples.Enqueue(ms);while(samples.Count>16)samples.TryDequeue(out _);}
    public long Sent,Received,Probes,Lost;
    public Task Reader{get;set;}=Task.CompletedTask;
    public Action? Closed;
    public OverlayLink(string name,TcpClient tcp,Func<byte[],CancellationToken,Task> sendUdp,Func<string> network)
    {Name=name;this.tcp=tcp;tcp.NoDelay=true;this.sendUdp=sendUdp;this.network=network;}
    public async Task Send(byte[] data,bool udp,CancellationToken ct)
    {
        if(!Live)throw new IOException("传输路径已关闭。");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token);linked.CancelAfter(TimeSpan.FromSeconds(5));
        if(udp){await sendUdp(data,linked.Token);Interlocked.Add(ref Sent,data.Length);return;}
        await write.WaitAsync(linked.Token);
        try
        {
            var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,data.Length);
            await tcp.GetStream().WriteAsync(header,linked.Token);await tcp.GetStream().WriteAsync(data,linked.Token);
            Interlocked.Add(ref Sent,data.Length);
        }
        finally{write.Release();}
    }
    public async Task Read(Func<OverlayLink,byte[],bool,Task> receive)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var size=BinaryPrimitives.ReadInt32BigEndian(await Wire.Read(tcp.GetStream(),4,stop.Token));
                if(size is <38 or >65536)throw new InvalidDataException("无效覆盖层帧大小。");
                var data=await Wire.Read(tcp.GetStream(),size,stop.Token);Interlocked.Add(ref Received,size);
                await receive(this,data,false);
            }
        }
        catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {Diagnostics.Log("link-ended",Name+": "+ex.Message);}
        finally{stop.Cancel();tcp.Dispose();Closed?.Invoke();}
    }
    public ValueTask DisposeAsync(){stop.Cancel();tcp.Dispose();return ValueTask.CompletedTask;}
}
