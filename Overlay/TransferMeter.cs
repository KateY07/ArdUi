namespace ArdUi;

// Time data arrival, not request/response latency. Short capped samples are lower bounds.
sealed class TransferMeter
{
    readonly object sync=new();
    double first=-1,last;
    long bytes;
    public readonly TaskCompletionSource Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Add(int size,double seconds)
    {
        lock(sync)
        {
            if(first<0){first=last=seconds;return;}
            bytes+=size;last=seconds;
        }
    }
    public (double? Rate,double? LowerBound) Result()
    {
        lock(sync)
        {
            var elapsed=last-first;
            if(bytes<65536||elapsed<=0)return(null,null);
            return elapsed>=.1?(bytes*8d/elapsed/1e6,null):(null,bytes*8d/.1/1e6);
        }
    }
}
