namespace ArdUi;

static class Diagnostics
{
    static readonly ConcurrentQueue<object> events = new();
    public static NetDiagReport? LastNetwork { get; set; }   // the latest run of the network diagnosis page: kinds and verdicts only, no addresses
    public static string Redact(string value)
    {
        value=Regex.Replace(value,@"\b(?:\d{1,3}\.){3}\d{1,3}\b","[IPv4]");
        return Regex.Replace(value,@"(?<![\w])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]+","[IPv6]");
    }
    public static void Log(string kind,string message)
    {
        var clean=Redact(message);events.Enqueue(new{time=DateTimeOffset.UtcNow,kind,message=clean});
        while(events.Count>2048)events.TryDequeue(out _);
        Trace.WriteLine($"[v2 {kind}] {clean}");
    }
    public static string Export(string root,Engine? engine)
    {
        var folder=Path.Combine(root,"diagnostics");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,$"ArdUi-{Program.Version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        void Entry(string name,object value)
        {using var writer=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Optimal).Open());writer.Write(JsonSerializer.Serialize(value,Wire.Json));}
        using var process=Process.GetCurrentProcess();
        Entry("status.json",new{version=Program.Version,ardPath=Ard.Exe,time=DateTimeOffset.UtcNow,
            endpoint=engine?.Id,os=Environment.OSVersion.VersionString,memoryBytes=process.WorkingSet64,cpuSeconds=process.TotalProcessorTime.TotalSeconds,
            sessions=engine?.Outgoing.Values.Concat(engine.Incoming.Values).Select(s=>new{peer=s.PeerId,host=s.Host,network=s.Network,s.TcpRtt,s.UdpRtt,s.BandwidthMbps,s.BandwidthLowerBoundMbps,overlay=s.Overlay?.Snapshot()}).ToArray()});
        Entry("events.json",events.ToArray());
        if(LastNetwork is{} net)Entry("network.json",new{at=net.At,nat=net.Nat,upnpMapping=net.UpnpMappingAvailable,sections=net.Sections.Select(x=>new{x.Title,level=x.Level.ToString(),summary=Redact(x.Summary)})});
        Entry("privacy.json",new{ipAddresses="redacted",excluded=new[]{"private keys","passwords","session keys","capabilities","tickets","payloads","handshakes"}});
        return path;
    }
}
