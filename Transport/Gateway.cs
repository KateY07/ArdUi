namespace ArdUi;

sealed class Gateway : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly UdpClient udp;
    readonly Settings settings;
    readonly byte[] token;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> tasks = new();
    readonly ConcurrentDictionary<int,TcpClient> clients=new();
    readonly ConcurrentDictionary<string, TargetUdp> udpFlows = new();
    readonly ConcurrentDictionary<int,byte> frdTcp=new(),frdUdp=new(),voiceUdp=new();
    // The UDP port this machine listens on for a voice call (open only while that call lasts).
    public void PermitVoiceUdp(int port,bool enabled)
    {
        if(enabled){voiceUdp[port]=0;return;}
        voiceUdp.TryRemove(port,out _);
        foreach(var pair in udpFlows.Where(p=>p.Key.EndsWith("/"+port,StringComparison.Ordinal)).ToArray())
            if(udpFlows.TryRemove(pair.Key,out var flow))flow.Dispose();
    }
    // Is the screen being used right now: FRD running on this machine, or an RDP connection open.
    int rdpBridges; bool lastScreenActive; readonly object activityGate = new();
    public bool ScreenActive => !frdTcp.IsEmpty || Volatile.Read(ref rdpBridges) > 0;
    public event Action<bool>? ScreenActivity;
    void UpdateActivity()
    {
        bool now, changed;
        lock (activityGate) { now = ScreenActive; changed = now != lastScreenActive; lastScreenActive = now; }
        if (changed) ScreenActivity?.Invoke(now);
    }
    public void PermitFrdTcp(int port,bool enabled){if(enabled)frdTcp[port]=0;else frdTcp.TryRemove(port,out _);UpdateActivity();}
    public void PermitFrdUdp(int port,bool enabled)
    {
        if(enabled){frdUdp[port]=0;return;}
        frdUdp.TryRemove(port,out _);
        foreach(var pair in udpFlows.Where(p=>p.Key.EndsWith("/"+port,StringComparison.Ordinal)).ToArray())
            if(udpFlows.TryRemove(pair.Key,out var flow))flow.Dispose();
    }
    readonly SemaphoreSlim tcpSlots = new(128);
    readonly Task acceptTask, udpTask;
    int serial,stopped,admission;
    public int Port { get; }
    public event Action? Attached;
    public Func<TcpClient,byte,CancellationToken,Task>? AttachOverlay;
    public OverlaySession? Overlay;
    IPEndPoint? overlayRemote;
    public async Task SendOverlayUdp(byte[] data,CancellationToken ct)
    {if(overlayRemote is{} remote)await udp.SendAsync(Wire.Packet(token,65535,data),remote,ct);}
    readonly Func<PasswordRequest, CancellationToken, Task<Authorization?>>? authenticate;
    // What the controller of this gateway may reach: set when it is authorised (everything configured until then, as before).
    int[] tcpAllowed, udpAllowed;
    Perm perms = Perm.All;
    public Perm Perms => perms;
    public bool Once { get; private set; }
    public bool FrdAllowed => perms.HasFlag(Perm.Frd);
    void Apply(Perm value)
    {
        perms = value; tcpAllowed = Permissions.TcpPorts(settings, value); udpAllowed = Permissions.UdpPorts(settings, value);
    }
    public Gateway(Settings settings, byte[] token, Func<PasswordRequest, CancellationToken, Task<Authorization?>>? authenticate = null)
    {
        this.settings = settings; this.token = token; this.authenticate = authenticate;
        tcpAllowed = settings.TcpPorts.Where(p => p != Permissions.RetiredFilesPort).ToArray(); udpAllowed = settings.UdpPorts.Where(p => p != Permissions.RetiredFilesPort).ToArray();
        UdpClient? pair; (listener, pair) = Wire.LoopbackPair(); udp = pair!;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Wire.ConfigureUdp(udp);
        acceptTask = Accept(); udpTask = Receive();
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!tcpSlots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref serial);
                clients[id]=client;
                if(stop.IsCancellationRequested){clients.TryRemove(id,out _);client.Dispose();tcpSlots.Release();break;}
                var task = Serve(client); tasks[id] = task;
                _ = task.ContinueWith(t => { tcpSlots.Release(); tasks.TryRemove(id, out _);clients.TryRemove(id,out _); }, TaskScheduler.Default);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client)
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var head = await Wire.Read(stream, 23, handshake.Token);
                if (!head.AsSpan(0, 4).SequenceEqual("AUI1"u8)) return;
                var command = head[20]; var port = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(21));
                if (command == 2 && port == 0 && authenticate != null)
                {
                    handshake.CancelAfter(TimeSpan.FromMinutes(5));
                    var request = await Wire.ReadJson<PasswordRequest>(stream, handshake.Token);
                    // One admission per session, and one at a time. The gateway listens on loopback, so any local process can reach it, not
                    // only the ARD tunnel; a second admission must not hand out the session token again or change what the session may do.
                    if (Interlocked.CompareExchange(ref admission, 1, 0) != 0)
                    {
                        await Wire.WriteJson(stream, new AdmissionReply(false, "本次连接已完成准入。", null, [], [], null, 0, false), handshake.Token);
                        return;
                    }
                    Authorization? authorization = null;
                    try { authorization = await authenticate(request, handshake.Token); }
                    finally { if (authorization == null) Volatile.Write(ref admission, 0); }   // a refused attempt does not use the session up
                    var accepted = authorization != null;
                    if (authorization != null) { Apply(authorization.Perms); Once = authorization.Once; }
                    await Wire.WriteJson(stream, new AdmissionReply(accepted,
                        accepted ? null : ConnectFlow.RejectedText,
                        accepted ? Convert.ToHexString(token) : null,
                        accepted ? tcpAllowed : [], accepted ? udpAllowed : [], authorization?.Grant,
                        accepted ? Permissions.Store(perms) : 0, accepted && Once), handshake.Token);
                    if (accepted) Attached?.Invoke();
                    return;
                }
                if (!CryptographicOperations.FixedTimeEquals(head.AsSpan(4, 16), token)) return;
                if(command is 4 or 5 && port==0 && AttachOverlay!=null)
                {handshake.CancelAfter(Timeout.InfiniteTimeSpan);await AttachOverlay(client,command,stop.Token);return;}
                if (command == 0 && port == 0)
                { Attached?.Invoke(); await stream.WriteAsync(new byte[] { 0 }, handshake.Token); return; }
                if(command==3&&port==0)
                {
                    Attached?.Invoke();client.NoDelay=true;await stream.WriteAsync(new byte[]{0},handshake.Token);
                    var block=new byte[8192];for(var sent=0;sent<Wire.BandwidthProbeSize;sent+=block.Length)await stream.WriteAsync(block,handshake.Token);
                    return;
                }
                if (command != 1 || !tcpAllowed.Contains((int)port)&&!frdTcp.ContainsKey(port))
                { await stream.WriteAsync(new byte[] { 2 }, handshake.Token); return; }
                using var target = new TcpClient { NoDelay = true };
                try { await target.ConnectAsync(IPAddress.Loopback, port, handshake.Token); }
                catch { await stream.WriteAsync(new byte[] { 1 }, stop.Token); return; }
                client.NoDelay = true;
                await stream.WriteAsync(new byte[] { 0 }, handshake.Token);
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                var rdp = !frdTcp.ContainsKey(port);   // anything that is not FRD is a desktop (RDP)
                if (rdp) { Interlocked.Increment(ref rdpBridges); UpdateActivity(); }
                try { await Wire.Bridge(client, target, stop.Token); }
                finally { if (rdp) { Interlocked.Decrement(ref rdpBridges); UpdateActivity(); } }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Diagnostics.Log("gateway-tcp",ex.Message); }
        }
    }
    async Task Receive()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(stop.Token);
                try
                {
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || !Wire.ValidPacket(packet.Buffer, token)) continue;
                var port = (int)BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16));
                if(port==65535&&Overlay!=null)
                {overlayRemote=packet.RemoteEndPoint;await Overlay.ReceiveBaseUdp(packet.Buffer[18..]);continue;}
                if (port == 0)
                {
                    await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, stop.Token);
                    Attached?.Invoke();
                    continue;
                }
                if (!udpAllowed.Contains(port)&&!frdUdp.ContainsKey(port)&&!voiceUdp.ContainsKey(port)) continue;
                var key = $"{packet.RemoteEndPoint}/{port}";
                if (!udpFlows.TryGetValue(key, out var flow))
                {
                    if (udpFlows.Count >= 256) continue;
                    flow = new TargetUdp(port, async data =>
                        await udp.SendAsync(Wire.Packet(token, port, data), packet.RemoteEndPoint, stop.Token), stop.Token);
                    udpFlows[key] = flow;
                    var captured = flow;
                    _ = flow.Completion.ContinueWith(t =>
                    { udpFlows.TryRemove(new KeyValuePair<string, TargetUdp>(key, captured)); captured.Dispose(); }, TaskScheduler.Default);
                }
                try { await flow.Send(packet.Buffer.AsMemory(18)); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { Diagnostics.Log("gateway-udp",ex.Message); }
                }
                catch(Exception ex)when(!stop.IsCancellationRequested&&(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or CryptographicException))
                {Diagnostics.Log("gateway-udp-packet",ex.Message);}
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    public void Stop()
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        stop.Cancel(); listener.Stop(); udp.Dispose();
        foreach(var client in clients.Values)client.Dispose();
        foreach (var flow in udpFlows.Values) flow.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await Task.WhenAll(acceptTask, udpTask); } catch(Exception ex){Diagnostics.Log("gateway-close",ex.Message);}
        await Task.WhenAll(tasks.Values); stop.Dispose();
    }
}

sealed class TargetUdp : IDisposable
{
    readonly UdpClient client = new(AddressFamily.InterNetwork);
    readonly CancellationTokenSource stop;
    readonly Func<byte[], Task> reply;
    readonly int maximum;
    public Task Completion { get; }
    public TargetUdp(int port, Func<byte[], Task> reply, CancellationToken ct, int maximum = Wire.MaxUdp)
    {
        this.reply = reply; this.maximum = maximum; stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Wire.ConfigureUdp(client);
        client.Connect(IPAddress.Loopback, port); stop.CancelAfter(TimeSpan.FromMinutes(2));
        Completion = Receive();
    }
    public async Task Send(ReadOnlyMemory<byte> data)
    { stop.CancelAfter(TimeSpan.FromMinutes(2)); await client.SendAsync(data, stop.Token); }
    async Task Receive()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(stop.Token);
                stop.CancelAfter(TimeSpan.FromMinutes(2));
                if (result.Buffer.Length <= maximum) await reply(result.Buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Diagnostics.Log("target-udp",ex.Message); }
    }
    int disposed;
    public void Dispose()
    { if (Interlocked.Exchange(ref disposed, 1) == 0) { stop.Cancel(); client.Dispose(); /* receive continuation still owns token */ } }
}
