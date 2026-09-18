using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArdUi;
// FRD TCP negotiation and its UDP source-port rules are adapted inside the authorized session.
static class FrdWire
{
    public static readonly byte[] Registration=[0x46,0x52,0x44,0x32,3,0,0,0];
    public static async Task<JsonObject> Read(Stream stream,CancellationToken ct)
    {
        var header=await Wire.Read(stream,4,ct);var size=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size is <1 or >65536)throw new InvalidDataException("FRD 握手长度无效。");
        return JsonNode.Parse(await Wire.Read(stream,size,ct),documentOptions:new JsonDocumentOptions{MaxDepth=32}) as JsonObject
            ??throw new InvalidDataException("FRD 握手必须是 JSON 对象。");
    }
    public static async Task Write(Stream stream,JsonObject value,CancellationToken ct)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length is <1 or >65536)throw new InvalidDataException("FRD 握手过大。");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,ct);await stream.WriteAsync(bytes,ct);
    }
    public static string Text(JsonObject value,string field)=>value[field]?.GetValue<string>()??"";
    public static int Port(JsonNode? value)
    {var port=value?.GetValue<int>()??0;return port is >0 and <=65535?port:throw new InvalidDataException("FRD 协商端口无效。");}
    public static void Authenticate(JsonObject hello,string token)
    {
        var supplied=Text(hello,"Token");
        if(supplied.Length is <1 or >4096||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),SHA256.HashData(Encoding.UTF8.GetBytes(token))))
            throw new IOException("FRD 临时会话口令无效。");
    }
    public static bool IsRegistration(byte[] data)=>data.AsSpan().SequenceEqual(Registration);
    public static bool IsFeedback(byte[] data)=>data.Length==72&&BinaryPrimitives.ReadUInt32LittleEndian(data)==0x32445246&&data[4]==2;
    public static bool IsVideo(byte[] data)
    {
        if(data.Length is <36 or >1172||BinaryPrimitives.ReadUInt32LittleEndian(data)!=0x32445246||data[4]!=1||data[5]>1)return false;
        var total=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));var offset=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
        var count=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(32));
        return total is >0 and <=8388608&&offset>=0&&offset<total&&offset%1136==0&&count==Math.Min(1136,total-offset)&&data.Length==36+count;
    }
    public static bool IsDiagnostic(byte[] data)=>data.Length is 48 or 88&&BinaryPrimitives.ReadUInt32LittleEndian(data)==0x44445246;
    public static bool Loopback(IPEndPoint point)=>IPAddress.IsLoopback(point.Address);
}

abstract class FrdTcpProxy : IAsyncDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);
    readonly ConcurrentDictionary<int,TcpClient> clients=new();
    readonly ConcurrentDictionary<int,Task> tasks=new();
    readonly SemaphoreSlim slots=new(8);
    readonly TaskCompletionSource<bool> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string secret;
    protected readonly CancellationTokenSource StopToken;
    Task accept=Task.CompletedTask;
    int serial,disposed;
    public int Port{get;}
    public bool Live=>!StopToken.IsCancellationRequested;
    public Task Completion=>completion.Task;
    public Action<string>? Failed{get;set;}
    protected FrdTcpProxy(string secret,CancellationToken ct)
    {this.secret=secret;StopToken=CancellationTokenSource.CreateLinkedTokenSource(ct);listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;}
    protected void Start()=>accept=Accept();
    protected void Stop()
    {
        StopToken.Cancel();listener.Stop();foreach(var client in clients.Values)client.Dispose();completion.TrySetResult(true);
    }
    protected void Report(Exception error)
    {
        var message=string.IsNullOrEmpty(secret)?error.Message:error.Message.Replace(secret,"[redacted]",StringComparison.Ordinal);
        Diagnostics.Log("frd-proxy",message);
        if(!StopToken.IsCancellationRequested)
            try{Failed?.Invoke(message);}catch(Exception ex){Diagnostics.Log("frd-error-handler",ex.Message);}
    }
    protected abstract Task Forward(TcpClient client,CancellationToken ct);
    async Task Accept()
    {
        try
        {
            while(!StopToken.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(StopToken.Token);
                if(!FrdWire.Loopback((IPEndPoint)client.Client.RemoteEndPoint!)||!slots.Wait(0)){client.Dispose();continue;}
                client.NoDelay=true;var id=Interlocked.Increment(ref serial);clients[id]=client;
                var task=Handle(id,client);tasks[id]=task;
                _=task.ContinueWith(_=>{tasks.TryRemove(id,out var removed);},TaskScheduler.Default);
            }
        }
        catch(Exception ex){Report(ex);}
        finally{Stop();}
    }
    async Task Handle(int id,TcpClient client)
    {
        try{using(client)await Forward(client,StopToken.Token);}
        catch(Exception ex){Report(ex);}
        finally{clients.TryRemove(id,out var removed);slots.Release();}
    }
    public virtual async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;Stop();
        try{await accept;await Task.WhenAll(tasks.Values);}catch(Exception ex){Diagnostics.Log("frd-proxy-close",ex.Message);}
        StopToken.Dispose();slots.Dispose();
    }
}

