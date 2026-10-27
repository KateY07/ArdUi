namespace ArdUi;

// What the floating island shows, decided from a few plain facts: how many devices are connected, how many messages are unread,
// which files are on their way, and which message has just arrived. No windows, no timers of its own: the host feeds it and calls
// Tick, so the rules can be tested with a fake clock.
sealed class IslandModel
{
    public static readonly TimeSpan BubbleTime = TimeSpan.FromSeconds(4);
    readonly Func<DateTimeOffset> now;

    public IslandModel(Func<DateTimeOffset>? clock = null) { now = clock ?? (() => DateTimeOffset.Now); }

    public int Connected { get; private set; }
    public int Unread { get; private set; }
    public bool Suppressed { get; private set; }   // full screen or a presentation: the island stays out of the way
    public bool Enabled { get; private set; } = true;
    public IReadOnlyList<ChatTransfer> Transfers { get; private set; } = [];
    public IReadOnlyList<string> Controlled { get; private set; } = [];   // the devices that are controlling this PC right now
    public string? BubbleSender { get; private set; }
    public string? BubbleText { get; private set; }
    public string? BubblePeer { get; private set; }
    public string? UnreadPeer { get; private set; }   // the device whose message came last: where a click on the unread badge leads
    public bool BubbleSticky { get; private set; }    // "抖一抖": the bubble stays until it is clicked or closed
    public int Nudges { get; private set; }           // counts nudges, so the island shakes once for each
    // A voice call, if one is ringing or running: it takes the island over until it ends.
    public CallState Call { get; private set; }
    public string? CallPeer { get; private set; }
    public string? CallLabel { get; private set; }
    public TimeSpan CallElapsed { get; private set; }
    public bool CallMuted { get; private set; }
    public string? CallNote { get; private set; }
    DateTimeOffset bubbleUntil;
    public event Action? Changed;

    // Something is worth showing: a connection, an unread message, or a file on its way.
    public bool Visible => Enabled && (!Suppressed || Call != CallState.Idle) && (Connected > 0 || Unread > 0 || Transfers.Count > 0 || Call != CallState.Idle);
    public bool BubbleVisible => Visible && BubbleText != null;
    public ChatTransfer? Transfer => Transfers.FirstOrDefault();

    public void SetCall(CallState state, string? peer, string? label, TimeSpan elapsed, bool muted, string? note = null)
    {
        var seconds = (long)elapsed.TotalSeconds; var before = (Call, CallPeer, CallLabel, (long)CallElapsed.TotalSeconds, CallMuted, CallNote);
        Call = state; CallPeer = state == CallState.Idle ? null : peer; CallLabel = state == CallState.Idle ? null : label; CallElapsed = state == CallState.Idle ? default : elapsed; CallMuted = state == CallState.Idle ? false : muted; CallNote = state == CallState.Idle ? null : note;
        if (before != (Call, CallPeer, CallLabel, seconds, CallMuted, CallNote)) Changed?.Invoke();
    }
    public void SetEnabled(bool value) { if (Enabled == value) return; Enabled = value; Changed?.Invoke(); }
    public void SetSuppressed(bool value) { if (Suppressed == value) return; Suppressed = value; Changed?.Invoke(); }
    public void SetConnected(int count) { if (Connected == count) return; Connected = count; Changed?.Invoke(); }
    public void SetControlled(IReadOnlyList<string> labels)
    {
        if (Controlled.SequenceEqual(labels)) return;
        Controlled = labels; Changed?.Invoke();
    }
    public void SetUnread(int count, string? peer = null)
    {
        if (peer != null) UnreadPeer = peer;
        if (Unread == count) return;
        Unread = count; if (count == 0) UnreadPeer = null; Changed?.Invoke();
    }
    public void SetTransfers(IReadOnlyList<ChatTransfer> active)
    {
        if (Transfers.Count == active.Count && Transfers.Zip(active).All(p => ReferenceEquals(p.First, p.Second))) return;
        Transfers = active; Changed?.Invoke();
    }

    // A message from the other side: shown for four seconds as a bubble, then it is only an unread count.
    public void MessageArrived(string peer, string sender, string text)
    {
        UnreadPeer = peer;
        if (BubbleSticky) { Changed?.Invoke(); return; }   // a nudge waiting to be seen keeps its place
        BubblePeer = peer; BubbleSender = sender; BubbleText = text; bubbleUntil = now() + BubbleTime;
        Changed?.Invoke();
    }

    // "xxx 发送了抖一抖": shown until it is clicked or closed, never timed out.
    public void NudgeArrived(string peer, string sender)
    {
        BubblePeer = peer; BubbleSender = null; BubbleText = sender + " 发送了抖一抖"; UnreadPeer = peer; BubbleSticky = true; Nudges++;
        Changed?.Invoke();
    }

    // Opening the conversation or closing the bubble: it goes at once.
    public void DismissBubble() { if (BubbleText == null) return; BubbleText = null; BubbleSender = null; BubbleSticky = false; Changed?.Invoke(); }

    public void Tick()
    {
        if (BubbleText != null && !BubbleSticky && now() >= bubbleUntil) DismissBubble();
    }
}
