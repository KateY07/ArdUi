using System.Collections.Concurrent;

namespace ArdUi;

// Where a call's sound comes from and goes to, and how its packets travel. All three are interfaces so that the engine can be tested with
// tones, memory and loopback sockets, and run with the sound card and the overlay session in the real program.
interface IVoiceInput : IDisposable { event Action<short[]>? Frame; void Start(); }          // 20 ms frames of 16 kHz mono
interface IVoiceOutput : IDisposable { void Start(); void Write(short[] frame); }
interface IVoiceTransport : IDisposable { event Action<byte[]>? Received; void Start(); Task Send(byte[] data, CancellationToken ct); }

static class VoiceLevel
{
    // 0 to 1, from the RMS of a frame (speech is around 0.02 to 0.3).
    public static double Of(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty) return 0;
        double sum = 0; foreach (var sample in frame) sum += (double)sample * sample;
        return Math.Min(1, Math.Sqrt(sum / frame.Length) / 12000.0);
    }
}

sealed class VoiceEngine : IDisposable
{
    readonly IVoiceInput input; readonly IVoiceOutput output; readonly IVoiceTransport transport;
    readonly VoiceCodec encoder = new(), decoder = new();
    readonly JitterBuffer jitter = new();
    readonly BlockingCollection<short[]> captured = new(boundedCapacity: 12);
    readonly CancellationTokenSource stop = new();
    Thread? playout; Task? sender;
    ushort sequence; int mutedFrames;
    long lastReceived; long peerFlags; long peerAt;
    bool noInput, noOutput;
    int started, disposed;

    public bool Muted { get; set; }
    public double InputLevel { get; private set; }
    public double OutputLevel { get; private set; }
    public long Sent, Dropped;
    public int Received => jitter.Received;
    public int Lost => jitter.Lost;
    public int Late => jitter.Late;
    public int JitterTarget => jitter.Target;
    public TimeSpan SinceLastPacket => lastReceived == 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastReceived));
    public event Action<string>? Failed;
    // Not every machine has a working microphone or speaker: the call goes on without it, and both sides are told.
    public string? InputError { get; private set; }
    public string? OutputError { get; private set; }
    // What the other side's last packets said: switched the microphone off, or has none.
    public bool PeerMuted => Environment.TickCount64 - Interlocked.Read(ref peerAt) < 1500 && (Interlocked.Read(ref peerFlags) & VoicePacket.Muted) != 0;
    public bool PeerNoMic => PeerMuted && (Interlocked.Read(ref peerFlags) & VoicePacket.NoMic) != 0;

    public VoiceEngine(IVoiceInput input, IVoiceOutput output, IVoiceTransport transport)
    { this.input = input; this.output = output; this.transport = transport; }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        try
        {
            transport.Received += OnPacket; transport.Start();
            try { output.Start(); } catch (IOException ex) { noOutput = true; OutputError = ex.Message; }
            sender = Task.Factory.StartNew(SendLoop, TaskCreationOptions.LongRunning);
            try { input.Frame += OnFrame; input.Start(); } catch (IOException ex) { noInput = true; InputError = ex.Message; input.Frame -= OnFrame; }
            playout = new Thread(PlayoutLoop) { IsBackground = true, Name = "voice-playout", Priority = ThreadPriority.AboveNormal };
            playout.Start();
        }
        catch (Exception ex) { Failed?.Invoke(ex.Message); throw; }
    }

    void OnFrame(short[] frame)
    {
        if (!captured.TryAdd(frame)) Interlocked.Increment(ref Dropped);   // the sender is behind: drop rather than grow a delay
    }

    void SendLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                short[]? frame = null;
                if (!noInput) { if (!captured.TryTake(out frame, 100, stop.Token)) { if (captured.IsCompleted) break; continue; } }
                else stop.Token.WaitHandle.WaitOne(VoiceFormat.FrameMs);   // no microphone: only keepalives, on the clock
                var quiet = Muted || frame == null;
                InputLevel = quiet ? 0 : VoiceLevel.Of(frame!);
                byte[] data;
                if (quiet)
                {
                    if (mutedFrames++ % 10 != 0) continue;   // a keepalive every 200 ms keeps the path open
                    data = VoicePacket.Build(sequence++, (byte)(VoicePacket.Muted | (noInput ? VoicePacket.NoMic : 0)), []);
                }
                else { mutedFrames = 0; data = VoicePacket.Build(sequence++, 0, encoder.Encode(frame!)); }
                try { transport.Send(data, stop.Token).ContinueWith(t => { if (t.IsFaulted) Interlocked.Increment(ref Dropped); }, TaskScheduler.Default); Interlocked.Increment(ref Sent); }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { Interlocked.Increment(ref Dropped); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed?.Invoke(ex.Message); }
    }

    void OnPacket(byte[] data)
    {
        if (!VoicePacket.TryParse(data, out var number, out var flags, out var opus)) return;
        Interlocked.Exchange(ref lastReceived, Environment.TickCount64); Interlocked.Exchange(ref peerFlags, flags); Interlocked.Exchange(ref peerAt, Environment.TickCount64);
        jitter.Push(number, flags, opus);
    }

    // Every 20 ms one frame goes to the speaker, whatever happened on the network (silence when nothing is there).
    void PlayoutLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew(); long tick = 0;
        var silence = new short[VoiceFormat.FrameSamples];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var due = ++tick * VoiceFormat.FrameMs;
                var wait = due - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait); else if (wait < -200) { tick = clock.ElapsedMilliseconds / VoiceFormat.FrameMs; }   // far behind: skip ahead
                var (kind, opus) = jitter.Pop();
                var frame = kind switch { VoiceTick.Packet => decoder.Decode(opus), VoiceTick.Lost => decoder.Decode(null), _ => silence };
                OutputLevel = kind == VoiceTick.Packet || kind == VoiceTick.Lost ? VoiceLevel.Of(frame) : 0;
                if (!noOutput) output.Write(frame);
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested) { Failed?.Invoke(ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); captured.CompleteAdding();
        try { input.Frame -= OnFrame; input.Dispose(); } catch (Exception ex) { Diagnostics.Log("voice-input", ex.Message); }
        try { transport.Received -= OnPacket; transport.Dispose(); } catch (Exception ex) { Diagnostics.Log("voice-transport", ex.Message); }
        try { output.Dispose(); } catch (Exception ex) { Diagnostics.Log("voice-output", ex.Message); }
        try { playout?.Join(500); sender?.Wait(500); } catch (Exception ex) when (ex is AggregateException or ThreadStateException) { }
    }
}

