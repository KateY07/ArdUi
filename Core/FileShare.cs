using System.Xml.Linq;

namespace ArdUi;

// The shared folder ("文件"): a small WebDAV server on loopback that the gateway forwards to an authorised controller, over direct paths
// only (relayed sessions would spend the volunteer ArdFerry nodes' bandwidth). One folder, chosen in the settings, with full read and
// write; deleting (and overwriting) moves the old content into a hidden recycle folder inside it. Every path is checked to stay inside
// the folder: no "..", no junctions or symbolic links on the way, no alternate data streams, no reserved device names. Downloads resume
// (Range); the controller uses Explorer (\\127.0.0.1@port\DavWWWRoot), a browser, or any WebDAV client.
sealed class FileShareServer : IAsyncDisposable
{
    public const string Trash = ".ardui-trash";
    const string UploadPrefix = ".ardui-upload-";
    const int MaxConnections = 64, MaxHeader = 32 * 1024, MaxPath = 2000;
    static readonly XNamespace Dav = "DAV:";
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    readonly object sync = new();
    readonly SemaphoreSlim connections = new(MaxConnections);
    readonly CancellationTokenSource stop = new();
    TcpListener? listener;
    Task? accept;
    volatile string? root;

    // Who this server acts for: a controller ("计算机名 (设备ID)") or "本机" (this machine's own browser). Every change and every download
    // goes into that controller's audit record (files.jsonl next to its screen recording and conversation), and each uploaded file
    // carries its uploader (an NTFS stream next to its content, shown as "上传人").
    public string Actor { get; set; } = "本机";
    public Action<object>? Audit { get; set; }
    const string UploaderStream = ":ardui.uploader";
    void Record(string operation, string path, long? size = null, string? to = null)
    {
        Diagnostics.Log("files", $"{Actor} {operation} {path}" + (to != null ? " → " + to : "") + (size is { } bytes ? " " + Size(bytes) : ""));
        try { Audit?.Invoke(new { at = DateTimeOffset.Now, by = Actor, operation, path, to, size }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Log("files-audit", ex.Message); }
    }
    static string Size(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1 << 20 ? $"{bytes / 1024.0:F1} KB" : bytes < 1 << 30 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1073741824.0:F2} GB";
    static string Shown(string[] segments) => "/" + string.Join("/", segments);
    void Stamp(string full)
    {
        try { File.WriteAllText(full + UploaderStream, JsonSerializer.Serialize(new { by = Actor, at = DateTimeOffset.Now.ToUnixTimeSeconds() })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { Diagnostics.Log("files-stamp", ex.Message); }
    }
    static string? Uploader(string full)
    {
        try { using var document = JsonDocument.Parse(File.ReadAllText(full + UploaderStream)); return document.RootElement.GetProperty("by").GetString(); }
        catch { return null; }
    }

    // The shared folder, or null when none is set (then every request is refused).
    public string? Root
    {
        get => root;
        set => root = string.IsNullOrWhiteSpace(value) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    // The loopback port, started on first use.
    public int Port
    {
        get
        {
            lock (sync)
            {
                if (listener == null)
                {
                    listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                    accept = Accept();
                }
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }
    }

    async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            if (!connections.Wait(0)) { client.Dispose(); continue; }
            _ = Task.Run(async () => { try { await Serve(client, stop.Token); } finally { connections.Release(); } });
        }
    }

    // ---- HTTP/1.1, as much as WebDAV clients use ---------------------------------------------------------------------------------------
    sealed class Connection(Stream stream)
    {
        readonly byte[] buffer = new byte[64 * 1024];
        int start, end;
        public Stream Stream => stream;
        async Task<bool> Fill(CancellationToken ct)
        {
            if (start == end) { start = end = 0; }
            else if (end == buffer.Length) { Buffer.BlockCopy(buffer, start, buffer, 0, end - start); end -= start; start = 0; }
            var read = await stream.ReadAsync(buffer.AsMemory(end), ct);
            end += read; return read > 0;
        }
        public async Task<string?> ReadLine(CancellationToken ct, int limit)
        {
            var line = new List<byte>();
            while (true)
            {
                for (var i = start; i < end; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.AddRange(buffer.AsSpan(start, i - start).ToArray()); start = i + 1;
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    return Encoding.UTF8.GetString(line.ToArray());
                }
                line.AddRange(buffer.AsSpan(start, end - start).ToArray()); start = end;
                if (line.Count > limit) throw new InvalidDataException("请求头过长。");
                if (!await Fill(ct)) return line.Count == 0 ? null : throw new IOException("连接中断。");
            }
        }
        public async ValueTask<int> Read(Memory<byte> into, CancellationToken ct)
        {
            if (start == end && !await Fill(ct)) return 0;
            var count = Math.Min(into.Length, end - start);
            buffer.AsMemory(start, count).CopyTo(into); start += count; return count;
        }
    }

    sealed class Request
    {
        public required string Method, Target;
        public required Dictionary<string, string> Headers;
        public long? Length; public bool Chunked;
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    // The request body: by Content-Length or chunked; what a handler leaves unread is read away before the next request.
    sealed class Body(Connection connection, Request request) : Stream
    {
        long left = request.Length ?? 0; long chunk; bool done = !request.Chunked && (request.Length ?? 0) == 0;
        public override async ValueTask<int> ReadAsync(Memory<byte> into, CancellationToken ct = default)
        {
            if (done) return 0;
            if (!request.Chunked)
            {
                var read = await connection.Read(into[..(int)Math.Min(into.Length, left)], ct);
                if (read == 0) throw new IOException("上传中断。");
                left -= read; done = left == 0; return read;
            }
            if (chunk == 0)
            {
                var size = (await connection.ReadLine(ct, 1024) ?? throw new IOException("上传中断。")).Split(';')[0].Trim();
                chunk = long.Parse(size, System.Globalization.NumberStyles.HexNumber);
                if (chunk == 0) { while (!string.IsNullOrEmpty(await connection.ReadLine(ct, MaxHeader))) { } done = true; return 0; }
            }
            var got = await connection.Read(into[..(int)Math.Min(into.Length, chunk)], ct);
            if (got == 0) throw new IOException("上传中断。");
            chunk -= got;
            if (chunk == 0) await connection.ReadLine(ct, 16);
            return got;
        }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public async Task Drain(CancellationToken ct) { var sink = new byte[16384]; while (await ReadAsync(sink, ct) > 0) { } }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    sealed class Reply(int status, string reason)
    {
        public int Status = status; public string Reason = reason;
        public readonly List<(string, string)> Headers = [];
        public byte[] Content = [];
        public Func<Stream, CancellationToken, Task>? Stream; public long StreamLength;
        public static Reply Text(int status, string reason, string text) => new(status, reason) { Content = Encoding.UTF8.GetBytes(text), Headers = { ("Content-Type", "text/plain; charset=utf-8") } };
    }

    async Task Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var connection = new Connection(client.GetStream());
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromMinutes(2));
                    var line = await connection.ReadLine(idle.Token, MaxHeader);
                    if (line == null) return;
                    if (line.Length == 0) continue;
                    var parts = line.Split(' ');
                    if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var size = 0;
                    while (true)
                    {
                        var header = await connection.ReadLine(idle.Token, MaxHeader) ?? throw new IOException("连接中断。");
                        if (header.Length == 0) break;
                        if ((size += header.Length) > MaxHeader) return;
                        var colon = header.IndexOf(':');
                        if (colon > 0) headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
                    }
                    var request = new Request { Method = parts[0].ToUpperInvariant(), Target = parts[1], Headers = headers };
                    request.Chunked = request.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
                    if (!request.Chunked && request.Header("Content-Length") is { } length) request.Length = long.TryParse(length, out var value) && value >= 0 ? value : throw new InvalidDataException("无效的内容长度。");
                    var body = new Body(connection, request);
                    Reply reply;
                    try { reply = await Handle(request, body, ct); }
                    catch (ShareError error) { reply = Reply.Text(error.Status, error.Reason, error.Message); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Diagnostics.Log("files", request.Method + " " + ex.Message); reply = Reply.Text(ex is UnauthorizedAccessException ? 403 : 409, ex is UnauthorizedAccessException ? "Forbidden" : "Conflict", ex.Message); }
                    var keep = request.Header("Connection")?.Equals("close", StringComparison.OrdinalIgnoreCase) != true;
                    // whatever the handler did not read of the body goes, so the next request starts where it should
                    try { using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct); drain.CancelAfter(TimeSpan.FromSeconds(30)); await body.Drain(drain.Token); }
                    catch { keep = false; }
                    await Send(connection.Stream, request, reply, keep, ct);
                    if (!keep) return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException or FormatException) { }
        }
    }

