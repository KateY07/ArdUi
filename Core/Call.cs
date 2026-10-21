namespace ArdUi;

enum CallState { Idle, Calling, Ringing, Active }

// How a call ended, for the user and for the host's audit record.
sealed record CallEnd(string Id, bool Incoming, string Reason, TimeSpan Duration, DateTimeOffset At, bool Answered);

sealed class SessionCallLink(Session session) : ICallLink
{
    public Session Session => session;
    public object Owner => session;
    public bool Host => session.Host;
    public ChatSender? Sender => session.Overlay is { } overlay && !overlay.Token.IsCancellationRequested && session.Live ? overlay.Request : null;
    public CallMedia OpenHost() => session.OpenVoiceHost();
    public CallMedia OpenController(int port) => session.OpenVoiceController(port);
}

// What carries a call's sound once it is accepted. Host: its listening port (the gateway lets the overlay deliver to it for the call only).
sealed record CallMedia(IVoiceTransport Transport, int Port, Action Release);

// The session a call runs on, as the call sees it (the real one wraps a Session; tests use a fake).
interface ICallLink
{
    object Owner { get; }                    // what the call lives on (the session): when it goes, the call ends
    bool Host { get; }                       // this machine is the host of that session
    ChatSender? Sender { get; }              // control requests to the other side
    CallMedia OpenHost();                    // host: start listening for the sound
    CallMedia OpenController(int port);      // controller: send the sound to the host's port
}

// A voice call with one device, either way round. Signalling goes over the session's control channel (call-offer, call-answer, call-end);
// the sound itself goes as small UDP datagrams through the same encrypted overlay, to a port the HOST opens, because only the host side of a
// session accepts such traffic: the host listens, the controller sends first.
sealed class CallChannel
{
    public const int Version = 1;
    readonly Func<ICallLink?> resolve;
    readonly Func<IVoiceInput> input;
    readonly Func<IVoiceOutput> output;
    readonly Func<bool> enabled;
    readonly object gate = new();
    ICallLink? link; CallMedia? media; VoiceEngine? engine; Ringtone? ringtone;
    CancellationTokenSource? timers;
    int remotePort;
    DateTimeOffset startedAt;
    bool muted;

