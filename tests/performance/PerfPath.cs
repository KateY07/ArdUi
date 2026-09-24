namespace ArdUi;

sealed class PerfPath : IAsyncDisposable
{
    readonly List<IAsyncDisposable> resources=new();
    readonly List<Child> children=new();
    readonly List<(Session Session,Child Process)> sessions=new();
    readonly ConcurrentDictionary<Child,ConcurrentQueue<string>> logs=new();
    byte[]? capability;
    int target;
    OverlaySession? overlayHost,overlay;
    public int Port { get; set; }
    public double ChildCpuSeconds()
    {CheckSessionProcesses();return children.Sum(child=>{child.Process.Refresh();return child.Process.TotalProcessorTime.TotalSeconds;});}
    public static async Task<PerfPath> Create(string mode,int target,string root,CancellationToken ct)
    {
        var result=new PerfPath{Port=target,target=target};Directory.CreateDirectory(root);
        try
        {
            if(mode=="raw")return result;
            if(mode=="overlay")
            {
                var cap=RandomNumberGenerator.GetBytes(16);result.capability=cap;
                var gateway=new Gateway(new Settings{TcpPorts=[target],UdpPorts=[target]},cap);result.resources.Add(gateway);
                gateway.AttachOverlay=async(tcp,command,cancel)=>
                {
                    if(command==4){result.overlayHost=await OverlaySession.AcceptHello(tcp,gateway.Port,cap,()=>"in-process loopback",cancel);gateway.Overlay=result.overlayHost;}
                    else{var id=await Wire.ReadJson<JsonElement>(tcp.GetStream(),cancel);if(id.GetProperty("session").GetString()!=result.overlayHost!.Id)throw new IOException("Resume identity mismatch");await tcp.GetStream().WriteAsync(new byte[]{0},cancel);}
                    var link=new OverlayLink("base",tcp,gateway.SendOverlayUdp,()=>"in-process loopback");await result.overlayHost!.Add(link);await link.Reader;
                };
                result.overlay=await OverlaySession.Connect(gateway.Port,cap,()=>"in-process loopback",ct);result.Port=result.overlay.Port;
                return result;
            }
            var relay=Required("ARDUI_TEST_RELAY");Loopback(relay);var settings=new Settings{Relay=relay,RelayKey=Required("ARDUI_TEST_RELAY_KEY"),TcpPorts=[target],UdpPorts=[target]};
            if(mode=="ard")
            {
                var a=Path.Combine(root,"a");var b=Path.Combine(root,"b");var aid=await Ard.Identity(a,ct);var bid=await Ard.Identity(b,ct);
                var host=Ard.Start(b,true,target,aid,settings);result.Track(host);result.resources.Add(host);await host.WaitFor("relay online",ct);
                result.Port=Wire.Port();var caller=Ard.Start(a,false,result.Port,bid,settings);result.Track(caller);result.resources.Add(caller);await caller.WaitFor("READY:",ct);
                return result;
            }
            if(mode=="full")
            {
                var server=Required("ARDUI_TEST_SERVER");Loopback(server);
                var a=Path.Combine(root,"a");var b=Path.Combine(root,"b");IdentityStore.Prepare(a);IdentityStore.Prepare(b);
                var caller=await Engine.Create(a,settings);result.resources.Add(caller);var host=await Engine.Create(b,settings);result.resources.Add(host);
                var ad=new DirectoryClient(caller,server);result.resources.Add(ad);var bd=new DirectoryClient(host,server);result.resources.Add(bd);
                ad.Confirm=(_,_)=>Task.FromResult(true);bd.Confirm=(_,_)=>Task.FromResult(true);
                ad.Notice+=message=>Console.WriteLine("A: "+message);bd.Notice+=message=>Console.WriteLine("B: "+message);
                await ad.Register(ct);await bd.Register(ct);await bd.SetAccess(true,ct);ad.Start();bd.Start();await ad.Connect(bd.Code,bd.AccessPassword,ct);
                var session=caller.Outgoing[host.Id];var incoming=host.Incoming[caller.Id];result.Track(session.Process);result.Track(incoming.Process);
                result.sessions.Add((session,session.Process));result.sessions.Add((incoming,incoming.Process));
                result.overlay=session.Overlay;result.overlayHost=incoming.Overlay;result.Port=session.Forward(target).Port;return result;
            }
            throw new ArgumentException("Unknown mode: "+mode);
        }
        catch{await result.DisposeAsync();throw;}
    }
    static string Required(string key)=>Environment.GetEnvironmentVariable(key)??throw new IOException("Missing isolated fixture variable "+key);
    static void Loopback(string address){if(!Uri.TryCreate(address,UriKind.Absolute,out var uri)||!uri.IsLoopback)throw new IOException("Performance fixture only permits loopback servers");}
    void Track(Child child)
    {
        children.Add(child);var captured=new ConcurrentQueue<string>();logs[child]=captured;
        child.Output+=line=>captured.Enqueue(line);
        var field=typeof(Child).GetField("lines",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)??throw new MissingFieldException("Child diagnostic queue missing");
        foreach(var line in (ConcurrentQueue<string>)field.GetValue(child)!)captured.Enqueue(line);
    }
    public async Task<TcpClient> Open(CancellationToken ct)
    {
        var client=new TcpClient{NoDelay=true};
        try
        {
            await client.ConnectAsync(IPAddress.Loopback,Port,ct);
            if(capability!=null)
            {
                var head=new byte[23];"AUI1"u8.CopyTo(head);capability.CopyTo(head,4);head[20]=1;BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(21),(ushort)target);
                await client.GetStream().WriteAsync(head,ct);if((await Wire.Read(client.GetStream(),1,ct))[0]!=0)throw new IOException("Overlay gateway denied target");
            }
            return client;
        }
        catch{client.Dispose();throw;}
    }
    public byte[] EncodeUdp(byte[] data)=>capability==null?data:Wire.Packet(capability,target,data);
    public byte[] DecodeUdp(byte[] data)
    {
        if(capability==null)return data;
        if(!Wire.ValidPacket(data,capability))throw new IOException("Invalid overlay UDP reply");
        return data[18..];
    }
    public EvidenceSnapshot Evidence()
    {
        CheckSessionProcesses();
        var paths=new List<BusinessPath>();
        foreach(var child in children)
        {
            var lines=logs[child].Distinct().OrderBy(line=>line,StringComparer.Ordinal).ToArray();
            var business=lines.LastOrDefault(l=>l.Contains("local TCP connection accepted")||l.Contains("TCP/UDP exposure ready")||l.Contains("peer TCP stream accepted"));
            var id=business==null?null:Regex.Match(business,@"\btunnel=(\d+)").Groups[1].Value;
            var selected=id==null?null:lines.LastOrDefault(l=>Regex.IsMatch(l,@"\btunnel="+Regex.Escape(id)+@"\b")&&(l.Contains("CONNECTED")||l.Contains("network path selected")||l.Contains("selected network path closed")));
            paths.Add(new(child.Process.Id,id,business,selected,selected?.Contains("transport=\"direct\"")==true&&!selected.Contains("closed"),lines));
        }
        return new(paths.ToArray(),overlay==null?null:JsonSerializer.SerializeToElement(overlay.Snapshot()),overlayHost==null?null:JsonSerializer.SerializeToElement(overlayHost.Snapshot()));
    }
    public void CheckDirect(EvidenceSnapshot evidence)
    {
        CheckSessionProcesses();
        if(evidence.BusinessPaths.Any(p=>!p.Direct))throw new IOException("Business direct proof missing: "+JsonSerializer.Serialize(evidence));
        if(overlay!=null&&overlay.Selected!="base")throw new IOException("Performance test business moved to candidate path");
    }
    void CheckSessionProcesses()
    {
        foreach(var tracked in sessions)
            if(!ReferenceEquals(tracked.Session.Process,tracked.Process)||tracked.Process.Exited.IsCompleted)
                throw new IOException("Production Engine replaced or exited a tracked business ARD process during measurement");
    }
    public IntervalEvents[] CheckInterval(EvidenceSnapshot before,EvidenceSnapshot after)
    {
        CheckSessionProcesses();
        if(before.BusinessPaths.Length!=after.BusinessPaths.Length)throw new IOException("Business process count changed during measurement");
        var result=new List<IntervalEvents>();
        foreach(var previous in before.BusinessPaths)
        {
            var current=after.BusinessPaths.SingleOrDefault(p=>p.Pid==previous.Pid);
            if(current==null||current.Tunnel!=previous.Tunnel)throw new IOException("Business PID/tunnel changed during measurement");
            var added=current.Logs.Except(previous.Logs,StringComparer.Ordinal).Where(line=>
                Regex.IsMatch(line,@"\btunnel="+Regex.Escape(current.Tunnel!)+@"\b")&&
                (line.Contains("network path selected")||line.Contains("CONNECTED")||line.Contains("selected network path closed")||line.Contains("network path log events were dropped"))).ToArray();
            foreach(var line in added)
                if(line.Contains("selected network path closed")||line.Contains("network path log events were dropped")||!line.Contains("transport=\"direct\""))
                    throw new IOException("Business path interrupted, relayed or diagnostic events lost during measurement: "+line);
            result.Add(new(current.Pid,current.Tunnel!,added));
        }
        return result.ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        if(resources.All(r=>r is not Engine))
        {if(overlay!=null)await overlay.DisposeAsync();if(overlayHost!=null)await overlayHost.DisposeAsync();}
        foreach(var resource in resources.AsEnumerable().Reverse())await resource.DisposeAsync();
    }
}
sealed record BusinessPath(int Pid,string? Tunnel,string? BusinessEvent,string? SelectedPathEvent,bool Direct,string[] Logs);
sealed record EvidenceSnapshot(BusinessPath[] BusinessPaths,JsonElement? Overlay,JsonElement? PeerOverlay);
sealed record IntervalEvents(int Pid,string Tunnel,string[] Events);
