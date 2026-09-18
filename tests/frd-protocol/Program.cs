global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Net;
global using System.Net.Sockets;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;

namespace ArdUi;

static class FrdProtocolTest
{
    public static async Task Main()
    {
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));var ct=timeout.Token;
const string secret="test-only-secret";
var allowed=new ConcurrentDictionary<int,bool>();var hosts=new ConcurrentBag<Task>();
var mainHello=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
var fake=new TcpListener(IPAddress.Loopback,0);fake.Start();var fakePort=((IPEndPoint)fake.LocalEndpoint).Port;
using var sender=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var senderPort=((IPEndPoint)sender.Client.LocalEndPoint!).Port;
var accepting=Task.Run(async()=>
{
    try{while(!ct.IsCancellationRequested){var tcp=await fake.AcceptTcpClientAsync(ct);hosts.Add(Handle(tcp));}}
    catch(Exception e){Diagnostics.Log("test-listener",e.Message);}
});
async Task Handle(TcpClient tcp)
{
    using(tcp)
    try
    {
        var hello=await FrdWire.Read(tcp.GetStream(),ct);FrdWire.Authenticate(hello,secret);
        if(FrdWire.Text(hello,"Kind")=="hello")
        {
            mainHello.TrySetResult(hello);
            await FrdWire.Write(tcp.GetStream(),new JsonObject{["Success"]=true,["Welcome"]=new JsonObject{["Session"]="ABCDEF0123456789ABCDEF0123456789",["SenderPort"]=senderPort,["Untouched"]=1234}},ct);
            while(true){var request=await FrdWire.Read(tcp.GetStream(),ct);await FrdWire.Write(tcp.GetStream(),new JsonObject{["Success"]=true,["Kind"]=FrdWire.Text(request,"Kind")},ct);}
        }
        else
        {
            await FrdWire.Write(tcp.GetStream(),new JsonObject{["Success"]=true},ct);
            var body=await Wire.Read(tcp.GetStream(),7,ct);await tcp.GetStream().WriteAsync(body,ct);
        }
    }
    catch(Exception e){Diagnostics.Log("test-host",e.Message);}
}
await using var host=new FrdHostProxy(fakePort,secret,(p,yes)=>{if(yes)allowed[p]=true;else allowed.TryRemove(p,out var removed);},ct);
await using var client=new FrdClientProxy(new Session(),host.Port,secret,ct);
async Task<TcpClient> Connect(int port)
{var socket=new TcpClient{NoDelay=true};await socket.ConnectAsync(IPAddress.Loopback,port,ct);return socket;}
void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
async Task Rejected(JsonObject request)
{
    using var tcp=await Connect(client.Port);await FrdWire.Write(tcp.GetStream(),request,ct);
    Check(await tcp.GetStream().ReadAsync(new byte[1],ct)==0,"invalid unauthenticated/unknown/duplicate request rejected");
}
await Rejected(new JsonObject{["Kind"]="hello",["Token"]="wrong"});
await Rejected(new JsonObject{["Kind"]="arbitrary",["Token"]=secret});
using var video=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var diagnostic=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
var videoPort=((IPEndPoint)video.Client.LocalEndPoint!).Port;var diagnosticPort=((IPEndPoint)diagnostic.Client.LocalEndPoint!).Port;
using var main=await Connect(client.Port);
await FrdWire.Write(main.GetStream(),new JsonObject{["Kind"]="hello",["Token"]=secret,["VideoPort"]=videoPort,["DiagnosticPort"]=diagnosticPort,["Session"]=""},ct);
var welcome=await FrdWire.Read(main.GetStream(),ct);var altered=await mainHello.Task;var v=FrdWire.Port(altered["VideoPort"]);var d=FrdWire.Port(altered["DiagnosticPort"]);
Check(v!=videoPort&&d!=diagnosticPort&&v!=d&&allowed.Count==2,"hello UDP ports rewritten and both temporary permissions installed");
Check(welcome["ArdUiUdpPorts"]==null&&welcome["Welcome"]!["Untouched"]!.GetValue<int>()==1234,"private proxy metadata removed, unknown welcome fields retained");
var proxyPort=FrdWire.Port(welcome["Welcome"]!["SenderPort"]);var target=new IPEndPoint(IPAddress.Loopback,proxyPort);
await video.SendAsync(FrdWire.Registration,target,ct);await diagnostic.SendAsync(FrdWire.Registration,target,ct);
var registered=new HashSet<int>();
while(registered.Count<2){var p=await sender.ReceiveAsync(ct);if(FrdWire.IsRegistration(p.Buffer))registered.Add(p.RemoteEndPoint.Port);}
Check(registered.SetEquals([v,d]),"both receiver registrations reached actual sender from original B proxy sockets");
var frame=new byte[1172];BinaryPrimitives.WriteUInt32LittleEndian(frame,0x32445246);frame[4]=1;
BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(24),1136);BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(32),1136);
var diag=new byte[88];BinaryPrimitives.WriteUInt32LittleEndian(diag,0x44445246);
await sender.SendAsync(frame,new IPEndPoint(IPAddress.Loopback,v),ct);await sender.SendAsync(diag,new IPEndPoint(IPAddress.Loopback,d),ct);
var gotVideo=await video.ReceiveAsync(ct);var gotDiagnostic=await diagnostic.ReceiveAsync(ct);
Check(gotVideo.Buffer.SequenceEqual(frame)&&gotDiagnostic.Buffer.SequenceEqual(diag),"1172-byte video and 88-byte diagnostics preserved");
Check(gotVideo.RemoteEndPoint.Equals(target)&&gotDiagnostic.RemoteEndPoint.Equals(target),"both A UDP channels have the single rewritten SenderPort source");
var feedback=new byte[72];BinaryPrimitives.WriteUInt32LittleEndian(feedback,0x32445246);feedback[4]=2;
await video.SendAsync(feedback,target,ct);UdpReceiveResult response;
do{response=await sender.ReceiveAsync(ct);}while(FrdWire.IsRegistration(response.Buffer));
Check(response.Buffer.SequenceEqual(feedback)&&response.RemoteEndPoint.Port==v,"feedback source matches B negotiated video port");
foreach(var kind in new[]{"input","cursor","clipboard"})
{
    using var channel=await Connect(client.Port);await FrdWire.Write(channel.GetStream(),new JsonObject{["Kind"]=kind,["Token"]=secret,["Session"]=welcome["Welcome"]!["Session"]!.GetValue<string>()},ct);
    Check((await FrdWire.Read(channel.GetStream(),ct))["Success"]!.GetValue<bool>(),kind+" handshake forwarded");
    var bytes=Encoding.ASCII.GetBytes("1234567");await channel.GetStream().WriteAsync(bytes,ct);
    Check((await Wire.Read(channel.GetStream(),7,ct)).SequenceEqual(bytes),kind+" raw payload forwarded without OS interaction");
}
await Rejected(new JsonObject{["Kind"]="hello",["Token"]=secret,["VideoPort"]=videoPort,["DiagnosticPort"]=diagnosticPort});
await FrdWire.Write(main.GetStream(),new JsonObject{["Kind"]="status"},ct);
Check((await FrdWire.Read(main.GetStream(),ct))["Kind"]!.GetValue<string>()=="status","main control stream stays transparent after handshake");
using(var rogue=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
{
    await rogue.SendAsync(FrdWire.Registration,new IPEndPoint(IPAddress.Loopback,v),ct);
    await rogue.SendAsync(feedback,new IPEndPoint(IPAddress.Loopback,v),ct);
    await sender.SendAsync(frame,new IPEndPoint(IPAddress.Loopback,v),ct);
    Check((await video.ReceiveAsync(ct)).Buffer.SequenceEqual(frame),"another source cannot seize a recently registered UDP mapping");
}
main.Dispose();await client.Completion.WaitAsync(ct);await host.Completion.WaitAsync(ct);
for(var i=0;i<50&&allowed.Count!=0;i++)await Task.Delay(20,ct);
Check(!client.Live&&!host.Live&&allowed.IsEmpty,"main disconnect stops both proxies and removes temporary UDP access");
fake.Stop();timeout.Cancel();await accepting;await Task.WhenAll(hosts);
Console.WriteLine("ALL FRD PROTOCOL ADAPTER TESTS PASSED");

    }
}

