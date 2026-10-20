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
    public string? BubbleSender { get; private set; }
    public string? BubbleText { get; private set; }
    public string? BubblePeer { get; private set; }
    public string? UnreadPeer { get; private set; }   // the device whose message came last: where a click on the unread badge leads
    DateTimeOffset bubbleUntil;
    public event Action? Changed;

    // Something is worth showing: a connection, an unread message, or a file on its way.
    public bool Visible => Enabled && !Suppressed && (Connected > 0 || Unread > 0 || Transfers.Count > 0);
    public bool BubbleVisible => Visible && BubbleText != null;
    public ChatTransfer? Transfer => Transfers.FirstOrDefault();

    public void SetEnabled(bool value) { if (Enabled == value) return; Enabled = value; Changed?.Invoke(); }
    public void SetSuppressed(bool value) { if (Suppressed == value) return; Suppressed = value; Changed?.Invoke(); }
    public void SetConnected(int count) { if (Connected == count) return; Connected = count; Changed?.Invoke(); }
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
        BubblePeer = peer; BubbleSender = sender; BubbleText = text; UnreadPeer = peer; bubbleUntil = now() + BubbleTime;
        Changed?.Invoke();
    }

    // Opening the conversation: the bubble goes at once.
    public void DismissBubble() { if (BubbleText == null) return; BubbleText = null; BubbleSender = null; Changed?.Invoke(); }

    public void Tick()
    {
        if (BubbleText != null && now() >= bubbleUntil) DismissBubble();
    }
}
