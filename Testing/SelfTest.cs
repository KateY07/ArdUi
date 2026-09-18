namespace ArdUi;

static class SelfTest
{
    public static async Task<int> Prototype()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-prototype-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = timeout.Token;
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        var port = ((IPEndPoint)echo.LocalEndpoint).Port;
        async Task Echo()
        {
            using var connection = await echo.AcceptTcpClientAsync(ct);
            var stream = connection.GetStream();
            var data = await Wire.Read(stream, 12, ct);
            await stream.WriteAsync(data, ct);
        }
        try
        {
            var a = Path.Combine(root,"a"); var b = Path.Combine(root,"b");
            IdentityStore.Prepare(a); IdentityStore.Prepare(b);
            var settings = new Settings { TcpPorts = [port], UdpPorts = [] };
            settings.Relay=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY")??settings.Relay;
            settings.RelayKey=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY_KEY")??settings.RelayKey;
            await using var host = await Engine.Create(a, settings);
            await using var caller = await Engine.Create(b, settings);
            await using var hd = new DirectoryClient(host,Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER"));
            await using var cd = new DirectoryClient(caller,Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER"));
            var hostChecks = 0; var callerChecks = 0;
            var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            hd.Confirm = (pair, token) =>
            {
                if (!pair.Incoming || pair.Endpoint != caller.Id) throw new Exception("Host fingerprint mismatch.");
                Interlocked.Increment(ref hostChecks); prompt.TrySetResult(); return approval.Task.WaitAsync(token);
            };
            cd.Confirm = (pair, token) =>
            {
                if (pair.Incoming || pair.Endpoint != host.Id) throw new Exception("Caller fingerprint mismatch.");
                Interlocked.Increment(ref callerChecks); return Task.FromResult(true);
            };
            hd.Notice += message => Console.WriteLine("HOST: " + message);
            await hd.Register(ct); await cd.Register(ct);
            var original = cd.Code; await cd.Register(ct);
            if (original != cd.Code || hd.Enabled) throw new Exception("Registration/default access failure.");
            Console.WriteLine("PASS: HTTPS registration, stable machine code, host disabled by default.");
            var minute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;var enrollmentPassword=hd.AccessPassword;
            if(!Regex.IsMatch(enrollmentPassword,@"\A[0-9]{6}\z")||enrollmentPassword==hd.AccessPasswordFor(minute+1))throw new Exception("Rotating password failure.");
            await hd.SetAccess(true,ct);hd.Start();cd.Start();
            var connection = cd.Connect(hd.Code,enrollmentPassword,ct);
            await prompt.Task.WaitAsync(ct);
            if (caller.State.Peers.Count != 0 || connection.IsCompleted) throw new Exception("Peer granted before consent.");
            approval.SetResult(true); await connection;
            if (hostChecks != 1 || callerChecks != 1 || caller.State.Peers.Count != 1) throw new Exception("Consent failure.");
            Console.WriteLine("PASS: both fingerprints checked; peer saved only after host approval and signed grant.");
            async Task VerifyFlow()
            {
                var response = Echo();
                var session = caller.Outgoing[host.Id];
                using var local = new TcpClient();
                await local.ConnectAsync(IPAddress.Loopback,session.Forward(port).Port,ct);
                var message = "ardui-e2e-ok"u8.ToArray();
                await local.GetStream().WriteAsync(message,ct);
                if (!(await Wire.Read(local.GetStream(),message.Length,ct)).SequenceEqual(message)) throw new Exception("TCP proxy corrupted data.");
                await response;
            }
            await VerifyFlow();Console.WriteLine("PASS: loopback TCP proxy through authenticated ARD to remote service.");
            var first=caller.Outgoing[host.Id];var child=first.Process;var stablePort=first.Forward(port).Port;await child.DisposeAsync();
            using(var recovery=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                recovery.CancelAfter(TimeSpan.FromSeconds(90));
                while(!caller.Outgoing.TryGetValue(host.Id,out var restored)||ReferenceEquals(child,restored.Process)||!restored.Live||restored.Overlay?.Link("base")?.Live!=true)await Task.Delay(100,recovery.Token);
            }
            if(caller.Outgoing[host.Id].Forward(port).Port!=stablePort)throw new Exception("Local forwarding port changed across reconnect.");
            await VerifyFlow();Console.WriteLine("PASS: killed ARD child recovered automatically with the same local forwarding port.");
            var savedPeer=caller.State.Peers.Single();await cd.Pause(savedPeer);await host.Disconnect(caller.Id);
            await cd.Connect(hd.Code,"",ct,enrolling:false);
            if (hostChecks != 1 || callerChecks != 1) throw new Exception("Authorized reconnect prompted again.");
            await VerifyFlow(); Console.WriteLine("PASS: authorized reconnect without enrollment password or consent.");
            await hd.Revoke(caller.Id);
            if (host.Incoming.Count != 0 || hd.Controllers.Length != 0) throw new Exception("Revoke failed.");
            await cd.Pause(caller.State.Peers.Single());
            try { await cd.Connect(hd.Code,"",ct,enrolling:false); throw new Exception("Revoked caller admitted."); }
            catch (IOException) { }
            Console.WriteLine("PASS: revocation removes ACL and denies reconnect.");
            hd.Confirm=(pair,token)=>Task.FromResult(pair.Incoming&&pair.Endpoint==caller.Id);
            for(var round=1;round<=3;round++)
            {
                Console.WriteLine($"TEST: repeated add round {round} connecting.");
                await cd.Connect(hd.Code,hd.AccessPassword,ct).WaitAsync(TimeSpan.FromSeconds(75),ct);
                await VerifyFlow();
                var saved=caller.State.Peers.Single();await cd.RemoveLocal(saved);
                if(caller.State.Peers.Count!=0||caller.Outgoing.Count!=0)throw new Exception("Repeated local removal failed.");
                Console.WriteLine($"TEST: repeated add round {round} removed.");
            }
            await hd.Revoke(caller.Id);
            Console.WriteLine("PASS: three add, transfer, remove, and immediate re-add cycles.");
            await hd.SetAccess(false,ct);
            if (hd.Enabled || host.Incoming.Count != 0) throw new Exception("Disable failed.");
            Console.WriteLine("PASS: host disable stops incoming sessions.");
            return 0;
        }
        finally
        {
            echo.Stop();
            if (Directory.Exists(root)) Directory.Delete(root,true);
        }
    }
    public static Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = IdentityStore.Prepare(root);
            var path = Path.Combine(root, "device", "identity");
            var digest = SHA256.HashData(File.ReadAllBytes(path));
            if (first.Length != 64 || IdentityStore.Prepare(root) != first ||
                !digest.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new Exception("重复安装改变了身份。");
            File.WriteAllBytes(path, [1, 2, 3]);
            try { IdentityStore.Prepare(root); throw new Exception("损坏身份被接受。"); }
            catch (IOException) { }
            if (!File.ReadAllBytes(path).SequenceEqual(new byte[] {1,2,3})) throw new Exception("损坏身份被覆盖。");
            Console.WriteLine("PASS: identity creation, reinstall preservation, corrupt identity refusal.");
            var preview=Path.Combine(root,"headless.png"); Preview.Render(preview);
            if(new FileInfo(preview).Length<1000)throw new Exception("无头渲染输出无效。");
            Console.WriteLine("PASS: Avalonia code-only headless layout and rendering.");
            return Task.FromResult(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
