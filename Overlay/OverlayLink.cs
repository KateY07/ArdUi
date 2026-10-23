namespace ArdUi;

sealed class OverlayLink : IAsyncDisposable
{
    readonly TcpClient tcp;
    readonly Stream output;
    readonly SemaphoreSlim write=new(1);
    readonly CancellationTokenSource stop=new();
    readonly Func<byte[],CancellationToken,Task> sendUdp;
    readonly Func<string> network;
    public string Name{get;}
    public CancellationToken Token=>stop.Token;
    public bool Live=>!stop.IsCancellationRequested;
    public string Network=>network();
    public double? RttMs, JitterMs, BandwidthMbps;
    public double? BandwidthLowerBoundMbps;
    public double? BandwidthRttMs;
    public double? ServedRttMs;
    public bool BandwidthRefreshPending;
    public long MeasuredAt, ServedAt;
    public long UnresponsiveSince;
    public bool RttChangedFrom(double? reference)=>reference is{} measured&&RttMs is{} current&&Math.Abs(current-measured)>=Math.Max(10,measured*.5);
    public bool NeedsBandwidthRefresh(long now)=>Responsive&&MeasuredAt!=0&&now-MeasuredAt>=5000&&
        (BandwidthRefreshPending||RttChangedFrom(BandwidthRttMs)||BandwidthMbps==null&&BandwidthLowerBoundMbps==null);
    public bool CanServeBandwidth(long now)=>ServedAt==0||now-ServedAt>=30000||now-ServedAt>=5000&&RttChangedFrom(ServedRttMs);
    readonly ConcurrentQueue<double?> samples=new();
    public PathQuality Quality=>PathQuality.From(samples.ToArray(),BandwidthMbps);
    public bool RecoverySlowerThan(OverlayLink baseline)
    {
        var reference=baseline.Quality;
        // The first slow response after migration is hidden by the old P90 window.
        // Only recovery admission uses the latest RTT; stable routes keep hysteresis.
        return Quality.SlowerThan(reference)||reference.Received>=8&&RttMs is{} latest&&latest>reference.P90Ms*1.3+5;
    }
    public void Sample(double? ms)
    {
        var previous=Quality;
        samples.Enqueue(ms);while(samples.Count>16)samples.TryDequeue(out _);
        var current=Quality;
        // Responsiveness can recover before old losses leave the quality window.
        // Wake selection when eligibility or eligible loss improves; retain the
        // full window and avoid turning ordinary RTT jitter into polling traffic.
        if(current.Eligible!=previous.Eligible||current.Eligible&&
            (current.Loss<previous.Loss||previous.Score-current.Score>=Math.Max(5,previous.Score*.15)))
            HealthChanged?.Invoke();
    }
    public long Sent,Received,Probes,Lost;
    public Task Reader{get;set;}=Task.CompletedTask;
    public Action? Closed;
    public Action? HealthChanged;
    public volatile bool Responsive=true;
    public volatile bool HealthReady=true;
    // A probe answers for the moment it was sent. Probes overlap (one waits up to 2 s), so a late answer from an older probe must
    // not undo what a newer one found: one sent during a blackout times out after the path is back, or answers after it failed.
    readonly object responseGate=new();long responseSentAt=long.MinValue;
    public void ObserveResponse(bool responsive,long? sentAt=null)
    {
        lock(responseGate)
        {
            var at=sentAt??Environment.TickCount64;
            if(at<responseSentAt)return;
            responseSentAt=at;
            if(Responsive==responsive)return;
            Responsive=responsive;
        }
        HealthChanged?.Invoke();
    }
    public OverlayLink(string name,TcpClient tcp,Func<byte[],CancellationToken,Task> sendUdp,Func<string> network,Stream? output=null)
    {Name=name;this.tcp=tcp;this.output=output??tcp.GetStream();tcp.NoDelay=true;this.sendUdp=sendUdp;this.network=network;}
    public async Task Send(byte[] data,bool udp,CancellationToken ct)
    {
        if(!Live)throw new IOException("传输路径已关闭。");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token);linked.CancelAfter(TimeSpan.FromSeconds(5));
        if(udp){await sendUdp(data,linked.Token);Interlocked.Add(ref Sent,data.Length);return;}
        await write.WaitAsync(linked.Token);
        try
        {
            // Cancellation inside a framed TCP write must close the stream. Give an
            // in-flight frame the same recovery grace as health monitoring, so a brief
            // blackout during bandwidth diagnostics does not destroy a reusable path.
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,data.Length);
            await output.WriteAsync(header,linked.Token);await output.WriteAsync(data,linked.Token);
            Interlocked.Add(ref Sent,data.Length);
        }
        catch(Exception ex)
        {
            Diagnostics.Log("link-write-failed",Name+": "+ex.Message);
            stop.Cancel();tcp.Dispose();throw;
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
