namespace ArdUi;

static class FrdTest
{
    static void Check(bool value,string message){if(!value)throw new IOException(message);}
    public static async Task<int> Run(string[] args)
    {
        var root=Environment.GetEnvironmentVariable("ARDUI_TEST_ROOT")??throw new IOException("Run tests/integration_v2.py --frd to start an isolated fixture.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(150));var ct=timeout.Token;
        var settings=new Settings{Relay=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY")!,RelayKey=Environment.GetEnvironmentVariable("ARDUI_TEST_RELAY_KEY")!,TcpPorts=[],UdpPorts=[]};
        var a=Path.Combine(root,"frd-a");var b=Path.Combine(root,"frd-b");IdentityStore.Prepare(a);IdentityStore.Prepare(b);
        await using var caller=await Engine.Create(a,settings);await using var host=await Engine.Create(b,settings);
        var server=Environment.GetEnvironmentVariable("ARDUI_TEST_SERVER")!;
        await using var ad=new DirectoryClient(caller,server);await using var bd=new DirectoryClient(host,server);
        ad.Confirm=(_,_)=>Task.FromResult(true);bd.Confirm=(_,_)=>Task.FromResult(true);
        try
        {
            await ad.Register(ct);await bd.Register(ct);await bd.SetAccess(true,ct);ad.Start();bd.Start();
            await ad.Connect(bd.Code,bd.AccessPassword,ct);
            var session=caller.Outgoing[host.Id];var incoming=host.Incoming[caller.Id];
            var denied=false;
            try{using var unauthorized=await session.OpenTcp(9,ct);}catch(IOException){denied=true;}
            Check(denied,"FRD expanded general TCP permissions.");
            denied=false;
            try{await incoming.Overlay!.Request("frd-start",new{},ct);}catch(IOException){denied=true;}
            Check(denied,"Unapproved reverse FRD host launch was allowed.");
            Console.WriteLine("PASS: only the authorized controller can start FRD; general port allowlist remains closed.");
            async Task View(string name)
            {
                var report=Path.Combine(root,name+".json");var opened=await session.OpenFrd(ct,8,report);
                await opened.Completion.WaitAsync(TimeSpan.FromSeconds(40),ct);
                using var document=JsonDocument.Parse(await File.ReadAllTextAsync(report,ct));var result=document.RootElement;
                Check(opened.ExitCode==0&&result.GetProperty("Passed").GetBoolean(),"FRD reported failure; inspect "+report);
                Check(result.GetProperty("ReceivedFrames").GetInt64()>0&&result.GetProperty("GpuConfirmedFrames").GetInt64()>0,"FRD received no rendered desktop frames.");
                Check(result.GetProperty("Input").GetProperty("SentEvents").GetInt64()==0,"Test unexpectedly injected input.");
                Console.WriteLine($"PASS: {name}: real FRD captured/received/rendered {result.GetProperty("ReceivedFrames")} frames via encrypted native UDP; no keyboard or clipboard actions.");
            }
            await View("frd-base");
            var candidates=(await ad.Call<TransitCandidates>("/api/v2/transit/candidates",new{target=host.Id},ct)).Candidates;
            Check(candidates.Length>0&&await session.Transit!.ProbeCandidate(candidates[0],true,ct),"FRD test candidate unavailable.");
            Check(session.Overlay!.Selected!="base","Candidate route not selected.");
            await View("frd-transit");
            var revoked=await session.OpenFrd(ct,30,Path.Combine(root,"frd-revoked.json"));
            await Task.Delay(2000,ct);await bd.Revoke(caller.Id);
            await revoked.Completion.WaitAsync(TimeSpan.FromSeconds(15),ct);
            Check(!incoming.FrdLive&&!session.FrdLive,"Revocation left an FRD process or proxy running.");
            Console.WriteLine("PASS: revocation closes FRD processes and dynamic forwarding permissions.");
            return 0;
        }
        finally{Console.WriteLine("Diagnostics: "+Diagnostics.Export(root,caller));}
    }
}
