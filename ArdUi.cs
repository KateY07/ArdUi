namespace ArdUi;

// ARD authenticates each hop; the v2 overlay authenticates and preserves the A–B session.
static class Program
{
    public const string Version = "v2.pre3";
    public static string DataRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("ARDUI_DATA_ROOT") ?? Path.Combine(AppContext.BaseDirectory,"data"));
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--identity-store")
            { Console.WriteLine(IdentityStore.Prepare(args[1])); return 0; }
            if (args.Length == 4 && args[0] == "--verify-release")
            { Release.Verify(args[1],args[2],args[3]); return 0; }
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
