using System.Diagnostics;

namespace ArdUi;

// --connect-test CODE [COUNT]: this machine, with its real identity, data and settings, connects to an already authorized device
// COUNT times, the way the person does, and reports how long each stage took (the ARD lines of each connection are in the log).
// Close the ArdUi window first: one instance per machine.
static class ConnectTiming
{
    public static async Task<int> Run(string[] args)
    {
        var at = Array.IndexOf(args, "--connect-test");
        if (at + 1 >= args.Length) { Console.Error.WriteLine("usage: ArdUi --connect-test CODE [COUNT]"); return 2; }
        var code = args[at + 1].ToUpperInvariant();
        var count = at + 2 < args.Length && int.TryParse(args[at + 2], out var n) ? n : 3;
        var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(Program.DataRoot, "config.json")), Wire.Json)!;
        FileLog.Start(Program.DataRoot);
        await using var engine = await Engine.Create(Program.DataRoot, settings);
        await using var directory = new DirectoryClient(engine);
        var watch = new Stopwatch();
        var stages = new List<(ConnectStage Stage, double At)>();
        directory.Stage += (_, stage) => { lock (stages) stages.Add((stage, watch.Elapsed.TotalSeconds)); };
        // as the window does: the client runs (presence, the ArdFerry measurements every 30 s), so a connection finds its ArdFerry
        // already measured
        directory.Start();
        var ready = Stopwatch.StartNew();
        while ((directory.Ferries?.Latest.Count ?? 0) == 0 && ready.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(200);
        Console.WriteLine($"client running; ArdFerry measured after {ready.Elapsed.TotalSeconds:F1} s");
        var totals = new List<double>();
        // each round starts from no session: a plain disconnect lets the device's auto-reconnect bring the session back at once (the
        // next round then finds it up and has no stages to time), so a round ends with a pause; the setting is restored at the end
        Peer? Peer() { lock (engine.State.Peers) return engine.State.Peers.FirstOrDefault(p => p.Code == code); }
        var autoConnect = Peer()?.AutoConnect;
        for (var round = 1; round <= count; round++)
        {
            lock (stages) stages.Clear();
            watch.Restart();
            try
            {
                await directory.Connect(code, "", CancellationToken.None, enrolling: false);
                var total = watch.Elapsed.TotalSeconds; totals.Add(total);
                string Span(ConnectStage from, ConnectStage to)
                {
                    lock (stages)
                    {
                        var a = from == ConnectStage.Lookup ? 0 : stages.FirstOrDefault(s => s.Stage == from).At;
                        var b = to == ConnectStage.Done ? total : stages.FirstOrDefault(s => s.Stage == to).At;
                        return $"{b - a,5:F2}";
                    }
                }
                Console.WriteLine($"TIME round {round}: total {total,5:F2} s | before signal {Span(ConnectStage.Lookup, ConnectStage.Signal)} | " +
                                  $"signal {Span(ConnectStage.Signal, ConnectStage.Link)} | link {Span(ConnectStage.Link, ConnectStage.Done)}");
            }
            catch (Exception ex) { Console.WriteLine($"FAIL round {round} after {watch.Elapsed.TotalSeconds:F2} s: {ex.Message}"); }
            if (Peer() is { } peer) await directory.Pause(peer); else foreach (var id in engine.Outgoing.Keys.ToArray()) await engine.Disconnect(id);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        if (Peer() is { } again && autoConnect is { } before) { again.AutoConnect = before; engine.State.Save(); }
        if (totals.Count > 0) Console.WriteLine($"SUMMARY {totals.Count}/{count} connected; average {totals.Average():F2} s, min {totals.Min():F2} s, max {totals.Max():F2} s");
        await Task.Delay(1500);   // the log writer flushes on its own pace
        return totals.Count == count ? 0 : 1;
    }
}
