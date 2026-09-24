namespace ArdUi;

sealed class Receiver : IAsyncDisposable
{
    readonly TcpListener tcp=new(IPAddress.Loopback,0);
    readonly UdpClient udp;
    readonly CancellationTokenSource stop=new();
    readonly ConcurrentBag<Task> workers=new();
    readonly Task accept,datagrams;
    public int Port=>((IPEndPoint)tcp.LocalEndpoint).Port;
    public Receiver(){tcp.Start();udp=new(new IPEndPoint(IPAddress.Loopback,Port));Wire.ConfigureUdp(udp);accept=Accept();datagrams=Datagrams();}
    async Task Accept()
    {
        try{while(!stop.IsCancellationRequested){var client=await tcp.AcceptTcpClientAsync(stop.Token);client.NoDelay=true;workers.Add(Serve(client));}}
        catch(Exception ex)when(stop.IsCancellationRequested){Console.WriteLine("Receiver accept stopped: "+ex.GetType().Name);}
    }
    async Task Serve(TcpClient client)
    {
        using(client)
        try
        {
            var ct=stop.Token;var stream=client.GetStream();var command=(await Wire.Read(stream,1,ct))[0];
            if(command==(byte)'E')
            {var data=new byte[64];while(true){await stream.ReadExactlyAsync(data,ct);await stream.WriteAsync(data,ct);}}
            if(command==(byte)'U')
            {
                var buffer=new byte[65536];long count=0;var watch=Stopwatch.StartNew();
                while(true){var size=await ReadSize(stream,ct);if(size==0)break;await stream.ReadExactlyAsync(buffer.AsMemory(0,size),ct);count+=size;}
                var ack=new byte[16];BinaryPrimitives.WriteInt64LittleEndian(ack,count);BinaryPrimitives.WriteInt64LittleEndian(ack.AsSpan(8),watch.ElapsedTicks);
                await stream.WriteAsync(ack,ct);return;
            }
            if(command==(byte)'D')
            {
                var milliseconds=BinaryPrimitives.ReadInt32LittleEndian(await Wire.Read(stream,4,ct));
                if(milliseconds is <1000 or >60000)throw new IOException("Invalid duration");
                var data=RandomNumberGenerator.GetBytes(65536);var size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,data.Length);
                long count=0;var watch=Stopwatch.StartNew();
                while(watch.ElapsedMilliseconds<milliseconds){await stream.WriteAsync(size,ct);await stream.WriteAsync(data,ct);count+=data.Length;}
                await stream.WriteAsync(new byte[4],ct);
                var receipt=BinaryPrimitives.ReadInt64LittleEndian(await Wire.Read(stream,8,ct));
                if(receipt!=count)throw new IOException("Download receiver ACK count mismatch");
                var ack=new byte[8];BinaryPrimitives.WriteInt64LittleEndian(ack,count);await stream.WriteAsync(ack,ct);return;
            }
            throw new IOException("Unknown measurement command");
        }
        catch(EndOfStreamException){Console.WriteLine("Receiver echo peer closed");}
        catch(Exception ex)when(stop.IsCancellationRequested){Console.WriteLine("Receiver worker stopped: "+ex.GetType().Name);}
        catch(Exception ex){Console.Error.WriteLine("Receiver error: "+ex);throw;}
    }
    async Task Datagrams()
    {
        try{while(!stop.IsCancellationRequested){var p=await udp.ReceiveAsync(stop.Token);await udp.SendAsync(p.Buffer,p.RemoteEndPoint,stop.Token);}}
        catch(Exception ex)when(stop.IsCancellationRequested){Console.WriteLine("Receiver UDP stopped: "+ex.GetType().Name);}
    }
    public static async Task<int> ReadSize(Stream stream,CancellationToken ct)
    {var size=BinaryPrimitives.ReadInt32LittleEndian(await Wire.Read(stream,4,ct));return size is >=0 and <=65536?size:throw new IOException("Invalid frame size");}
    public async ValueTask DisposeAsync(){stop.Cancel();tcp.Stop();udp.Dispose();await Task.WhenAll(accept,datagrams);await Task.WhenAll(workers);stop.Dispose();}
}