sealed class FrdHostProxy : FrdTcpProxy
{
    readonly int hostPort;
    readonly string token;
    readonly Action<int,bool> permitUdp;
    readonly object gate=new();
    readonly HashSet<string> attached=new();
    string activeSession="";
    int primary;
    public FrdHostProxy(int hostPort,string token,Action<int,bool> permitUdp,CancellationToken ct):base(token,ct)
    {this.hostPort=hostPort;this.token=token;this.permitUdp=permitUdp;Start();}
    protected override async Task Forward(TcpClient client,CancellationToken ct)
    {
        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(ct);handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var hello=await FrdWire.Read(client.GetStream(),handshake.Token);FrdWire.Authenticate(hello,token);
        var kind=FrdWire.Text(hello,"Kind");
        if(kind=="hello")
        {
            if(Interlocked.CompareExchange(ref primary,1,0)!=0)throw new IOException("FRD 主连接已建立。");
            try
            {
                var originalVideo=FrdWire.Port(hello["VideoPort"]);var originalDiagnostic=FrdWire.Port(hello["DiagnosticPort"]);
                if(originalVideo==originalDiagnostic)throw new InvalidDataException("FRD 视频和诊断端口必须不同。");
                void UdpFailed(Exception error){Report(error);Stop();}
                await using var video=new FrdHostUdp(false,permitUdp,ct,UdpFailed);await using var diagnostic=new FrdHostUdp(true,permitUdp,ct,UdpFailed);
                hello["VideoPort"]=video.Port;hello["DiagnosticPort"]=diagnostic.Port;
                using var upstream=new TcpClient{NoDelay=true};await upstream.ConnectAsync(IPAddress.Loopback,hostPort,handshake.Token);
                await FrdWire.Write(upstream.GetStream(),hello,handshake.Token);var reply=await FrdWire.Read(upstream.GetStream(),handshake.Token);
                if(reply["Success"]?.GetValue<bool>()==true)
                {
                    var welcome=reply["Welcome"] as JsonObject??throw new InvalidDataException("FRD 缺少会话信息。");
                    var sender=FrdWire.Port(welcome["SenderPort"]);var id=FrdWire.Text(welcome,"Session");
                    if(id.Length is <1 or >128||sender==video.Port||sender==diagnostic.Port)throw new InvalidDataException("FRD 会话信息无效。");
                    video.SetHost(sender);diagnostic.SetHost(sender);lock(gate)activeSession=id;
                    reply["ArdUiUdpPorts"]=new JsonArray(video.Port,diagnostic.Port);
                }
                await FrdWire.Write(client.GetStream(),reply,handshake.Token);handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                if(reply["Success"]?.GetValue<bool>()==true)await Wire.Bridge(client,upstream,ct);
                else Report(new IOException(FrdWire.Text(reply,"Message")));
            }
            catch(Exception ex){Report(ex);throw;}
            finally{lock(gate)activeSession="";Stop();}
            return;
        }
        if(kind is not ("input" or "cursor" or "clipboard"))throw new IOException("FRD 通道类型无效。");
        lock(gate)
        {
            if(activeSession.Length==0||FrdWire.Text(hello,"Session")!=activeSession||!attached.Add(kind))throw new IOException("FRD 附属通道不属于当前会话。");
        }
        try
        {
            using var upstream=new TcpClient{NoDelay=true};await upstream.ConnectAsync(IPAddress.Loopback,hostPort,handshake.Token);
            await FrdWire.Write(upstream.GetStream(),hello,handshake.Token);handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            await Wire.Bridge(client,upstream,ct);
        }
        finally{lock(gate)attached.Remove(kind);}
    }
}

