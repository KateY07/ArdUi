namespace ArdUi;

static class Measurement
{
    public static async Task<object> Run(PerfPath path,int seconds,int samples,bool loaded,CancellationToken ct)
    {
        using var process=Process.GetCurrentProcess();var cpuBefore=process.TotalProcessorTime.TotalSeconds;
        var childCpuBefore=path.ChildCpuSeconds();var allocatedBefore=GC.GetTotalAllocatedBytes(false);var gcBefore=new[]{GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)};
        var tcp=await TcpRtt(path,samples,ct);var udp=await UdpRtt(path,samples,64,ct);var udp1200=await UdpRtt(path,samples,1200,ct);
        var up=await Upload(path,seconds,ct);var down=await Download(path,seconds,ct);
        var loadedResult=loaded?await LoadedUdp(path,seconds,ct):null;
        var harnessCpu=process.TotalProcessorTime.TotalSeconds-cpuBefore;var childCpu=path.ChildCpuSeconds()-childCpuBefore;
        return new{tcpEcho=tcp,udpEcho=udp,udp1200Echo=udp1200,tcpUpload=up,tcpDownload=down,loadedDownload=loadedResult,
            processCost=new{cpuSeconds=harnessCpu,ardCpuSeconds=childCpu,totalCpuSeconds=harnessCpu+childCpu,allocatedBytes=GC.GetTotalAllocatedBytes(false)-allocatedBefore,
                gcCollections=new[]{GC.CollectionCount(0)-gcBefore[0],GC.CollectionCount(1)-gcBefore[1],GC.CollectionCount(2)-gcBefore[2]},
                scope="cpuSeconds includes both benchmark endpoints and linked C#; ardCpuSeconds separately sums owned ARD children; totalCpuSeconds includes both, excluding directory/Relay."}};
    }
    static async Task<object> LoadedUdp(PerfPath path,int seconds,CancellationToken ct)
    {
        using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(client);client.Connect(IPAddress.Loopback,path.Port);
        var data=RandomNumberGenerator.GetBytes(1200);var times=new List<double>();var requested=0;var lost=0;
        var load=Download(path,seconds,ct);await Task.Delay(250,ct);
        while(!load.IsCompleted)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data,++requested);var packet=path.EncodeUdp(data);var start=Stopwatch.GetTimestamp();
            await client.SendAsync(packet,ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while(true)
                {
                    var reply=path.DecodeUdp((await client.ReceiveAsync(timeout.Token)).Buffer);
                    if(!data.AsSpan().SequenceEqual(reply))continue;
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);break;
                }
            }
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){lost++;Console.WriteLine($"Loaded UDP timeout sequence {requested}");}
            await Task.Delay(10,ct);
        }
        return new{tcpDownload=await load,udpEcho=Stats(times,requested,lost,1200),sampling="1200B application echo while a separate TCP download is active, 10ms delay between replies, after 250ms load warmup"};
    }
    static object Stats(List<double> times,int requested,int timeouts,int payloadBytes=64)
    {
        times.Sort();double? P(double p)=>times.Count==0?null:times[Math.Max(0,(int)Math.Ceiling(p*times.Count)-1)];
        return new{payloadBytes,requested,received=times.Count,timeouts,p50Ms=P(.50),p95Ms=P(.95),p99Ms=P(.99),minMs=times.Count>0?(double?)times[0]:null,maxMs=times.Count>0?(double?)times[^1]:null};
    }
    static async Task<object> TcpRtt(PerfPath path,int samples,CancellationToken ct)
    {
        using var client=await path.Open(ct);var stream=client.GetStream();await stream.WriteAsync("E"u8.ToArray(),ct);
        var data=RandomNumberGenerator.GetBytes(64);var reply=new byte[64];var times=new List<double>(samples);
        for(var i=-50;i<samples;i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data,i);var start=Stopwatch.GetTimestamp();
            await stream.WriteAsync(data,ct);await stream.ReadExactlyAsync(reply,ct);
            var elapsed=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if(!data.AsSpan().SequenceEqual(reply))throw new IOException("TCP echo payload mismatch");
            if(i>=0)times.Add(elapsed);
        }
        return Stats(times,samples,0);
    }
    static async Task<object> UdpRtt(PerfPath path,int samples,int payloadBytes,CancellationToken ct)
    {
        using var client=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));Wire.ConfigureUdp(client);client.Connect(IPAddress.Loopback,path.Port);
        var data=RandomNumberGenerator.GetBytes(payloadBytes);var times=new List<double>(samples);var lost=0;
        for(var i=-50;i<samples;i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data,i);var packet=path.EncodeUdp(data);var start=Stopwatch.GetTimestamp();
            await client.SendAsync(packet,ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while(true)
                {
                    var reply=path.DecodeUdp((await client.ReceiveAsync(timeout.Token)).Buffer);
                    if(!data.AsSpan().SequenceEqual(reply))continue;
                    if(i>=0)times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);break;
                }
            }
            catch(OperationCanceledException)when(!ct.IsCancellationRequested)
            {Console.WriteLine($"UDP echo timeout sequence {i}");if(i>=0)lost++;if(lost>10)throw new IOException("Too many UDP echo timeouts");}
        }
        return Stats(times,samples,lost,payloadBytes);
    }
    static async Task<object> Upload(PerfPath path,int seconds,CancellationToken ct)
    {
        using var client=await path.Open(ct);var stream=client.GetStream();await stream.WriteAsync("U"u8.ToArray(),ct);
        var data=RandomNumberGenerator.GetBytes(65536);var size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,data.Length);
        long sent=0;var watch=Stopwatch.StartNew();
        while(watch.Elapsed.TotalSeconds<seconds){await stream.WriteAsync(size,ct);await stream.WriteAsync(data,ct);sent+=data.Length;}
        var sendSeconds=watch.Elapsed.TotalSeconds;await stream.WriteAsync(new byte[4],ct);
        var ack=await Wire.Read(stream,16,ct);var elapsed=watch.Elapsed.TotalSeconds;
        var received=BinaryPrimitives.ReadInt64LittleEndian(ack);var receiveSeconds=BinaryPrimitives.ReadInt64LittleEndian(ack.AsSpan(8))/(double)Stopwatch.Frequency;
        if(received!=sent)throw new IOException("Upload receiver count differs from sender count");
        return new{receivedBytes=received,acknowledged=true,requestedSeconds=seconds,sendSeconds,elapsedSeconds=elapsed,receiverSeconds=receiveSeconds,mbps=received*8/elapsed/1e6};
    }
    static async Task<object> Download(PerfPath path,int seconds,CancellationToken ct)
    {
        using var client=await path.Open(ct);var stream=client.GetStream();var command=new byte[5];command[0]=(byte)'D';BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(1),seconds*1000);
        var data=new byte[65536];long received=0;var watch=Stopwatch.StartNew();await stream.WriteAsync(command,ct);
        while(true){var size=await Receiver.ReadSize(stream,ct);if(size==0)break;await stream.ReadExactlyAsync(data.AsMemory(0,size),ct);received+=size;}
        var ack=new byte[8];BinaryPrimitives.WriteInt64LittleEndian(ack,received);await stream.WriteAsync(ack,ct);
        var confirmed=BinaryPrimitives.ReadInt64LittleEndian(await Wire.Read(stream,8,ct));var elapsed=watch.Elapsed.TotalSeconds;
        if(confirmed!=received)throw new IOException("Download receiver/server acknowledgement differs");
        return new{receivedBytes=received,acknowledged=true,requestedSeconds=seconds,elapsedSeconds=elapsed,mbps=received*8/elapsed/1e6};
    }
}
