namespace ArdUi;

// Guided updates, not automatic ones: M announces its newest ArdUi installer (GET /api/v1/release); this machine downloads it in the
// background from M into data\updates and asks the person before running it. M is trusted and reached over HTTPS; the file is used
// only when its size and SHA-256 are what M announced. A broken-off download resumes where it stopped, a verified one is not
// fetched again, a file that does not match is deleted. Failures wait longer each time (1 minute doubling to an hour); a good
// look waits CheckEvery. The installer keeps earlier versions in versions\<version>, so going back stays possible.
sealed class Updater : IAsyncDisposable
{
    public sealed record Offer(string Version, string File, long Size, string Sha256);
    public enum Phase { Idle, Downloading, Ready, Failed }

    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6), FirstWait = TimeSpan.FromMinutes(1), LongestWait = TimeSpan.FromHours(1);
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);   // no byte for this long ends a download attempt (it resumes later)
    const long Largest = 1L << 28;
    readonly HttpClient http;
    readonly string folder;
    readonly string current;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim kick = new(0, 1);
    readonly object sync = new();
    Task? loop;
    int failures;

    public Phase State { get; private set; }
    public Offer? Latest { get; private set; }
    public long Received { get; private set; }
    public string? Error { get; private set; }
    public string? ReadyPath { get; private set; }
    public event Action? Changed;

    public Updater(string server, string dataRoot, string current, bool allowLoopbackHttp = false)
    {
        var uri = new Uri(server);
        if (uri.Scheme != "https" && !(allowLoopbackHttp && uri.IsLoopback && uri.Scheme == "http"))
            throw new InvalidDataException("可信服务器必须使用 HTTPS。");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("X-ArdUi-Client", current);
        folder = Path.Combine(dataRoot, "updates");
        this.current = current;
    }

    public void Start(TimeSpan? delay = null) => loop ??= Run(delay ?? TimeSpan.FromSeconds(20), stop.Token);

    // Look now instead of at the next planned time (this version was just refused): at most one look waits, and none sooner than
    // FirstWait after the previous one, so this never turns into quick retries.
    public void CheckNow()
    {
        lock (sync) if (looked.IsRunning && looked.Elapsed < FirstWait) return;
        try { kick.Release(); } catch (SemaphoreFullException) { }
    }
    readonly Stopwatch looked = new();

    async Task Run(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (sync) looked.Restart();
            try { await Once(ct); failures = 0; wait = CheckEvery; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                failures++;
                wait = TimeSpan.FromTicks(Math.Min(LongestWait.Ticks, FirstWait.Ticks << Math.Min(failures - 1, 10)));
                Diagnostics.Log("update", ex.Message);
                Set(Phase.Failed, error: ex.Message);
            }
            try { await kick.WaitAsync(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task Once(CancellationToken ct)
    {
        var offer = await Announced(ct);
        if (offer == null || !Newer(offer.Version, current))
        {
            lock (sync) { Latest = null; ReadyPath = null; Received = 0; }
            Clean(null);
            Set(Phase.Idle);
            return;
        }
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, offer.File);
        lock (sync) Latest = offer;
        Clean(offer.File);
        if (!File.Exists(target) || !Matches(target, offer))
        {
            File.Delete(target);
            await Download(offer, target, ct);
        }
        lock (sync) { ReadyPath = target; Received = offer.Size; }
        Set(Phase.Ready);
    }

    async Task<Offer?> Announced(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync("/api/v1/release", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new IOException($"服务器没有给出新版本信息（HTTP {(int)response.StatusCode}）。");
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (text.Length > 4096) throw new InvalidDataException("服务器的新版本信息无效。");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True) return null;
        var version = root.GetProperty("version").GetString() ?? "";
        var file = root.GetProperty("file").GetString() ?? "";
        var size = root.GetProperty("size").GetInt64();
        var sha256 = root.GetProperty("sha256").GetString() ?? "";
        if (!Regex.IsMatch(version, @"\Av\d{1,3}\.(?:pre)?\d{1,3}\z") || file != $"ArdUi-{version}_setup.exe" || size is <= 0 or > Largest
            || !Regex.IsMatch(sha256, @"\A[0-9a-f]{64}\z"))
            throw new InvalidDataException("服务器的新版本信息无效。");
        return new Offer(version, file, size, sha256);
    }

    async Task Download(Offer offer, string target, CancellationToken ct)
    {
        var partial = target + ".partial";
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > offer.Size) { File.Delete(partial); have = 0; }
        lock (sync) Received = have;
        Set(Phase.Downloading);
        if (have < offer.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/release/" + offer.File);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(Stall);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); throw new IOException("下载需要重新开始。"); }
            if (!response.IsSuccessStatusCode) throw new IOException($"下载新版本失败（HTTP {(int)response.StatusCode}）。");
            var resumed = response.StatusCode == HttpStatusCode.PartialContent;
            if (resumed && response.Content.Headers.ContentRange?.From != have) throw new IOException("服务器续传的位置不对。");
            if (!resumed) { have = 0; lock (sync) Received = 0; }
            await using (var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                var buffer = new byte[1 << 16];
                var reported = Stopwatch.StartNew();
                while (true)
                {
                    timeout.CancelAfter(Stall);
                    var read = await input.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    have += read;
                    if (have > offer.Size) { output.Close(); File.Delete(partial); throw new InvalidDataException("下载的文件比服务器说的大，已删除。"); }
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    lock (sync) Received = have;
                    if (reported.ElapsedMilliseconds >= 250) { reported.Restart(); Changed?.Invoke(); }
                }
            }
            if (have < offer.Size) throw new IOException("下载中断，稍后继续。");
        }
        if (!Matches(partial, offer)) { File.Delete(partial); throw new InvalidDataException("下载的文件校验失败，已删除，稍后重新下载。"); }
        File.Move(partial, target, true);
    }

    static bool Matches(string path, Offer offer)
    {
        using var file = File.OpenRead(path);
        return file.Length == offer.Size && Convert.ToHexString(SHA256.HashData(file)).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Only the announced installer (and its partial download) stays: earlier ones are not needed again.
    void Clean(string? keep)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (keep != null && (name == keep || name == keep + ".partial")) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // v6.3 and v6.pre3 stand for the same step (see Program.WireVersion): newer is a higher major, or the same major and a higher step.
    internal static bool Newer(string offered, string mine)
    {
        static (int, int)? Parse(string value)
        {
            var match = Regex.Match(value, @"\Av(\d{1,3})\.(?:pre)?(\d{1,3})\z");
            return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
        }
        return Parse(offered) is { } a && Parse(mine) is { } b && a.CompareTo(b) > 0;
    }

    // Runs the downloaded installer, checked once more right before (it closes this ArdUi and installs next to the current version).
    public void Install()
    {
        Offer offer; string path;
        lock (sync) { offer = Latest ?? throw new InvalidOperationException("没有可安装的新版本。"); path = ReadyPath ?? throw new InvalidOperationException("新版本还没有下载完成。"); }
        if (!File.Exists(path) || !Matches(path, offer))
        {
            try { File.Delete(path); } catch (IOException) { }
            lock (sync) ReadyPath = null;
            Set(Phase.Failed, error: "下载的安装包已损坏，正在重新下载。"); CheckNow();
            throw new InvalidDataException("下载的安装包已损坏，正在重新下载。");
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = folder });
    }

    void Set(Phase phase, string? error = null)
    {
        lock (sync) { State = phase; Error = error; }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop != null) try { await loop; } catch { }
        http.Dispose(); stop.Dispose();
    }
}
