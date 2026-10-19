namespace ArdUi;

// One ArdUi per data directory: a second launch asks the running instance to show its window, then exits.
static class SingleInstance
{
    static Mutex? mutex;
    static CancellationTokenSource? stop;
    public static event Action? Activated;

    static string Key(string root)
    {
        var path = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
    }

    // True when this process is the instance for the data directory; false when another one was asked to show itself.
    public static bool TryAcquire(string root)
    {
        var key = Key(root);
        mutex = new Mutex(true, "Local\\ArdUi-" + key, out var created);
        if (created) { Listen(key); return true; }
        mutex.Dispose(); mutex = null;
        try
        {
            using var client = new NamedPipeClientStream(".", "ArdUi-activate-" + key, PipeDirection.Out);
            client.Connect(2000); client.WriteByte(1); client.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
        return false;
    }

    static void Listen(string key)
    {
        stop = new CancellationTokenSource();
        var token = stop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream("ArdUi-activate-" + key, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    var buffer = new byte[1];
                    if (await server.ReadAsync(buffer, token) == 1) Activated?.Invoke();
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { await Task.Delay(500, token).ContinueWith(_ => { }); }
            }
        }, token);
    }

    public static void Release()
    {
        stop?.Cancel();
        try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        mutex?.Dispose(); mutex = null;
    }
}
