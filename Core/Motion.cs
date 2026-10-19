using System.Runtime.InteropServices;

namespace ArdUi;

// Whether the interface animates. Off when the user chose "reduce animations" in ArdUi, or Windows animations are switched off;
// then every transition collapses to an instant change. Read once at start-up (styles are built from it).
static class Motion
{
    const uint SpiGetClientAreaAnimation = 0x1042;
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    static readonly string prefsPath = Path.Combine(Program.DataRoot, "ui.json");
    sealed class Prefs { public bool ReduceMotion { get; set; } }

    public static bool UserReduced { get; private set; }
    public static bool Enabled { get; private set; }

    static Motion()
    {
        try { if (File.Exists(prefsPath)) UserReduced = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(prefsPath), Wire.Json)?.ReduceMotion == true; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        var system = true;
        try { if (OperatingSystem.IsWindows() && SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var on, 0)) system = on; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        Enabled = !UserReduced && system;
    }

    public static TimeSpan Ms(int milliseconds) => Enabled ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;

    // Takes effect after ArdUi is restarted (the shared styles are built at start-up).
    public static void SetReduced(bool reduced)
    {
        UserReduced = reduced;
        File.WriteAllText(prefsPath, JsonSerializer.Serialize(new Prefs { ReduceMotion = reduced }, Wire.Json));
    }
}
