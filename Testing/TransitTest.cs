namespace ArdUi;

static class TransitTest
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static async Task<int> Run(string[] args)
    {
        if(args.Contains("--integration-only")){await Integration(args);return 0;}
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));var ct=timeout.Token;
        var baseline=PathQuality.From([40,41,42,42,43,44,45,46],20);
        Check(PathQuality.From([20,21,22,22,23,24,25,26],20).BetterThan(baseline),"significant RTT improvement rejected");
        Check(!PathQuality.From([38,39,40,40,41,42,43,44],20).BetterThan(baseline),"marginal improvement triggered switch");
        Check(!PathQuality.From([10,11,12,12,13,14,null,null],20).BetterThan(baseline),"lossy candidate selected");
        Check(!PathQuality.From([10,11,12,12,13,14,15,16],10).BetterThan(baseline),"insufficient bandwidth selected");
        Check(PathQuality.From([40,41,42,42,43,44,45,46],31).BetterThan(baseline),"material bandwidth improvement rejected");
        Console.WriteLine("PASS: candidate quality rejects noise, loss and bandwidth regression; accepts meaningful gains.");
        using(var receive=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        using(var send=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            send.Connect((IPEndPoint)receive.Client.LocalEndPoint!);
            await send.SendAsync(new byte[8],ct);var hello=await receive.ReceiveAsync(ct);await receive.SendAsync(hello.Buffer,hello.RemoteEndPoint,ct);await send.ReceiveAsync(ct);
            await send.SendAsync(new byte[1158],ct);await send.SendAsync(new byte[458],ct);
            Check((await receive.ReceiveAsync(ct)).Buffer.Length==1158,"raw UDP first");
            Check((await receive.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2),ct)).Buffer.Length==458,"raw UDP second");
        }
        Console.WriteLine("PASS: raw localhost UDP burst.");
        var secret=RandomNumberGenerator.GetBytes(32);var context=RandomNumberGenerator.GetBytes(32);
        using(var a=new OverlayCipher(secret,context,true))
        using(var b=new OverlayCipher(secret,context,false))
        {
            var plain=RandomNumberGenerator.GetBytes(400);var cipher=a.Encrypt(plain,true);
            Check(b.Decrypt(cipher,true)!.SequenceEqual(plain),"AEAD round trip");
            Check(b.Decrypt(cipher,true)==null,"UDP replay accepted");
            var modified=a.Encrypt(plain,true);modified[^1]^=1;Check(b.Decrypt(modified,true)==null,"Tamper accepted");
            var wrong=RandomNumberGenerator.GetBytes(32);using var c=new OverlayCipher(wrong,context,false);
            Check(c.Decrypt(a.Encrypt(plain,false),false)==null,"C decrypted payload");
        }
        Console.WriteLine("PASS: directional AEAD, UDP replay rejection, tamper rejection, wrong-key rejection.");
        var echo=new TcpListener(IPAddress.Loopback,0);echo.Start();var echoPort=((IPEndPoint)echo.LocalEndpoint).Port;
        using var echoUdp=new UdpClient(new IPEndPoint(IPAddress.Loopback,echoPort));
        var echoTasks=new ConcurrentBag<Task>();
        var accept=Task.Run(async()=>
        {
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    var tcp=await echo.AcceptTcpClientAsync(ct);tcp.NoDelay=true;echoTasks.Add(Task.Run(async()=>
                    {
                        using(tcp)
                        try{var buffer=new byte[32768];int size;while((size=await tcp.GetStream().ReadAsync(buffer,ct))>0)await tcp.GetStream().WriteAsync(buffer.AsMemory(0,size),ct);tcp.Client.Shutdown(SocketShutdown.Send);}
                        catch(Exception ex){Diagnostics.Log("test-echo",ex.Message);}
                    }));
                }
            }
            catch(Exception ex){Diagnostics.Log("test-accept",ex.Message);}
        });
        var udpEcho=Task.Run(async()=>
        {
            try{while(!ct.IsCancellationRequested){var p=await echoUdp.ReceiveAsync(ct);await echoUdp.SendAsync(p.Buffer,p.RemoteEndPoint,ct);}}
            catch(Exception ex){Diagnostics.Log("test-udp",ex.Message);}
        });
        var cap=RandomNumberGenerator.GetBytes(16);OverlaySession? host=null;
        await using var gateway=new Gateway(new Settings{TcpPorts=[echoPort],UdpPorts=[echoPort]},cap);
        gateway.AttachOverlay=async(tcp,command,cancel)=>
        {
            if(command==4){host=await OverlaySession.AcceptHello(tcp,gateway.Port,cap,()=>"P2P / localhost-test",cancel);gateway.Overlay=host;}
            else{var id=await Wire.ReadJson<JsonElement>(tcp.GetStream(),cancel);Check(id.GetProperty("session").GetString()==host!.Id,"resume identity");await tcp.GetStream().WriteAsync(new byte[]{0},cancel);}
            var link=new OverlayLink("base",tcp,gateway.SendOverlayUdp,()=>"P2P / localhost-test");await host!.Add(link);await link.Reader;
        };
        var root=Path.Combine(Path.GetTempPath(),"ArdUi-v2-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var caller=await OverlaySession.Connect(gateway.Port,cap,()=>"P2P / localhost-test",ct);
            using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,caller.Port,ct);
            var header=new byte[23];"AUI1"u8.CopyTo(header);cap.CopyTo(header,4);header[20]=1;BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(21),(ushort)echoPort);
            await tcp.GetStream().WriteAsync(header,ct);Check((await Wire.Read(tcp.GetStream(),1,ct))[0]==0,"gateway open");
            async Task Exchange(int length)
            {
                var payload=RandomNumberGenerator.GetBytes(length);
                var read=Wire.Read(tcp.GetStream(),length,ct);await tcp.GetStream().WriteAsync(payload,ct);
                try{Check((await read.WaitAsync(TimeSpan.FromSeconds(12),ct)).SequenceEqual(payload),"TCP bytes changed or duplicated");}
                catch{Diagnostics.Log("test-caller-state",JsonSerializer.Serialize(caller.Snapshot()));Diagnostics.Log("test-host-state",JsonSerializer.Serialize(host!.Snapshot()));throw;}
            }
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));udp.Connect(IPAddress.Loopback,caller.Port);
            async Task Datagram()
            {
                var payload=RandomNumberGenerator.GetBytes(Wire.MaxUdp);var packet=Wire.Packet(cap,echoPort,payload);
                await udp.SendAsync(packet,ct);UdpReceiveResult response;
                try{response=await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5),ct);}
                catch{Diagnostics.Log("test-caller-state",JsonSerializer.Serialize(caller.Snapshot()));Diagnostics.Log("test-host-state",JsonSerializer.Serialize(host!.Snapshot()));throw;}
                Check(response.Buffer.SequenceEqual(packet),"fragmented native UDP corrupted");
            }
            await Exchange(100000);await Datagram();Console.WriteLine("PASS: multiplexed TCP and full-sized native UDP through the authenticated gateway.");
            await Exchange(10*1024*1024);Console.WriteLine("PASS: intact 10 MiB stream crosses the 8 MiB reliable window without deadlock.");
            Check(caller.UdpFlowCount>0,"UDP mapping was not recorded");caller.ExpireUdp(Environment.TickCount64+120001);
            Check(caller.UdpFlowCount==0,"idle UDP mappings did not expire");await Datagram();
            Console.WriteLine("PASS: idle UDP mapping is released and the same source socket reconnects.");
            await using var relay=await TestRelay.Create(caller,host!,ct);
            await caller.Request("select",new{path="test-C"},ct);caller.Select("test-C");
            await Exchange(100000);await Datagram();Console.WriteLine("PASS: same TCP socket survives base-to-C switch; UDP traverses C with fragmentation.");
            relay.DuplicateUdp=true;await Datagram();Console.WriteLine("PASS: duplicated relay datagrams rejected without duplicate business delivery.");
            await caller.Remove("base");await Exchange(50000);
            await Task.Delay(2000,ct);Check(caller.Link("base")?.Live==true,"base did not resume");
            Console.WriteLine("PASS: standby base TCP connection reconnects while C carries business.");
            await relay.Fail();await Exchange(100000);await Datagram();
            Check(caller.Selected=="base","C failure did not fall back");
            Console.WriteLine("PASS: C failure falls back automatically; original TCP socket and UDP mapping remain usable.");
            var business=tcp.GetStream();tcp.Client.Shutdown(SocketShutdown.Send);Check(await business.ReadAsync(new byte[1],ct)==0,"TCP half-close");
            var report=Diagnostics.Export(root,null);
            using(var archive=ZipFile.OpenRead(report))
            {
                Check(archive.Entries.Count>=3,"diagnostic ZIP incomplete");
                foreach(var entry in archive.Entries){using var reader=new StreamReader(entry.Open());var text=reader.ReadToEnd();Check(!text.Contains(Convert.ToHexString(cap),StringComparison.OrdinalIgnoreCase),"diagnostic capability leak");}
            }
            Console.WriteLine("PASS: TCP half-close and redacted diagnostic ZIP.");
            if(args.Contains("--integration"))await Integration(args);
            return 0;
        }
        catch
        {Console.Error.WriteLine("Diagnostics: "+Diagnostics.Export(root,null));throw;}
        finally
        {
            if(host!=null)await host.DisposeAsync();timeout.Cancel();echo.Stop();echoUdp.Dispose();
            await Task.WhenAll(accept,udpEcho);await Task.WhenAll(echoTasks);
        }
    }
    static async Task Integration(string[] args)
    {
        var root=Environment.GetEnvironmentVariable("ARDUI_TEST_ROOT")??throw new IOException("Run tests/integration_v2.py to start the isolated fixture.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(180));var ct=timeout.Token;
        var echo=new TcpListener(IPAddress.Loopback,0);echo.Start();var port=((IPEndPoint)echo.LocalEndpoint).Port;
        using var udpEcho=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));Wire.ConfigureUdp(udpEcho);
        var sockets=new ConcurrentBag<TcpClient>();var workers=new ConcurrentBag<Task>();
        var accept=Task.Run(async()=>
        {
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    var socket=await echo.AcceptTcpClientAsync(ct);socket.NoDelay=true;sockets.Add(socket);
                    workers.Add(Task.Run(async()=>
                    {try{var data=new byte[32768];int n;while((n=await socket.GetStream().ReadAsync(data,ct))>0)await socket.GetStream().WriteAsync(data.AsMemory(0,n),ct);}
                     catch(Exception ex){Diagnostics.Log("integration-echo",ex.Message);}}));
                }
            }
            catch(Exception ex){Diagnostics.Log("integration-accept",ex.Message);}
        });
        var datagrams=Task.Run(async()=>
        {
            try{while(!ct.IsCancellationRequested){var p=await udpEcho.ReceiveAsync(ct);await udpEcho.SendAsync(p.Buffer,p.RemoteEndPoint,ct);}}
            catch(Exception ex){Diagnostics.Log("integration-udp",ex.Message);}
        });
        var settings=new Settings{Relay=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY")!,RelayKey=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY_KEY")!,TcpPorts=[port],UdpPorts=[port]};
        var server=Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER")!;
        var a=Path.Combine(root,"a");var b=Path.Combine(root,"b");IdentityStore.Prepare(a);IdentityStore.Prepare(b);
        await using var caller=await Engine.Create(a,settings);await using var host=await Engine.Create(b,settings);
        await using var ad=new DirectoryClient(caller,server);await using var bd=new DirectoryClient(host,server);
        ad.Confirm=(_,_)=>Task.FromResult(true);bd.Confirm=(_,_)=>Task.FromResult(true);
        ad.Notice+=message=>Console.WriteLine("A: "+message);bd.Notice+=message=>Console.WriteLine("B: "+message);
        try
        {
            await ad.Register(ct);await bd.Register(ct);await bd.SetAccess(true,ct);ad.Start();bd.Start();
            await ad.Connect(bd.Code,bd.AccessPassword,ct);var session=caller.Outgoing[host.Id];
            Check(session.Overlay!=null&&host.Incoming[caller.Id].Overlay!=null,"v2 authenticated overlay missing");
            Console.WriteLine("PASS: two real ARD clients, signed identities, enrollment and inner E2E handshake.");
            var stablePort=session.Forward(port).Port;
            using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,stablePort,ct);
            var business=tcp.GetStream();
            async Task Exchange(int size=262144)
            {
                var payload=RandomNumberGenerator.GetBytes(size);var reply=Wire.Read(business,size,ct);
                await business.WriteAsync(payload,ct);Check((await reply.WaitAsync(TimeSpan.FromSeconds(20),ct)).SequenceEqual(payload),"real ARD business stream corruption");
            }
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(udp);udp.Connect(IPAddress.Loopback,stablePort);
            async Task Udp()
            {
                for(var i=0;i<3;i++)
                {
                    var payload=RandomNumberGenerator.GetBytes(Wire.MaxUdp);await udp.SendAsync(payload,ct);
                    using var attempt=CancellationTokenSource.CreateLinkedTokenSource(ct);attempt.CancelAfter(TimeSpan.FromSeconds(3));
                    try{var reply=await udp.ReceiveAsync(attempt.Token);Check(reply.Buffer.SequenceEqual(payload),"native RDP-sized UDP corruption");return;}
                    catch(OperationCanceledException)when(!ct.IsCancellationRequested){Diagnostics.Log("integration-udp-retry",i.ToString());}
                }
                throw new IOException("Native UDP failed three probes.");
            }
            await Exchange();await Udp();Console.WriteLine("PASS: TCP and native RDP-sized UDP through the real base ARD path.");
            TransitCandidate[] candidates=[];
            for(var i=0;i<20&&candidates.Length==0;i++)
            {candidates=(await ad.Call<TransitCandidates>("/api/v2/transit/candidates",new{target=host.Id},ct)).Candidates;if(candidates.Length==0)await Task.Delay(500,ct);}
            Check(candidates.Length>0,"ArdTransit was not registered");
            Check(await session.Transit!.ProbeCandidate(candidates[0],true,ct),"candidate rejected");
            var route=session.Overlay!.Selected;Check(route!="base","C was not selected");
            Check(session.Overlay.Link(route)!.Network.StartsWith("P2P",StringComparison.Ordinal),"A-C not Direct");
            Check(host.Incoming[caller.Id].Overlay!.Link(route)!.Network.StartsWith("P2P",StringComparison.Ordinal),"B-C not Direct");
            Console.WriteLine("PASS: independent Python C, NJ signed ticket, C signed temporary identities, both real ARD legs Direct.");
            await Exchange(1048576);await Udp();Console.WriteLine("PASS: same business TCP socket and native UDP switched to C (explicit test selection because localhost base is faster).");
            using(var c=Process.GetProcessById(int.Parse(Environment.GetEnvironmentVariable("ARDUI_TEST_TRANSIT_PID")!)))
            {c.Kill(true);await c.WaitForExitAsync(ct);}
            await Exchange(1048576);await Udp();Check(session.Overlay.Selected=="base","real C failure did not fall back");
            Console.WriteLine("PASS: killed C and its ARD children; same TCP stream survived automatic fallback with intact 1 MiB payload.");
            var old=session.Process;var recovery=Stopwatch.StartNew();await old.DisposeAsync();
            for(var i=0;i<300&&(ReferenceEquals(old,session.Process)||session.Overlay.Link("base")?.Live!=true);i++)await Task.Delay(100,ct);
            Check(!ReferenceEquals(old,session.Process),"base ARD was not restarted");
            Check(session.Overlay.Link("base")?.Live==true,"base transport did not resume within 30 seconds");
            await Exchange();await Udp();Check(session.Forward(port).Port==stablePort,"local port changed");
            Console.WriteLine($"PASS: killed base ARD process; overlay and original business socket resumed on the same local port in {recovery.Elapsed.TotalSeconds:F1}s.");
            await bd.Revoke(caller.Id);Check(host.Incoming.Count==0&&bd.Controllers.Length==0,"revoke did not destroy all host routes");
            Console.WriteLine("PASS: revocation destroys host overlay, transit paths and business sockets.");
            Console.WriteLine("Diagnostics: "+Diagnostics.Export(root,caller));
        }
        catch{Console.Error.WriteLine("Diagnostics: "+Diagnostics.Export(root,caller));throw;}
        finally
        {
            timeout.Cancel();echo.Stop();udpEcho.Dispose();foreach(var socket in sockets)socket.Dispose();
            await Task.WhenAll(accept,datagrams);await Task.WhenAll(workers);
        }
    }
    sealed class TestRelay : IAsyncDisposable
    {
        readonly TcpClient a,b;
        readonly UdpClient ua=new(new IPEndPoint(IPAddress.Loopback,0)),ub=new(new IPEndPoint(IPAddress.Loopback,0));
        readonly CancellationTokenSource stop=new();
        readonly List<Task> tasks=new();
        IPEndPoint? ra,rb;
        public bool DuplicateUdp;
        TestRelay(TcpClient a,TcpClient b){this.a=a;this.b=b;}
        public static async Task<TestRelay> Create(OverlaySession caller,OverlaySession host,CancellationToken ct)
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            var ca=new TcpClient();await ca.ConnectAsync(IPAddress.Loopback,port,ct);var a=await listener.AcceptTcpClientAsync(ct);
            var cb=new TcpClient();await cb.ConnectAsync(IPAddress.Loopback,port,ct);var b=await listener.AcceptTcpClientAsync(ct);listener.Stop();
            var relay=new TestRelay(a,b);
            relay.tasks.Add(Task.Run(async()=>{try{await Wire.Bridge(a,b,relay.stop.Token);}catch(Exception ex){Diagnostics.Log("test-relay",ex.Message);}}));
            relay.tasks.Add(relay.Udp(true));relay.tasks.Add(relay.Udp(false));
            async Task Add(OverlaySession target,TcpClient socket,int endpoint)
            {
                var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));udp.Connect(IPAddress.Loopback,endpoint);
                var link=new OverlayLink("test-C",socket,async(data,cancel)=>await udp.SendAsync(data,cancel),()=>"P2P / localhost-test");
                link.Closed+=()=>udp.Dispose();await target.Add(link);
                relay.tasks.Add(Task.Run(async()=>
                {try{while(!relay.stop.IsCancellationRequested){var p=await udp.ReceiveAsync(relay.stop.Token);await target.ReceiveUdp(link,p.Buffer);}}
                 catch(Exception ex){Diagnostics.Log("test-relay-udp",ex.Message);}}));
            }
            await Add(caller,ca,((IPEndPoint)relay.ua.Client.LocalEndPoint!).Port);
            await Add(host,cb,((IPEndPoint)relay.ub.Client.LocalEndPoint!).Port);
            await Task.Delay(2300,ct);return relay;
        }
        async Task Udp(bool side)
        {
            try
            {
                var from=side?ua:ub;var to=side?ub:ua;
                while(!stop.IsCancellationRequested)
                {
                    var packet=await from.ReceiveAsync(stop.Token);if(side)ra=packet.RemoteEndPoint;else rb=packet.RemoteEndPoint;
                    var remote=side?rb:ra;if(remote==null)continue;
                    await to.SendAsync(packet.Buffer,remote,stop.Token);if(DuplicateUdp)await to.SendAsync(packet.Buffer,remote,stop.Token);
                }
            }
            catch(Exception ex){Diagnostics.Log("test-relay-udp-stop",ex.Message);}
        }
        public Task Fail(){stop.Cancel();a.Dispose();b.Dispose();ua.Dispose();ub.Dispose();return Task.CompletedTask;}
        public async ValueTask DisposeAsync(){await Fail();await Task.WhenAll(tasks);}
    }
}
