namespace ArdUi;

sealed class State : IDisposable
{
    public string Root { get; }
    public string IdentityDirectory => Path.Combine(Root, "device");
    public List<Peer> Peers { get; }
    readonly FileStream processLock;
    public State(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        processLock = new FileStream(Path.Combine(Root, "app.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(Root, "peers.json");
            Peers = File.Exists(path) ? JsonSerializer.Deserialize<List<Peer>>(File.ReadAllText(path), Wire.Json)!
                : [];
            if (Peers is null || Peers.Any(p => p is null || Wire.Endpoint(p.Id) != p.Id || !IPAddress.TryParse(p.Address, out var ip) || !IPAddress.IsLoopback(ip) || p.Grant == null) ||
                Peers.Select(p => p.Id).Distinct().Count() != Peers.Count ||
                Peers.Select(p => p.Address).Distinct().Count() != Peers.Count)
                throw new InvalidDataException("设备地址记录无效，请检查 data/peers.json；不会自动重置已保存的地址。");
        }
        catch { processLock.Dispose(); throw; }
    }
    public Peer GetOrAdd(string id, string code, SignedEnvelope grant)
    {
        id = Wire.Endpoint(id);
        lock (Peers)
        {
            if (Peers.Find(p => p.Id == id) is { } existing) { existing.Grant = grant; existing.Code = code; Save(); return existing; }
            var address = Enumerable.Range(0, 64000).Select(i => $"127.77.{i / 250}.{i % 250 + 1}")
                .FirstOrDefault(ip => Peers.All(p => p.Address != ip))
                ?? throw new InvalidOperationException("已保存设备数量达到上限。");
            var peer = new Peer { Id = id, Address = address, Code = code, Grant = grant, AutoConnect=false };
            var next = Peers.Append(peer).ToArray();
            var path = Path.Combine(Root, "peers.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next, Wire.Json));
            File.Move(path + ".tmp", path, true);
            Peers.Add(peer);
            return peer;
        }
    }
    public void Save()
    {
        lock (Peers)
        {
            var path = Path.Combine(Root, "peers.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Peers, Wire.Json));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void Remove(string id) { lock (Peers) { Peers.RemoveAll(p => p.Id == id); Save(); } }
    public string NewSessionDirectory()
    {
        var dir = Path.Combine(Root, "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    public void Dispose() => processLock.Dispose();
}
sealed record IdentityBackup(int Version, string Endpoint, string Salt, string Nonce, string Ciphertext, string Tag);

static class IdentityStore
{
    public static string Prepare(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("身份安装需要 Windows。");
        var folder = Path.Combine(Path.GetFullPath(root), "device");
        using var mutex = new Mutex(false, "Local\\ArdUi-Identity-Install");
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("另一项身份安装正在运行。");
        try
        {
            Directory.CreateDirectory(folder);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确定当前 Windows 用户。");
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
            var destination = Path.Combine(folder, "identity");
            if (!File.Exists(destination))
            {
                var seed = RandomNumberGenerator.GetBytes(32);
                var temporary = Path.Combine(folder, ".identity-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(seed); file.Flush(true); }
                    // Atomic creation only: never replace an existing identity.
                    try { File.Move(temporary, destination, false); }
                    catch (IOException) when (File.Exists(destination)) { }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seed);
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            var existing = File.ReadAllBytes(destination);
            try
            {
                if (existing.Length != 32) throw new IOException("已有身份文件无效，安装已停止，未覆盖原文件。");
                using var key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519,
                    existing, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
                return Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant();
            }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