    public TimeSpan RingTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan Silence { get; set; } = TimeSpan.FromSeconds(10);   // no sound packets for this long: the line is dead
    public bool PlayRingtone { get; set; } = true;
    public CallState State { get; private set; }
    public bool Incoming { get; private set; }
    public string? Id { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? Started => State == CallState.Active ? startedAt : null;
    public TimeSpan Elapsed => State == CallState.Active ? DateTimeOffset.UtcNow - startedAt : TimeSpan.Zero;
    public double InputLevel => engine?.InputLevel ?? 0;
    public double OutputLevel => engine?.OutputLevel ?? 0;
    // Things worth telling the user during a call: this machine's microphone or speaker cannot be used, or the other side is silent on purpose / by necessity.
    public string? LocalNote => engine is { } e ? (e.InputError != null && e.OutputError != null ? "本机麦克风和扬声器都不可用" : e.InputError != null ? "本机麦克风不可用，对方听不到你" : e.OutputError != null ? "本机扬声器不可用，你听不到对方" : null) : null;
    public string? PeerNote => engine is { } e ? (e.PeerNoMic ? "对方麦克风不可用，听不到对方" : e.PeerMuted ? "对方已静音" : null) : null;
    public string? Note => LocalNote ?? PeerNote;
    public CallEnd? Last { get; private set; }
    public event Action? Changed;
    public event Action<CallEnd>? Ended;

    public CallChannel(Func<ICallLink?> resolve, Func<IVoiceInput> input, Func<IVoiceOutput> output, Func<bool>? enabled = null)
    {
        this.resolve = resolve; this.input = input; this.output = output; this.enabled = enabled ?? (() => true);
    }

    public bool Muted
    {
        get => muted;
        set { muted = value; var e = engine; if (e != null) e.Muted = value; Changed?.Invoke(); }
    }

    // ---- this side starts, answers or ends a call -------------------------------------------------------------------------------
    public async Task Start(CancellationToken ct = default)
    {
        string id;
        lock (gate)
        {
            if (State != CallState.Idle) throw new InvalidOperationException("已经在通话中。");
            id = Guid.NewGuid().ToString("N"); Id = id; Incoming = false; State = CallState.Calling; Error = null; muted = false;
        }
        Changed?.Invoke();
        try
        {
            var current = resolve() ?? throw new IOException("设备连接已断开，无法发起通话。");
            link = current;
            var port = 0;
            if (current.Host) { media = current.OpenHost(); port = media.Port; }    // the host is ready to listen before the other side hears the offer
            var send = current.Sender ?? throw new IOException("设备连接已断开，无法发起通话。");
            await send("call-offer", new { id, v = Version, port }, ct);
            if (PlayRingtone) (ringtone ??= new Ringtone()).Start(false);
            Arm(id, RingTimeout, "无人接听");
        }
        catch (Exception ex) { Finish(id, ex is IOException ? ex.Message : "无法发起通话：" + ex.Message, notify: false); throw; }
    }

    public async Task Accept(CancellationToken ct = default)
    {
        string id; int port;
        lock (gate)
        {
            if (State != CallState.Ringing || !Incoming) throw new InvalidOperationException("没有来电。");
            id = Id!; port = remotePort;
        }
        try
        {
            var current = link ?? throw new IOException("设备连接已断开。");
            var mine = 0;
            if (current.Host) { media = current.OpenHost(); mine = media.Port; }
            else media = current.OpenController(port);
            var send = current.Sender ?? throw new IOException("设备连接已断开。");
            await send("call-answer", new { id, accept = true, port = mine }, ct);
            Activate(id);
        }
        catch (Exception ex) { Finish(id, ex is IOException ? ex.Message : "无法接听：" + ex.Message, notify: true); throw; }
    }

    public async Task Decline(CancellationToken ct = default)
    {
        string? id; lock (gate) { if (State != CallState.Ringing) return; id = Id; }
        try { if (link?.Sender is { } send) await send("call-answer", new { id, accept = false, port = 0 }, ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        Finish(id!, "已拒绝", notify: false);
    }

    public async Task Hangup(CancellationToken ct = default)
    {
        string? id; CallState state; lock (gate) { id = Id; state = State; }
        if (id == null || state == CallState.Idle) return;
        try { if (link?.Sender is { } send) await send("call-end", new { id, reason = state == CallState.Active ? "hangup" : "cancel" }, ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        Finish(id, state == CallState.Active ? "通话结束" : "已取消", notify: false);
    }

    // The session this call runs on is gone.
    public void Close(object owner) { if (link?.Owner == owner) Close(); }
    public void Close() { string? id; lock (gate) id = State == CallState.Idle ? null : Id; if (id != null) Finish(id, "连接已断开", notify: true); }

    // ---- the other side speaks ----------------------------------------------------------------------------------------------------
    public Task<object> Handle(ICallLink via, string method, JsonElement data, CancellationToken ct)
    {
        string Text(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        int Number(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        bool Flag(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        var id = Text("id");
        if (id.Length is < 8 or > 64) throw new IOException("通话请求无效。");
        switch (method)
        {
            case "call-offer":
            {
                if (Number("v") != Version) throw new IOException("对方的语音通话版本不同，请更新两端 ArdUi。");
                var port = Number("port");
                if (!enabled()) throw new IOException("对方已关闭语音通话。");
                lock (gate)
                {
                    if (State != CallState.Idle) throw new IOException("对方正忙。");
                    if (!via.Host && port is < 1024 or > 65535) throw new IOException("通话请求无效。");   // a host-initiated offer names the host's port
                    Id = id; Incoming = true; State = CallState.Ringing; remotePort = port; Error = null; muted = false; link = via;
                }
                if (PlayRingtone) (ringtone ??= new Ringtone()).Start(true);
                Arm(id, RingTimeout, "未接来电");
                Changed?.Invoke();
                return Task.FromResult<object>(new { ok = true });
            }
            case "call-answer":
            {
                bool accepted = Flag("accept"); var port = Number("port");
                lock (gate) { if (State != CallState.Calling || Id != id) throw new IOException("没有这个呼叫。"); }
                if (!accepted) { Finish(id, "对方已拒绝", notify: true); return Task.FromResult<object>(new { ok = true }); }
                try
                {
                    if (!link!.Host) { if (port is < 1024 or > 65535) throw new IOException("对方没有给出有效的通话端口。"); media = link.OpenController(port); }
                    Activate(id);
                }
                catch (Exception ex) { Finish(id, ex is IOException ? ex.Message : "无法开始通话：" + ex.Message, notify: true); throw new IOException("无法开始通话。"); }
                return Task.FromResult<object>(new { ok = true });
            }
            case "call-end":
            {
                lock (gate) { if (State == CallState.Idle || Id != id) return Task.FromResult<object>(new { ok = true }); }
                var was = State;
                Finish(id, Text("reason") == "hangup" ? "对方已挂断" : was == CallState.Ringing ? "对方已取消呼叫" : "对方已结束通话", notify: true);
                return Task.FromResult<object>(new { ok = true });
            }
            default: throw new IOException("未知的通话请求。");
        }
    }

    // ---- inside ---------------------------------------------------------------------------------------------------------------------
    void Activate(string id)
    {
        VoiceEngine? started;
        lock (gate)
        {
            if (Id != id || media == null) return;
            ringtone?.Stop(); timers?.Cancel();
            engine = new VoiceEngine(input(), output(), media.Transport) { Muted = muted };
            started = engine;
            State = CallState.Active; startedAt = DateTimeOffset.UtcNow; Error = null;
        }
        try { started.Failed += reason => Finish(id, "声音设备出错：" + reason, notify: true); started.Start(); }
        catch (Exception ex) { Finish(id, ex.Message, notify: true); throw new IOException(ex.Message, ex); }
        Changed?.Invoke();
        Watch(id, started);
    }

    // a call that goes quiet (no packets for a while) is over, whatever the signalling says
    void Watch(string id, VoiceEngine watched)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var begun = Environment.TickCount64;
                while (true)
                {
                    await Task.Delay(1000);
                    lock (gate) if (Id != id || State != CallState.Active) return;
                    var quiet = watched.SinceLastPacket == TimeSpan.MaxValue ? TimeSpan.FromMilliseconds(Environment.TickCount64 - begun) : watched.SinceLastPacket;
                    if (quiet > Silence) { Finish(id, "通话中断（长时间没有收到对方的声音）", notify: true); return; }
                }
            }
            catch (Exception ex) { Diagnostics.Log("call-watch", ex.Message); }
        });
    }

    // Rings for a while, then gives up (the caller says "no answer", the callee "missed").
    void Arm(string id, TimeSpan timeout, string reason)
    {
        CancellationTokenSource source;
        lock (gate) { timers?.Cancel(); timers = source = new CancellationTokenSource(); }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(timeout, source.Token); }
            catch (OperationCanceledException) { return; }
            string? still; CallState state; lock (gate) { still = Id; state = State; }
            if (still != id || state is not (CallState.Calling or CallState.Ringing)) return;
            if (state == CallState.Calling) { try { if (link?.Sender is { } send) await send("call-end", new { id, reason = "timeout" }, CancellationToken.None); } catch (Exception ex) when (ex is IOException or JsonException) { } }
            Finish(id, reason, notify: true);
        });
    }

    void Finish(string id, string reason, bool notify)
    {
        VoiceEngine? stopped; CallMedia? released; CallEnd end; bool had;
        lock (gate)
        {
            if (Id != id || State == CallState.Idle) return;
            had = State == CallState.Active;
            end = new CallEnd(id, Incoming, reason, had ? DateTimeOffset.UtcNow - startedAt : TimeSpan.Zero, DateTimeOffset.UtcNow, had);
            stopped = engine; released = media; engine = null; media = null;
            timers?.Cancel(); State = CallState.Idle; Id = null; Last = end;
            if (new[] { "无法", "出错", "麦克风", "扬声器", "中断" }.Any(word => reason.Contains(word, StringComparison.Ordinal))) Error = reason;
            link = null;
        }
        ringtone?.Stop();
        try { stopped?.Dispose(); } catch (Exception ex) { Diagnostics.Log("call-engine", ex.Message); }
        try { released?.Release(); released?.Transport.Dispose(); } catch (Exception ex) { Diagnostics.Log("call-media", ex.Message); }
        Ended?.Invoke(end); Changed?.Invoke();
    }
}
