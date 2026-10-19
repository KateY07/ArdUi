using System.Threading.Channels;

namespace ArdUi;

// Verbose log file: everything ard.exe says and every technical event goes to <data>\logs\ardui-YYYYMMDD.txt
// (.txt so Windows always has an editor for it). Writing happens on a background task, never on the UI thread.
static class FileLog
{
    const long MaxBytes = 20 * 1024 * 1024;
    static readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    static string folder = "";
    static int started;

    public static void Start(string dataRoot)
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        folder = Path.Combine(dataRoot, "logs");
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "ardui-*.txt*").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))) File.Delete(old);
        }
        catch (IOException) { }
        _ = Task.Run(Pump);
        Write("系统", $"ArdUi {Program.Version} 启动；数据目录 {dataRoot}");
    }

    public static void Write(string group, string text)
    {
        if (started == 1) queue.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{group}] {text}");
    }

    static string PathFor(DateTime day) => Path.Combine(folder, $"ardui-{day:yyyyMMdd}.txt");

    // The file the "日志" menu item opens; created empty if nothing was written today.
    public static string EnsureCurrent()
    {
        var path = PathFor(DateTime.Now);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }

    static async Task Pump()
    {
        var reader = queue.Reader;
        while (await reader.WaitToReadAsync())
        {
            var batch = new StringBuilder();
            while (reader.TryRead(out var line)) batch.AppendLine(line);
            var path = PathFor(DateTime.Now);
            try
            {
                File.AppendAllText(path, batch.ToString());
                if (new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", true);   // one rotation per day is enough
            }
            catch (IOException) { }
        }
    }
}
