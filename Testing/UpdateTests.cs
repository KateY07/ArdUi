namespace ArdUi;

// `ArdUi.exe --update-test`: guided updates against a stand-in M on loopback. An older or equal version downloads nothing; a newer
// one is downloaded, checked and offered; a verified download is not fetched again; a broken-off one resumes from where it stopped;
// a file that does not match what M announced is deleted and never offered; an installer changed on disk is not run; earlier
// installers are cleared away.
static class UpdateTests
{
    sealed class FakeM : IDisposable
    {
        readonly HttpListener listener = new();
        public string Url { get; }
        public string Version = "v6.3";
        public byte[] Body = RandomNumberGenerator.GetBytes(700_000);
        public byte[]? Served;                // what the download sends, when it differs from what was announced
        public int? CutAfter;                 // break the connection after this many bytes
        public int Downloads;
        public string? LastRange;
        public FakeM()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Url); listener.Start();
            _ = Task.Run(Serve);
        }
        string File => $"ArdUi-{Version}_setup.exe";
        async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                var response = context.Response;
                try
                {
                    var path = context.Request.Url!.AbsolutePath;
                    if (path == "/api/v1/release")
                    {
                        var text = JsonSerializer.Serialize(new { available = true, version = Version, file = File, size = Body.Length, sha256 = Convert.ToHexString(SHA256.HashData(Body)).ToLowerInvariant(), url = "/api/v1/release/" + File });
                        var bytes = Encoding.UTF8.GetBytes(text); response.ContentType = "application/json"; response.ContentLength64 = bytes.Length;
                        await response.OutputStream.WriteAsync(bytes); response.Close(); continue;
                    }
                    if (path != "/api/v1/release/" + File) { response.StatusCode = 404; response.Close(); continue; }
                    Interlocked.Increment(ref Downloads);
                    var body = Served ?? Body; var start = 0;
                    LastRange = context.Request.Headers["Range"];
                    if (LastRange is { } range && Regex.Match(range, @"^bytes=(\d+)-$") is { Success: true } m)
                    {
                        start = int.Parse(m.Groups[1].Value);
                        response.StatusCode = 206; response.Headers["Content-Range"] = $"bytes {start}-{body.Length - 1}/{body.Length}";
                    }
                    var count = body.Length - start;
                    response.ContentLength64 = count; response.SendChunked = false;
                    if (CutAfter is { } cut && cut < count)
                    {
                        await response.OutputStream.WriteAsync(body.AsMemory(start, cut)); await response.OutputStream.FlushAsync();
                        response.Abort(); continue;
                    }
                    await response.OutputStream.WriteAsync(body.AsMemory(start, count)); response.Close();
                }
                catch { try { response.Abort(); } catch { } }
            }
        }
        public void Dispose() { listener.Stop(); listener.Close(); }
    }

    public static int Run() { try { return RunAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; } }

    static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }
        var root = Path.Combine(Path.GetTempPath(), "ardui-update-test-" + Guid.NewGuid().ToString("N"));
        var updates = Path.Combine(root, "updates");
        using var m = new FakeM();
        try
        {
            Check(Updater.Newer("v6.3", "v6.pre2") && Updater.Newer("v7.pre1", "v6.pre9") && !Updater.Newer("v6.2", "v6.pre2") && !Updater.Newer("v6.pre1", "v6.pre2")
                  && !Updater.Newer("v6.x", "v6.pre2"), "v6.3 and v6.pre3 are the same step; only a higher one is newer");
            try { _ = new Updater("http://example.com/", root, "v6.pre2"); Check(false, "plain HTTP is refused"); }
            catch (InvalidDataException) { Check(true, "plain HTTP is refused"); }

            await using (var same = new Updater(m.Url, root, "v6.pre3", allowLoopbackHttp: true))
            {
                await same.Once(CancellationToken.None);
                Check(same.State == Updater.Phase.Idle && same.Latest == null && m.Downloads == 0 && !Directory.Exists(updates), "the same version downloads nothing");
            }

            await using var updater = new Updater(m.Url, root, "v6.pre2", allowLoopbackHttp: true);
            var target = Path.Combine(updates, "ArdUi-v6.3_setup.exe");
            await updater.Once(CancellationToken.None);
            Check(updater.State == Updater.Phase.Ready && updater.ReadyPath == target && File.ReadAllBytes(target).SequenceEqual(m.Body) && m.Downloads == 1,
                "a newer version is downloaded, checked and offered");
            Check(!File.Exists(target + ".partial"), "no partial file is left behind");

            await updater.Once(CancellationToken.None);
            Check(updater.State == Updater.Phase.Ready && m.Downloads == 1, "a verified download is not fetched again");

            File.Delete(target); m.CutAfter = 200_000;
            try { await updater.Once(CancellationToken.None); Check(false, "a broken-off download fails this attempt"); }
            catch (Exception) { }
            var kept = File.Exists(target + ".partial") ? new FileInfo(target + ".partial").Length : 0;
            Check(kept is > 0 and <= 200_000 && !File.Exists(target), "a broken-off download keeps what arrived, offers nothing");
            m.CutAfter = null;
            await updater.Once(CancellationToken.None);
            Check(m.LastRange == $"bytes={kept}-" && updater.State == Updater.Phase.Ready && File.ReadAllBytes(target).SequenceEqual(m.Body),
                "the next attempt resumes from where it stopped");

            File.Delete(target); m.Served = m.Body.ToArray(); m.Served[12345] ^= 1;
            try { await updater.Once(CancellationToken.None); Check(false, "a file that does not match is refused"); }
            catch (InvalidDataException) { Check(!File.Exists(target) && !File.Exists(target + ".partial"), "a file that does not match is deleted and not offered"); }
            m.Served = null;

            await updater.Once(CancellationToken.None);
            var changed = File.ReadAllBytes(target); changed[0] ^= 1; File.WriteAllBytes(target, changed);
            try { updater.Install(); Check(false, "an installer changed on disk is not run"); }
            catch (InvalidDataException) { Check(!File.Exists(target) && updater.ReadyPath == null, "an installer changed on disk is not run, and is deleted"); }

            File.WriteAllBytes(Path.Combine(updates, "ArdUi-v6.1_setup.exe"), [1, 2, 3]);
            m.Version = "v6.4";
            await updater.Once(CancellationToken.None);
            Check(updater.Latest?.Version == "v6.4" && Directory.GetFiles(updates).Select(Path.GetFileName).SequenceEqual(["ArdUi-v6.4_setup.exe"]),
                "only the announced installer is kept");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(failures == 0 ? "PASS: guided updates download, check, resume and offer only what M announced." : $"{failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