static class Diagnostics{public static void Log(string category,string message)=>Console.Error.WriteLine($"[{category}] {message}");}
static class Wire
{
    public static void ConfigureUdp(UdpClient socket){}
    public static async Task<byte[]> Read(Stream stream,int size,CancellationToken ct){var bytes=new byte[size];await stream.ReadExactlyAsync(bytes,ct);return bytes;}
    public static async Task Bridge(TcpClient a,TcpClient b,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Copy(TcpClient from,TcpClient to)
        {try{await from.GetStream().CopyToAsync(to.GetStream(),linked.Token);to.Client.Shutdown(SocketShutdown.Send);}catch{linked.Cancel();throw;}}
        await Task.WhenAll(Copy(a,b),Copy(b,a));
    }
}
sealed class Session
{
    public async Task<TcpClient> OpenFrdTcp(int port,CancellationToken ct)
    {var client=new TcpClient{NoDelay=true};await client.ConnectAsync(IPAddress.Loopback,port,ct);return client;}
}
sealed class LocalUdpFlow:IDisposable
{
    readonly UdpClient socket=new(new IPEndPoint(IPAddress.Loopback,0));readonly CancellationTokenSource stop;
    public Task Completion{get;}
    public LocalUdpFlow(Session session,int port,IPEndPoint target,Func<byte[],IPEndPoint,Task> callback,CancellationToken ct)
    {
        stop=CancellationTokenSource.CreateLinkedTokenSource(ct);socket.Connect(IPAddress.Loopback,port);Completion=Read();
        async Task Read(){try{while(!stop.IsCancellationRequested){var p=await socket.ReceiveAsync(stop.Token);await callback(p.Buffer,target);}}catch(Exception e){Diagnostics.Log("test-flow",e.Message);}}
    }
    public async Task Send(byte[] data)=>await socket.SendAsync(data,stop.Token);
    public void Dispose(){stop.Cancel();socket.Dispose();}
}
