namespace ArdUi;

// The directory routes signed public session descriptions. Passwords and flow
// capabilities only cross ARD after both long-term identity signatures verify.
sealed class DirectoryClient : IAsyncDisposable
{
    sealed class ReconnectJob(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; }=stop;
        public Task Task { get; set; }=Task.CompletedTask;
    }
    readonly Engine engine;
    readonly HttpClient http;
    readonly NSec.Cryptography.Key key;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim edits = new(1), confirmations = new(1);
    readonly OperationGate connects=new();
    readonly Dictionary<string,long> revocations=new();
    long accessRevision;
    readonly object sync = new();
    readonly string path;
    readonly AccessSettings access;
    readonly byte[] enrollmentSecret;
    readonly ConcurrentDictionary<string, Task> jobs = new();
    readonly ConcurrentDictionary<string,ReconnectJob> reconnects=new();
    readonly Dictionary<string, long> seen = new();
    readonly Queue<DateTime> attempts = new();
    readonly Dictionary<string, Queue<DateTime>> attemptsBy = new();
    internal const int PasswordTriesPerDevice = 5, PasswordTriesOverall = 30;   // per minute
    CancellationTokenSource incoming = new();
    // Who is online, learned from the trusted server only; nothing here opens a connection to a device.
    readonly ConcurrentDictionary<string,(PresenceEntry Entry,DateTimeOffset At)> presence=new();
    DateTimeOffset presenceAt;
    Task loop = Task.CompletedTask;
    string code = "";
    bool online;
    public string Code => code;
    public bool Online => online;
    public bool Enabled { get { lock (sync) return access.Enabled; } }
    public bool AlwaysAsk { get { lock (sync) return access.AlwaysAsk; } }
    public void SetAlwaysAsk(bool value) { lock (sync) { access.AlwaysAsk = value; Save(); } Changed?.Invoke(); }
    public string? SharedFolder { get { lock (sync) return access.SharedFolder; } }
    // The shared folder for "文件" (null: none). It must exist; the server follows at once.
    public void SetSharedFolder(string? folder)
    {
        if (folder != null)
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(folder)) throw new IOException("文件夹不存在：" + folder);
            if (Path.GetPathRoot(folder) == folder + Path.DirectorySeparatorChar || Path.GetPathRoot(folder) == folder) throw new IOException("不能共享整个磁盘，请选择一个文件夹。");
        }
        lock (sync) { access.SharedFolder = folder; Save(); }
        engine.Files.Root = folder;
        Changed?.Invoke();
    }
    // Asked before every session when AlwaysAsk is on: does the person at this machine let this controller in now?
    public Func<Pairing, CancellationToken, Task<bool>>? ConfirmSession;
    public double SideWidth { get { lock(sync)return Math.Clamp(access.SideWidth,180,360); } }
    public string AccessPassword=>AccessPasswordFor(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60);
    public Func<Pairing, CancellationToken, Task<bool>>? Confirm { get; set; }
    public event Action? Changed;
    public event Action<string>? Notice;
    public DirectoryClient(Engine engine, string? testServer = null)
    {
        this.engine = engine;
        engine.DirectoryApi=this;
        var server = new Uri(testServer ?? engine.Settings.Server);
        if (server.Scheme != "https" && !(testServer != null && server.IsLoopback && server.Scheme == "http"))
            throw new InvalidDataException("可信服务器必须使用 HTTPS。");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 65536 };
        http.DefaultRequestHeaders.Add("X-ArdUi-Client", Program.WireVersion);   // the server refuses clients older than v5.pre1
        var seed = File.ReadAllBytes(Path.Combine(engine.State.IdentityDirectory, "identity"));
        try { key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, seed, NSec.Cryptography.KeyBlobFormat.RawPrivateKey); }
        finally { CryptographicOperations.ZeroMemory(seed); }
        if (Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant() != engine.Id)
            throw new InvalidDataException("设备密钥与 EndpointId 不匹配。");
        path = Path.Combine(engine.State.Root, "access.json");
        access = File.Exists(path) ? JsonSerializer.Deserialize<AccessSettings>(File.ReadAllText(path), Wire.Json)!
            : new AccessSettings();
        if (access == null || access.Controllers == null || access.Targets == null) throw new InvalidDataException("访问配置损坏。");
        engine.Files.Root = access.SharedFolder is { } shared && Directory.Exists(shared) ? shared : null;
        if(access.EnrollmentSecret==null)
        {
            var secret=RandomNumberGenerator.GetBytes(32);
            try{access.EnrollmentSecret=Convert.ToBase64String(ProtectedData.Protect(secret,null,DataProtectionScope.CurrentUser));Save();}
            finally{CryptographicOperations.ZeroMemory(secret);}
        }
        try{enrollmentSecret=ProtectedData.Unprotect(Convert.FromBase64String(access.EnrollmentSecret),null,DataProtectionScope.CurrentUser);}
        catch(Exception ex){throw new InvalidDataException("首次连接密码密钥损坏。",ex);}
        if(enrollmentSecret.Length!=32){CryptographicOperations.ZeroMemory(enrollmentSecret);throw new InvalidDataException("首次连接密码密钥长度无效。");}
        // A changed directory authority requires a separate trust namespace.
        var authorityPath = Path.Combine(engine.State.Root, "directory.txt");
        if (File.Exists(authorityPath) && File.ReadAllText(authorityPath) != server.AbsoluteUri)
            throw new InvalidDataException("可信服务器已改变。请先恢复原配置，或使用独立应用目录完成重新核对。");
        File.WriteAllText(authorityPath, server.AbsoluteUri);
        LoadNetwork();
    }
    void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(access, Wire.Json));
        File.Move(path + ".tmp", path, true);
    }
    public string AccessPasswordFor(long minute)
    {
        Span<byte> period=stackalloc byte[8];BinaryPrimitives.WriteInt64BigEndian(period,minute);
        var digest=HMACSHA256.HashData(enrollmentSecret,period);
        try{return (BinaryPrimitives.ReadUInt32BigEndian(digest)%1_000_000).ToString("D6");}
        finally{CryptographicOperations.ZeroMemory(digest);}
    }
    // Signs this device's half of a session's key exchange with the device key, for the session with `peer` (see OverlayIdentity).
    public OverlayIdentity OverlayIdentity(string peer) =>
        new(engine.Id, Wire.Endpoint(peer), data => NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(key, data));
    SignedEnvelope Sign(string route, object value)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var unsigned = new SignedEnvelope(engine.Id, now, nonce, payload, "");
        return unsigned with { Signature = Convert.ToBase64String(NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(key, SignedBytes(route, unsigned))) };
    }
    static byte[] SignedBytes(string route, SignedEnvelope value) => Encoding.UTF8.GetBytes(
        $"ArdUiServer/1\n{route}\n{value.Endpoint}\n{value.IssuedAt}\n{value.Nonce}\n{value.Payload}");
    public static T Verify<T>(SignedEnvelope value, string route, string endpoint, bool expiring = true)
    {
        if (Wire.Endpoint(value.Endpoint) != endpoint || (expiring && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - value.IssuedAt) > 360) ||
            value.Payload.Length > 20000 || !Regex.IsMatch(value.Nonce, @"\A[0-9a-f]{32}\z"))
            throw new InvalidDataException("对端身份签名无效或过期。");
        var pub = NSec.Cryptography.PublicKey.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519,
            Convert.FromHexString(endpoint), NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        if (!NSec.Cryptography.SignatureAlgorithm.Ed25519.Verify(pub, SignedBytes(route, value), Convert.FromBase64String(value.Signature)))
            throw new InvalidDataException("对端会话被替换或签名不正确，连接已阻止。");
        return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value.Payload), Wire.Json) ?? throw new InvalidDataException("对端会话为空。");
    }
    public async Task<T> Call<T>(string route, object value, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(Sign(route, value), Wire.Json), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string message;
            try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!; }
            catch { message = "可信服务器暂时不可用。"; }
            if ((int)response.StatusCode == 426) throw new OutdatedClientException(message);
            throw new IOException(message);
        }
        return JsonSerializer.Deserialize<T>(text, Wire.Json) ?? throw new InvalidDataException("服务器响应为空。");
    }
    // The server no longer accepts this version (HTTP 426): nothing works until ArdUi is updated, so say that, not "network trouble".
    public sealed class OutdatedClientException(string message) : IOException(message);
    public string? Outdated { get; private set; }
    public FerryMonitor? Ferries { get; private set; }
    Task? ferryTask;
    // ---- this network's relays: M says where its relay directory and address discovery are, and its key ------------------------
    // Learned from the server (the one domain this client is configured with), kept in data\network.json so that a client that
    // starts while the server is unreachable still knows them. Refreshed hourly.
    string NetworkCache => Path.Combine(engine.State.Root, "network.json");
    DateTimeOffset networkAt;
    void LoadNetwork()
    {
        if (engine.Settings.StaticRelay || !File.Exists(NetworkCache)) return;
        try { engine.Settings.Network = JsonSerializer.Deserialize<NetworkInfo>(File.ReadAllText(NetworkCache), Wire.Json)?.Checked(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { Diagnostics.Log("network-cache", ex.Message); }
    }
    public async Task RefreshNetwork(CancellationToken ct)
    {
        if (engine.Settings.StaticRelay) return;
        using var response = await http.GetAsync("/api/v1/network", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string message;
            try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!; }
            catch { message = "可信服务器没有提供中继信息。"; }
            throw new IOException(message);
        }
        var network = (JsonSerializer.Deserialize<NetworkInfo>(text, Wire.Json) ?? throw new InvalidDataException("可信服务器返回的中继信息为空。")).Checked();
        if (network != engine.Settings.Network)
        {
            engine.Settings.Network = network;
            File.WriteAllText(NetworkCache + ".tmp", JsonSerializer.Serialize(network, Wire.Json));
            File.Move(NetworkCache + ".tmp", NetworkCache, true);
            engine.Events.Add("INFO", "服务器", "中继目录：" + network.RelayDirectory);
        }
        networkAt = DateTimeOffset.UtcNow;
    }
    // A session cannot start without knowing the relays; ask now if this client has never been told.
    async Task EnsureNetwork(CancellationToken ct)
    {
        if (engine.Settings.StaticRelay || engine.Settings.Network != null) return;
        await RefreshNetwork(ct);
    }
    public async Task Register(CancellationToken ct)
    {
        var info = await Call<MachineInfo>("/api/v1/register", new { }, ct);
        if (info.Endpoint != engine.Id || !Regex.IsMatch(info.Code, @"\A[A-Z0-9]{6}\z")) throw new InvalidDataException("服务器返回无效机器编号。");
        var saved = Path.Combine(engine.State.Root, "machine.json");
        if (File.Exists(saved))
        {
            var old = JsonSerializer.Deserialize<MachineInfo>(File.ReadAllText(saved), Wire.Json)!;
            if (old.Endpoint != info.Endpoint || old.Code != info.Code) throw new InvalidDataException("服务器返回的机器编号与本机记录不符，请检查服务器数据恢复情况。");
        }
        File.WriteAllText(saved, JsonSerializer.Serialize(info, Wire.Json));
        code = info.Code; online = true; Changed?.Invoke();
        try { await RefreshAccount(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Log("account", ex.Message); }
    }

    // ---- the account -------------------------------------------------------------------------------------------------
    // A device is anonymous until it signs in with a user an administrator of this M made. Only a signed-in device may connect to
    // (control) another one; M refuses an anonymous controller too. Being controlled needs no account.
    sealed record AccountReply(string? Username, bool Admin);
    public string? Username { get; private set; }
    // false until this M answered the account question: an M without accounts (older) lets every device connect, and this side
    // does not stop its own connections then
    public bool AccountsKnown { get; private set; }
    void Apply(AccountReply reply) { Username = reply.Username; AccountsKnown = true; Changed?.Invoke(); }
    public const string LoginFirst = "请先登录：只有注册用户可以连接其他设备（账号由管理员创建）。";
    public async Task RefreshAccount(CancellationToken ct) => Apply(await Call<AccountReply>("/api/v1/account", new { }, ct));
    public async Task Login(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0 || string.IsNullOrEmpty(password)) throw new InvalidOperationException("请输入用户名和密码。");
        Apply(await Call<AccountReply>("/api/v1/account/login", new { username, password }, ct));
    }
    public event Action? LoginNeeded;   // a connection the person started needs this device signed in
    // Signing in (and registering) happens on this M's account page in a browser: the device gets a one-time request, the page is
    // opened with it, and the device waits until the person signed it in there (or the request ran out).
    sealed record LoginStart(string Token, string Url, long Expires);
    public async Task<(string Token, Uri Page, DateTimeOffset Until)> StartWebLogin(CancellationToken ct = default)
    {
        var start = await Call<LoginStart>("/api/v1/account/login-start", new { }, ct);
        if (!Regex.IsMatch(start.Token, @"\A[A-Za-z0-9_-]{16,64}\z") || !start.Url.StartsWith("/api/account?login=", StringComparison.Ordinal))
            throw new InvalidDataException("服务器返回的登录请求无效。");
        return (start.Token, new Uri(http.BaseAddress!, start.Url), DateTimeOffset.FromUnixTimeSeconds(start.Expires));
    }
    public async Task<bool> WaitWebLogin(string token, DateTimeOffset until, CancellationToken ct = default)
    {
        while (DateTimeOffset.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await Call<JsonElement>("/api/v1/account/login-wait", new { token }, ct);   // held by M until something happens
            if (reply.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String)
            { Apply(new AccountReply(name.GetString(), reply.TryGetProperty("admin", out var admin) && admin.ValueKind == JsonValueKind.True)); return true; }
        }
        return false;
    }
    // The account page (registering: "#register", changing the password: "#password").
    public Uri AccountPage(string part = "") => new(http.BaseAddress!, "/api/account" + part);
    public async Task Logout(CancellationToken ct = default) => Apply(await Call<AccountReply>("/api/v1/account/logout", new { }, ct));
    public async Task ChangePassword(string old, string password, CancellationToken ct = default)
    {
        if ((password ?? "").Length < 8) throw new InvalidOperationException("新密码至少 8 个字符。");
        await Call<JsonElement>("/api/v1/account/password", new { old, @new = password }, ct);
    }
    // The loop (network calls, signing, file writes) runs on the thread pool; UI code only receives its events.
    public void Start(){engine.Events.Add("INFO","服务器","正在连接可信服务器 "+engine.Settings.Server);loop=Task.Run(Run);}
    // null = unknown (server unreachable, old server, or no fresh answer yet).
    public bool? IsOnline(string endpoint)
    {
        if(!presence.TryGetValue(endpoint,out var seen)||DateTimeOffset.UtcNow-seen.At>TimeSpan.FromSeconds(60))return null;
        return seen.Entry.Online;
    }
    public bool IsReconnecting(string endpoint)=>reconnects.ContainsKey(endpoint);
    async Task RefreshPresence(CancellationToken ct)
    {
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.Where(p=>p.Grant!=null).Take(64).ToArray();
        if(peers.Length==0)return;
        var reply=await Call<PresenceReply>("/api/v1/presence",new{peers=peers.Select(p=>new{endpoint=p.Id,grant=p.Grant}).ToArray()},ct);
        var now=DateTimeOffset.UtcNow;var changed=false;
        foreach(var peer in peers)
        {
            if(!reply.Peers.TryGetValue(peer.Id,out var entry))continue;
            presence[peer.Id]=(entry,now);
            if(entry.Name.Length!=0&&peer.ComputerName!=entry.Name){peer.ComputerName=entry.Name;changed=true;}
        }
        if(changed)engine.State.Save();
        Changed?.Invoke();
    }
    void EnsureReconnect(Peer peer)
    {
        if(!peer.AutoConnect||stop.IsCancellationRequested||reconnects.ContainsKey(peer.Id))return;
        var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);var job=new ReconnectJob(linked);
        if(!reconnects.TryAdd(peer.Id,job)){linked.Dispose();return;}
        job.Task=Supervise(peer,linked.Token);_=CleanupReconnect(peer.Id,job);
    }
    async Task CleanupReconnect(string id,ReconnectJob job)
    {
        try{await job.Task;}catch{}
        finally{if(reconnects.TryRemove(new KeyValuePair<string,ReconnectJob>(id,job)))job.Stop.Dispose();}
    }
    async Task Supervise(Peer peer,CancellationToken ct)
    {
        var delay=1;var announced=false;
        while(!ct.IsCancellationRequested&&peer.AutoConnect)
        {
            if(engine.Outgoing.TryGetValue(peer.Id,out var active)&&active.Live)
            {
                try{await active.Completion.WaitAsync(ct);}catch(OperationCanceledException){break;}
                continue;
            }
            // Never ask the server to wake a device that is known to be offline.
            if(IsOnline(peer.Id)==false)
            {
                engine.SetStatus(peer.Id,"对方离线 · 上线后自动恢复");
                try{await Task.Delay(TimeSpan.FromSeconds(5),ct);}catch(OperationCanceledException){break;}
                continue;
            }
            engine.SetStatus(peer.Id,delay==1?"正在自动恢复连接…":$"自动重连等待 {delay} 秒…");
            try
            {
                await ConnectOnce(peer.Code,"",false,ct,true);delay=1;announced=false;continue;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                engine.SetStatus(peer.Id,"自动重连中 · "+ex.Message);
                if(!announced){Notice?.Invoke(peer.Code+" 自动重连中："+ex.Message);announced=true;}
            }
            try{await Task.Delay(TimeSpan.FromSeconds(delay),ct);}catch(OperationCanceledException){break;}
            delay=Math.Min(delay*2,30);
        }
    }
    async Task StopReconnect(string id)
    {
        if(!reconnects.TryRemove(id,out var job))return;
        job.Stop.Cancel();try{await job.Task;}catch{}finally{job.Stop.Dispose();}
    }
    async Task Run()
    {
        var announced = false;
        while (!stop.IsCancellationRequested)
        {
            var waited = false;
            try
            {
                if (code.Length == 0)
                {
                    await Register(stop.Token);
                    if (Enabled) await Call<JsonElement>("/api/v1/access", new { enabled = true }, stop.Token);
                }
                if (engine.Settings.Network == null || DateTimeOffset.UtcNow - networkAt >= TimeSpan.FromHours(1))
                {
                    // the cached value keeps working if this fails; a missing one is reported when a connection needs it
                    try { await RefreshNetwork(stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
                    { networkAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(55); Diagnostics.Log("network-refresh", ex.Message); }
                }
                await Poll(stop.Token);
                waited = true;
                Outdated = null;
                // once the network's relays are known: measure the ArdFerry nodes from here and report to M
                if (ferryTask == null && engine.Settings.Network != null)
                {
                    Ferries = new FerryMonitor(engine, (route, value, ct) => Call<JsonElement>(route, value, ct));
                    ferryTask = Task.Run(() => Ferries.Run(stop.Token));
                }
                // The status line keeps the last notice, so recovery must replace the earlier failure text.
                if (announced) { Notice?.Invoke("网络已恢复"); engine.Events.Add("INFO", "服务器", "可信服务器连接已恢复"); }
                online = true; announced = false; Changed?.Invoke();
                if (DateTimeOffset.UtcNow - presenceAt >= TimeSpan.FromSeconds(15))
                {
                    presenceAt = DateTimeOffset.UtcNow;
                    // Presence is informational: a failure (or an older server) leaves devices "unknown", never offline.
                    try { await RefreshPresence(stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch { }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (OutdatedClientException ex)
            {
                // refused for being too old: tell the person once, plainly, and ask the server again only now and then
                online = false;
                if (Outdated == null) { Outdated = ex.Message; Notice?.Invoke(ex.Message); engine.Events.Add("ERROR", "服务器", "可信服务器拒绝了此版本：" + ex.Message); }
                Changed?.Invoke();
                try { await Task.Delay(TimeSpan.FromMinutes(5), stop.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
            catch (Exception ex)
            { online = false; Changed?.Invoke(); if (!announced) { Notice?.Invoke("网络不稳定，正在重试…"); engine.Events.Add("ERROR", "服务器", "无法连接可信服务器，每 3 秒重试一次：" + ex.Message); } announced = true; }
            if (waited) continue;   // the server held the poll until there was news (or 12 s): ask again right away
            try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Poll(CancellationToken ct)
    {
        // The server holds the poll until a connection request arrives that is not among `known` (the ones being handled here)
        // or the access switch changes, or for 12 s. It only reads the switch (changed through /access), so this poll does not
        // take the lock that access changes take: switching access is never kept waiting by it.
        {
            var known = seen.Keys.Take(16).ToArray();
            var reply = await Call<PollReply>("/api/v1/poll", new { enabled = Enabled, name = Environment.MachineName, known }, ct);
            if (!Enabled) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var old in seen.Where(p => p.Value < now).Select(p => p.Key).ToArray()) seen.Remove(old);
            foreach (var ticket in reply.Tickets.Take(16))
            {
                if (seen.ContainsKey(ticket.Id) || jobs.Count >= 16) continue;
                seen[ticket.Id] = ticket.Expires;
                var token = incoming.Token;
                var task = Handle(ticket, token); jobs[ticket.Id] = task;
                _ = task.ContinueWith(t => jobs.TryRemove(ticket.Id, out _), TaskScheduler.Default);
            }
        }
    }
    // Controllers that asked to connect and whose session is still being set up (shown as "connecting" before a session exists).
    readonly Dictionary<string, string> arriving = new();
    public (string Endpoint, string Code)[] Arriving { get { lock (sync) return arriving.Select(p => (p.Key, p.Value)).ToArray(); } }
    async Task Handle(ServerTicket ticket, CancellationToken ct)
    {
        lock (sync) { arriving[ticket.ControllerEndpoint] = ticket.ControllerCode; seenCodes[ticket.ControllerEndpoint] = ticket.ControllerCode; }
        Changed?.Invoke();
        try
        {
            if (!Enabled || ticket.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;
            var request = Verify<ServerRequest>(ticket.Proof, "/api/v1/connect", Wire.Endpoint(ticket.ControllerEndpoint));
            // the controller chose the session's ArdFerry and signed it into its request: this side uses the same one
            var ferry = engine.Settings.StaticRelay ? null : CheckedFerry(request.Ferry);
            if (request.TargetEndpoint != engine.Id || request.Code != Code || request.SessionId != ticket.ClientSessionId || request.RequestId != ticket.Id || request.Expires != ticket.Expires)
                throw new InvalidDataException("连接请求与设备签名不匹配。");
            if (!ClientVersion.Allowed(request.Client))
                throw new InvalidDataException($"对方的 ArdUi 版本过旧（需要 {ClientVersion.Minimum} 或更高），已拒绝其接入。");
            // the host's share of the set-up time, next to the controller's stages (Lookup, Verify, Signal, Link)
            engine.Events.Add("INFO", "接入", ticket.ControllerCode + " · 收到连接请求");
            await EnsureNetwork(ct);
            var sessionId = await engine.AcceptServerSession(ticket, ferry, (request, token) => AuthorizePassword(ticket, request, token), ct);
            engine.Events.Add("INFO", "接入", ticket.ControllerCode + " · 本机就绪");
            await Call<JsonElement>($"/api/v1/tickets/{ticket.Id}/ready",
                new ServerOffer(sessionId, ticket.Id, ticket.ControllerEndpoint, engine.Id, ticket.ClientSessionId, ticket.Expires), ct);
            engine.Events.Add("INFO", "接入", ticket.ControllerCode + " · 已通知 M");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Notice?.Invoke(ex.Message);
            try { await Call<JsonElement>($"/api/v1/tickets/{ticket.Id}/reject", new { }, stop.Token); } catch { }
        }
        finally
        {
            lock (sync) arriving.Remove(ticket.ControllerEndpoint);
            Changed?.Invoke();
        }
    }
    // The code a controller connected with, for sessions that are not saved (accepted only once).
    readonly Dictionary<string, string> seenCodes = new();
    public string CodeOf(string endpoint)
    {
        lock (sync)
        {
            if (access.Controllers.TryGetValue(endpoint, out var saved)) return saved.Code;
            if (temporary.TryGetValue(endpoint, out var once)) return once.Code;
            return seenCodes.GetValueOrDefault(endpoint) ?? endpoint[..8];
        }
    }
    // The person at this machine decides: accept at all, only this once, and which permissions the controller gets.
    public Func<Pairing, CancellationToken, Task<Decision>>? ConfirmIncoming;
    // Controllers accepted only this once: valid while their session lives, never saved.
    readonly Dictionary<string, ControllerGrant> temporary = new();
    async Task<bool> AskEverySession(ServerTicket ticket, CancellationToken ct)
    {
        if (!AlwaysAsk) return true;
        if (ConfirmSession == null) return false;
        await confirmations.WaitAsync(ct);   // one prompt at a time, like the first-connection approval
        try { return await ConfirmSession(new Pairing(ticket.ControllerCode, ticket.ControllerEndpoint, true), ct); }
        finally { confirmations.Release(); }
    }
    // After a prompt the answer may come late: the device must still be trusted, access still on, and no revocation since the request began.
    bool StillGranted(string controller, long revision)
    {
        lock (sync) return access.Enabled && access.Controllers.ContainsKey(controller) && revocations.GetValueOrDefault(controller) == revision;
    }
    async Task<Authorization?> AuthorizePassword(ServerTicket ticket, PasswordRequest request, CancellationToken ct)
    {
        long revision;
        Authorization? returning=null;
        lock(sync)
        {
            revision=revocations.GetValueOrDefault(ticket.ControllerEndpoint);
            if(!access.Enabled) return null;
            if(!request.Enroll)
            {
                if(!access.Controllers.TryGetValue(ticket.ControllerEndpoint,out var saved)) return null;
                returning=new Authorization(saved.Receipt,Permissions.Parse(saved.Perms),false);
            }
        }
        if(returning!=null) return await AskEverySession(ticket,ct) && StillGranted(ticket.ControllerEndpoint,revision) ? returning : null;
        lock (sync)
        {
            // Password guesses are limited per device, so one device cannot use up the attempts of everyone else; a wider limit across all
            // devices still bounds a guesser that keeps making new identities.
            var now = DateTime.UtcNow;
            static void Expire(Queue<DateTime> queue, DateTime now) { while (queue.TryPeek(out var time) && time < now.AddMinutes(-1)) queue.Dequeue(); }
            Expire(attempts, now);
            foreach (var idle in attemptsBy.Where(p => { Expire(p.Value, now); return p.Value.Count == 0; }).Select(p => p.Key).ToArray()) attemptsBy.Remove(idle);
            var mine = attemptsBy.TryGetValue(ticket.ControllerEndpoint, out var queue) ? queue : attemptsBy[ticket.ControllerEndpoint] = new();
            if (!access.Enabled || mine.Count >= PasswordTriesPerDevice || attempts.Count >= PasswordTriesOverall || !Regex.IsMatch(request.Password,@"\A[0-9]{6}\z")) return null;
            attempts.Enqueue(now); mine.Enqueue(now);
        }
        var supplied=Encoding.ASCII.GetBytes(request.Password);var valid=false;
        try
        {
            var minute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;
            for(var offset=0;offset<=1;offset++)
            {
                var expected=Encoding.ASCII.GetBytes(AccessPasswordFor(minute-offset));
                try{valid|=CryptographicOperations.FixedTimeEquals(supplied,expected);}finally{CryptographicOperations.ZeroMemory(expected);}
            }
        }
        finally{CryptographicOperations.ZeroMemory(supplied);}
        if (!valid) return null;
        await confirmations.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            Authorization? knownAuth=null;
            lock (sync) if (access.Enabled && access.Controllers.TryGetValue(ticket.ControllerEndpoint,out var known)) knownAuth=new Authorization(known.Receipt,Permissions.Parse(known.Perms),false);
            if (knownAuth != null)
            {
                if (AlwaysAsk && !(ConfirmSession != null && await ConfirmSession(new Pairing(ticket.ControllerCode, ticket.ControllerEndpoint, true), ct))) return null;
                return StillGranted(ticket.ControllerEndpoint, revision) ? knownAuth : null;
            }
            var decision = ConfirmIncoming == null ? Decision.Rejected : await ConfirmIncoming(new Pairing(ticket.ControllerCode, ticket.ControllerEndpoint, true), ct);
            if (!decision.Accepted) return null;
            ct.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!access.Enabled||revocations.GetValueOrDefault(ticket.ControllerEndpoint)!=revision) return null;
                var receipt = Sign("/grant/v1",new GrantReceipt(ticket.ControllerEndpoint,engine.Id,Guid.NewGuid().ToString("N")));
                var grant = new ControllerGrant(ticket.ControllerCode, receipt, Permissions.Store(decision.Perms));
                foreach (var gone in temporary.Keys.Where(k => !engine.Incoming.ContainsKey(k)).ToArray()) temporary.Remove(gone);
                if (decision.Once) temporary[ticket.ControllerEndpoint] = grant;
                else { access.Controllers[ticket.ControllerEndpoint] = grant; Save(); Changed?.Invoke(); }
                return new Authorization(receipt, decision.Perms, decision.Once);
            }
        }
        finally { confirmations.Release(); }
    }
    public async Task SetAccess(bool enabled, CancellationToken ct = default)
    {
        var revision=Interlocked.Increment(ref accessRevision);
        lock(sync){access.Enabled=false;Save();incoming.Cancel();}
        Changed?.Invoke();
        await engine.StopIncoming();
        await edits.WaitAsync(ct);
        try
        {
            await Task.WhenAll(jobs.Values);
            if(revision!=Volatile.Read(ref accessRevision))return;
            lock(sync){incoming.Dispose();incoming=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);}
            if (code.Length == 0) await Register(ct);
            await Call<JsonElement>("/api/v1/access", new { enabled }, ct);
            lock (sync) { if(revision!=Volatile.Read(ref accessRevision))return;access.Enabled = enabled; Save(); }
            Changed?.Invoke();
        }
        finally { edits.Release(); }
    }
    async Task<FerryChoice> ChooseFerry(CancellationToken ct)
    {
        await EnsureNetwork(ct);
        var monitor = Ferries ?? new FerryMonitor(engine, (route, value, token) => Call<JsonElement>(route, value, token));
        return await monitor.ChooseNow(ct) ?? throw new IOException("没有可用的 ArdFerry：刚测过的中转节点都连不上，请检查网络。");
    }
    static readonly Regex FerryNode = new(@"\A[A-Za-z0-9_-]{1,64}\z"), FerryKey = new(@"\Aspki:[0-9a-f]{2,512}\z");
    static FerryChoice CheckedFerry(FerryChoice? ferry)
    {
        if (ferry == null || !FerryNode.IsMatch(ferry.Node) || !FerryKey.IsMatch(ferry.Key) || ferry.Addresses is not { Length: >= 1 and <= 8 }
            || ferry.Addresses.Any(a => !System.Net.IPEndPoint.TryParse(a, out var e) || e.Port == 0))
            throw new InvalidDataException("对方没有指定有效的 ArdFerry，请升级两端 ArdUi。");
        return ferry;
    }
    // Spawning ard.exe and the session directory work are synchronous at the start of a connection, so keep them off the UI thread.
    // Raised as a connection moves through its stages, for the progress display.
    public event Action<string,ConnectStage>? Stage;
    void Report(string machine,ConnectStage stage){engine.Events.Add("INFO","连接",machine+" · "+stage);Stage?.Invoke(machine,stage);}
    public Task Connect(string machine,string password,CancellationToken ct=default,bool enrolling=true)=>Task.Run(()=>ConnectCore(machine,password,ct,enrolling),ct);
    async Task ConnectCore(string machine,string password,CancellationToken ct,bool enrolling)
    {
        engine.Events.Add("INFO","连接","正在连接 "+machine+(enrolling?"（首次授权）":""));
        try{await ConnectOnce(machine,password,enrolling,ct,false);}
        catch(Exception ex)when(ex is not OperationCanceledException){engine.Events.Add("ERROR","连接","连接 "+machine+" 失败："+ex.Message);throw;}
        engine.Events.Add("INFO","连接","已连接 "+machine);Stage?.Invoke(machine,ConnectStage.Done);
        machine=Wire.Machine(machine);Peer peer;lock(engine.State.Peers)peer=engine.State.Peers.Single(p=>p.Code==machine);
        EnsureReconnect(peer);Changed?.Invoke();
    }
    async Task ConnectOnce(string machine,string password,bool enrolling,CancellationToken ct,bool background)
    {
        machine=Wire.Machine(machine);
        using var operation=await connects.Enter(machine,ct);
        try{await ConnectOnceCore(machine,password,enrolling,operation.Token,background);}
        catch(Exception ex)when(ex.Message.Contains("请先登录",StringComparison.Ordinal))
        {
            // anonymous (or no longer signed in, as M says): the person is asked to sign in, not a background reconnect
            if(!ex.Message.StartsWith(LoginFirst,StringComparison.Ordinal)){Username=null;AccountsKnown=true;Changed?.Invoke();}
            if(!background)LoginNeeded?.Invoke();
            throw;
        }
        operation.Commit(()=>
        {
            lock(engine.State.Peers)
            {
                var peer=engine.State.Peers.Single(p=>p.Code==machine);
                peer.LastConnectedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if(!background&&!peer.Temporary)peer.AutoConnect=true;   // only a connection the user asked for is kept alive afterwards (never a one-time device)
                engine.State.Save();
            }
        });
    }
    async Task ConnectOnceCore(string machine,string password,bool enrolling,CancellationToken ct,bool background)
    {
        machine = Wire.Machine(machine);
        password=password.Trim();
        if (code.Length == 0) await Register(ct);
        if (machine == Code) throw new InvalidOperationException("不能连接本机。");
        await EnsureNetwork(ct);
        if (AccountsKnown && Username == null) throw new InvalidOperationException(LoginFirst);   // M refuses it too; saying so here saves the round trips
        Report(machine,ConnectStage.Lookup);
        var target = await Call<MachineInfo>("/api/v1/lookup", new { code = machine }, ct);
        if (target.Code != machine) throw new InvalidDataException("机器编号不匹配。");
        Wire.Endpoint(target.Endpoint);
        string? pinned;
        lock (sync) access.Targets.TryGetValue(machine, out pinned);
        if (pinned != null && pinned != target.Endpoint) throw new InvalidDataException("该机器的 EndpointId 已改变！已阻止连接，请通过独立渠道核对，不能自动信任服务器的新映射。");
        Report(machine,ConnectStage.Verify);
        if (pinned == null)
        {
            if (Confirm == null || !await Confirm(new Pairing(machine, target.Endpoint, false), ct)) throw new OperationCanceledException("尚未核对对端 EndpointId。");
            lock (sync) { access.Targets[machine] = target.Endpoint; Save(); }
        }
        if(engine.Outgoing.TryGetValue(target.Endpoint,out var active))
        {
            if(active.Live)return;
            await engine.DropOutgoing(target.Endpoint);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(background?TimeSpan.FromSeconds(75):TimeSpan.FromMinutes(5));
        var dir = engine.State.NewSessionDirectory();
        var transferred = false;
        try
        {
            var local = await Ard.Identity(dir, timeout.Token);
            // the session's ArdFerry: this side chooses it from its own measurements; the host uses the same one
            var ferry = engine.Settings.StaticRelay ? null : await ChooseFerry(timeout.Token);
            var request = new ServerRequest(machine,target.Endpoint,local,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddSeconds(background?60:300).ToUnixTimeSeconds(),Program.WireVersion,ferry);
            Report(machine,ConnectStage.Signal);
            await Call<JsonElement>("/api/v1/connect", request, timeout.Token);
            ServerOffer? offer = null;
            while (offer == null)
            {
                // held by the server until the host answers (or 12 s): no pause between the looks
                var status = await Call<TicketReply>($"/api/v1/tickets/{request.RequestId}", new { }, timeout.Token);
                if (status.Status == "rejected") throw new IOException("对端未接受连接，请检查允许访问开关。");
                if (status.Status != "ready") continue;
                offer = Verify<ServerOffer>(status.Offer ?? throw new InvalidDataException("缺少对端签名。"), $"/api/v1/tickets/{request.RequestId}/ready", target.Endpoint);
                if (offer.RequestId != request.RequestId || offer.ControllerEndpoint != engine.Id || offer.TargetEndpoint != target.Endpoint || offer.ClientSessionId != local || offer.Expires != request.Expires)
                    throw new InvalidDataException("对端签名没有绑定当前会话，已阻止连接。");
                Wire.Endpoint(offer.SessionId);
            }
            transferred = true;
            Report(machine,ConnectStage.Link);
            await engine.ConnectServerSession(target, dir, offer, ferry, enrolling, password, timeout.Token);
        }
        finally { if (!transferred) try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
    public KeyValuePair<string,ControllerGrant>[] Controllers { get { lock(sync) return access.Controllers.ToArray(); } }
    // Changing what a saved controller may use ends its running session; it reconnects with the new permissions.
    public async Task SetControllerPerms(string endpoint, Perm perms)
    {
        lock(sync)
        {
            if(!access.Controllers.TryGetValue(endpoint,out var grant))throw new IOException("该设备已没有授权。");
            access.Controllers[endpoint]=grant with{Perms=Permissions.Store(perms)};Save();
        }
        var live=engine.Incoming.ContainsKey(endpoint);
        if(live){await engine.DropIncoming(endpoint);engine.SetStatus(endpoint,"权限已更改，对方需要重新连接");}
        Changed?.Invoke();
    }
    public async Task Revoke(string endpoint)
    {
        lock(sync){revocations[endpoint]=revocations.GetValueOrDefault(endpoint)+1;access.Controllers.Remove(endpoint);temporary.Remove(endpoint);Save();}
        await engine.DropIncoming(endpoint);
        Changed?.Invoke();
    }
    public async Task RemoveLocal(Peer peer)
    {
        peer.AutoConnect=false;engine.State.Save();connects.Cancel(peer.Code);await engine.RemoveOutgoing(peer.Id);await StopReconnect(peer.Id);
        engine.State.Remove(peer.Id);
        lock(sync){access.Targets.Remove(peer.Code);Save();}
        Changed?.Invoke();
    }
    public void RestoreLocal(Peer snapshot)
    {
        if(snapshot.Grant==null)throw new InvalidDataException("设备授权记录不完整，无法撤销移除。");
        var peer=engine.State.GetOrAdd(snapshot.Id,snapshot.Code,snapshot.Grant);
        peer.Name=snapshot.Name;peer.Group=snapshot.Group;peer.ComputerName=snapshot.ComputerName;
        peer.LastConnectedAt=snapshot.LastConnectedAt;peer.AutoConnect=false;engine.State.Save();
        lock(sync){access.Targets[peer.Code]=peer.Id;Save();}
        Changed?.Invoke();
    }
    public void SetSideWidth(double width)
    {
        lock(sync){access.SideWidth=Math.Clamp(width,180,360);Save();}
    }
    // The host ended the session on purpose: stop recovering it (like Pause), and say why.
    public async Task PeerHungUp(string id,string text)
    {
        Peer? peer;lock(engine.State.Peers)peer=engine.State.Peers.Find(p=>p.Id==id);
        if(peer==null)return;
        peer.AutoConnect=false;engine.State.Save();connects.Cancel(peer.Code);await engine.DropOutgoing(peer.Id);await StopReconnect(peer.Id);
        engine.SetStatus(peer.Id,"已授权 · "+text+"，已暂停自动重连");Changed?.Invoke();
    }
    public async Task Pause(Peer peer)
    {
        peer.AutoConnect=false;engine.State.Save();connects.Cancel(peer.Code);await engine.Goodbye(peer.Id);await engine.DropOutgoing(peer.Id);await StopReconnect(peer.Id);
        engine.SetStatus(peer.Id,"已授权 · 已暂停");Changed?.Invoke();
    }
    public void ExportIdentity(string destination, string password)
    {
        if (password.Length < 12) throw new InvalidDataException("备份密码至少需要 12 个字符，请妥善保管。");
        var seed = File.ReadAllBytes(Path.Combine(engine.State.IdentityDirectory, "identity"));
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[32]; var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(derived, 16); aes.Encrypt(nonce, seed, cipher, tag, "ArdUi identity backup v1"u8);
            File.WriteAllText(destination, JsonSerializer.Serialize(new IdentityBackup(1, engine.Id, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)), Wire.Json));
        }
        finally { CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(derived); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); incoming.Cancel();
        await connects.Close();
        foreach(var job in reconnects.Values)job.Stop.Cancel();
        try { await loop; } catch { }
        if (ferryTask != null) try { await ferryTask; } catch { }
        await engine.StopIncoming();
        try { await Task.WhenAll(jobs.Values); } catch { }
        try{await Task.WhenAll(reconnects.Values.Select(j=>j.Task));}catch{}
        foreach(var job in reconnects.Values)job.Stop.Dispose();reconnects.Clear();
        CryptographicOperations.ZeroMemory(enrollmentSecret);http.Dispose();key.Dispose();incoming.Dispose();stop.Dispose();
    }
}
