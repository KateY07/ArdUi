using System.Net.Http.Json;
namespace ArdUi;

static class SelfTest
{
    public static async Task<int> Prototype()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArdUi-prototype-" + Guid.NewGuid().ToString("N"));
        // ARDUI_TEST_LOG=DIR keeps the run's log (both devices, every ARD line from start-up on) for timing analysis
        var keepLog = Environment.GetEnvironmentVariable("ARDUI_TEST_LOG");
        if (keepLog != null) FileLog.Start(root);
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
            if(Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY") is not null)settings.StaticRelay=true;
            settings.Relay=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY")??settings.Relay;
            settings.RelayKey=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY_KEY")??settings.RelayKey;
            await using var host = await Engine.Create(a, settings);
            await using var caller = await Engine.Create(b, settings);
            await using var hd = new DirectoryClient(host,Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER"));
            await using var cd = new DirectoryClient(caller,Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER"));
            var hostChecks = 0; var callerChecks = 0;
            var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // the person at the host approves a new controller (with RDP, which the loopback echo service stands in for)
            var granted = Permissions.Default | Perm.Rdp;
            hd.ConfirmIncoming = async (pair, token) =>
            {
                if (!pair.Incoming || pair.Endpoint != caller.Id) throw new Exception("Host fingerprint mismatch.");
                Interlocked.Increment(ref hostChecks); prompt.TrySetResult();
                return await approval.Task.WaitAsync(token) ? new Decision(true, false, granted) : Decision.Rejected;
            };
            cd.Confirm = (pair, token) =>
            {
                if (pair.Incoming || pair.Endpoint != host.Id) throw new Exception("Caller fingerprint mismatch.");
                Interlocked.Increment(ref callerChecks); return Task.FromResult(true);
            };
            hd.Notice += message => Console.WriteLine("HOST: " + message);
            await hd.Register(ct); await cd.Register(ct);
            // only a signed-in device may start a session: the controller signs in with the network's test account
            // (ARDUI_TEST_USER / ARDUI_TEST_PASSWORD; an administrator makes it: arduiserver.py --add-user NAME)
            if (Environment.GetEnvironmentVariable("ARDUI_TEST_USER") is { Length: > 0 } testUser)
            {
                var (token, _, until) = await cd.StartWebLogin(ct);
                using var web = new HttpClient();
                using var signIn = new HttpRequestMessage(HttpMethod.Post, new Uri(cd.AccountPage(), "/api/account/login"))
                { Content = JsonContent.Create(new { login = token, name = testUser, password = Environment.GetEnvironmentVariable("ARDUI_TEST_PASSWORD") ?? "" }) };
                signIn.Headers.Add("X-ArdUi-Web", "1");
                using (var signed = await web.SendAsync(signIn, ct)) if (!signed.IsSuccessStatusCode) throw new Exception("Test account sign-in failed: " + await signed.Content.ReadAsStringAsync(ct));
                if (!await cd.WaitWebLogin(token, until, ct)) throw new Exception("The device did not learn that it was signed in.");
            }
            var original = cd.Code; await cd.Register(ct);
            if (original != cd.Code || hd.Enabled) throw new Exception("Registration/default access failure.");
            Console.WriteLine("PASS: HTTPS registration, stable machine code, host disabled by default.");
            var minute=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;var enrollmentPassword=hd.AccessPassword;
            if(!Regex.IsMatch(enrollmentPassword,@"\A[0-9]{6}\z")||enrollmentPassword==hd.AccessPasswordFor(minute+1))throw new Exception("Rotating password failure.");
            await hd.SetAccess(true,ct);hd.Start();cd.Start();
            using var firstAttempt=CancellationTokenSource.CreateLinkedTokenSource(ct);
            var connection = cd.Connect(hd.Code,enrollmentPassword,firstAttempt.Token);
            await prompt.Task.WaitAsync(ct);
            if (caller.State.Peers.Count != 0 || connection.IsCompleted) throw new Exception("Peer granted before consent.");
            Console.WriteLine("TEST: disabling host while confirmation is pending.");
            await hd.SetAccess(false,ct).WaitAsync(TimeSpan.FromSeconds(3),ct);
            Console.WriteLine("TEST: host disabled; cancelling the caller's pending request independently.");
            if(hd.Enabled||host.Incoming.Count!=0)throw new Exception("Pending confirmation blocked host disable.");
            approval.TrySetResult(true);
            if(hd.Controllers.Length!=0||caller.State.Peers.Count!=0)throw new Exception("Late approval granted a disabled session.");
            firstAttempt.Cancel();
            try{await connection.WaitAsync(TimeSpan.FromSeconds(3),ct);throw new Exception("Disabled host granted pending request.");}
            catch(Exception ex)when(ex is IOException or OperationCanceledException)
            {Console.WriteLine("PASS: host disable cancels a pending confirmation without a global operation lock.");}
            await hd.SetAccess(true,ct);prompt=new(TaskCreationOptions.RunContinuationsAsynchronously);approval=new(TaskCreationOptions.RunContinuationsAsynchronously);
            connection=cd.Connect(hd.Code,hd.AccessPassword,ct);await prompt.Task.WaitAsync(ct);
            approval.SetResult(true); await connection;
            if (hostChecks != 2 || callerChecks != 1 || caller.State.Peers.Count != 1) throw new Exception("Consent failure.");
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
            // the session's control channel runs over the device-signed overlay: a message goes through it
            await caller.ChatFor(host.Id).SendText("ardui-signed-session",ct);
            if(!host.ChatFor(caller.Id).Messages.Any(m=>m.Text=="ardui-signed-session"))throw new Exception("Message did not cross the signed session.");
            Console.WriteLine("PASS: both devices signed the session's key exchange; a message crossed it.");
            // a local process that finds the host's loopback gateway cannot be admitted a second time and gets no session token
            using(var intruder=new TcpClient())
            {
                await intruder.ConnectAsync(IPAddress.Loopback,host.Incoming[caller.Id].LocalPort,ct);
                var head=new byte[23];"AUI1"u8.CopyTo(head);head[20]=2;await intruder.GetStream().WriteAsync(head,ct);
                await Wire.WriteJson(intruder.GetStream(),new PasswordRequest(false,""),ct);
                var reply=await Wire.ReadJson<AdmissionReply>(intruder.GetStream(),ct);
                if(reply.Accepted||reply.Token!=null)throw new Exception("A second admission handed out the session token.");
            }
            Console.WriteLine("PASS: a local process reaching the host gateway is refused a second admission and gets no token.");
            var first=caller.Outgoing[host.Id];var child=first.Process;var stablePort=first.Forward(port).Port;await child.DisposeAsync();
            using(var recovery=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                recovery.CancelAfter(TimeSpan.FromSeconds(90));
                while(!caller.Outgoing.TryGetValue(host.Id,out var restored)||ReferenceEquals(child,restored.Process)||!restored.Live||restored.Overlay?.Link("base")?.Live!=true)await Task.Delay(100,recovery.Token);
            }
            if(caller.Outgoing[host.Id].Forward(port).Port!=stablePort)throw new Exception("Local forwarding port changed across reconnect.");
            await VerifyFlow();Console.WriteLine("PASS: killed ARD child recovered automatically with the same local forwarding port.");
            var savedPeer=caller.State.Peers.Single();await cd.Pause(savedPeer);await host.Disconnect(caller.Id);
            var reconnectWatch=System.Diagnostics.Stopwatch.StartNew();
            await cd.Connect(hd.Code,"",ct,enrolling:false);
            Console.WriteLine($"TIME: authorized reconnect {reconnectWatch.Elapsed.TotalSeconds:F1} s");
            if (hostChecks != 2 || callerChecks != 1) throw new Exception("Authorized reconnect prompted again.");
            await VerifyFlow(); Console.WriteLine("PASS: authorized reconnect without enrollment password or consent.");
            // A controller that vanishes without a goodbye (machine offline, ArdUi killed) is not waited for forever. (Here, where a
            // session exists without a new enrollment: enrollment attempts are rate limited per device.)
            Engine.IncomingRecoveryBudget=TimeSpan.FromSeconds(5);
            if(!caller.Outgoing.TryRemove(host.Id,out var vanished))throw new Exception("Outgoing session missing.");
            await vanished.DisposeAsync();   // no "bye": the host only sees its paths go
            using(var abandon=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                abandon.CancelAfter(TimeSpan.FromSeconds(60));
                while(host.Incoming.ContainsKey(caller.Id))await Task.Delay(250,abandon.Token);
            }
            Engine.IncomingRecoveryBudget=TimeSpan.FromMinutes(2);
            Console.WriteLine("PASS: an incoming session whose controller vanished without a goodbye ends after the recovery budget.");
            await hd.Revoke(caller.Id);
            if (host.Incoming.Count != 0 || hd.Controllers.Length != 0) throw new Exception("Revoke failed.");
            await cd.Pause(caller.State.Peers.Single());
            try { await cd.Connect(hd.Code,"",ct,enrolling:false); throw new Exception("Revoked caller admitted."); }
            catch (IOException) { }
            Console.WriteLine("PASS: revocation removes ACL and denies reconnect.");
            hd.ConfirmIncoming=(pair,token)=>Task.FromResult(pair.Incoming&&pair.Endpoint==caller.Id?new Decision(true,false,granted):Decision.Rejected);
            for(var round=1;round<=3;round++)
            {
                Console.WriteLine($"TEST: repeated add round {round} connecting.");
                var roundWatch=System.Diagnostics.Stopwatch.StartNew();
                await cd.Connect(hd.Code,hd.AccessPassword,ct).WaitAsync(TimeSpan.FromSeconds(75),ct);
                Console.WriteLine($"TIME: add round {round} connect {roundWatch.Elapsed.TotalSeconds:F1} s");
                await VerifyFlow();
                var saved=caller.State.Peers.Single();await cd.RemoveLocal(saved);
                if(caller.State.Peers.Count!=0||caller.Outgoing.Count!=0)throw new Exception("Repeated local removal failed.");
                Console.WriteLine($"TEST: repeated add round {round} removed.");
            }
            Console.WriteLine("PASS: three add, transfer, remove, and immediate re-add cycles.");
            await hd.Revoke(caller.Id);
            await hd.SetAccess(false,ct);
            if (hd.Enabled || host.Incoming.Count != 0) throw new Exception("Disable failed.");
            Console.WriteLine("PASS: host disable stops incoming sessions.");
            return 0;
        }
        finally
        {
            echo.Stop();
            if (keepLog != null)
            {
                await Task.Delay(1500);   // the log writer flushes on its own pace
                Directory.CreateDirectory(keepLog);
                foreach (var file in Directory.GetFiles(Path.Combine(root, "logs"))) File.Copy(file, Path.Combine(keepLog, Path.GetFileName(file)), true);
                Console.WriteLine("LOG: " + keepLog);
            }
            if (Directory.Exists(root)) Directory.Delete(root,true);
        }
    }
    static async Task CheckChildReadiness()
    {
        var exe=Environment.ProcessPath??throw new IOException("Missing process path.");
        var args=new List<string>();
        if(Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            args.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        args.Add("--child-readiness-fixture");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var child=new Child(exe,AppContext.BaseDirectory,args.ToArray());
        await child.WaitFor("fixture-flood-complete",timeout.Token);
        await child.WaitFor("relay online",timeout.Token);
        await child.WaitFor("READY:",timeout.Token);
        Console.WriteLine("PASS: startup milestones survive 500 diagnostic lines before the caller begins waiting.");
    }
    public static Task<int> RunAsync()
    {
        CheckChildReadiness().GetAwaiter().GetResult();
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
            var custom=Path.Combine(root,"custom-frd");Directory.CreateDirectory(custom);var customExe=Path.Combine(custom,"FRD.exe");File.WriteAllText(customExe,"fixture");
            var bundled=Path.Combine(AppContext.BaseDirectory,"frd","FRD.exe");var expected=File.Exists(bundled)?bundled:customExe;
            if(FrdRuntime.Resolve(customExe)!=Path.GetFullPath(expected))throw new Exception("FRD runtime resolution failed.");
            Console.WriteLine("PASS: bundled FRD layout takes precedence; explicit path remains available without a bundle.");
            var preview=Path.Combine(root,"headless.png"); Preview.Render(preview);
            if(new FileInfo(preview).Length<1000)throw new Exception("无头渲染输出无效。");
            Console.WriteLine("PASS: Avalonia code-only headless layout and rendering.");
            return Task.FromResult(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
