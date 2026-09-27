namespace ArdUi;

static class EasyTierTest
{
    public static async Task<int> Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"ArdUi-EasyTier-"+Guid.NewGuid().ToString("N"));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(100));var ct=stop.Token;
        var a=Path.Combine(root,"a");var b=Path.Combine(root,"b");var c=Path.Combine(root,"c");
        var aid=EasyTier.SessionIdentity(a);var bid=EasyTier.SessionIdentity(b);var cid=EasyTier.SessionIdentity(c);
        var an=EasyTier.Network(a,bid);var bn=EasyTier.Network(b,aid);var cn=EasyTier.Network(c,bid);
        if(an!=bn||an.Secret==cn.Secret||an.Name==cn.Name)throw new Exception("Session network isolation failed");
        Console.WriteLine("PASS: signed-key network derivation is symmetric and isolated per pair.");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var service=((IPEndPoint)listener.LocalEndpoint).Port;
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,service));
        var tasks=new List<Task>();
        async Task EchoTcp()
        {
            for(var i=0;i<8;i++)
            {
                var client=await listener.AcceptTcpClientAsync(ct);
                tasks.Add(Task.Run(async()=>{using(client){var stream=client.GetStream();await stream.CopyToAsync(stream,ct);}},ct));
            }
        }
        async Task EchoUdp()
        {
            try{while(!ct.IsCancellationRequested){var packet=await udp.ReceiveAsync(ct);await udp.SendAsync(packet.Buffer,packet.RemoteEndPoint,ct);}}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){Console.WriteLine("UDP echo stopped");}
        }
        var accept=EchoTcp();var datagrams=EchoUdp();var local=Wire.Port();
        try
        {
            await using var host=EasyTier.Start(b,true,service,aid,0);await host.WaitFor("relay online",ct);
            await using var caller=EasyTier.Start(a,false,local,bid,service);await caller.WaitFor("READY:",ct);
            var connections=new List<TcpClient>();
            try
            {
                for(var i=0;i<8;i++){var client=new TcpClient{NoDelay=true};connections.Add(client);await client.ConnectAsync(IPAddress.Loopback,local,ct);}
                await Task.WhenAll(connections.Select(async(client,index)=>
                {
                    var payload=RandomNumberGenerator.GetBytes(4096+index);var stream=client.GetStream();await stream.WriteAsync(payload,ct);
                    var response=await Wire.Read(stream,payload.Length,ct);if(!payload.SequenceEqual(response))throw new Exception("TCP bytes changed");
                    client.Client.Shutdown(SocketShutdown.Send);
                    if(await stream.ReadAsync(new byte[1],ct)!=0)throw new Exception("TCP half-close was not preserved");
                }));
                Console.WriteLine("PASS: 8 independent concurrent TCP streams, exact bytes and half-close.");
            }
            finally{foreach(var client in connections)client.Dispose();}
            await accept;await Task.WhenAll(tasks);
            for(var i=0;i<4;i++)
            {
                using var source=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var data=RandomNumberGenerator.GetBytes(i%2==0?1172:1200);
                var received=false;
                for(var attempt=0;attempt<5;attempt++)
                {
                    await source.SendAsync(data,new IPEndPoint(IPAddress.Loopback,local),ct);
                    using var probe=CancellationTokenSource.CreateLinkedTokenSource(ct);probe.CancelAfter(1500);
                    try{var response=await source.ReceiveAsync(probe.Token);if(!data.SequenceEqual(response.Buffer))throw new Exception("UDP bytes changed");received=true;break;}
                    catch(OperationCanceledException)when(!ct.IsCancellationRequested){Console.WriteLine($"UDP source {i} attempt {attempt+1} timed out");}
                }
                if(!received)throw new Exception("UDP return mapping failed after five bounded probes");
            }
            Console.WriteLine("PASS: UDP 1172/1200-byte datagrams and distinct-source return mappings; larger application datagrams require overlay fragmentation.");
            Console.WriteLine($"Transport: {caller.Network}; diagnostics root: {root}");return 0;
        }
        finally{stop.Cancel();listener.Stop();}
    }
}