// ---- transports ----------------------------------------------------------------------------------------------------------
// The host listens on a loopback UDP port that its gateway lets the overlay session deliver to (and only for the length of the call);
// whatever arrives comes from the overlay, and the answers go back to the same place.
sealed class HostVoiceTransport : IVoiceTransport
{
    readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    IPEndPoint? remote;
    CancellationTokenSource? stop;
    public int Port { get; }
    public event Action<byte[]>? Received;

    public HostVoiceTransport() { Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port; Wire.ConfigureUdp(udp); }

    public void Start()
    {
        stop = new CancellationTokenSource(); var token = stop.Token;
        _ = Task.Run(async () =>
        {
            try { while (!token.IsCancellationRequested) { var packet = await udp.ReceiveAsync(token); Volatile.Write(ref remote, packet.RemoteEndPoint); Received?.Invoke(packet.Buffer); } }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }, token);
    }

    public async Task Send(byte[] data, CancellationToken ct)
    {
        if (Volatile.Read(ref remote) is { } target) await udp.SendAsync(data, target, ct);
    }

    public void Dispose() { stop?.Cancel(); udp.Dispose(); }
}

// The controller sends through its session's local UDP facade, addressed to the host's voice port; the overlay carries it, encrypted, to the host.
sealed class ControllerVoiceTransport : IVoiceTransport
{
    readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly byte[] capability; readonly int target;
    CancellationTokenSource? stop;
    public event Action<byte[]>? Received;

    public ControllerVoiceTransport(int localPort, byte[] capability, int targetPort)
    { this.capability = capability; target = targetPort; udp.Connect(IPAddress.Loopback, localPort); Wire.ConfigureUdp(udp); }

    public void Start()
    {
        stop = new CancellationTokenSource(); var token = stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(token);
                    if (Wire.ValidPacket(packet.Buffer, capability) && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(16)) == target) Received?.Invoke(packet.Buffer[18..]);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }, token);
    }

    public async Task Send(byte[] data, CancellationToken ct) => await udp.SendAsync(Wire.Packet(capability, target, data), ct);

    public void Dispose() { stop?.Cancel(); udp.Dispose(); }
}
