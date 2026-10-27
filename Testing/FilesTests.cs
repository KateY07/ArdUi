namespace ArdUi;

// `ArdUi.exe --files-test`: the shared folder's WebDAV server on a temporary folder: listing, upload, ranged download, folders, rename,
// delete into the recycle folder, overwrite keeps the old content, and nothing outside the folder is reachable (.., encoded dots,
// alternate data streams, device names, a junction, the recycle folder itself).
static class FilesTests
{
    public static int Run() { try { return RunAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; } }

    static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }
        var root = Path.Combine(Path.GetTempPath(), "ardui-files-test-" + Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        await using var server = new FileShareServer { Root = root };
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        async Task<HttpResponseMessage> Send(string method, string path, HttpContent? content = null, params (string, string)[] headers)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            return await http.SendAsync(request);
        }
        try
        {
            var data = RandomNumberGenerator.GetBytes(300_000);
            Check((await Send("PUT", "/report%20one.bin", new ByteArrayContent(data))).StatusCode == HttpStatusCode.Created, "upload a file");
            Check(File.ReadAllBytes(Path.Combine(root, "report one.bin")).SequenceEqual(data), "the upload is the file on disk");
            Check((await Send("MKCOL", "/文档/")).StatusCode == HttpStatusCode.Created && Directory.Exists(Path.Combine(root, "文档")), "make a folder (non-ASCII name)");
            var list = await (await Send("PROPFIND", "/", null, ("Depth", "1"))).Content.ReadAsStringAsync();
            Check(list.Contains("report%20one.bin") && list.Contains("%E6%96%87%E6%A1%A3/") && list.Contains("<D:collection"), "PROPFIND lists the folder");
            var range = await Send("GET", "/report%20one.bin", null, ("Range", "bytes=100000-"));
            Check(range.StatusCode == HttpStatusCode.PartialContent && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(data[100000..]), "a download resumes (Range)");
            Check((await Send("MOVE", "/report%20one.bin", null, ("Destination", http.BaseAddress + "%E6%96%87%E6%A1%A3/r.bin"))).StatusCode == HttpStatusCode.Created
                  && File.Exists(Path.Combine(root, "文档", "r.bin")), "rename / move");
            Check((await Send("PUT", "/%E6%96%87%E6%A1%A3/r.bin", new ByteArrayContent([1, 2, 3]))).StatusCode == HttpStatusCode.NoContent, "overwrite");
            Check((await Send("DELETE", "/%E6%96%87%E6%A1%A3/")).StatusCode == HttpStatusCode.NoContent && !Directory.Exists(Path.Combine(root, "文档")), "delete");
            var trash = Path.Combine(root, FileShareServer.Trash);
            var kept = Directory.Exists(trash) ? Directory.GetFiles(trash, "r.bin", SearchOption.AllDirectories) : [];
            Check(kept.Any(f => new FileInfo(f).Length == data.Length) && kept.Any(f => new FileInfo(f).Length == 3) && new DirectoryInfo(trash).Attributes.HasFlag(FileAttributes.Hidden),
                "deleted and overwritten content is kept in the hidden recycle folder");
            Check(!(await (await Send("PROPFIND", "/", null, ("Depth", "1"))).Content.ReadAsStringAsync()).Contains(FileShareServer.Trash), "the recycle folder is not listed");
            // the page's uploads: in pieces, continuing where they stopped
            var big = RandomNumberGenerator.GetBytes(250_000);
            (string, string)[] Up(long offset) => [("X-ArdUi-Upload-Key", "k0123456789abcdef"), ("X-ArdUi-Upload-Total", big.Length.ToString()), ("X-ArdUi-Upload-Offset", offset.ToString())];
            static long Have(HttpResponseMessage r) => long.Parse(r.Headers.GetValues("X-ArdUi-Upload-Have").First());
            var probe = await Send("PUT", "/big.bin", new ByteArrayContent([]), [("X-ArdUi-Upload-Key", "k0123456789abcdef"), ("X-ArdUi-Upload-Total", big.Length.ToString()), ("X-ArdUi-Upload-Probe", "1")]);
            Check(probe.StatusCode == HttpStatusCode.OK && Have(probe) == 0, "a new upload has nothing yet");
            var first = await Send("PUT", "/big.bin", new ByteArrayContent(big[..100_000]), Up(0));
            Check(first.StatusCode == HttpStatusCode.Accepted && Have(first) == 100_000 && !File.Exists(Path.Combine(root, "big.bin")), "a piece is kept, the file does not appear yet");
            var wrong = await Send("PUT", "/big.bin", new ByteArrayContent(big[50_000..]), Up(50_000));
            Check(wrong.StatusCode == HttpStatusCode.Conflict && Have(wrong) == 100_000, "a piece at the wrong place is refused and told where to go on");
            var rest = await Send("PUT", "/big.bin", new ByteArrayContent(big[100_000..]), Up(100_000));
            Check(rest.StatusCode == HttpStatusCode.Created && File.ReadAllBytes(Path.Combine(root, "big.bin")).SequenceEqual(big)
                  && !Directory.GetFiles(root).Any(f => Path.GetFileName(f).StartsWith(".ardui-upload-")), "the last piece completes the file, nothing is left over");
            using (var listed = JsonDocument.Parse(await (await Send("GET", "/?json")).Content.ReadAsStringAsync()))
                Check(listed.RootElement.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "big.bin" && e.GetProperty("by").GetString() == "本机"), "the uploader is recorded");
            var page = await (await Send("GET", "/")).Content.ReadAsStringAsync();
            Check(page.Contains("共享文件夹") && page.Contains("\"entries\""), "a browser gets the folder page");

            // nothing outside the folder
            var name = Path.GetFileName(outside);
            foreach (var path in new[] { "/../" + name + "/secret.txt", "/%2e%2e/" + name + "/secret.txt", "/..%2f" + name + "%2fsecret.txt", "/a.txt:stream", "/CON", "/nul.txt", "/" + FileShareServer.Trash + "/" })
            {
                var status = (await Send("GET", path)).StatusCode;
                Check(status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden, "refused: " + path + " (" + (int)status + ")");
            }
            var link = Path.Combine(root, "link");
            var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!; made.WaitForExit();
            if (Directory.Exists(link))
            {
                var status = (await Send("GET", "/link/secret.txt")).StatusCode;
                Check(status == HttpStatusCode.Forbidden, "refused: a junction to outside (" + (int)status + ")");
                Check(!(await (await Send("PROPFIND", "/", null, ("Depth", "1"))).Content.ReadAsStringAsync()).Contains("link"), "a junction is not listed");
            }
            server.Root = null;
            Check((await Send("GET", "/")).StatusCode == HttpStatusCode.ServiceUnavailable, "no folder set: nothing is served");
        }
        finally
        {
            try { var link = Path.Combine(root, "link"); if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { Directory.Delete(root, true); Directory.Delete(outside, true); } catch { }
        }
        Console.WriteLine(failures == 0 ? "PASS: the shared folder reads, writes, resumes, keeps what is deleted and stays inside its folder." : $"{failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
