namespace ArdUi;

sealed record LogEntry(DateTime Time, string Level, string Group, string Text);

// Technical record for the log page. The main window only shows short, friendly status text; everything with
// server names, addresses and error details goes here instead.
sealed class EventLog
{
    const int Limit = 500;
    readonly object gate = new();
    readonly LinkedList<LogEntry> entries = new();
    public event Action? Changed;

    public void Add(string level, string group, string text, bool toFile = true)
    {
        lock (gate)
        {
            entries.AddFirst(new LogEntry(DateTime.Now, level, group, text));
            while (entries.Count > Limit) entries.RemoveLast();
        }
        if (toFile) FileLog.Write(group, level + " " + text);
        Changed?.Invoke();
    }

    // Newest first.
    public LogEntry[] Snapshot() { lock (gate) return entries.ToArray(); }

    // ard.exe prefixes its lines with a timestamp and level ("2026-… INFO ard: …").
    public void AddFromArd(string line)
    {
        var match = Regex.Match(line, @"\b(ERROR|WARN|INFO)\b\s*(?:[\w:]+:\s*)?(.*)$");
        var level = match.Success ? match.Groups[1].Value : "INFO";
        var text = match.Success ? match.Groups[2].Value : line;
        var group = text.Contains("network path", StringComparison.Ordinal) || text.Contains("CONNECTED", StringComparison.Ordinal) ? "连接" : "网络";
        Add(level, group, text.Length > 400 ? text[..400] + "…" : text, toFile: false);   // the raw line is already in the file
    }
}
