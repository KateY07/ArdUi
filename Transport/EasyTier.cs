namespace ArdUi;

// EasyTier carries opaque gateway traffic. Device signatures bind the two temporary
// X25519 public keys; only those endpoints can derive this session's network secret.
static class EasyTier
{
    public const string Protocol="easytier-v1";
    const string HostIp="10.177.99.2",ClientIp="10.177.99.1";
    public static string Folder=>Path.GetFullPath(Environment.GetEnvironmentVariable("ARDUI_EASYTIER_DIR")??Path.Combine(AppContext.BaseDirectory,"easytier"));
    public static string Exe=>Required("easytier-core.exe");
    static string Required(string name)
    {
        var path=Path.Combine(Folder,name);
        if(!File.Exists(path))throw new IOException("缺少 EasyTier 运行文件："+name+"。请重新安装完整安装包。");
        return path;
    }
    public static string DeviceIdentity(string directory)
    {
        var seed=File.ReadAllBytes(Path.Combine(directory,"identity"));
        try
        {
            if(seed.Length!=32)throw new InvalidDataException("原有设备密钥无效，不会覆盖。");
            using var key=NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519,seed,NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
            return Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant();
        }
        finally{CryptographicOperations.ZeroMemory(seed);}
    }
    static byte[] Seed(string directory)
    {
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,"easytier.key");
        if(!File.Exists(path))
        {
            var generated=RandomNumberGenerator.GetBytes(32);
            try{using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(generated);file.Flush(true);}
            finally{CryptographicOperations.ZeroMemory(generated);}
        }
        var seed=File.ReadAllBytes(path);
        if(seed.Length!=32){CryptographicOperations.ZeroMemory(seed);throw new InvalidDataException("EasyTier 临时密钥无效。");}
        return seed;
    }
    public static string SessionIdentity(string directory)
    {
        var seed=Seed(directory);
        try
        {
            using var key=NSec.Cryptography.Key.Import(NSec.Cryptography.KeyAgreementAlgorithm.X25519,seed,NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
            return Convert.ToHexString(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)).ToLowerInvariant();
        }
        finally{CryptographicOperations.ZeroMemory(seed);}
    }
    internal static (string Name,string Secret) Network(string directory,string remote)
    {
        remote=Wire.Endpoint(remote);var local=SessionIdentity(directory);
        if(local==remote)throw new CryptographicException("不能与自身临时身份建立连接。");
        var context=Encoding.ASCII.GetBytes("ArdUi/EasyTier/v1\n"+(string.CompareOrdinal(local,remote)<0?local+remote:remote+local));
        var seed=Seed(directory);
        try
        {
            using var key=NSec.Cryptography.Key.Import(NSec.Cryptography.KeyAgreementAlgorithm.X25519,seed,NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
            var pub=NSec.Cryptography.PublicKey.Import(NSec.Cryptography.KeyAgreementAlgorithm.X25519,Convert.FromHexString(remote),NSec.Cryptography.KeyBlobFormat.RawPublicKey);
            using var shared=NSec.Cryptography.KeyAgreementAlgorithm.X25519.Agree(key,pub)??throw new CryptographicException("无效临时公钥。");
            var secret=NSec.Cryptography.KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared,ReadOnlySpan<byte>.Empty,context,32);
            try{return ("ardui-v4-"+Convert.ToHexString(SHA256.HashData(context)).ToLowerInvariant(),Convert.ToHexString(secret).ToLowerInvariant());}
            finally{CryptographicOperations.ZeroMemory(secret);}
        }
        finally{CryptographicOperations.ZeroMemory(seed);}
    }
    sealed record Relay(string Url,string PublicKey);
    static string Quote(string value)=>JsonSerializer.Serialize(value);
    public static Child Start(string directory,bool host,int port,string remote,int remotePort)
    {
        Required("Packet.dll");Required("wintun.dll");Required("easytier-cli.exe");
        var relay=JsonSerializer.Deserialize<Relay>(File.ReadAllText(Required("relay.json")),Wire.Json)??throw new IOException("缺少 EasyTier 中继配置。");
        if(!Uri.TryCreate(relay.Url,UriKind.Absolute,out var url)||url.Scheme is not ("tcp" or "udp" or "ws" or "wss")||!string.IsNullOrEmpty(url.UserInfo))
            throw new IOException("EasyTier 中继地址无效。");
        if(Convert.FromBase64String(relay.PublicKey).Length!=32)throw new IOException("EasyTier 中继固定公钥无效。");
        if(!host&&remotePort is <1 or >65535)throw new IOException("对端网关端口无效。");
        var network=Network(directory,remote);var seed=Seed(directory);var rpc=Wire.Port();
        try
        {
            var config=new StringBuilder();
            config.AppendLine($"instance_name = {Quote("ardui-"+Path.GetFileName(directory))}");
            config.AppendLine($"hostname = {Quote(host?"host":"client")}");
            config.AppendLine($"ipv4 = {Quote(host?HostIp:ClientIp)}");
            config.AppendLine("listeners = [\"tcp://0.0.0.0:0\", \"udp://0.0.0.0:0\"]");
            config.AppendLine($"rpc_portal = {Quote("127.0.0.1:"+rpc)}");
            config.AppendLine("[[peer]]");config.AppendLine($"uri = {Quote(relay.Url)}");config.AppendLine($"peer_public_key = {Quote(relay.PublicKey)}");
            config.AppendLine("[network_identity]");config.AppendLine($"network_name = {Quote(network.Name)}");config.AppendLine($"network_secret = {Quote(network.Secret)}");
            config.AppendLine("[secure_mode]");config.AppendLine("enabled = true");config.AppendLine($"local_private_key = {Quote(Convert.ToBase64String(seed))}");
            config.AppendLine($"local_public_key = {Quote(Convert.ToBase64String(Convert.FromHexString(SessionIdentity(directory))))}");
            // No TUN/routes are installed: let the OS select the source interface, including loopback.
            config.AppendLine("[flags]\nno_tun = true\nuse_smoltcp = true\nprivate_mode = true\nbind_device = false");
            config.AppendLine("[[acl.acl_v1.chains]]\nname = \"gateway-in\"\nchain_type = 1\nenabled = true\ndefault_action = 2");
            if(host)config.AppendLine($"[[acl.acl_v1.chains.rules]]\nname = \"gateway\"\nenabled = true\nprotocol = 5\nports = [\"{port}\"]\naction = 1");
            config.AppendLine("[[acl.acl_v1.chains]]\nname = \"outbound\"\nchain_type = 2\nenabled = true\ndefault_action = 1");
            config.AppendLine("[[acl.acl_v1.chains]]\nname = \"no-subnet\"\nchain_type = 3\nenabled = true\ndefault_action = 2");
            var path=Path.Combine(directory,"easytier.toml");File.WriteAllText(path,config.ToString());
            // Only the authenticated application gateway is reachable; no RDP/SMB service is exposed by the overlay network.
            var args=new List<string>{"-c",path,"--rpc-portal","127.0.0.1:"+rpc,"--console-log-level","warn"};
            if(!host)args.AddRange(["--port-forward",$"tcp://127.0.0.1:{port}/{HostIp}:{remotePort}",$"udp://127.0.0.1:{port}/{HostIp}:{remotePort}"]);
            var child=new Child(Exe,directory,args.ToArray());child.Track(new Status(rpc,host));return child;
        }
        finally{CryptographicOperations.ZeroMemory(seed);}
    }
    public sealed class Status(int rpc,bool host)
    {
        public string Network{get;set;}="EasyTier 正在连接";
        public double? Rtt{get;set;}
        public Status Copy()=>new(rpc,host);
        public async Task Run(Child child,CancellationToken ct)
        {
            var last="";var ready=false;
            while(!ct.IsCancellationRequested&&!child.Exited.IsCompleted)
            {
                try
                {
                    var start=new ProcessStartInfo(Required("easytier-cli.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                    foreach(var arg in new[]{"-p","127.0.0.1:"+rpc,"-v","peer"})start.ArgumentList.Add(arg);
                    using var process=Process.Start(start)??throw new IOException("无法启动 EasyTier 状态查询。");
                    var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(4000);
                    try{await process.WaitForExitAsync(timeout.Token);}catch{if(!process.HasExited)process.Kill(true);throw;}
                    var text=await output;var err=await error;
                    if(process.ExitCode!=0)throw new IOException(err);
                    using var json=JsonDocument.Parse(text);var found=false;
                    foreach(var entry in json.RootElement.EnumerateArray())
                    {
                        if(!entry.TryGetProperty("route",out var route)||route.ValueKind!=JsonValueKind.Object||route.GetProperty("hostname").GetString()!=(host?"client":"host"))continue;
                        found=true;var direct=route.GetProperty("cost").GetInt32()==1;
                        Network=direct?"P2P / EasyTier":"EasyTier 中继";
                        Rtt=route.TryGetProperty("path_latency",out var latency)?latency.GetDouble():null;
                    }
                    if(!found){Network="EasyTier 等待对端";Rtt=null;}
                    var milestone=!ready&&(host||found)?host?"relay online":"READY:":null;
                    if(milestone!=null)ready=true;
                    if(last!=text||milestone!=null)
                    {
                        child.Report("network path selected "+Network+" "+text,milestone);last=text;
                    }
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
                catch(Exception ex){Diagnostics.Log("easytier-status",ex.Message);Network="EasyTier 状态暂不可用";Rtt=null;}
                try{await Task.Delay(2000,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            }
        }
    }
}
