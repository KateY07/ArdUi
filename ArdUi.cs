namespace ArdUi;

// ARD authenticates each hop; the v2 overlay authenticates and preserves the A–B session.
static class Program
{
    public static string Version
    {
        get
        {
            var value=typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? throw new InvalidOperationException("缺少程序集版本信息。");
            var match=Regex.Match(value,@"^(\d+)\.\d+\.\d+-pre\.(\d+)$");
            if(match.Success)return "v"+match.Groups[1].Value+".pre"+match.Groups[2].Value;
            var release=Regex.Match(value,@"^(\d+)\.(\d+)\.\d+$");
            if(!release.Success)throw new InvalidOperationException("程序集版本格式无效："+value);
            return "v"+release.Groups[1].Value+"."+release.Groups[2].Value;   // a release: v5.2
        }
    }
    // What goes over the wire to the directory server and to other machines. The version gate (since v5.pre1) reads "v<major>.pre<n>", so a release
    // v<major>.<minor> is announced as v<major>.pre<minor>: that is never older than the same major's earlier versions, and a server that
    // only knows the pre-release form keeps accepting it.
    public static string WireVersion
    {
        get
        {
            var match=Regex.Match(Version,@"^v(\d+)\.(\d+)$");
            return match.Success?"v"+match.Groups[1].Value+".pre"+match.Groups[2].Value:Version;
        }
    }
    public static string DataRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("ARDUI_DATA_ROOT") ?? Path.Combine(AppContext.BaseDirectory,"data"));
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Mark("Main entered");
        if(Environment.GetEnvironmentVariable("ARDUI_LANG")!=null)Loc.Init((string?)null);   // render-only: preview the interface in another language
        var dataRootArg=Array.IndexOf(args,"--data-root");
        if(dataRootArg>=0&&dataRootArg+1<args.Length)Environment.SetEnvironmentVariable("ARDUI_DATA_ROOT",args[dataRootArg+1]);
        try
        {
            if(args.Contains("--child-readiness-fixture"))
            {
                Console.Error.WriteLine("relay online");Console.Error.WriteLine("READY:");
                for(var i=0;i<500;i++)Console.Error.WriteLine("diagnostic flood "+i);
                Console.Error.WriteLine("fixture-flood-complete");
                Thread.Sleep(10000);return 0;
            }
            if (args.Length == 2 && args[0] == "--identity-store")
            { Console.WriteLine(IdentityStore.Prepare(args[1])); return 0; }
            if (args.Length == 4 && args[0] == "--verify-release")
            { Release.Verify(args[1],args[2],args[3]); return 0; }
            if (args.Length == 1 && args[0] == "--audit-test")
                return AuditTests.Run().GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--make-icon")
            { AppIcon.WriteIco(args[1]); return 0; }
            if (args.Length >= 1 && args[0] == "--netdiag")
            { using var output = Console.OpenStandardOutput(); output.Write(System.Text.Encoding.UTF8.GetBytes(NetDiag.Run(new NetDiagOptions { DirectoryUrl = new Settings().Server, RelayHostPort = new Uri(new Settings().Relay).Authority, Probe = args.Length > 1 ? (_ => NetProbeLive.Ticket(args[1], new Settings().Server)) : null, ProbeFallbackHost = new Uri(new Settings().Server).Host }).GetAwaiter().GetResult().ToText())); return 0; }
            if (args.Length >= 2 && args[0] == "--render-netdiag")
                return NetDiagPreview.Render(args[1], args.Length > 2 ? args[2] : null);
            if (args.Length >= 2 && args[0] == "--mapping-live")
                return NetProbeLive.Mapping(args[1], args.Length > 2 ? args[2] : new Settings().Server).GetAwaiter().GetResult();
            if (args.Length >= 2 && args[0] == "--probe-live")
                return NetProbeLive.Run(args[1], args.Length > 2 ? args[2] : new Settings().Server).GetAwaiter().GetResult();
            if (args.Contains("--input-test"))
                return InputTests.Run();
            if (args.Contains("--rtc-test"))
                return RtcTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--rtc-host"))
                return RtcTests.Host(args).GetAwaiter().GetResult();
            if (args.Contains("--rtc-join"))
                return RtcTests.Join(args).GetAwaiter().GetResult();
            if (args.Contains("--i18n-test"))
                return LocTests.Run();
            if (args.Contains("--voice-test"))
                return VoiceTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--netprobe-test"))
                return NetProbeTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--netdiag-test"))
                return NetDiagTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--chat-ui-test"))
                return ChatUiTests.Run();
            if (args.Contains("--chat-test"))
                return ChatTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--perm-test"))
                return PermissionTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--connect-flow-test"))
                return ConnectFlowTests.Run();
            if (args.Contains("--self-test"))
                return SelfTest.RunAsync().GetAwaiter().GetResult();
            if (args.Contains("--prototype-test"))
                return SelfTest.Prototype().GetAwaiter().GetResult();
            if (args.Contains("--transit-test"))
                return TransitTest.Run(args).GetAwaiter().GetResult();
            if (args.Contains("--frd-test"))
                return FrdTest.Run(args).GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--render-preview")
                return Preview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-island")
                return IslandPreview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-host-preview")
                return HostPreview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-placement")
                return PlacementPreview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-titlebars")
                return TitleBarPreview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-concepts")
                return ConceptPreview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-logo-preview")
                return LogoPreview.Render(args[1]);
            if (args.Length == 2 && args[0] == "--render-home-preview")
                return HomePreview.Render(args[1]);
            // A second launch (autostart, shortcut, installer) shows the running window instead of starting a clash.
            if(!SingleInstance.TryAcquire(DataRoot))return 0;
            StartupTrace.Mark("single instance acquired; building app");
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var path = Path.Combine(Path.GetTempPath(), "ArdUi-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {ex}\n");
            if (args.Length > 0) Console.Error.WriteLine(ex.Message);
            if (OperatingSystem.IsWindows() && args.Length == 0) MessageBox(0, ex.Message + "\n\n" + path, "ArdUi", 16);
            return 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(nint window, string text, string caption, uint type);
}
