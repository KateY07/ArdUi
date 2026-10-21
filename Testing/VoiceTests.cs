namespace ArdUi;

// `ArdUi.exe --voice-test`: Opus round trip, the packet format, the jitter buffer, the engine between two fake sound cards, the call
// signalling between two ends, and the sound through real loopback sockets behind a stand-in for the overlay. No sound card is used.
static class VoiceTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    // ---- fakes ---------------------------------------------------------------------------------------------------------------
    sealed class ToneInput(double frequency, double amplitude = 8000) : IVoiceInput
    {
        public event Action<short[]>? Frame;
        Timer? timer; long n; int started;
        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            var watch = System.Diagnostics.Stopwatch.StartNew(); long sent = 0;
            timer = new Timer(_ =>
            {
                while (sent * VoiceFormat.FrameMs <= watch.ElapsedMilliseconds)
                {
                    var frame = new short[VoiceFormat.FrameSamples];
                    for (var i = 0; i < frame.Length; i++) frame[i] = (short)(amplitude * Math.Sin(2 * Math.PI * frequency * (n++) / VoiceFormat.SampleRate));
                    Frame?.Invoke(frame); sent++;
                }
            }, null, 0, 5);
        }
        public void Dispose() => timer?.Dispose();
    }

    sealed class BrokenInput : IVoiceInput
    {
        public event Action<short[]>? Frame;
        public void Start() => throw new IOException("无法打开麦克风");
        public void Dispose() { }
    }

    sealed class MemoryOutput : IVoiceOutput
    {
        public readonly List<short> Samples = new(); public int Frames;
        public void Start() { }
        public void Write(short[] frame) { lock (Samples) { Samples.AddRange(frame); Frames++; } }
        public short[] Last(int seconds) { lock (Samples) return Samples.Skip(Math.Max(0, Samples.Count - seconds * VoiceFormat.SampleRate)).ToArray(); }
        public void Dispose() { }
    }

    // Two ends of a wire in memory: what one sends, the other receives (optionally losing some).
    sealed class MemoryTransport : IVoiceTransport
    {
        public MemoryTransport? Peer; public Func<int, bool> Drop = _ => false; int count; public int SentPackets;
        public event Action<byte[]>? Received;
        public void Start() { }
        public Task Send(byte[] data, CancellationToken ct) { SentPackets++; if (!Drop(count++)) Peer?.Received?.Invoke(data); return Task.CompletedTask; }
        public void Deliver(byte[] data) => Received?.Invoke(data);
        public void Dispose() { }
    }

    static (MemoryTransport A, MemoryTransport B) Wire2() { var a = new MemoryTransport(); var b = new MemoryTransport { Peer = a }; a.Peer = b; return (a, b); }

    // How much of a frequency is in the sound (Goertzel), relative to the whole.
    static double Tone(short[] samples, double frequency)
    {
        if (samples.Length == 0) return 0;
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / VoiceFormat.SampleRate), s1 = 0, s2 = 0, energy = 0;
        foreach (var x in samples) { var s = x + coefficient * s1 - s2; s2 = s1; s1 = s; energy += (double)x * x; }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return energy == 0 ? 0 : power / (energy * samples.Length / 2);
    }

    public static async Task<int> Run()
    {
        Codec(); Packets(); Jitter();
        await Engines();
        await Calls();
        await ThroughTheOverlay();
        return failures == 0 ? 0 : 1;
    }

    static void Codec()
    {
        var codec = new VoiceCodec(); var decoded = new List<short>(); var bytes = 0;
        for (var f = 0; f < 50; f++)
        {
            var frame = new short[VoiceFormat.FrameSamples];
            for (var i = 0; i < frame.Length; i++) frame[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (f * frame.Length + i) / VoiceFormat.SampleRate));
            var packet = codec.Encode(frame); bytes += packet.Length;
            decoded.AddRange(codec.Decode(packet));
        }
        var tail = decoded.Skip(VoiceFormat.SampleRate / 2).ToArray();
        Check(Tone(tail, 440) > 0.8, "a 440 Hz tone survives Opus (" + Tone(tail, 440).ToString("0.00") + " of the energy at 440 Hz)");
        Check(Tone(tail, 1500) < 0.02, "and nothing else is added");
        Check(bytes / 50 is > 20 and < 120, "about 24 kbit/s: " + bytes / 50 + " bytes per 20 ms frame");
        var concealed = codec.Decode(null);
        Check(concealed.Length == VoiceFormat.FrameSamples && concealed.Any(x => x != 0), "a lost frame is concealed from the previous ones, not silence");
    }

    static void Packets()
    {
        var data = VoicePacket.Build(65535, VoicePacket.Muted, new byte[] { 1, 2, 3 });
        Check(VoicePacket.TryParse(data, out var seq, out var flags, out var opus) && seq == 65535 && flags == VoicePacket.Muted && opus.SequenceEqual(new byte[] { 1, 2, 3 }), "a voice packet is built and parsed back");
        Check(!VoicePacket.TryParse(new byte[] { 1, 2, 3 }, out _, out _, out _) && !VoicePacket.TryParse(new byte[VoicePacket.Header + 500], out _, out _, out _) && !VoicePacket.TryParse("XX12345"u8.ToArray(), out _, out _, out _), "short, oversized and foreign datagrams are refused");
    }

    static void Jitter()
    {
        byte[] P(int n) => [(byte)n];
        var jitter = new JitterBuffer();
        for (ushort s = 0; s < 6; s++) jitter.Push(s, 0, P(s));
        var order = new List<int>(); for (var i = 0; i < 6; i++) { var (kind, opus) = jitter.Pop(); if (kind == VoiceTick.Packet) order.Add(opus[0]); }
        Check(order.SequenceEqual([0, 1, 2, 3, 4, 5]), "packets in order come out in order");

        jitter = new JitterBuffer();
        foreach (ushort s in new ushort[] { 0, 1, 3, 2, 5, 4 }) jitter.Push(s, 0, P(s));
        order.Clear(); for (var i = 0; i < 6; i++) { var (kind, opus) = jitter.Pop(); if (kind == VoiceTick.Packet) order.Add(opus[0]); }
        Check(order.SequenceEqual([0, 1, 2, 3, 4, 5]), "reordered packets are put right");

        jitter = new JitterBuffer();
        foreach (ushort s in new ushort[] { 0, 1, 3, 4, 5, 6, 7 }) jitter.Push(s, 0, P(s));
        var kinds = new List<VoiceTick>(); for (var i = 0; i < 8; i++) kinds.Add(jitter.Pop().Kind);
        Check(kinds.Count(k => k == VoiceTick.Lost) == 1 && jitter.Lost == 1, "a packet that never arrives is reported lost once (so that it is concealed)");

        jitter = new JitterBuffer();
        for (ushort s = 65532; s != 4; s++) jitter.Push(s, 0, P(s & 0xFF));
        var wrapped = 0; for (var i = 0; i < 12; i++) if (jitter.Pop().Kind == VoiceTick.Packet) wrapped++;
        Check(wrapped == 8 && jitter.Lost == 0, "the 16-bit sequence number wraps without loss");

        jitter = new JitterBuffer();
        for (ushort s = 0; s < 4; s++) jitter.Push(s, 0, P(s));
        jitter.Pop(); jitter.Pop(); jitter.Pop(); jitter.Push(0, 0, P(0)); jitter.Push(1, 0, P(1));
        Check(jitter.Late == 2 && jitter.Target > 3, "late packets are counted and the buffer waits longer afterwards");

        jitter = new JitterBuffer(); jitter.Push(1, 0, P(1)); jitter.Push(1, 0, P(1));
        Check(jitter.Duplicates == 1, "a duplicate is dropped");

        jitter = new JitterBuffer();
        for (ushort s = 0; s < 40; s++) jitter.Push(s, 0, P(s));
        jitter.Pop();
        Check(jitter.Depth <= jitter.Target + 7, "after a burst the delay is cut back (" + jitter.Depth + " frames kept)");

        jitter = new JitterBuffer();
        for (ushort s = 0; s < 5; s++) jitter.Push(s, 0, P(s));
        for (var i = 0; i < 12; i++) jitter.Pop();
        Check(jitter.Underruns > 0 && jitter.Target > 3, "a line that runs dry makes the buffer rebuffer with a longer wait");
        jitter.Push(10, 0, []);
        var muted = new JitterBuffer(); muted.Push(0, VoicePacket.Muted, []); muted.Push(1, VoicePacket.Muted, []); muted.Push(2, VoicePacket.Muted, []);
        Check(muted.Pop().Kind == VoiceTick.Muted, "an empty muted packet is a mute marker, not sound");
    }

    static async Task Engines()
    {
        var (a, b) = Wire2();
        var outA = new MemoryOutput(); var outB = new MemoryOutput();
        using var engineA = new VoiceEngine(new ToneInput(440), outA, a); using var engineB = new VoiceEngine(new ToneInput(1000), outB, b);
        engineA.Start(); engineB.Start();
        await Task.Delay(2200);
        Check(Tone(outB.Last(1), 440) > 0.7, "the other side hears this side's tone (" + Tone(outB.Last(1), 440).ToString("0.00") + ")");
        Check(Tone(outA.Last(1), 1000) > 0.7, "and the other way round (" + Tone(outA.Last(1), 1000).ToString("0.00") + ")");
        Check(engineA.Sent > 80 && engineB.Received > 80 && engineB.Lost == 0, $"over 2 s about 100 frames went across ({engineA.Sent} sent, {engineB.Received} received, {engineB.Lost} lost)");
        Check(engineA.InputLevel > 0.1 && engineB.OutputLevel > 0.1, "the level meters move");

        engineA.Muted = true; await Task.Delay(1500);
        var sentMuted = a.SentPackets;
        Check(Tone(outB.Last(1), 440) < 0.05 && outB.Last(1).Max(x => Math.Abs((int)x)) < 200, "a muted side is silent for the other");
        await Task.Delay(1000);
        Check(a.SentPackets - sentMuted is >= 3 and <= 8, "while muted only a keepalive goes out every 200 ms (" + (a.SentPackets - sentMuted) + " in a second)");
        engineA.Muted = false; await Task.Delay(1200);
        Check(Tone(outB.Last(1), 440) > 0.5, "unmuting brings the sound back");

        // one side without a working microphone: the call goes on, it hears the other, and the other is told
        var (e, f) = Wire2(); var outF = new MemoryOutput(); var outE = new MemoryOutput();
        using var noMic = new VoiceEngine(new BrokenInput(), outE, e); using var talker = new VoiceEngine(new ToneInput(900), outF, f);
        noMic.Start(); talker.Start(); await Task.Delay(2200);
        Check(MicDiag.Devices().Title.Length > 0 && MicDiag.Privacy().Lines.Count >= 2, "the microphone diagnosis reads devices and privacy settings");
        Check(noMic.InputError != null && noMic.OutputError == null, "a missing microphone is noted, the call is not refused");
        Check(Tone(outE.Last(1), 900) > 0.6, "and the side without a microphone still hears the other");
        Check(talker.PeerNoMic && talker.PeerMuted, "the other side is told that this one has no microphone");
        Check(!noMic.PeerMuted, "while the working side is not reported as muted");

        // 10% of the packets lost on the way: the sound goes on and the loss is concealed
        var (c, d) = Wire2(); c.Drop = n => n % 10 == 3; var outD = new MemoryOutput();
        using var lossy = new VoiceEngine(new ToneInput(500), new MemoryOutput(), c); using var hearing = new VoiceEngine(new ToneInput(700), outD, d);
        lossy.Start(); hearing.Start(); await Task.Delay(2500);
        Check(hearing.Lost >= 10 && Tone(outD.Last(1), 500) > 0.4, "with 10% loss the tone is still there (" + Tone(outD.Last(1), 500).ToString("0.00") + ", " + hearing.Lost + " frames concealed)");
    }

    // ---- the calls -------------------------------------------------------------------------------------------------------------
    sealed class FakeLink(bool host, object owner, Func<ChatSender?> sender, Func<int, CallMedia> controller, Func<CallMedia> hostMedia) : ICallLink
    {
        public object Owner => owner; public bool Host => host; public ChatSender? Sender => sender();
        public CallMedia OpenHost() => hostMedia(); public CallMedia OpenController(int port) => controller(port);
    }

    sealed class Pair
    {
        public CallChannel Controller = null!, Host = null!; public bool HostAnswers = true, HostEnabled = true; public bool Connected = true;
        public readonly List<CallEnd> ControllerEnds = new(), HostEnds = new();
        MemoryTransport? hostEnd, controllerEnd; public int HostPort = 41234; public int ReleaseCount; public int OpenedController;
        public readonly object ControllerOwner = new(), HostOwner = new();
        FakeLink controllerLink = null!, hostLink = null!;

        public Pair(Action<CallChannel>? tune = null)
        {
            ChatSender FromControllerToHost() => async (method, value, ct) => Reach(await Task.Run(() => Host.Handle(hostLink, method, JsonSerializer.SerializeToElement(value, Wire.Json), ct)));
            ChatSender FromHostToController() => async (method, value, ct) => Reach(await Task.Run(() => Controller.Handle(controllerLink, method, JsonSerializer.SerializeToElement(value, Wire.Json), ct)));
            CallMedia HostMedia() { (controllerEnd, hostEnd) = Wire2(); return new CallMedia(hostEnd, HostPort, () => ReleaseCount++); }
            CallMedia ControllerMedia(int port) { OpenedController = port; return new CallMedia(controllerEnd ?? throw new IOException("the host has not opened its port"), 0, () => { }); }
            controllerLink = new FakeLink(false, ControllerOwner, () => Connected ? FromControllerToHost() : null, ControllerMedia, HostMedia);
            hostLink = new FakeLink(true, HostOwner, () => Connected ? FromHostToController() : null, ControllerMedia, HostMedia);
            Controller = new CallChannel(() => controllerLink, () => new ToneInput(440), () => new MemoryOutput(), () => true) { PlayRingtone = false };
            Host = new CallChannel(() => hostLink, () => new ToneInput(660), () => new MemoryOutput(), () => HostEnabled) { PlayRingtone = false };
            Controller.Ended += ControllerEnds.Add; Host.Ended += HostEnds.Add;
            tune?.Invoke(Controller); tune?.Invoke(Host);
        }
        static JsonElement Reach(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    }

    static async Task<bool> Until(Func<bool> condition, int ms = 3000) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (condition()) return true; await Task.Delay(20); } return condition(); }

    static async Task Calls()
    {
        // the controller calls, the host answers
        var pair = new Pair();
        await pair.Controller.Start();
        Check(pair.Controller.State == CallState.Calling && await Until(() => pair.Host.State == CallState.Ringing) && pair.Host.Incoming, "a call rings on the other side");
        await pair.Host.Accept();
        Check(await Until(() => pair.Controller.State == CallState.Active && pair.Host.State == CallState.Active), "both sides are in the call once it is answered");
        Check(pair.OpenedController == pair.HostPort, "the controller sends the sound to the port the host opened and named in its answer");
        await Task.Delay(1500);
        Check(pair.Controller.OutputLevel > 0.05 && pair.Host.OutputLevel > 0.05, "sound flows both ways (" + pair.Controller.OutputLevel.ToString("0.00") + " / " + pair.Host.OutputLevel.ToString("0.00") + ")");
        pair.Controller.Muted = true; Check(pair.Controller.Muted, "mute is a state of the call");
        await Task.Delay(300);
        await pair.Controller.Hangup();
        Check(pair.Controller.State == CallState.Idle && await Until(() => pair.Host.State == CallState.Idle), "hanging up ends it on both sides");
        Check(pair.HostEnds.Count == 1 && pair.HostEnds[0].Answered && pair.HostEnds[0].Duration > TimeSpan.FromSeconds(1) && pair.HostEnds[0].Reason.Contains("挂断", StringComparison.Ordinal) && pair.HostEnds[0].Incoming, "the host's record says: answered, how long, hung up by the other side");
        Check(pair.ReleaseCount == 1, "the host's voice port is closed again when the call ends");

        // the host calls, the controller answers (the host names its port in the offer)
        pair = new Pair();
        await pair.Host.Start();
        Check(await Until(() => pair.Controller.State == CallState.Ringing), "a host-initiated call rings on the controller");
        await pair.Controller.Accept();
        Check(await Until(() => pair.Host.State == CallState.Active && pair.Controller.State == CallState.Active) && pair.OpenedController == pair.HostPort, "the controller connects to the port from the host's offer");
        await Task.Delay(1200);
        Check(pair.Controller.OutputLevel > 0.05 && pair.Host.OutputLevel > 0.05, "sound flows in a call the host started");
        await pair.Host.Hangup();
        Check(await Until(() => pair.Controller.State == CallState.Idle), "the host can hang up too");

        // declined
        pair = new Pair();
        await pair.Controller.Start(); await Until(() => pair.Host.State == CallState.Ringing);
        await pair.Host.Decline();
        Check(await Until(() => pair.Controller.State == CallState.Idle) && pair.ControllerEnds[0].Reason.Contains("拒绝", StringComparison.Ordinal) && !pair.ControllerEnds[0].Answered, "a declined call ends for the caller with the reason");

        // cancelled while ringing
        pair = new Pair();
        await pair.Controller.Start(); await Until(() => pair.Host.State == CallState.Ringing);
        await pair.Controller.Hangup();
        Check(await Until(() => pair.Host.State == CallState.Idle) && pair.HostEnds[0].Reason.Contains("取消", StringComparison.Ordinal), "a caller who gives up stops the ringing");

        // nobody answers
        pair = new Pair(call => call.RingTimeout = TimeSpan.FromMilliseconds(400));
        await pair.Controller.Start();
        Check(await Until(() => pair.Controller.State == CallState.Idle && pair.Host.State == CallState.Idle, 3000) && pair.ControllerEnds[0].Reason == "无人接听" && pair.HostEnds[0].Reason == "未接来电", "an unanswered call times out on both sides (no answer / missed)");

        // busy, disabled, offline
        pair = new Pair();
        await pair.Controller.Start(); await Until(() => pair.Host.State == CallState.Ringing);
        var second = new Pair(); // a second offer to a host that is already ringing
        try { await pair.Host.Handle(new FakeLink(true, pair.HostOwner, () => null, _ => null!, () => null!), "call-offer", JsonSerializer.SerializeToElement(new { id = Guid.NewGuid().ToString("N"), v = 1, port = 0 }, Wire.Json), CancellationToken.None); Check(false, "a second call while ringing is refused"); }
        catch (IOException ex) { Check(ex.Message.Contains("正忙", StringComparison.Ordinal), "a second call while ringing is refused (busy)"); }
        pair.Host.Close(); _ = second;
        pair = new Pair { HostEnabled = false };
        try { await pair.Controller.Start(); Check(false, "calls to a device that switched them off are refused"); }
        catch (IOException ex) { Check(ex.Message.Contains("已关闭", StringComparison.Ordinal) && pair.Controller.State == CallState.Idle, "calls to a device that switched them off are refused, and the caller is told"); }
        pair = new Pair { Connected = false };
        try { await pair.Controller.Start(); Check(false, "no call without a connection"); }
        catch (IOException) { Check(pair.Controller.State == CallState.Idle, "no call without a connection"); }

        // the session goes away in the middle of a call
        pair = new Pair();
        await pair.Controller.Start(); await Until(() => pair.Host.State == CallState.Ringing); await pair.Host.Accept(); await Until(() => pair.Host.State == CallState.Active);
        pair.Host.Close(new object()); Check(pair.Host.State == CallState.Active, "closing another session does not end this call");
        pair.Host.Close(pair.HostOwner);
        Check(pair.Host.State == CallState.Idle && pair.HostEnds[0].Reason == "连接已断开", "when the session goes, the call goes");
        await pair.Controller.Hangup();

        // a call that falls silent is over
        pair = new Pair(call => call.Silence = TimeSpan.FromMilliseconds(1200));
        await pair.Controller.Start(); await Until(() => pair.Host.State == CallState.Ringing); await pair.Host.Accept(); await Until(() => pair.Host.State == CallState.Active);
        pair.Connected = false;   // the signalling is gone and the sound stops: the controller side hears nothing more
        await Task.Delay(200);
        pair.Host.Close(); pair.Connected = true;
        Check(await Until(() => pair.ControllerEnds.Count == 1, 5000) && pair.ControllerEnds[0].Reason.Contains("中断", StringComparison.Ordinal) && pair.ControllerEnds[0].Answered, "a call whose sound stops ends by itself (" + pair.ControllerEnds.FirstOrDefault()?.Reason + ")");
    }

    // ---- the sound through real sockets, with a stand-in for the overlay ------------------------------------------------------
    static async Task ThroughTheOverlay()
    {
        // controller -> local facade (UDP) -> [overlay: unwrap, deliver] -> host's voice port, and the answers back the same way
        var capability = new byte[16]; Random.Shared.NextBytes(capability);
        using var host = new HostVoiceTransport();
        using var facade = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var facadePort = ((IPEndPoint)facade.Client.LocalEndPoint!).Port;
        using var toHost = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint? controllerSide = null; var stop = new CancellationTokenSource();
        _ = Task.Run(async () => { try { while (!stop.IsCancellationRequested) { var p = await facade.ReceiveAsync(stop.Token); controllerSide = p.RemoteEndPoint; if (Wire.ValidPacket(p.Buffer, capability)) await toHost.SendAsync(p.Buffer[18..], new IPEndPoint(IPAddress.Loopback, BitConverter.ToUInt16([p.Buffer[17], p.Buffer[16]], 0))); } } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { } });
        _ = Task.Run(async () => { try { while (!stop.IsCancellationRequested) { var p = await toHost.ReceiveAsync(stop.Token); if (controllerSide != null) await facade.SendAsync(Wire.Packet(capability, host.Port, p.Buffer), controllerSide); } } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { } });
        using var controller = new ControllerVoiceTransport(facadePort, capability, host.Port);
        var outHost = new MemoryOutput(); var outController = new MemoryOutput();
        using var hostEngine = new VoiceEngine(new ToneInput(700), outHost, host); using var controllerEngine = new VoiceEngine(new ToneInput(900), outController, controller);
        controllerEngine.Start(); await Task.Delay(300); hostEngine.Start();   // the controller sends first
        await Task.Delay(2200);
        Check(Tone(outHost.Last(1), 900) > 0.6, "through the facade and the host's port the host hears the controller (" + Tone(outHost.Last(1), 900).ToString("0.00") + ")");
        Check(Tone(outController.Last(1), 700) > 0.6, "and the controller hears the host, answered through the same flow (" + Tone(outController.Last(1), 700).ToString("0.00") + ")");
        stop.Cancel();
    }
}
