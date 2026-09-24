namespace ArdUi;

static class Program
{
    public const string Version="v2.pre2-performance";
    public static string DataRoot { get; set; }="";
    public static async Task<int> Main(string[] args)
    {
        if(args.Contains("--cipher-stress"))return await CipherStress.Run();
        if(args.Contains("--transit-test"))return await TransitTest.Run(args);
        string Opt(string key,string fallback){var i=Array.IndexOf(args,key);return i<0?fallback:args[i+1];}
        if(args.Contains("--help")){Console.WriteLine("--mode raw|ard|overlay|full|all or comma list, e.g. full,ard,raw; --seconds 8 --samples 1000 --output result.json; isolated ARDUI_TEST_* environment required for ard/full");return 0;}
        var seconds=int.Parse(Opt("--seconds","8"));var samples=int.Parse(Opt("--samples","1000"));
        if(seconds is <1 or >60||samples is <10 or >10000)throw new ArgumentOutOfRangeException(nameof(args));
        var mode=Opt("--mode","all");var modes=mode=="all"?new[]{"raw","ard","overlay","full"}:mode.Split(',',StringSplitOptions.TrimEntries);
        if(modes.Any(m=>m is not("raw" or "ard" or "overlay" or "full")))throw new ArgumentException("Unknown performance mode");
        var output=Path.GetFullPath(Opt("--output","performance.json"));
        var parent=Environment.GetEnvironmentVariable("ARDUI_TEST_ROOT")??Path.Combine(Path.GetTempPath(),"ArdUi-performance");
        DataRoot=Path.Combine(Path.GetFullPath(parent),"measure-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(DataRoot);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));var ct=timeout.Token;
        var rows=new List<object>();Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            await using var receiver=new Receiver();
            foreach(var name in modes)
            {
                Console.WriteLine($"MODE {name}: preparing target 127.0.0.1:{receiver.Port}");
                await using var path=await PerfPath.Create(name,receiver.Port,Path.Combine(DataRoot,name),ct);
                using(var warmup=await path.Open(ct)){await warmup.GetStream().WriteAsync("E"u8.ToArray(),ct);var bytes=new byte[64];await warmup.GetStream().WriteAsync(bytes,ct);await warmup.GetStream().ReadExactlyAsync(bytes,ct);}
                await Task.Delay(100,ct);var before=path.Evidence();path.CheckDirect(before);
                Console.WriteLine($"MODE {name}: measuring TCP/UDP echo and TCP upload/download {seconds}s each");
                var measurementStartedUtc=DateTimeOffset.UtcNow;
                var measured=await Measurement.Run(path,seconds,samples,args.Contains("--loaded"),ct);
                var measurementFinishedUtc=DateTimeOffset.UtcNow;
                var after=path.Evidence();path.CheckDirect(after);var intervalPathEvents=path.CheckInterval(before,after);
                var diagnosticArchive=Diagnostics.Export(DataRoot,null);
                rows.Add(new{mode=name,target=$"127.0.0.1:{receiver.Port}",entry=$"127.0.0.1:{path.Port}",measurementStartedUtc,measurementFinishedUtc,before,after,intervalPathEvents,measured,diagnosticArchive});
                Write(null);Console.WriteLine($"MODE {name}: receiver-confirmed measurements complete");
            }
            Write(null);Console.WriteLine("RESULT "+output);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Write(ex.ToString());return 1;}
        void Write(string? error)=>File.WriteAllText(output,JsonSerializer.Serialize(new{
            schema="ardui-performance-v1",time=DateTimeOffset.UtcNow,root=DataRoot,
            environment=new{os=Environment.OSVersion.VersionString,runtime=RuntimeInformation.FrameworkDescription,cpu=Environment.ProcessorCount,stopwatchFrequency=Stopwatch.Frequency},
            parameters=new{seconds,samples,loaded=args.Contains("--loaded"),variant=Environment.GetEnvironmentVariable("ARDUI_BENCH_VARIANT"),ardCopyKiB=Environment.GetEnvironmentVariable("ARD_BENCH_COPY_KIB")},
            methodology=new{rtt="64-byte application echo on one persistent TCP_NODELAY connection; 64/1200-byte UDP sequence echo. Connection/handshake excluded, 50 warmups per test.",
                percentiles="Nearest-rank sorted successful RTT: ceil(p*n)-1. Timeout/loss separately reported.",
                tcpMbps="Receiver-confirmed payload bytes *8 / elapsed seconds /1e6. Includes draining sender buffers and final receiver acknowledgement; no bandwidth calculated from write acceptance alone.",
                directProof="Business tunnel from actual local TCP connection accepted or TCP/UDP exposure ready; same-tunnel last CONNECTED/path-selected state required direct. Helper tunnel ignored.",
                full="Engine/DirectoryClient/Gateway/Overlay/LocalForwarder linked from selected source root; fixture records source hashes and optional experimental variant. Normal background metrics active; no FRD/RDP started."},results=rows,error},new JsonSerializerOptions{WriteIndented=true}));
    }
}
