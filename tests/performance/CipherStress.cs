namespace ArdUi;

static class CipherStress
{
    public static async Task<int> Run()
    {
        const int workers=8,framesPerWorker=192,framesPerChannel=workers*framesPerWorker;
        var watch=Stopwatch.StartNew();var secret=RandomNumberGenerator.GetBytes(32);var context=RandomNumberGenerator.GetBytes(32);
        using var a=new OverlayCipher(secret,context,true);using var b=new OverlayCipher(secret,context,false);
        CryptographicOperations.ZeroMemory(secret);
        var nonces=new ConcurrentDictionary<(int Direction,bool Udp,ulong Sequence),byte>();
        using var start=new ManualResetEventSlim(false);
        Console.WriteLine($"Cipher stress: {workers} workers, {framesPerChannel} frames per direction/channel; TCP and UDP run concurrently.");
        var tasks=Enumerable.Range(0,workers).Select(worker=>Task.Factory.StartNew(()=>
        {
            start.Wait();
            for(var index=0;index<framesPerWorker;index++)
            for(var direction=0;direction<2;direction++)
            for(var channel=0;channel<2;channel++)
            {
                var udp=channel==1;var sender=direction==0?a:b;var receiver=direction==0?b:a;
                var length=(index%4) switch{0=>13,1=>64,2=>1200,_=>udp?1500:16384};
                var plain=RandomNumberGenerator.GetBytes(length);
                BinaryPrimitives.WriteInt32LittleEndian(plain,worker);BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(4),index);
                BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(8),direction);plain[12]=(byte)channel;
                var packet=sender.Encrypt(plain,udp);
                if(packet.Length!=plain.Length+25||packet[0]!=channel)throw new IOException("Cipher stress: malformed encrypted packet.");
                var sequence=BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(1,8));
                if(sequence==0||!nonces.TryAdd((direction,udp,sequence),0))throw new CryptographicException("Cipher stress: reused nonce for a direction/channel key.");
                var decoded=receiver.Decrypt(packet,udp);
                if(decoded==null||!decoded.AsSpan().SequenceEqual(plain))throw new CryptographicException($"Cipher stress: payload mismatch worker={worker} frame={index} direction={direction} udp={udp}.");
                if(udp&&receiver.Decrypt(packet,true)!=null)throw new CryptographicException("Cipher stress: UDP replay accepted.");
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray();
        start.Set();
        try{await Task.WhenAll(tasks);}
        catch
        {
            foreach(var task in tasks.Where(task=>task.IsFaulted))Console.Error.WriteLine(task.Exception!.Flatten());
            throw;
        }
        for(var direction=0;direction<2;direction++)
        for(var channel=0;channel<2;channel++)
        {
            var sequences=nonces.Keys.Where(n=>n.Direction==direction&&n.Udp==(channel==1)).Select(n=>n.Sequence).Order().ToArray();
            if(sequences.Length!=framesPerChannel||sequences.Where((n,index)=>n!=(ulong)index+1).Any())
                throw new CryptographicException("Cipher stress: sequence gap or missing encrypted frame.");
        }
        if(a.Replays!=framesPerChannel||b.Replays!=framesPerChannel||a.Rejected!=0||b.Rejected!=0)
            throw new CryptographicException($"Cipher stress: unexpected counters A replay/rejected={a.Replays}/{a.Rejected}, B={b.Replays}/{b.Rejected}.");
        Console.WriteLine($"PASS: {nonces.Count} concurrent bidirectional TCP/UDP frames intact; all nonces unique within direction/channel; {a.Replays+b.Replays} UDP replays rejected; {watch.Elapsed.TotalSeconds:F3}s.");
        return 0;
    }
}
