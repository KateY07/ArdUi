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
    sealed class Prefs { public bool ReduceMotion { get; set; } public bool NoIsland { get; set; } public bool IslandInFullscreen { get; set; } public int? IslandX { get; set; } public int? IslandY { get; set; } public bool NoVoice { get; set; } public string? Language { get; set; } public bool NoReadReceipts { get; set; } }

    public static bool UserReduced { get; private set; }
    public static string Language { get; private set; } = "auto";   // "auto" (follow Windows), "zh" or "en"
    public static bool NoVoice { get; private set; }             // incoming voice calls are refused
    public static bool NoReadReceipts { get; private set; }      // the other side is not told which of its messages were read here
    public static bool NoIsland { get; private set; }            // the floating island is switched off
    public static (int X, int Y)? IslandPosition { get; private set; }   // where the user dragged the island to
    public static bool IslandInFullscreen { get; private set; }   // ...and may also show over full-screen apps and presentations
    public static bool Enabled { get; private set; }

    static Motion()
    {
        try { if (File.Exists(prefsPath) && JsonSerializer.Deserialize<Prefs>(File.ReadAllText(prefsPath), Wire.Json) is { } prefs) { UserReduced = prefs.ReduceMotion; NoIsland = prefs.NoIsland; NoVoice = prefs.NoVoice; NoReadReceipts = prefs.NoReadReceipts; Language = prefs.Language is "zh" or "en" ? prefs.Language : "auto"; IslandInFullscreen = prefs.IslandInFullscreen; if (prefs.IslandX is { } x && prefs.IslandY is { } y) IslandPosition = (x, y); } }
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
        UserReduced = reduced; Save();
    }

    public static void SetLanguage(string language) { Language = language is "zh" or "en" ? language : "auto"; Save(); }
    public static void SetVoice(bool allowed) { NoVoice = !allowed; Save(); }
    public static void SetReadReceipts(bool sent) { NoReadReceipts = !sent; Save(); }
    public static void SetIsland(bool shown, bool inFullscreen) { NoIsland = !shown; IslandInFullscreen = inFullscreen; Save(); }

    public static void SetIslandPosition((int X, int Y)? position) { IslandPosition = position; Save(); }

    static void Save() => File.WriteAllText(prefsPath, JsonSerializer.Serialize(new Prefs { ReduceMotion = UserReduced, NoIsland = NoIsland, NoVoice = NoVoice, NoReadReceipts = NoReadReceipts, Language = Language == "auto" ? null : Language, IslandInFullscreen = IslandInFullscreen, IslandX = IslandPosition?.X, IslandY = IslandPosition?.Y }, Wire.Json));
}
