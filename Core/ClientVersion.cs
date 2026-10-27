namespace ArdUi;

// Which ArdUi versions may take part: since v5.pre1 the directory server refuses older clients, and a controlled machine refuses a
// controller that does not announce a version at least this new (the version is part of the controller's signed connection request).
// v5.5 (announced as v5.pre5) signs each session's key exchange with the device keys; older versions cannot, so they are turned away early.
static class ClientVersion
{
    // v6.6 (announced as v6.pre6): the conversation carries files and calls, "文件" is the shared folder (older versions read the
    // permissions differently), messages are acknowledged, resumed and marked read. v6.7: accounts (only a signed-in device may
    // connect to others; older versions cannot sign in). v6.10: signing in only on M's account page (v6.7–v6.9 sign in inside ArdUi,
    // which M no longer accepts, and their updates were silent).
    public const int MinMajor = 6, MinPre = 10;
    public static string Minimum => $"v{MinMajor}.pre{MinPre}";

    public static bool Allowed(string? version)
    {
        var match = Regex.Match(version ?? "", @"\Av(\d{1,3})\.pre(\d{1,3})\z");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value); var pre = int.Parse(match.Groups[2].Value);
        return major > MinMajor || major == MinMajor && pre >= MinPre;
    }
}
