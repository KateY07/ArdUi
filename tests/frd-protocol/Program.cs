global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Net;
global using System.Net.Sockets;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.RegularExpressions;

namespace ArdUi;

static class FrdForwardingTest
{
    static void Check(bool value,string message)
    {if(!value)throw new IOException(message);Console.WriteLine("PASS: "+message);}

    public static async Task Main()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));var ct=timeout.Token;
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var gateway=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));Wire.ConfigureUdp(gateway);
        var session=new Session(port);var observations=new ConcurrentQueue<(byte[] Data,IPEndPoint Source)>();
        var sockets=new ConcurrentBag<TcpClient>();var tasks=new ConcurrentBag<Task>();
        var greeting="opaque server-first greeting"u8.ToArray();
        var accept=Task.Run(async()=>
        {
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    var client=await listener.AcceptTcpClientAsync(ct);sockets.Add(client);
                    tasks.Add(Task.Run(async()=>
                    {
                        using(client)
                        try
                        {
                            var stream=client.GetStream();await stream.WriteAsync(greeting,ct);var buffer=new byte[8192];int count;
                            while((count=await stream.ReadAsync(buffer,ct))>0)await stream.WriteAsync(buffer.AsMemory(0,count),ct);
                            await stream.WriteAsync("after-fin"u8.ToArray(),ct);client.Client.Shutdown(SocketShutdown.Send);
                        }
                        catch(Exception ex){Diagnostics.Log("test-tcp",ex.Message);}
                    },ct));
                }
            }
            catch(Exception ex){Diagnostics.Log("test-listener",ex.Message);}
        },ct);
        var echo=Task.Run(async()=>
        {
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    var packet=await gateway.ReceiveAsync(ct);
                    if(!Wire.ValidPacket(packet.Buffer,session.Capability)||BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16))!=port)
                        throw new InvalidDataException("Invalid transport capability or target.");
                    observations.Enqueue((packet.Buffer[18..],packet.RemoteEndPoint));
                    await gateway.SendAsync(packet.Buffer,packet.RemoteEndPoint,ct);
                }
            }
            catch(Exception ex)when(ct.IsCancellationRequested){Diagnostics.Log("test-udp",ex.Message);}
        },ct);
        await using var forwarder=new FrdForwarder(session,port,ct);
        try
        {
            async Task Tcp()
            {
                using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,forwarder.Port,ct);
                var stream=tcp.GetStream();Check((await Wire.Read(stream,greeting.Length,ct)).SequenceEqual(greeting),"server-first TCP works without any FRD handshake");
                var bytes=RandomNumberGenerator.GetBytes(100000);bytes[0]=0xff;
                var reply=Wire.Read(stream,bytes.Length,ct);await stream.WriteAsync(bytes,ct);
                Check((await reply).SequenceEqual(bytes),"arbitrary 100000-byte TCP stream is preserved");
                tcp.Client.Shutdown(SocketShutdown.Send);
                Check((await Wire.Read(stream,9,ct)).SequenceEqual("after-fin"u8.ToArray()),"TCP half-close preserves reverse traffic");
            }
            await Task.WhenAll(Tcp(),Tcp());
            var concurrent=new List<TcpClient>();
            async Task<TcpClient> Connect()
            {
                var tcp=new TcpClient{NoDelay=true};
                try
                {
                    await tcp.ConnectAsync(IPAddress.Loopback,forwarder.Port,ct);
                    Check((await Wire.Read(tcp.GetStream(),greeting.Length,ct)).SequenceEqual(greeting),"concurrent TCP receives its own upstream greeting");
                    return tcp;
                }
                catch{tcp.Dispose();throw;}
            }
            try
            {
                for(var index=0;index<FrdForwarder.MaxTcpConnections;index++)concurrent.Add(await Connect());
                await Task.WhenAll(concurrent.Select(async(tcp,index)=>
                {
                    var payload=Encoding.UTF8.GetBytes($"independent connection {index}");
                    await tcp.GetStream().WriteAsync(payload,ct);
                    Check((await Wire.Read(tcp.GetStream(),payload.Length,ct)).SequenceEqual(payload),$"parallel stream {index} remains isolated");
                }));
                using(var rejected=new TcpClient())
                {
                    await rejected.ConnectAsync(IPAddress.Loopback,forwarder.Port,ct);
                    Check(await rejected.GetStream().ReadAsync(new byte[1],ct)==0,"connection limit rejects only the additional connection");
                }
                var closingStream=concurrent[0].GetStream();concurrent[0].Client.Shutdown(SocketShutdown.Send);
                Check((await Wire.Read(closingStream,9,ct)).SequenceEqual("after-fin"u8.ToArray()),"one parallel connection can half-close independently");
                Check(await closingStream.ReadAsync(new byte[1],ct)==0,"closed stream reaches EOF");
                concurrent[0].Dispose();await Task.Delay(100,ct);concurrent[0]=await Connect();
                foreach(var tcp in concurrent)
                {
                    var data=RandomNumberGenerator.GetBytes(128);await tcp.GetStream().WriteAsync(data,ct);
                    Check((await Wire.Read(tcp.GetStream(),data.Length,ct)).SequenceEqual(data),"other streams survive sibling closure and reconnection");
                }
            }
            finally{foreach(var tcp in concurrent)tcp.Dispose();}
            using var a=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var b=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            a.Connect(IPAddress.Loopback,forwarder.Port);b.Connect(IPAddress.Loopback,forwarder.Port);
            var sources=new Dictionary<UdpClient,IPEndPoint>();
            foreach(var length in new[]{0,1,7,24,40,55,56,57,72,88,1172,Wire.MaxUdp})
            {
                foreach(var sender in new[]{a,b})
                {
                    var payload=RandomNumberGenerator.GetBytes(length);await sender.SendAsync(payload,ct);
                    var received=await sender.ReceiveAsync(ct);
                    Check(received.Buffer.SequenceEqual(payload)&&received.RemoteEndPoint.Port==forwarder.Port,$"UDP {length} bytes round trip unchanged on the fixed local port");
                    Check(observations.TryDequeue(out var observed)&&observed.Data.SequenceEqual(payload),"UDP payload is not rewritten");
                    if(sources.TryGetValue(sender,out var previous))Check(previous.Equals(observed.Source),"UDP flow source is stable");
                    else sources[sender]=observed.Source;
                }
            }
            Check(!sources[a].Equals(sources[b]),"two local UDP sources have isolated return mappings");
            await forwarder.DisposeAsync();Check(!forwarder.Live&&forwarder.Completion.IsCompleted,"dispose closes both listeners");
            using var reboundUdp=new UdpClient(new IPEndPoint(IPAddress.Loopback,forwarder.Port));
            var reboundTcp=new TcpListener(IPAddress.Loopback,forwarder.Port);reboundTcp.Start();reboundTcp.Stop();
            Check(true,"TCP and UDP ports are released");
            using var lifetime=new CancellationTokenSource();await using var cancelled=new FrdForwarder(session,port,lifetime.Token);
            lifetime.Cancel();await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(2),ct);
            Check(!cancelled.Live,"authorization lifetime cancellation closes forwarding");
            await using(var hub=new ForwardHub())
            {
                hub.Attach(session);var ordinary=hub.Forward(port);
                using var sender=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));sender.Connect(IPAddress.Loopback,ordinary.Port);
                var source=(IPEndPoint)sender.Client.LocalEndPoint!;
                var field=typeof(LocalForwarder).GetField("udpFlows",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
                var flows=(ConcurrentDictionary<IPEndPoint,LocalUdpFlow>)field.GetValue(ordinary)!;
                var failed=new LocalUdpFlow(session,port,source,(_,_)=>Task.CompletedTask,ct);failed.Dispose();await failed.Completion;
                flows[source]=failed;
                await sender.SendAsync(new byte[]{1},ct);
                var deadline=DateTime.UtcNow.AddSeconds(2);
                while(flows.ContainsKey(source)&&DateTime.UtcNow<deadline)await Task.Delay(10,ct);
                Check(!flows.ContainsKey(source),"ordinary UDP isolates a disposed mapping instead of exiting the receive loop");
                var oldSession=new Session(port);
                var stale=new LocalUdpFlow(oldSession,port,source,(_,_)=>Task.CompletedTask,ct);
                hub.Attach(session);flows[source]=stale;
                var afterSwitch=RandomNumberGenerator.GetBytes(1172);await sender.SendAsync(afterSwitch,ct);
                Check((await sender.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2),ct)).Buffer.SequenceEqual(afterSwitch),"mapping published after Reset is rejected when it belongs to the old session");
                for(var round=0;round<20;round++)
                {
                    ordinary.Reset();var payload=RandomNumberGenerator.GetBytes(1172);await sender.SendAsync(payload,ct);
                    var response=await sender.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2),ct);
                    Check(response.Buffer.SequenceEqual(payload),"ordinary UDP resumes after reset with the same client socket");
                }
            }
            Console.WriteLine("ALL TRANSPARENT TCP/UDP FORWARDING TESTS PASSED");
        }
        finally
        {
            timeout.Cancel();listener.Stop();gateway.Dispose();foreach(var client in sockets)client.Dispose();
            await Task.WhenAll(accept,echo);await Task.WhenAll(tasks);
        }
    }
}

static class Diagnostics
{public static void Log(string category,string message)=>Console.Error.WriteLine($"[{category}] {message}");}

// Only the authenticated session boundary is substituted; forwarding and framing use production code.
sealed class Session(int port)
{
    public bool Live=>true;
    public int[] TcpPorts=>[port];
    public int[] UdpPorts=>[port];
    public Task<TcpClient> OpenTcp(int target,CancellationToken ct)=>OpenFrdTcp(target,ct);
    public int LocalPort=>port;
    public byte[] Capability{get;}=RandomNumberGenerator.GetBytes(16);
    public async Task<TcpClient> OpenFrdTcp(int target,CancellationToken ct)
    {
        if(target!=port)throw new IOException("Target not authorized.");
        var client=new TcpClient{NoDelay=true};
        try{await client.ConnectAsync(IPAddress.Loopback,target,ct);return client;}
        catch{client.Dispose();throw;}
    }
}

static class WindowsShares
{public static Task Remove(string key,string value)=>Task.CompletedTask;}
