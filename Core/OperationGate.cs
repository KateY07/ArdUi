namespace ArdUi;

// Cancellation and publication share a lock so a cancelled operation cannot attach late.
sealed class OperationGate
{
    readonly object sync=new();
    readonly Dictionary<string,Group> groups=new();
    bool closed;
    sealed class Group
    {
        public readonly SemaphoreSlim Gate=new(1);
        public readonly CancellationTokenSource Stop=new();
        public readonly TaskCompletionSource Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Users;
    }
    public sealed class Lease : IDisposable
    {
        readonly Action release;
        readonly Action<Action> commit;
        public CancellationToken Token{get;}
        int disposed;
        internal Lease(CancellationToken token,Action release,Action<Action> commit){Token=token;this.release=release;this.commit=commit;}
        public void Commit(Action action)=>commit(action);
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)release();}
    }
    public async Task<Lease> Enter(string key,CancellationToken ct)
    {
        Group group;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(closed,this);
            if(!groups.TryGetValue(key,out group!))groups[key]=group=new();
            group.Users++;
        }
        var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,group.Stop.Token);
        void Release(bool acquired)
        {
            linked.Dispose();
            lock(sync)
            {
                if(acquired)group.Gate.Release();
                if(--group.Users==0){groups.Remove(key);group.Gate.Dispose();group.Stop.Dispose();group.Done.TrySetResult();}
            }
        }
        try{await group.Gate.WaitAsync(linked.Token);}
        catch{Release(false);throw;}
        return new(linked.Token,()=>Release(true),action=>
        {lock(sync){linked.Token.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(closed,this);action();}});
    }
    public void Cancel(string key)=>CancelWhere(candidate=>candidate==key);
    public void CancelWhere(Func<string,bool> predicate)
    {lock(sync)foreach(var pair in groups.ToArray())if(predicate(pair.Key))pair.Value.Stop.Cancel();}
    public Task Close()
    {
        lock(sync)
        {
            closed=true;var active=groups.Values.ToArray();
            foreach(var group in active)group.Stop.Cancel();
            return Task.WhenAll(active.Select(group=>group.Done.Task));
        }
    }
}
