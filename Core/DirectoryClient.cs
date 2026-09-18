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
    readonly object sync = new();
    readonly string path;
    readonly AccessSettings access;
    readonly byte[] enrollmentSecret;
    readonly ConcurrentDictionary<string, Task> jobs = new();
    readonly ConcurrentDictionary<string,ReconnectJob> reconnects=new();
    readonly Dictionary<string, long> seen = new();
    readonly Queue<DateTime> attempts = new();
    CancellationTokenSource incoming = new();
    Task loop = Task.CompletedTask;
    string code = "";
    bool online;
    public string Code => code;
    public bool Online => online;
    public bool Enabled { get { lock (sync) return access.Enabled; } }
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
        var seed = File.ReadAllBytes(Path.Combine(engine.State.IdentityDirectory, "identity"));
        try { key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, seed, NSec.Cryptography.KeyBlobFormat.RawPrivateKey); }
        finally { CryptographicOperations.ZeroMemory(seed); }
        if (Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant() != engine.Id)
            throw new InvalidDataException("设备密钥与 EndpointId 不匹配。");
        path = Path.Combine(engine.State.Root, "access.json");
        access = File.Exists(path) ? JsonSerializer.Deserialize<AccessSettings>(File.ReadAllText(path), Wire.Json)!
            : new AccessSettings();
        if (access == null || access.Controllers == null || access.Targets == null) throw new InvalidDataException("访问配置损坏。");
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
    public SignedEnvelope ApproveTransit(string route,string controller,string candidate,string aSession,string bSession)
    {
        lock(sync)
        {
            if(!access.Enabled||!access.Controllers.ContainsKey(controller))throw new IOException("当前已无被控授权。");
            return Sign("/transit-approval/v2",new{route,a=controller,b=engine.Id,c=candidate,aSession,bSession,expires=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+90});
        }
    }
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
            throw new IOException(message);
        }
        return JsonSerializer.Deserialize<T>(text, Wire.Json) ?? throw new InvalidDataException("服务器响应为空。");
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
    }
    public void Start()=>loop=Run();
    void StartReconnects()
    {
        Peer[] peers;lock(engine.State.Peers)peers=engine.State.Peers.Where(p=>p.AutoConnect).ToArray();
        foreach(var peer in peers)EnsureReconnect(peer);
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
            try
            {
                if (code.Length == 0)
                {
                    await Register(stop.Token);
                    if (Enabled) await Call<JsonElement>("/api/v1/access", new { enabled = true }, stop.Token);
                }
                await Poll(stop.Token);
                StartReconnects();
                online = true; announced = false; Changed?.Invoke();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            { online = false; Changed?.Invoke(); if (!announced) Notice?.Invoke(ex.Message); announced = true; }
            try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Poll(CancellationToken ct)
    {
        // Serialize access changes with heartbeats: a stale disabled poll must not undo enable.
        await edits.WaitAsync(ct);
        try
        {
            var reply = await Call<PollReply>("/api/v1/poll", new { enabled = Enabled }, ct);
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
        finally { edits.Release(); }
    }
    async Task Handle(ServerTicket ticket, CancellationToken ct)
    {
        try
        {
            if (!Enabled || ticket.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;
            var request = Verify<ServerRequest>(ticket.Proof, "/api/v1/connect", Wire.Endpoint(ticket.ControllerEndpoint));
            if (request.TargetEndpoint != engine.Id || request.Code != Code || request.SessionId != ticket.ClientSessionId || request.RequestId != ticket.Id || request.Expires != ticket.Expires)
                throw new InvalidDataException("连接请求与设备签名不匹配。");
            var sessionId = await engine.AcceptServerSession(ticket, (request, token) => AuthorizePassword(ticket, request, token), ct);
            await Call<JsonElement>($"/api/v1/tickets/{ticket.Id}/ready",
                new ServerOffer(sessionId, ticket.Id, ticket.ControllerEndpoint, engine.Id, ticket.ClientSessionId, ticket.Expires), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Notice?.Invoke(ex.Message);
            try { await Call<JsonElement>($"/api/v1/tickets/{ticket.Id}/reject", new { }, stop.Token); } catch { }
        }
    }
    async Task<SignedEnvelope?> AuthorizePassword(ServerTicket ticket, PasswordRequest request, CancellationToken ct)
    {
        lock(sync)
        {
            if(!access.Enabled) return null;
            if(!request.Enroll) return access.Controllers.TryGetValue(ticket.ControllerEndpoint,out var saved) ? saved.Receipt : null;
        }
        lock (sync)
        {
            var now = DateTime.UtcNow;
            while (attempts.TryPeek(out var time) && time < now.AddMinutes(-1)) attempts.Dequeue();
            if (!access.Enabled || attempts.Count >= 10 || !Regex.IsMatch(request.Password,@"\A[0-9]{6}\z")) return null;
            attempts.Enqueue(now);
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
            lock (sync) if (access.Enabled && access.Controllers.TryGetValue(ticket.ControllerEndpoint,out var known)) return known.Receipt;
            if (Confirm == null || !await Confirm(new Pairing(ticket.ControllerCode, ticket.ControllerEndpoint, true), ct)) return null;
            ct.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!access.Enabled) return null;
                var receipt = Sign("/grant/v1",new GrantReceipt(ticket.ControllerEndpoint,engine.Id,Guid.NewGuid().ToString("N")));
                access.Controllers[ticket.ControllerEndpoint] = new ControllerGrant(ticket.ControllerCode,receipt); Save(); Changed?.Invoke();
                return receipt;
            }
        }
        finally { confirmations.Release(); }
    }
    public async Task SetAccess(bool enabled, CancellationToken ct = default)
    {
        await edits.WaitAsync(ct);
        try
        {
            lock (sync) { access.Enabled = false; Save(); }
            incoming.Cancel();
            await engine.StopIncoming();
            await Task.WhenAll(jobs.Values);
            incoming.Dispose(); incoming = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            if (code.Length == 0) await Register(ct);
            await Call<JsonElement>("/api/v1/access", new { enabled }, ct);
            lock (sync) { access.Enabled = enabled; Save(); }
            Changed?.Invoke();
        }
        finally { edits.Release(); }
    }
    public async Task Connect(string machine,string password,CancellationToken ct=default,bool enrolling=true)
    {
        await ConnectOnce(machine,password,enrolling,ct,false);
        machine=Wire.Machine(machine);Peer peer;lock(engine.State.Peers)peer=engine.State.Peers.Single(p=>p.Code==machine);
        peer.AutoConnect=true;engine.State.Save();EnsureReconnect(peer);Changed?.Invoke();
    }
    async Task ConnectOnce(string machine,string password,bool enrolling,CancellationToken ct,bool background)
    {
        machine = Wire.Machine(machine);
        password=password.Trim();
        if (code.Length == 0) await Register(ct);
        if (machine == Code) throw new InvalidOperationException("不能连接本机。");
        var target = await Call<MachineInfo>("/api/v1/lookup", new { code = machine }, ct);
        if (target.Code != machine) throw new InvalidDataException("机器编号不匹配。");
        Wire.Endpoint(target.Endpoint);
        string? pinned;
        lock (sync) access.Targets.TryGetValue(machine, out pinned);
        if (pinned != null && pinned != target.Endpoint) throw new InvalidDataException("该机器的 EndpointId 已改变！已阻止连接，请通过独立渠道核对，不能自动信任服务器的新映射。");
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
            var request = new ServerRequest(machine,target.Endpoint,local,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddSeconds(background?60:300).ToUnixTimeSeconds());
            await Call<JsonElement>("/api/v1/connect", request, timeout.Token);
            ServerOffer? offer = null;
            while (offer == null)
            {
                await Task.Delay(1000, timeout.Token);
                var status = await Call<TicketReply>($"/api/v1/tickets/{request.RequestId}", new { }, timeout.Token);
                if (status.Status == "rejected") throw new IOException("对端未接受连接，请检查允许访问开关。");
                if (status.Status != "ready") continue;
                offer = Verify<ServerOffer>(status.Offer ?? throw new InvalidDataException("缺少对端签名。"), $"/api/v1/tickets/{request.RequestId}/ready", target.Endpoint);
                if (offer.RequestId != request.RequestId || offer.ControllerEndpoint != engine.Id || offer.TargetEndpoint != target.Endpoint || offer.ClientSessionId != local || offer.Expires != request.Expires)
                    throw new InvalidDataException("对端签名没有绑定当前会话，已阻止连接。");
                Wire.Endpoint(offer.SessionId);
            }
            transferred = true;
            await engine.ConnectServerSession(target, dir, offer, enrolling, password, timeout.Token);
        }
        finally { if (!transferred) try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
    public KeyValuePair<string,ControllerGrant>[] Controllers { get { lock(sync) return access.Controllers.ToArray(); } }
    public async Task Revoke(string endpoint)
    {
        lock(sync){access.Controllers.Remove(endpoint);Save();}
        if(engine.Incoming.TryRemove(endpoint,out var session))await session.DisposeAsync();
        Changed?.Invoke();
    }
    public async Task RemoveLocal(Peer peer)
    {
        peer.AutoConnect=false;engine.State.Save();await StopReconnect(peer.Id);await engine.RemoveOutgoing(peer.Id);
        engine.State.Remove(peer.Id);
        lock(sync){access.Targets.Remove(peer.Code);Save();}
        Changed?.Invoke();
    }
    public void RestoreLocal(Peer snapshot)
    {
        if(snapshot.Grant==null)throw new InvalidDataException("设备授权记录不完整，无法撤销移除。");
        var peer=engine.State.GetOrAdd(snapshot.Id,snapshot.Code,snapshot.Grant);
        peer.Name=snapshot.Name;peer.AutoConnect=snapshot.AutoConnect;engine.State.Save();
        lock(sync){access.Targets[peer.Code]=peer.Id;Save();}
        if(peer.AutoConnect)EnsureReconnect(peer);Changed?.Invoke();
    }
    public void SetSideWidth(double width)
    {
        lock(sync){access.SideWidth=Math.Clamp(width,180,360);Save();}
    }
    public async Task Pause(Peer peer)
    {
        peer.AutoConnect=false;engine.State.Save();await StopReconnect(peer.Id);await engine.DropOutgoing(peer.Id);
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
        foreach(var job in reconnects.Values)job.Stop.Cancel();
        try { await loop; } catch { }
        await engine.StopIncoming();
        try { await Task.WhenAll(jobs.Values); } catch { }
        try{await Task.WhenAll(reconnects.Values.Select(j=>j.Task));}catch{}
        foreach(var job in reconnects.Values)job.Stop.Dispose();reconnects.Clear();
        CryptographicOperations.ZeroMemory(enrollmentSecret);http.Dispose();key.Dispose();incoming.Dispose();stop.Dispose();
    }
}
