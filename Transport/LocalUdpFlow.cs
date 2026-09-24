namespace ArdUi;

sealed class LocalUdpFlow : IDisposable
{
    readonly UdpClient upstream=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly Session session;
    public Session Session=>session;
    readonly int target;
    readonly IPEndPoint remote;
    readonly Func<byte[],IPEndPoint,Task> reply;
    readonly CancellationTokenSource stop;
    public Task Completion{get;}
    public LocalUdpFlow(Session session,int target,IPEndPoint remote,Func<byte[],IPEndPoint,Task> reply,CancellationToken ct)
    {
        this.session=session;this.target=target;this.remote=remote;this.reply=reply;
        stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            upstream.Connect(IPAddress.Loopback,session.LocalPort);Wire.ConfigureUdp(upstream);
            stop.CancelAfter(TimeSpan.FromMinutes(2));Completion=Receive();
        }
        catch{upstream.Dispose();stop.Dispose();throw;}
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
                if(Wire.ValidPacket(packet.Buffer,session.Capability)&&BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16))==target)
                    await reply(packet.Buffer[18..],remote);
            }
        }
        catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {Diagnostics.Log("local-udp",ex.Message);}
    }
    int disposed;
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){stop.Cancel();upstream.Dispose();}}
}
