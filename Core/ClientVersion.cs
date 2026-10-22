namespace ArdUi;

// Which ArdUi versions may take part: since v5.pre1 the directory server refuses older clients, and a controlled machine refuses a
// controller that does not announce a version at least this new (the version is part of the controller's signed connection request).
// v5.5 (announced as v5.pre5) signs each session's key exchange with the device keys; older versions cannot, so they are turned away early.
static class ClientVersion
{
    public const int MinMajor = 5, MinPre = 5;
    public static string Minimum => $"v{MinMajor}.pre{MinPre}";

    public static bool Allowed(string? version)
    {
        var match = Regex.Match(version ?? "", @"\Av(\d{1,3})\.pre(\d{1,3})\z");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value); var pre = int.Parse(match.Groups[2].Value);
        return major > MinMajor || major == MinMajor && pre >= MinPre;
    }
}
