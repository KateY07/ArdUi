namespace ArdUi;

// Opt-in start-up timing: set ARDUI_STARTUP_TRACE=<file> and every checkpoint is appended with the time since the process started.
static class StartupTrace
{
    static readonly string? path = Environment.GetEnvironmentVariable("ARDUI_STARTUP_TRACE");
    static readonly DateTime started = Process.GetCurrentProcess().StartTime;
    // Ticks on the UI thread; a long gap between ticks is a stretch where the UI thread was busy or blocked.
    public static void Watch()
    {
        if (string.IsNullOrEmpty(path)) return;
        var last = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp(); var gap = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds; last = now;
            if (gap > 200) Mark($"UI thread stalled {gap:F0} ms");
        };
        timer.Start();
    }
    public static void Mark(string what)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, $"{(DateTime.Now - started).TotalMilliseconds,8:F0} ms  {what}\n"); } catch (IOException) { }
    }
}