sealed class FrdHostUdp : IAsyncDisposable
{
    readonly UdpClient socket=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly CancellationTokenSource stop;
    readonly Action<int,bool> permit;
    readonly bool diagnostic;
    readonly Action<Exception>? failed;
    readonly Task receive;
    IPEndPoint? host,upstream;
    long touched;
    int disposed;
    public int Port{get;}
    public FrdHostUdp(bool diagnostic,Action<int,bool> permit,CancellationToken ct,Action<Exception>? failed=null)
    {
        this.diagnostic=diagnostic;this.permit=permit;this.failed=failed;stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        Port=((IPEndPoint)socket.Client.LocalEndPoint!).Port;Wire.ConfigureUdp(socket);
        try{permit(Port,true);receive=Receive();}
        catch{socket.Dispose();stop.Dispose();throw;}
    }
    public void SetHost(int port)=>Volatile.Write(ref host,new IPEndPoint(IPAddress.Loopback,port));
    async Task Receive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await socket.ReceiveAsync(stop.Token);var source=packet.RemoteEndPoint;var destination=Volatile.Read(ref host);
                if(destination==null||!FrdWire.Loopback(source))continue;
                if(source.Equals(destination))
                {
                    if(upstream!=null&&(diagnostic?FrdWire.IsDiagnostic(packet.Buffer):FrdWire.IsVideo(packet.Buffer)))
                        await socket.SendAsync(packet.Buffer,upstream,stop.Token);
                    continue;
                }
                var registration=FrdWire.IsRegistration(packet.Buffer);var now=Environment.TickCount64;
                if(upstream==null||!source.Equals(upstream))
                {
                    if(!registration||upstream!=null&&now-touched<15000)continue;
                    upstream=source;Diagnostics.Log("frd-udp",$"{(diagnostic?"diagnostic":"video")} flow registered");
                }
                if(!registration&&(diagnostic||!FrdWire.IsFeedback(packet.Buffer)))continue;
                touched=now;await socket.SendAsync(packet.Buffer,destination,stop.Token);
            }
        }
        catch(Exception ex){Diagnostics.Log("frd-host-udp",ex.Message);if(!stop.IsCancellationRequested)failed?.Invoke(ex);}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try{permit(Port,false);}catch(Exception ex){Diagnostics.Log("frd-permission-close",ex.Message);}
        stop.Cancel();socket.Dispose();try{await receive;}catch(Exception ex){Diagnostics.Log("frd-host-udp-close",ex.Message);}stop.Dispose();
    }
}