    static async Task Send(Stream stream, Request request, Reply reply, bool keep, CancellationToken ct)
    {
        var head = new StringBuilder($"HTTP/1.1 {reply.Status} {reply.Reason}\r\n");
        var length = reply.Stream != null ? reply.StreamLength : reply.Content.Length;
        foreach (var (name, value) in reply.Headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("Content-Length: ").Append(length).Append("\r\n");
        head.Append("Connection: ").Append(keep ? "keep-alive" : "close").Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), ct);
        if (request.Method != "HEAD")
        {
            if (reply.Stream != null) await reply.Stream(stream, ct);
            else await stream.WriteAsync(reply.Content, ct);
        }
        await stream.FlushAsync(ct);
    }

    sealed class ShareError(int status, string reason, string message) : Exception(message)
    {
        public int Status => status; public string Reason => reason;
    }
    static ShareError Error(int status, string reason, string message) => new(status, reason, message);

    // ---- paths ---------------------------------------------------------------------------------------------------------------------------
    // The URL path as checked segments under the shared folder. Each segment is a plain file name: no "." or "..", no ':' (alternate data
    // streams), no characters Windows does not allow, no trailing dot or space, no device names; the recycle folder is not reachable.
    internal static string[] Segments(string target)
    {
        var path = target.Split('?', '#')[0];
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") path = absolute.AbsolutePath;
        if (!path.StartsWith('/')) throw Error(400, "Bad Request", "路径无效。");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Length > 255 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ')
                || Reserved.Contains(segment.Split('.')[0].TrimEnd()))
                throw Error(400, "Bad Request", "文件名无效：" + segment);
        }
        if (segments.Length > 0 && (segments[0].Equals(Trash, StringComparison.OrdinalIgnoreCase))) throw Error(404, "Not Found", "没有这个文件。");
        if (segments.Any(s => s.StartsWith(UploadPrefix, StringComparison.OrdinalIgnoreCase))) throw Error(404, "Not Found", "没有这个文件。");
        return segments;
    }

    // The full path for these segments, refusing anything outside the folder and any junction or symbolic link on the way.
    string Resolve(string[] segments)
    {
        var folder = root ?? throw Error(503, "Service Unavailable", "被控端还没有设置共享文件夹。");
        if (!Directory.Exists(folder)) throw Error(503, "Service Unavailable", "被控端的共享文件夹不存在。");
        var full = Path.GetFullPath(Path.Combine([folder, .. segments]));
        if (full.Length > MaxPath || !(full.Equals(folder, StringComparison.OrdinalIgnoreCase) || full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw Error(403, "Forbidden", "不能访问共享文件夹以外的位置。");
        var walk = folder;
        foreach (var segment in segments)
        {
            walk = Path.Combine(walk, segment);
            var info = new FileInfo(walk);
            if (!info.Exists && !Directory.Exists(walk)) break;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Error(403, "Forbidden", "不能经过链接或目录联接。");
        }
        return full;
    }

    static string Href(IEnumerable<string> segments, bool directory)
    {
        var path = "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
        return directory && !path.EndsWith('/') ? path + "/" : path;
    }

    // Deleted or overwritten content is kept: .ardui-trash\<time>\<its path in the folder>.
    void ToTrash(string full)
    {
        var folder = root!;
        var target = Path.Combine(folder, Trash, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), Path.GetRelativePath(folder, full));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var trash = new DirectoryInfo(Path.Combine(folder, Trash));
        if (!trash.Attributes.HasFlag(FileAttributes.Hidden)) trash.Attributes |= FileAttributes.Hidden;
        if (Directory.Exists(full)) Directory.Move(full, target); else File.Move(full, target);
    }

    // ---- WebDAV ---------------------------------------------------------------------------------------------------------------------------
    async Task<Reply> Handle(Request request, Body body, CancellationToken ct)
    {
        if (request.Method == "OPTIONS")
        {
            var reply = new Reply(200, "OK");
            reply.Headers.AddRange([("DAV", "1, 2"), ("MS-Author-Via", "DAV"), ("Allow", "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, MOVE, COPY, LOCK, UNLOCK")]);
            return reply;
        }
        var segments = Segments(request.Target);
        var full = Resolve(segments);
        var isDirectory = Directory.Exists(full); var isFile = !isDirectory && File.Exists(full);
        switch (request.Method)
        {
            case "PROPFIND":
            {
                if (!isDirectory && !isFile) throw Error(404, "Not Found", "没有这个文件。");
                var depth = request.Header("Depth") ?? "1";
                var responses = new List<XElement> { Properties(segments, full, isDirectory) };
                if (isDirectory && depth != "0")
                    foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
                        if (Listed(entry, segments.Length == 0))
                            responses.Add(Properties([.. segments, entry.Name], entry.FullName, entry is DirectoryInfo));
                return Xml(207, "Multi-Status", new XElement(Dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", Dav), responses));
            }
            case "PROPPATCH":
            {
                if (!isDirectory && !isFile) throw Error(404, "Not Found", "没有这个文件。");
                XDocument? document = null;
                try { document = XDocument.Load(new MemoryStream(await ReadSmall(body, ct))); } catch (System.Xml.XmlException) { }
                var names = document?.Descendants(Dav + "prop").SelectMany(p => p.Elements()).Select(e => new XElement(e.Name)).ToArray() ?? [];
                return Xml(207, "Multi-Status", new XElement(Dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", Dav),
                    new XElement(Dav + "response", new XElement(Dav + "href", Href(segments, isDirectory)),
                        new XElement(Dav + "propstat", new XElement(Dav + "prop", names), new XElement(Dav + "status", "HTTP/1.1 200 OK")))));
            }
            case "GET" or "HEAD":
                if (isDirectory) return Listing(segments, full, request.Target.Contains("?json", StringComparison.Ordinal));
                if (!isFile) throw Error(404, "Not Found", "没有这个文件。");
                // a download is recorded once, when it starts at the beginning (resumed parts are the same download)
                if (request.Method == "GET" && (request.Header("Range") is not { } asked || asked.StartsWith("bytes=0-", StringComparison.Ordinal)))
                    Record("下载", Shown(segments), new FileInfo(full).Length);
                return Download(request, full);
            case "PUT":
            {
                if (isDirectory) throw Error(405, "Method Not Allowed", "这是一个文件夹。");
                if (segments.Length == 0 || !Directory.Exists(Path.GetDirectoryName(full))) throw Error(409, "Conflict", "上级文件夹不存在。");
                var temp = Path.Combine(Path.GetDirectoryName(full)!, UploadPrefix + Guid.NewGuid().ToString("N"));
                try
                {
                    try
                    {
                        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                        {
                            File.SetAttributes(temp, FileAttributes.Hidden);
                            await body.CopyToAsync(output, 1 << 16, ct);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { Record("上传中断", Shown(segments)); throw; }
                    File.SetAttributes(temp, FileAttributes.Normal);
                    var replaced = isFile && new FileInfo(full).Length > 0;
                    if (replaced) ToTrash(full);   // an empty placeholder (Explorer makes one first) is not worth keeping
                    File.Move(temp, full, true);
                    Stamp(full);
                    Record(replaced ? "上传（覆盖，旧文件在回收文件夹）" : "上传", Shown(segments), new FileInfo(full).Length);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return new Reply(isFile ? 204 : 201, isFile ? "No Content" : "Created");
            }
            case "DELETE":
                if (segments.Length == 0) throw Error(403, "Forbidden", "不能删除共享文件夹本身。");
                if (!isDirectory && !isFile) throw Error(404, "Not Found", "没有这个文件。");
                ToTrash(full);
                Record(isDirectory ? "删除文件夹（移到回收文件夹）" : "删除（移到回收文件夹）", Shown(segments), isFile ? null : null);
                return new Reply(204, "No Content");
            case "MKCOL":
                if (isDirectory || isFile) throw Error(405, "Method Not Allowed", "已经存在。");
                if (!Directory.Exists(Path.GetDirectoryName(full))) throw Error(409, "Conflict", "上级文件夹不存在。");
                Directory.CreateDirectory(full);
                Record("新建文件夹", Shown(segments));
                return new Reply(201, "Created");
            case "MOVE" or "COPY":
            {
                if (segments.Length == 0) throw Error(403, "Forbidden", "不能移动共享文件夹本身。");
                if (!isDirectory && !isFile) throw Error(404, "Not Found", "没有这个文件。");
                var destinationSegments = Segments(request.Header("Destination") ?? throw Error(400, "Bad Request", "缺少目标位置。"));
                var destination = Resolve(destinationSegments);
                if (destinationSegments.Length == 0 || destination.Equals(full, StringComparison.OrdinalIgnoreCase)) throw Error(403, "Forbidden", "目标位置无效。");
                if (isDirectory && destination.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Error(403, "Forbidden", "不能移到自己里面。");
                if (!Directory.Exists(Path.GetDirectoryName(destination))) throw Error(409, "Conflict", "目标的上级文件夹不存在。");
                var existed = Directory.Exists(destination) || File.Exists(destination);
                if (existed)
                {
                    if (request.Header("Overwrite")?.Equals("F", StringComparison.OrdinalIgnoreCase) == true) throw Error(412, "Precondition Failed", "目标已存在。");
                    ToTrash(destination);
                }
                if (request.Method == "MOVE") { if (isDirectory) Directory.Move(full, destination); else File.Move(full, destination); }
                else if (isDirectory) CopyDirectory(full, destination); else File.Copy(full, destination);
                Record((request.Method == "MOVE" ? "移动/重命名" : "复制") + (existed ? "（覆盖，旧内容在回收文件夹）" : ""), Shown(segments), null, Shown(destinationSegments));
                return new Reply(existed ? 204 : 201, existed ? "No Content" : "Created");
            }
            case "LOCK":
            {
                // Writers such as Explorer lock before they write; one person works on these files, so the lock is granted and not enforced.
                var created = false;
                if (!isDirectory && !isFile)
                {
                    if (segments.Length == 0 || !Directory.Exists(Path.GetDirectoryName(full))) throw Error(409, "Conflict", "上级文件夹不存在。");
                    await File.WriteAllBytesAsync(full, [], ct); created = true;
                }
                var token = "opaquelocktoken:" + Guid.NewGuid();
                var reply = Xml(created ? 201 : 200, created ? "Created" : "OK", new XElement(Dav + "prop", new XAttribute(XNamespace.Xmlns + "D", Dav),
                    new XElement(Dav + "lockdiscovery", new XElement(Dav + "activelock",
                        new XElement(Dav + "locktype", new XElement(Dav + "write")), new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                        new XElement(Dav + "depth", "0"), new XElement(Dav + "timeout", "Second-3600"),
                        new XElement(Dav + "locktoken", new XElement(Dav + "href", token)), new XElement(Dav + "lockroot", new XElement(Dav + "href", Href(segments, isDirectory)))))));
                reply.Headers.Add(("Lock-Token", "<" + token + ">"));
                return reply;
            }
            case "UNLOCK":
                return new Reply(204, "No Content");
            default:
                throw Error(405, "Method Not Allowed", "不支持的操作。");
        }
    }

    static bool Listed(FileSystemInfo entry, bool atRoot) =>
        !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !entry.Name.StartsWith(UploadPrefix, StringComparison.OrdinalIgnoreCase)
        && !(atRoot && entry.Name.Equals(Trash, StringComparison.OrdinalIgnoreCase));

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (entry is DirectoryInfo directory) CopyDirectory(directory.FullName, Path.Combine(to, entry.Name));
            else File.Copy(entry.FullName, Path.Combine(to, entry.Name));
        }
    }

    static async Task<byte[]> ReadSmall(Body body, CancellationToken ct)
    {
        using var memory = new MemoryStream(); var buffer = new byte[8192];
        while (await body.ReadAsync(buffer, ct) is var read and > 0) { memory.Write(buffer, 0, read); if (memory.Length > 1 << 20) throw Error(413, "Payload Too Large", "请求过大。"); }
        return memory.ToArray();
    }

    static XElement Properties(IEnumerable<string> segments, string full, bool directory)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(full) : new FileInfo(full);
        var props = new XElement(Dav + "prop",
            new XElement(Dav + "displayname", segments.LastOrDefault() ?? ""),
            new XElement(Dav + "resourcetype", directory ? new XElement(Dav + "collection") : null),
            new XElement(Dav + "creationdate", info.CreationTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
            new XElement(Dav + "getlastmodified", info.LastWriteTimeUtc.ToString("R")),
            new XElement(Dav + "supportedlock"), new XElement(Dav + "lockdiscovery"));
        if (info is FileInfo file)
        {
            props.Add(new XElement(Dav + "getcontentlength", file.Length), new XElement(Dav + "getcontenttype", "application/octet-stream"),
                new XElement(Dav + "getetag", ETag(file)));
        }
        return new XElement(Dav + "response", new XElement(Dav + "href", Href(segments, directory)),
            new XElement(Dav + "propstat", props, new XElement(Dav + "status", "HTTP/1.1 200 OK")));
    }

    static string ETag(FileInfo file) => $"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"";

    static Reply Xml(int status, string reason, XElement root)
    {
        var reply = new Reply(status, reason) { Content = Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + "\n" + root.ToString(SaveOptions.DisableFormatting)) };
        reply.Headers.Add(("Content-Type", "application/xml; charset=utf-8"));
        return reply;
    }

    // GET with Range: one range (a-b, a-, -n), so a broken-off download continues where it stopped.
    static Reply Download(Request request, string full)
    {
        var file = new FileInfo(full); var size = file.Length; long from = 0, to = size - 1; var partial = false;
        if (request.Header("Range") is { } range && Regex.Match(range, @"\Abytes=(\d*)-(\d*)\z") is { Success: true } m && (m.Groups[1].Length > 0 || m.Groups[2].Length > 0))
        {
            if (m.Groups[1].Length == 0) from = Math.Max(0, size - long.Parse(m.Groups[2].Value));
            else { from = long.Parse(m.Groups[1].Value); if (m.Groups[2].Length > 0) to = Math.Min(to, long.Parse(m.Groups[2].Value)); }
            if (from >= size || to < from)
            {
                var refused = new Reply(416, "Range Not Satisfiable"); refused.Headers.Add(("Content-Range", $"bytes */{size}")); return refused;
            }
            partial = true;
        }
        var reply = new Reply(partial ? 206 : 200, partial ? "Partial Content" : "OK") { StreamLength = to - from + 1 };
        reply.Headers.AddRange([("Content-Type", "application/octet-stream"), ("Accept-Ranges", "bytes"), ("ETag", ETag(file)), ("Last-Modified", file.LastWriteTimeUtc.ToString("R"))]);
        if (partial) reply.Headers.Add(("Content-Range", $"bytes {from}-{to}/{size}"));
        if (request.Method == "HEAD") return reply;
        reply.Stream = async (output, ct) =>
        {
            await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
            input.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[1 << 16]; var left = to - from + 1;
            while (left > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                if (read == 0) throw new IOException("文件在下载中变短了。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct); left -= read;
            }
        };
        return reply;
    }

    // A folder opened in a browser: a light, plain page with the folder's entries and the everyday actions (upload by button or by
    // dropping files, new folder, rename, delete into the recycle folder), all done with the same WebDAV requests a client would send.
    Reply Listing(string[] segments, string full, bool json)
    {
        var entries = new DirectoryInfo(full).EnumerateFileSystemInfos().Where(e => Listed(e, segments.Length == 0))
            .OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new { name = e.Name, dir = e is DirectoryInfo, size = (e as FileInfo)?.Length ?? 0, modified = e.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                by = e is FileInfo ? Uploader(e.FullName) : null });
        var data = JsonSerializer.Serialize(new { path = segments, me = Actor, entries }).Replace("<", "\\u003c");
        var reply = new Reply(200, "OK") { Content = Encoding.UTF8.GetBytes(json ? data : Page.Replace("/*DATA*/null", data)) };
        reply.Headers.AddRange([("Content-Type", json ? "application/json; charset=utf-8" : "text/html; charset=utf-8"), ("Cache-Control", "no-store")]);
        return reply;
    }

    const string Page = """
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>共享文件夹 · ArdUi</title>
<style>
:root{--bg:#fafaf8;--card:#fff;--line:#ecebe6;--text:#1f1e1d;--muted:#8a877f;--accent:#c96442;--accent-soft:#fbeee8;--hover:#f6f5f1;--ok:#2f7d4f;--bad:#b3261e}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI","Microsoft YaHei UI",system-ui,sans-serif}
.wrap{max-width:1000px;margin:0 auto;padding:28px 20px 120px}
header{display:flex;align-items:center;gap:12px;margin-bottom:18px;flex-wrap:wrap}
.logo{width:34px;height:34px;border-radius:9px;background:var(--accent);color:#fff;display:grid;place-items:center;font-weight:700}
h1{font-size:18px;margin:0;font-weight:600}
.sub{color:var(--muted);font-size:12px}
.crumbs{display:flex;flex-wrap:wrap;gap:4px;align-items:center;margin:0 0 14px}
.crumbs a{color:var(--text);text-decoration:none;padding:3px 8px;border-radius:6px}
.crumbs a:hover{background:var(--hover)}
.crumbs span{color:var(--muted)}
.bar{display:flex;gap:8px;margin-left:auto}
button{font:inherit;border:1px solid var(--line);background:var(--card);color:var(--text);border-radius:8px;padding:7px 14px;cursor:pointer}
button:hover{background:var(--hover)}
button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
button.primary:hover{filter:brightness(1.05)}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden}
table{width:100%;border-collapse:collapse}
th{font-weight:500;color:var(--muted);font-size:12px;text-align:left;padding:10px 16px;border-bottom:1px solid var(--line);white-space:nowrap}
td{padding:10px 16px;border-bottom:1px solid var(--line);vertical-align:middle}
tr:last-child td{border-bottom:0}
tbody tr:hover{background:var(--hover)}
tbody tr.fresh{animation:fresh 2.4s ease-out}
@keyframes fresh{from{background:var(--accent-soft)}to{background:transparent}}
td.name a{color:var(--text);text-decoration:none;display:flex;align-items:center;gap:10px;word-break:break-all}
td.name a:hover{color:var(--accent)}
.icon{width:20px;height:20px;flex:none}
td.by,td.size,td.time{color:var(--muted);white-space:nowrap;font-size:13px}
td.by .me{color:var(--accent)}
td.size{text-align:right}
td.ops{text-align:right;white-space:nowrap;width:1%}
td.ops button{padding:3px 10px;font-size:12px;border-color:transparent;background:transparent;color:var(--muted);visibility:hidden}
tbody tr:hover td.ops button{visibility:visible}
td.ops button:hover{background:var(--card);border-color:var(--line);color:var(--text)}
td.ops button.danger:hover{color:var(--bad)}
.empty{padding:48px 16px;text-align:center;color:var(--muted)}
.drop{position:fixed;inset:0;background:rgba(201,100,66,.07);border:2px dashed var(--accent);display:none;place-items:center;font-size:16px;color:var(--accent);pointer-events:none;z-index:5}
body.dragging .drop{display:grid}
.toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);background:#1f1e1d;color:#fff;padding:9px 16px;border-radius:10px;font-size:13px;opacity:0;transition:opacity .2s;pointer-events:none;max-width:90vw;z-index:9}
.toast.show{opacity:.95}
footer{margin-top:14px;color:var(--muted);font-size:12px}
/* the upload panel */
.up{position:fixed;right:20px;bottom:20px;width:380px;max-width:calc(100vw - 40px);background:var(--card);border:1px solid var(--line);border-radius:14px;box-shadow:0 12px 32px rgba(31,30,29,.12);display:none;z-index:6;overflow:hidden}
.up.show{display:block}
.up-head{display:flex;align-items:center;gap:10px;padding:12px 14px;border-bottom:1px solid var(--line)}
.up-title{font-weight:600}
.up-sum{color:var(--muted);font-size:12px;margin-left:auto;white-space:nowrap}
.up-head button{padding:2px 8px;border:0;background:transparent;color:var(--muted);font-size:16px;line-height:1}
.up-total{height:3px;background:var(--line)}
.up-total i{display:block;height:100%;width:0;background:var(--accent);transition:width .2s}
.up-list{max-height:320px;overflow:auto}
.up.min .up-list{display:none}
.item{padding:10px 14px;border-bottom:1px solid var(--line)}
.item:last-child{border-bottom:0}
.item-top{display:flex;align-items:center;gap:8px}
.item-name{flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.item-size{color:var(--muted);font-size:12px;white-space:nowrap}
.item button{padding:0 6px;border:0;background:transparent;color:var(--muted);font-size:13px}
.item button:hover{color:var(--text);background:transparent}
.track{height:4px;background:var(--hover);border-radius:2px;margin:7px 0 4px;overflow:hidden}
.track i{display:block;height:100%;width:0;background:var(--accent);border-radius:2px;transition:width .15s}
.item.done .track i{background:var(--ok)}
.item.failed .track i{background:var(--bad)}
.item-state{font-size:12px;color:var(--muted)}
.item.done .item-state{color:var(--ok)}
.item.failed .item-state{color:var(--bad)}
@media (max-width:700px){td.time,th.time,td.by,th.by{display:none}.bar{margin-left:0;width:100%}}
</style></head>
<body>
<div class="wrap">
  <header>
    <div class="logo">A</div>
    <div><h1>共享文件夹</h1><div class="sub" id="who"></div></div>
    <div class="bar">
      <button id="mkdir">新建文件夹</button>
      <button class="primary" id="upload">上传文件</button>
      <input type="file" id="picker" multiple hidden>
    </div>
  </header>
  <nav class="crumbs" id="crumbs"></nav>
  <div class="card"><table>
    <thead><tr><th>名称</th><th class="by">上传人</th><th class="time">修改时间</th><th style="text-align:right">大小</th><th></th></tr></thead>
    <tbody id="rows"></tbody>
  </table><div class="empty" id="empty" hidden>这个文件夹是空的。可以把文件拖到这里上传。</div></div>
  <footer>删除和覆盖的内容会保留在被控端的回收文件夹中；所有操作都记入被控端的审计记录。也可以在资源管理器中打开：\\127.0.0.1@<span id="port"></span>\DavWWWRoot</footer>
</div>
<div class="up" id="up">
  <div class="up-head"><span class="up-title" id="upTitle">上传</span><span class="up-sum" id="upSum"></span>
    <button id="upMin" title="收起">–</button><button id="upClose" title="关闭">×</button></div>
  <div class="up-total"><i id="upTotal"></i></div>
  <div class="up-list" id="upList"></div>
</div>
<div class="drop">松开即可上传到当前文件夹</div>
<div class="toast" id="toast"></div>
<script>
let data=/*DATA*/null;
const enc=s=>encodeURIComponent(s);
const base='/'+data.path.map(enc).join('/')+(data.path.length?'/':'');
const $=id=>document.getElementById(id);
const folderIcon='<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="#c96442" stroke-width="1.7"><path d="M3 7.5A1.5 1.5 0 0 1 4.5 6h4.4l2 2h8.6A1.5 1.5 0 0 1 21 9.5v8A1.5 1.5 0 0 1 19.5 19h-15A1.5 1.5 0 0 1 3 17.5z"/></svg>';
const fileIcon='<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="#8a877f" stroke-width="1.7"><path d="M7 3h7l5 5v12a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z"/><path d="M14 3v5h5"/></svg>';
function size(n){if(n<1024)return n+' B';const u=['KB','MB','GB','TB'];let i=-1;do{n/=1024;i++}while(n>=1024&&i<u.length-1);return n.toFixed(n<10?1:0)+' '+u[i]}
function time(sec){if(!isFinite(sec)||sec<0)return '';if(sec<60)return Math.ceil(sec)+' 秒';if(sec<3600)return Math.ceil(sec/60)+' 分钟';return (sec/3600).toFixed(1)+' 小时'}
function esc(s){return String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
function toast(t){const e=$('toast');e.textContent=t;e.classList.add('show');clearTimeout(e._t);e._t=setTimeout(()=>e.classList.remove('show'),2800)}
let fresh=new Set();
function render(){
  $('port').textContent=location.port;
  $('who').textContent='ArdUi · 你是 '+data.me;
  let c='<a href="/">共享文件夹</a>';let href='/';
  data.path.forEach(p=>{href+=enc(p)+'/';c+='<span>/</span><a href="'+href+'">'+esc(p)+'</a>'});
  $('crumbs').innerHTML=c;
  $('empty').hidden=data.entries.length>0;
  $('rows').innerHTML=data.entries.map((e,i)=>'<tr'+(fresh.has(e.name)?' class="fresh"':'')+'><td class="name"><a href="'+base+enc(e.name)+(e.dir?'/':'')+'"'+(e.dir?'':' download')+'>'+(e.dir?folderIcon:fileIcon)+'<span>'+esc(e.name)+'</span></a></td>'+
    '<td class="by">'+(e.by?'<span'+(e.by===data.me?' class="me"':'')+'>'+esc(e.by===data.me?'你':e.by)+'</span>':'—')+'</td>'+
    '<td class="time">'+e.modified+'</td><td class="size">'+(e.dir?'':size(e.size))+'</td>'+
    '<td class="ops"><button data-op="rename" data-i="'+i+'">重命名</button><button class="danger" data-op="delete" data-i="'+i+'">删除</button></td></tr>').join('');
  fresh.clear();
}
async function reload(){try{const r=await fetch(base+'?json',{cache:'no-store'});if(r.ok){data=await r.json();render()}}catch(x){}}
async function call(method,url,headers,body){const r=await fetch(url,{method,headers,body});if(!r.ok)throw new Error(await r.text()||r.status);return r}
$('rows').addEventListener('click',async ev=>{
  const b=ev.target.closest('button');if(!b)return;const e=data.entries[+b.dataset.i];const url=base+enc(e.name)+(e.dir?'/':'');
  try{
    if(b.dataset.op==='delete'){if(!confirm('删除“'+e.name+'”？\n它会移到被控端的回收文件夹，可以找回。'))return;await call('DELETE',url);toast('已删除 '+e.name);reload()}
    else{const n=prompt('新名称',e.name);if(!n||n===e.name)return;await call('MOVE',url,{Destination:location.origin+base+enc(n)+(e.dir?'/':''),Overwrite:'F'});fresh.add(n);reload()}
  }catch(x){toast(x.message)}
});
$('mkdir').onclick=async()=>{const n=prompt('文件夹名称');if(!n)return;try{await call('MKCOL',base+enc(n)+'/');fresh.add(n);reload()}catch(x){toast(x.message)}};

// ---- uploads: one at a time, each with its own row (progress, speed, time left, cancel / retry) ----
const queue=[];let running=null;
function add(files){
  for(const f of files){
    const it={file:f,sent:0,state:'wait',el:document.createElement('div')};it.el.className='item';queue.push(it);$('upList').appendChild(it.el);draw(it);
  }
  $('up').classList.add('show');$('up').classList.remove('min');summary();next();
}
function draw(it){
  const pct=it.file.size?Math.floor(it.sent/it.file.size*100):(it.state==='done'?100:0);
  const st={wait:'等待中',run:pct+'%'+(it.speed?' · '+size(it.speed)+'/s · 剩余 '+time((it.file.size-it.sent)/it.speed):''),done:'已完成',failed:'失败：'+(it.error||''),cancel:'已取消'}[it.state];
  it.el.className='item'+(it.state==='done'?' done':it.state==='failed'||it.state==='cancel'?' failed':'');
  it.el.innerHTML='<div class="item-top"><span class="item-name" title="'+esc(it.file.name)+'">'+esc(it.file.name)+'</span><span class="item-size">'+size(it.file.size)+'</span>'+
    (it.state==='wait'||it.state==='run'?'<button data-a="cancel" title="取消">×</button>':it.state==='failed'||it.state==='cancel'?'<button data-a="retry">重试</button>':'')+'</div>'+
    '<div class="track"><i style="width:'+(it.state==='done'?100:pct)+'%"></i></div><div class="item-state">'+esc(st)+'</div>';
  it.el.onclick=ev=>{const a=ev.target.closest('button')?.dataset.a;if(a==='cancel'){if(it.xhr)it.xhr.abort();it.state='cancel';draw(it);summary();if(running===it){running=null;next()}}
    if(a==='retry'){it.state='wait';it.sent=0;it.error='';draw(it);summary();next()}};
}
function summary(){
  const all=queue.length,done=queue.filter(i=>i.state==='done').length,failed=queue.filter(i=>i.state==='failed').length;
  const total=queue.reduce((a,i)=>a+i.file.size,0)||1,sent=queue.reduce((a,i)=>a+(i.state==='done'?i.file.size:i.sent),0);
  $('upTotal').style.width=(sent/total*100)+'%';
  const active=queue.some(i=>i.state==='wait'||i.state==='run');
  $('upTitle').textContent=active?'正在上传':failed?'上传结束，有失败':'上传完成';
  const speed=running?.speed;
  $('upSum').textContent=done+'/'+all+' 个'+(active&&speed?' · '+size(speed)+'/s · 剩余 '+time((total-sent)/speed):'');
}
function next(){
  if(running)return;const it=queue.find(i=>i.state==='wait');if(!it){summary();return}
  running=it;it.state='run';it.sent=0;it.speed=0;let last={t:performance.now(),b:0};
  const x=new XMLHttpRequest();it.xhr=x;x.open('PUT',base+enc(it.file.name));
  x.upload.onprogress=p=>{it.sent=p.loaded;const now=performance.now();if(now-last.t>400){const s=(p.loaded-last.b)/((now-last.t)/1000);it.speed=it.speed?it.speed*.6+s*.4:s;last={t:now,b:p.loaded}}draw(it);summary()};
  x.onload=()=>{if(x.status<300){it.state='done';it.sent=it.file.size;fresh.add(it.file.name);reload()}else{it.state='failed';it.error=x.responseText||String(x.status)}finish(it)};
  x.onerror=()=>{if(it.state!=='cancel'){it.state='failed';it.error='连接中断'}finish(it)};
  x.onabort=()=>{};
  draw(it);x.send(it.file);
}
function finish(it){it.xhr=null;draw(it);if(running===it)running=null;summary();next()}
$('upMin').onclick=()=>$('up').classList.toggle('min');
$('upClose').onclick=()=>{if(queue.some(i=>i.state==='wait'||i.state==='run')&&!confirm('还有文件在上传，关闭面板会取消它们。'))return;
  queue.forEach(i=>{if(i.xhr)i.xhr.abort();if(i.state==='wait'||i.state==='run')i.state='cancel'});running=null;queue.length=0;$('upList').innerHTML='';$('up').classList.remove('show')};
addEventListener('beforeunload',e=>{if(queue.some(i=>i.state==='wait'||i.state==='run')){e.preventDefault();e.returnValue=''}});
$('upload').onclick=()=>$('picker').click();
$('picker').onchange=()=>{add([...$('picker').files]);$('picker').value=''};
let depth=0;
addEventListener('dragenter',e=>{if(e.dataTransfer.types.includes('Files')){depth++;document.body.classList.add('dragging');e.preventDefault()}});
addEventListener('dragleave',()=>{if(--depth<=0){depth=0;document.body.classList.remove('dragging')}});
addEventListener('dragover',e=>e.preventDefault());
addEventListener('drop',e=>{e.preventDefault();depth=0;document.body.classList.remove('dragging');
  const files=[];let folders=0;
  [...e.dataTransfer.items].forEach(i=>{const entry=i.webkitGetAsEntry&&i.webkitGetAsEntry();if(entry&&entry.isDirectory){folders++;return}const f=i.getAsFile();if(f)files.push(f)});
  if(folders)toast('已跳过 '+folders+' 个文件夹（只能上传文件）');
  if(files.length)add(files)});
render();
</script></body></html>
""";

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        lock (sync) listener?.Stop();
        if (accept != null) try { await accept; } catch { }
        stop.Dispose();
    }
}
