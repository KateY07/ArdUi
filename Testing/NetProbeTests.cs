using System.Security.Cryptography;
using System.Text;

namespace ArdUi;

// `ArdUi.exe --netprobe-test`: the C# probe client against the real Python probe service (server/probe_udp.py) over loopback. The test also mints
// its own tokens with the same HMAC rule, which checks that the two languages agree on the format. Skipped when Python is not installed.
static class NetProbeTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    // The same token the directory server mints: nonce 8, expiry 4, first 16 bytes of HMAC-SHA256(key, "ARDP-token2\0" + nonce + expiry).
    static byte[] Mint(byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        var expiry = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(expiry, (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120));
        var message = Encoding.ASCII.GetBytes("ARDP-token2\0").Concat(nonce).Concat(expiry).ToArray();
        return nonce.Concat(expiry).Concat(HMACSHA256.HashData(key, message).Take(16)).ToArray();
    }

    static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    static int FreePair()
    {
        while (true)
        {
            using var a = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); a.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)a.LocalEndPoint!).Port;
            using var b = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { b.Bind(new IPEndPoint(IPAddress.Loopback, port + 1)); return port; } catch (SocketException) { }
        }
    }

    static string? FindPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (process != null && process.WaitForExit(5000) && process.ExitCode == 0) return name;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    // A router that speaks just enough NAT-PMP: maps UDP ports (the external port is the internal one) and deletes them again.
    static (UdpClient Socket, CancellationTokenSource Stop, Dictionary<int, int> Mappings) FakePmp()
    {
        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var stop = new CancellationTokenSource(); var mappings = new Dictionary<int, int>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(stop.Token);
                    if (packet.Buffer.Length >= 12 && packet.Buffer[1] == 1)
                    {
                        var internalPort = packet.Buffer[4] << 8 | packet.Buffer[5]; var lifetime = packet.Buffer[8] << 24 | packet.Buffer[9] << 16 | packet.Buffer[10] << 8 | packet.Buffer[11];
                        if (lifetime == 0) mappings.Remove(internalPort); else mappings[internalPort] = internalPort;
                        var reply = new byte[16]; reply[1] = 129; reply[10] = packet.Buffer[4]; reply[11] = packet.Buffer[5]; reply[8] = packet.Buffer[4]; reply[9] = packet.Buffer[5];
                        reply[12] = packet.Buffer[8]; reply[13] = packet.Buffer[9]; reply[14] = packet.Buffer[10]; reply[15] = packet.Buffer[11];
                        // bytes 8-9 internal port, 10-11 mapped external port
                        await socket.SendAsync(reply, packet.RemoteEndPoint, stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        });
        return (socket, stop, mappings);
    }

    public static async Task<int> Run()
    {
        var python = FindPython();
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "server", "probe_udp.py"));
        if (!File.Exists(script)) script = Path.GetFullPath(Path.Combine("server", "probe_udp.py"));
        if (python == null || !File.Exists(script)) { Console.WriteLine("skip (Python or server/probe_udp.py not found)"); return 0; }
        var folder = Path.Combine(Path.GetTempPath(), "ArdUi-netprobe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var keyPath = Path.Combine(folder, "probe-key"); var key = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(keyPath, key);
        var first = FreePair();
        var info = new ProcessStartInfo(python, "\"" + script + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.Environment["ARDUI_PROBE_KEY"] = keyPath; info.Environment["ARDUI_PROBE_PORT"] = first.ToString(); info.Environment["ARDUI_PROBE_BIND"] = "127.0.0.1"; info.Environment["ARDUI_PROBE_ALLOW_PRIVATE"] = "1";
        using var server = Process.Start(info)!;
        try
        {
            var ready = await Task.WhenAny(server.StandardOutput.ReadLineAsync(), Task.Delay(8000));
            if (ready is not Task<string?> line || (await line)?.Contains("listening", StringComparison.Ordinal) != true) { Check(false, "the probe service starts"); return 1; }
            Check(true, "the probe service starts");
            ProbeTicket Ticket() => new(Hex(Mint(key)), "127.0.0.1", [first, first + 1], "127.0.0.1", 0);
            var (section, findings) = await NetProbe.Verify(Ticket(), "", CancellationToken.None);
            Check(findings.Seen0 != null && findings.Seen0.Address.Equals(IPAddress.Loopback), "a token minted in C# is accepted by the Python service (after the cookie proof), and M reports the address it saw");
            Check(findings.Nat == "none", "no NAT between this machine and a server on the same address (mapped address is local)");
            Check(findings.Seen1 != null && findings.Seen0 != null && findings.Seen1.Address.Equals(findings.Seen0.Address), "the second server port also answers");
            Check(findings.PortRestricted == false, "a packet from the other server port comes through");
            Check(findings.Exits.Count == 1 && findings.Exits[0].Equals(IPAddress.Loopback), "several sockets all leave from one proven address: a single exit");
            Check(findings.Loss == 0 && findings.Rtt >= 0, "40 echo packets round-trip without loss");
            Check(findings.DownLoss is >= 0 and <= 5, "the downlink burst arrives (" + findings.DownLoss + "% lost)");
            Check(findings.DirectExposure == true, "an unsolicited packet reaches a listening port (loopback is directly reachable)");
            Check(section.Level == DiagLevel.Warn && section.Lines.Any(l => l.Label.Contains("入站", StringComparison.Ordinal) && l.Level == DiagLevel.Warn), "directly reachable is reported as a warning (the machine is exposed)");

            // the UPnP verification path: a mapped port that M reaches from the outside
            Func<CancellationToken, Task<ProbeTicket?>> source = _ => Task.FromResult<ProbeTicket?>(Ticket());
            var port = FreePair() + 7;
            var lines = await NetProbe.VerifyUdpMapping(source, "", port, true, CancellationToken.None);
            Check(lines.Count == 1 && lines[0].Level == DiagLevel.Good, "M's probe to a mapped (listening) port is reported as arrived");
            var named = await NetProbe.VerifyUdpMapping(source, "", FreePair() + 9, true, CancellationToken.None, IPAddress.Loopback, 0, "UPnP 映射");
            Check(named.Count == 1 && named[0].Level == DiagLevel.Good && named[0].Value.Contains("127.0.0.1", StringComparison.Ordinal), "the probe goes to the router's WAN address when it is one this machine leaves from");
            var elsewhere = await NetProbe.VerifyUdpMapping(source, "", FreePair() + 11, true, CancellationToken.None, IPAddress.Parse("203.0.113.9"), 0, "UPnP 映射");
            Check(elsewhere.Count == 1 && elsewhere[0].Level == DiagLevel.Warn && elsewhere[0].Value.Contains("203.0.113.9", StringComparison.Ordinal), "a WAN address this machine's packets never leave from cannot be probed, and the result says why");

            // NAT-PMP: add, probe from the outside, delete, probe again
            var (pmp, stopPmp, mappings) = FakePmp();
            var pmpPort = ((IPEndPoint)pmp.Client.LocalEndPoint!).Port;
            var test = await NetDiag.TestNatPmpMapping(IPAddress.Loopback, IPAddress.Loopback, CancellationToken.None, source, "", pmpPort);
            Check(test.Lines.Any(l => l.Label == "添加映射" && l.Level == DiagLevel.Good) && test.Lines.Any(l => l.Label == "删除映射" && l.Value == "已删除"), "a NAT-PMP mapping is added and removed");
            Check(mappings.Count == 0, "the fake NAT-PMP router has no mapping left behind");
            Check(test.Lines.Any(l => l.Label.Contains("从外部探测NAT-PMP 映射端口", StringComparison.Ordinal) && l.Level == DiagLevel.Good), "M's probe through the NAT-PMP mapping arrives");
            stopPmp.Cancel(); pmp.Dispose();

            // TCP
            using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            using var control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); control.Bind(new IPEndPoint(IPAddress.Any, 0));
            var tcpToken = Mint(key); uint sequence = 100; var tcpServer = new IPEndPoint(IPAddress.Loopback, first);
            Check(await NetProbe.Prove(control, tcpServer, tcpToken, () => ++sequence, CancellationToken.None) != null, "the control socket proves its address");
            var (connected, acked) = await NetProbe.ProbeTcpInbound(control, tcp, tcpServer, tcpToken, 5, CancellationToken.None);
            Check(connected && acked, "M connects to a listening TCP port and gets its nonce back");

            // a token the service did not mint
            var forged = Ticket() with { Token = Hex(RandomNumberGenerator.GetBytes(28)) };
            var (refused, none) = await NetProbe.Verify(forged, "", CancellationToken.None, quick: true);
            Check(refused.Level == DiagLevel.Bad && none.Seen0 == null, "a forged token gets no answer from M");

            // nobody listening
            var (silent, _) = await NetProbe.Verify(Ticket() with { Ports = [FreePair(), 0] }, "", CancellationToken.None, quick: true);
            Check(silent.Level == DiagLevel.Bad, "no service on those ports is reported, not hidden");
        }
        finally { try { server.Kill(true); } catch (InvalidOperationException) { } try { Directory.Delete(folder, true); } catch (IOException) { } }
        return failures == 0 ? 0 : 1;
    }
}
