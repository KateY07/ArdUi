global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Net;
global using System.Net.Sockets;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.RegularExpressions;
global using System.Threading.Channels;

namespace ArdUi;

static class Diagnostics
{
    public static void Log(string kind,string message)=>Console.WriteLine($"[{kind}] {message}");
}

static class Program
{
    static void Check(bool ok,string message)
    {if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
    public static async Task Main()
    {
        await Operations();Paths();Meters();Budgets();await BrokenWrite();await SlowFlow();await WindowThroughput();
        Console.WriteLine("V3 REGRESSION PASSED");
    }
    static async Task Operations()
    {
        var gate=new OperationGate();
        var first=await gate.Enter("a",CancellationToken.None);
        var waiting=gate.Enter("a",CancellationToken.None);
        using(var other=await gate.Enter("b",CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)))
            other.Commit(()=>Check(!waiting.IsCompleted,"another device does not wait for the blocked device"));
        gate.Cancel("a");Check(first.Token.IsCancellationRequested,"stop cancels active connection work");
        try{first.Commit(()=>throw new Exception("late attachment"));throw new Exception("commit was allowed");}
        catch(OperationCanceledException){Console.WriteLine("PASS: cancelled operation cannot attach a late session");}
        try{using var ignored=await waiting;throw new Exception("queued work continued");}
        catch(OperationCanceledException){Console.WriteLine("PASS: stop also cancels queued work");}
        var closing=gate.Close();Check(!closing.IsCompleted,"shutdown waits for active cleanup");first.Dispose();await closing.WaitAsync(TimeSpan.FromSeconds(1));
    }
    static void Paths()
    {
        var path=new ArdPathState();
        path.Observe("CONNECTED transport=\"direct\" network=\"ipv4\" rtt_ms=3");
        Check(path.Network.Contains("P2P")&&path.Rtt==3,"selected direct path updates status");
        path.Observe("unused network path closed transport=\"relay\" network=\"relay\"");
        Check(path.Network.Contains("P2P"),"unused closed path does not replace selected path");
        path.Observe("selected network path closed transport=\"direct\" network=\"ipv4\"");
        Check(!path.Network.Contains("P2P")&&path.Rtt==null,"closed selected path becomes unknown");
        path.Observe("network path selected transport=\"relay\" network=\"relay\" rtt_ms=20");
        path.Observe("network path log events were dropped; state resynchronized");
        Check(path.Rtt==null&&!path.Network.Contains("中继"),"lost events invalidate stale path state");
    }
    static void Meters()
    {
        static (double? Rate,double? LowerBound) Sample(double start,double interval)
        {var meter=new TransferMeter();for(var i=0;i<17;i++)meter.Add(16384,start+i*interval);return meter.Result();}
        var local=Sample(0,.01);var delayed=Sample(100,.01);
        Check(Math.Abs(local.Rate!.Value-delayed.Rate!.Value)<.001,"100 seconds request delay does not bias throughput");
        var shortSample=Sample(0,.0001);
        Check(shortSample.Rate==null&&shortSample.LowerBound>0,"short capped sample is only a lower bound");
        Check(PathQuality.From(Enumerable.Repeat<double?>(5,8).ToArray(),null).Eligible,"unknown throughput does not disable valid RTT/loss candidate evaluation");
        var healthy=PathQuality.From(Enumerable.Repeat<double?>(40,8).ToArray(),100);
        var unknown=PathQuality.From(Enumerable.Repeat<double?>(5,8).ToArray(),null);
        Check(!unknown.BetterThan(healthy),"unknown candidate bandwidth cannot bypass healthy baseline protection");
        Check(!PathQuality.From(Enumerable.Repeat<double?>(5,8).ToArray(),50).BetterThan(healthy),"known bandwidth degradation remains rejected");
        Check(PathQuality.From(Enumerable.Repeat<double?>(5,8).ToArray(),90).BetterThan(healthy),"measured acceptable bandwidth and lower latency remain eligible");
        Check(unknown.BetterThan(PathQuality.From([null,null,null,null,null,null,null,null],null)),"unavailable baseline permits emergency candidate recovery");
        Check(!unknown.BetterThan(PathQuality.From([],null)),"missing baseline samples are not mistaken for an unavailable path");
    }
    static void Budgets()
    {
        var budget=new FlowCreditBudget();
        Parallel.For(0,FlowCreditBudget.ExtraLimit*2,_=>budget.TryGrow());
        Check(budget.Used==FlowCreditBudget.ExtraLimit&&!budget.TryGrow(),"concurrent flow growth respects the shared 32 MiB extra budget");
        budget.Release(100);for(var i=0;i<100;i++)if(!budget.TryGrow())throw new Exception("Returned capacity was lost.");
        Check(!budget.TryGrow(),"reused budget remains bounded");
    }
    static async Task BrokenWrite()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Server.ReceiveBufferSize=1024;listener.Start();
        try
        {
            using var sender=new TcpClient{SendBufferSize=1024};
            await sender.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            using var receiver=await listener.AcceptTcpClientAsync();receiver.ReceiveBufferSize=1024;
            await using var link=new OverlayLink("test",sender,(_,_)=>Task.CompletedTask,()=>"test",new HeaderThenStall(sender.GetStream()));
            using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var send=link.Send(new byte[4096],false,cancel.Token);
            var header=await Wire.Read(receiver.GetStream(),4,cancel.Token);
            Check(BinaryPrimitives.ReadInt32BigEndian(header)==4096,"injected failure begins after frame header was transmitted");
            using var queued=new CancellationTokenSource();queued.Cancel();
            try{await link.Send(new byte[40],false,queued.Token);throw new Exception("cancelled write accepted");}
            catch(OperationCanceledException){Check(link.Live,"cancellation while waiting for write lock preserves link");}
            cancel.Cancel();
            try{await send;throw new Exception("blocked write unexpectedly finished");}
            catch(OperationCanceledException){Check(!link.Live,"partial frame cancellation retires the transport");}
            try{await link.Send(new byte[40],false,CancellationToken.None);throw new Exception("broken link reused");}
            catch(IOException){Console.WriteLine("PASS: broken transport cannot be reused");}
        }
        finally{listener.Stop();}
    }
    static async Task SlowFlow()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));var ct=deadline.Token;
        var target=new TcpListener(IPAddress.Loopback,0);target.Start();var port=((IPEndPoint)target.LocalEndpoint).Port;
        var cap=RandomNumberGenerator.GetBytes(16);await using var gateway=new Gateway(new Settings{TcpPorts=[port]},cap);
        OverlaySession? host=null;
        gateway.AttachOverlay=async(tcp,command,cancel)=>
        {
            host=await OverlaySession.AcceptHello(tcp,gateway.Port,cap,()=>"test",cancel);
            host.Control=(_,_,_)=>Task.FromResult<object>(new{ok=true});gateway.Overlay=host;
            var link=new OverlayLink("base",tcp,gateway.SendOverlayUdp,()=>"test");await host.Add(link);await link.Reader;
        };
        await using var caller=await OverlaySession.Connect(gateway.Port,cap,()=>"test",ct);
        async Task<TcpClient> Open()
        {
            var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,caller.Port,ct);
            var head=new byte[23];"AUI1"u8.CopyTo(head);cap.CopyTo(head,4);head[20]=1;
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(21),(ushort)port);
            await tcp.GetStream().WriteAsync(head,ct);await Wire.Read(tcp.GetStream(),1,ct);return tcp;
        }
        try
        {
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));udp.Connect(IPAddress.Loopback,caller.Port);
            var nonce=RandomNumberGenerator.GetBytes(12);
            async Task SendUdp()=>await udp.SendAsync(Wire.Packet(cap,0,nonce),ct);
            await SendUdp();Check((await udp.ReceiveAsync(ct)).Buffer.AsSpan(18).SequenceEqual(nonce),"Overlay UDP baseline round trip");
            var targets=(ConcurrentDictionary<uint,TargetUdp>)typeof(OverlaySession).GetField("targetUdp",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(host)!;
            var entry=targets.Single();entry.Value.Dispose();
            var failed=new TargetUdp(gateway.Port,_=>Task.CompletedTask,ct,1500);failed.Dispose();await failed.Completion;targets[entry.Key]=failed;
            await SendUdp();
            var limit=DateTime.UtcNow.AddSeconds(2);while(targets.ContainsKey(entry.Key)&&DateTime.UtcNow<limit)await Task.Delay(10,ct);
            Check(!targets.ContainsKey(entry.Key),"expired Overlay target mapping is isolated and removed");
            await SendUdp();Check((await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Buffer.AsSpan(18).SequenceEqual(nonce),"shared gateway UDP receiver survives target mapping failure");
            using var slow=await Open();using var sink=await target.AcceptTcpClientAsync(ct);sink.ReceiveBufferSize=1024;
            var payload=RandomNumberGenerator.GetBytes(16*1024*1024);
            async Task Fill()
            {for(var offset=0;offset<payload.Length;offset+=65536)await slow.GetStream().WriteAsync(payload.AsMemory(offset,65536),ct);}
            var fill=Fill();await Task.Delay(700,ct);
            Check(!fill.IsCompleted,"slow receiver creates real backpressure");
            await caller.Request("test",new{},ct).WaitAsync(TimeSpan.FromSeconds(2));
            Console.WriteLine("PASS: control request progresses while slow flow is blocked");
            await caller.MeasureThroughput(caller.Link("base")!,ct);
            Check(caller.Link("base")!.BandwidthMbps>0||caller.Link("base")!.BandwidthLowerBoundMbps>0,"stream throughput probe progresses while slow flow is blocked");
            using var fast=await Open();using var echo=await target.AcceptTcpClientAsync(ct);
            var message=RandomNumberGenerator.GetBytes(97);await fast.GetStream().WriteAsync(message,ct);
            var delivered=await Wire.Read(echo.GetStream(),message.Length,ct).WaitAsync(TimeSpan.FromSeconds(2));
            Check(delivered.SequenceEqual(message),"independent TCP flow progresses with exact bytes");
            sink.ReceiveBufferSize=1024*1024;
            var draining=Wire.Read(sink.GetStream(),payload.Length,ct);
            await fill;var received=await draining;
            Check(SHA256.HashData(payload).SequenceEqual(SHA256.HashData(received)),"slow flow resumes without dropped or reordered data");
            var state=JsonSerializer.SerializeToElement(host!.Snapshot());
            Check(state.GetProperty("extraReceiveCredits").GetInt32()>16,"drained bulk flow grows beyond the old 256 KiB receive limit");
            Check(state.GetProperty("extraReceiveCredits").GetInt32()<=FlowCreditBudget.ExtraLimit,"active flow growth remains inside aggregate budget");
            slow.Client.Shutdown(SocketShutdown.Send);
            var eof=await sink.GetStream().ReadAsync(new byte[1],ct);
            Check(eof==0,"independent half-close is preserved");
        }
        finally{deadline.Cancel();target.Stop();if(host!=null)await host.DisposeAsync();}
    }
    static async Task WindowThroughput()
    {
        async Task<double> Transfer(int maximum)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));var ct=timeout.Token;
            var cap=RandomNumberGenerator.GetBytes(16);var sink=new TcpListener(IPAddress.Loopback,0);sink.Start();
            var port=((IPEndPoint)sink.LocalEndpoint).Port;
            await using var gateway=new Gateway(new Settings{TcpPorts=[port]},cap);
            OverlaySession? host=null;LatencyStream? delay=null;
            gateway.AttachOverlay=async(tcp,_,token)=>
            {
                host=await OverlaySession.AcceptHello(tcp,gateway.Port,cap,()=>"test",token);gateway.Overlay=host;
                delay=new(tcp.GetStream(),TimeSpan.FromMilliseconds(100),ct);
                var link=new OverlayLink("base",tcp,gateway.SendOverlayUdp,()=>"test",delay);await host.Add(link);await link.Reader;
            };
            await using var caller=await OverlaySession.Connect(gateway.Port,cap,()=>"test",ct,maximum);
            try
            {
                using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,caller.Port,ct);
                var head=new byte[23];"AUI1"u8.CopyTo(head);cap.CopyTo(head,4);head[20]=1;BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(21),(ushort)port);
                await tcp.GetStream().WriteAsync(head,ct);await Wire.Read(tcp.GetStream(),1,ct);
                using var receiver=await sink.AcceptTcpClientAsync(ct);
                var payload=RandomNumberGenerator.GetBytes(8*1024*1024);var watch=Stopwatch.StartNew();
                var reading=Wire.Read(receiver.GetStream(),payload.Length,ct);
                for(var offset=0;offset<payload.Length;offset+=65536)await tcp.GetStream().WriteAsync(payload.AsMemory(offset,65536),ct);
                var bytes=await reading;watch.Stop();
                Check(SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(payload)),"100 ms credit/ACK path preserves complete bulk payload");
                return watch.Elapsed.TotalSeconds;
            }
            finally{timeout.Cancel();sink.Stop();if(host!=null)await host.DisposeAsync();if(delay!=null)await delay.Completion;}
        }
        var fixedWindow=await Transfer(16);var growingWindow=await Transfer(FlowCreditBudget.Maximum);
        Console.WriteLine($"WINDOW COMPARISON: fixed={fixedWindow:F3}s adaptive={growingWindow:F3}s; 8 MiB, injected 100 ms return-path latency (not an Internet benchmark).");
        Check(growingWindow<fixedWindow*.75,"adaptive credits remove the old small-window bottleneck under delayed feedback");
    }
    sealed class LatencyStream : Stream
    {
        readonly Stream target;
        readonly CancellationToken token;
        readonly TimeSpan latency;
        readonly Channel<(byte[] Bytes,long Due)> queue=Channel.CreateBounded<(byte[],long)>(4096);
        public Task Completion{get;}
        public LatencyStream(Stream target,TimeSpan latency,CancellationToken token)
        {this.target=target;this.latency=latency;this.token=token;Completion=Pump();}
        async Task Pump()
        {
            try
            {
                await foreach(var item in queue.Reader.ReadAllAsync(token))
                {
                    var remaining=item.Due-Environment.TickCount64;
                    if(remaining>0)await Task.Delay(TimeSpan.FromMilliseconds(remaining),token);
                    await target.WriteAsync(item.Bytes,token);
                }
            }
            catch(Exception ex)when(token.IsCancellationRequested){Diagnostics.Log("latency-stream",ex.Message);}
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)
        {await queue.Writer.WriteAsync((buffer.ToArray(),Environment.TickCount64+(long)latency.TotalMilliseconds),ct);}
        public override bool CanRead=>false;
        public override bool CanSeek=>false;
        public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>throw new NotSupportedException();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
    }
    sealed class HeaderThenStall(Stream target):Stream
    {
        int writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)
        {
            if(Interlocked.Increment(ref writes)==1){await target.WriteAsync(buffer,ct);return;}
            await target.WriteAsync(buffer[..1],ct);
            await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        }
        public override bool CanRead=>false;
        public override bool CanSeek=>false;
        public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>target.Flush();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
    }
}
