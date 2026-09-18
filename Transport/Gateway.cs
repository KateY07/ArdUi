namespace ArdUi;

sealed class Gateway : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly UdpClient udp;
    readonly Settings settings;
    readonly byte[] token;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<int, Task> tasks = new();
    readonly ConcurrentDictionary<string, TargetUdp> udpFlows = new();
    readonly ConcurrentDictionary<int,byte> frdTcp=new(),frdUdp=new();
    public void PermitFrdTcp(int port,bool enabled){if(enabled)frdTcp[port]=0;else frdTcp.TryRemove(port,out _);}
    public void PermitFrdUdp(int port,bool enabled)
    {
        if(enabled){frdUdp[port]=0;return;}
        frdUdp.TryRemove(port,out _);
        foreach(var pair in udpFlows.Where(p=>p.Key.EndsWith("/"+port,StringComparison.Ordinal)).ToArray())
            if(udpFlows.TryRemove(pair.Key,out var flow))flow.Dispose();
    }
    readonly SemaphoreSlim tcpSlots = new(128);
    readonly Task acceptTask, udpTask;
    int serial;
    public int Port { get; }
    public event Action? Attached;
    public Func<TcpClient,byte,CancellationToken,Task>? AttachOverlay;
    public OverlaySession? Overlay;
    IPEndPoint? overlayRemote;
    public async Task SendOverlayUdp(byte[] data,CancellationToken ct)
    {if(overlayRemote is{} remote)await udp.SendAsync(Wire.Packet(token,65535,data),remote,ct);}
    readonly Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>>? authenticate;
    public Gateway(Settings settings, byte[] token, Func<PasswordRequest, CancellationToken, Task<SignedEnvelope?>>? authenticate = null)
    {
        this.settings = settings; this.token = token; this.authenticate = authenticate;
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port)); }
        catch { listener.Stop(); throw; }
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
                var task = Serve(client); tasks[id] = task;
                _ = task.ContinueWith(t => { tcpSlots.Release(); tasks.TryRemove(id, out _); }, TaskScheduler.Default);
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
                    var grant = await authenticate(request, handshake.Token);
                    var accepted = grant != null;
                    await Wire.WriteJson(stream, new AdmissionReply(accepted,
                        accepted ? null : "密码错误、对端拒绝了首次指纹确认，或远程访问已关闭。",
                        accepted ? Convert.ToHexString(token) : null,
                        accepted ? settings.TcpPorts : [], accepted ? settings.UdpPorts : [], grant), handshake.Token);
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
                if (command != 1 || !settings.TcpPorts.Contains((int)port)&&!frdTcp.ContainsKey(port))
                { await stream.WriteAsync(new byte[] { 2 }, handshake.Token); return; }
                using var target = new TcpClient { NoDelay = true };
                try { await target.ConnectAsync(IPAddress.Loopback, port, handshake.Token); }
                catch { await stream.WriteAsync(new byte[] { 1 }, stop.Token); return; }
                client.NoDelay = true;
                await stream.WriteAsync(new byte[] { 0 }, handshake.Token);
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                await Wire.Bridge(client, target, stop.Token);
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
                if (!settings.UdpPorts.Contains(port)&&!frdUdp.ContainsKey(port)) continue;
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
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); udp.Dispose();
        foreach (var flow in udpFlows.Values) flow.Dispose();
        try { await Task.WhenAll(acceptTask, udpTask); } catch { }
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