sealed class FrdClientProxy : FrdTcpProxy
{
    readonly Session session;
    readonly int remotePort;
    readonly string token;
    readonly object gate=new();
    readonly HashSet<string> attached=new();
    string activeSession="";
    int primary;
    public FrdClientProxy(Session session,int remotePort,string token,CancellationToken ct):base(token,ct)
    {this.session=session;this.remotePort=remotePort;this.token=token;Start();}
    protected override async Task Forward(TcpClient client,CancellationToken ct)
    {
        using var handshake=CancellationTokenSource.CreateLinkedTokenSource(ct);handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var hello=await FrdWire.Read(client.GetStream(),handshake.Token);FrdWire.Authenticate(hello,token);
        var kind=FrdWire.Text(hello,"Kind");
        if(kind=="hello")
        {
            if(Interlocked.CompareExchange(ref primary,1,0)!=0)throw new IOException("FRD 主连接已建立。");
            try
            {
                var video=FrdWire.Port(hello["VideoPort"]);var diagnostic=FrdWire.Port(hello["DiagnosticPort"]);
                if(video==diagnostic)throw new InvalidDataException("FRD 视频和诊断端口必须不同。");
                using var upstream=await session.OpenFrdTcp(remotePort,handshake.Token);
                await FrdWire.Write(upstream.GetStream(),hello,handshake.Token);var reply=await FrdWire.Read(upstream.GetStream(),handshake.Token);
                if(reply["Success"]?.GetValue<bool>()!=true)
                {await FrdWire.Write(client.GetStream(),reply,handshake.Token);Report(new IOException(FrdWire.Text(reply,"Message")));return;}
                var welcome=reply["Welcome"] as JsonObject??throw new InvalidDataException("FRD 缺少会话信息。");
                var ports=reply["ArdUiUdpPorts"] as JsonArray??throw new InvalidDataException("对端未提供 FRD UDP 隧道。");
                if(ports.Count!=2)throw new InvalidDataException("FRD UDP 隧道数量无效。");
                var remoteVideo=FrdWire.Port(ports[0]);var remoteDiagnostic=FrdWire.Port(ports[1]);
                if(remoteVideo==remoteDiagnostic)throw new InvalidDataException("FRD UDP 隧道端口重复。");
                var id=FrdWire.Text(welcome,"Session");if(id.Length is <1 or >128)throw new InvalidDataException("FRD 会话编号无效。");
                var address=((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                void UdpFailed(Exception error){Report(error);Stop();}
                await using var udp=new FrdClientUdp(session,remoteVideo,remoteDiagnostic,new(address,video),new(address,diagnostic),ct,UdpFailed);
                welcome["SenderPort"]=udp.Port;reply.Remove("ArdUiUdpPorts");lock(gate)activeSession=id;
                await FrdWire.Write(client.GetStream(),reply,handshake.Token);handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                await Wire.Bridge(client,upstream,ct);
            }
            catch(Exception ex){Report(ex);throw;}
            finally{lock(gate)activeSession="";Stop();}
            return;
        }
        if(kind is not ("input" or "cursor" or "clipboard"))throw new IOException("FRD 通道类型无效。");
        lock(gate)
        {
            if(activeSession.Length==0||FrdWire.Text(hello,"Session")!=activeSession||!attached.Add(kind))throw new IOException("FRD 附属通道不属于当前会话。");
        }
        try
        {
            using var upstream=await session.OpenFrdTcp(remotePort,handshake.Token);
            await FrdWire.Write(upstream.GetStream(),hello,handshake.Token);handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            await Wire.Bridge(client,upstream,ct);
        }
        finally{lock(gate)attached.Remove(kind);}
    }
}

sealed class FrdClientUdp : IAsyncDisposable
{
    readonly UdpClient socket=new(new IPEndPoint(IPAddress.Loopback,0));
    readonly CancellationTokenSource stop;
    readonly IPEndPoint video,diagnostic;
    readonly LocalUdpFlow videoFlow,diagnosticFlow;
    readonly Action<Exception>? failed;
    readonly Task receive,keepalive;
    int disposed;
    public int Port{get;}
    public FrdClientUdp(Session session,int remoteVideo,int remoteDiagnostic,IPEndPoint video,IPEndPoint diagnostic,CancellationToken ct,Action<Exception>? failed=null)
    {
        this.video=video;this.diagnostic=diagnostic;this.failed=failed;stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        Port=((IPEndPoint)socket.Client.LocalEndPoint!).Port;Wire.ConfigureUdp(socket);
        try
        {
            videoFlow=new(session,remoteVideo,video,Reply,stop.Token);
            try{diagnosticFlow=new(session,remoteDiagnostic,diagnostic,Reply,stop.Token);}catch{videoFlow.Dispose();throw;}
            receive=Receive();keepalive=Keepalive();
        }
        catch{socket.Dispose();stop.Dispose();throw;}
    }
    async Task Reply(byte[] data,IPEndPoint target)
    {
        if(target.Equals(video)?FrdWire.IsVideo(data):target.Equals(diagnostic)&&FrdWire.IsDiagnostic(data))
            await socket.SendAsync(data,target,stop.Token);
    }
    async Task Receive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var packet=await socket.ReceiveAsync(stop.Token);
                if(packet.RemoteEndPoint.Equals(video)&&(FrdWire.IsRegistration(packet.Buffer)||FrdWire.IsFeedback(packet.Buffer)))await videoFlow.Send(packet.Buffer);
                else if(packet.RemoteEndPoint.Equals(diagnostic)&&FrdWire.IsRegistration(packet.Buffer))await diagnosticFlow.Send(packet.Buffer);
            }
        }
        catch(Exception ex){Diagnostics.Log("frd-client-udp",ex.Message);if(!stop.IsCancellationRequested)failed?.Invoke(ex);}
    }
    async Task Keepalive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await videoFlow.Send(FrdWire.Registration);await diagnosticFlow.Send(FrdWire.Registration);
                await Task.Delay(TimeSpan.FromSeconds(5),stop.Token);
            }
        }
        catch(Exception ex){Diagnostics.Log("frd-udp-keepalive",ex.Message);if(!stop.IsCancellationRequested)failed?.Invoke(ex);}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();socket.Dispose();videoFlow.Dispose();diagnosticFlow.Dispose();
        try{await Task.WhenAll(receive,keepalive,videoFlow.Completion,diagnosticFlow.Completion);}catch(Exception ex){Diagnostics.Log("frd-client-udp-close",ex.Message);}stop.Dispose();
    }
}
