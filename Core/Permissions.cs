namespace ArdUi;

// What a controlling device may use on this machine. New authorisations get only FRD; grants stored before permissions
// existed carry no value and keep All.
[Flags]
enum Perm { None = 0, Frd = 1, Rdp = 2, Files = 4, All = 7 }

// The decision of the person at the controlled machine: accept at all, only for this session, and with which permissions.
sealed record Decision(bool Accepted, bool Once, Perm Perms)
{
    public static readonly Decision Rejected = new(false, false, Perm.None);
}

// The outcome of verifying a controller's password and approval: the signed grant sent back to the controller,
// what it may use, and whether the authorisation lasts only for this session (it is then never stored).
sealed record Authorization(SignedEnvelope Grant, Perm Perms, bool Once);

static class Permissions
{
    public const int FilesPort = 445;
    public const Perm Default = Perm.Frd;

    public static Perm Parse(int value) => (Perm)(value & (int)Perm.All);

    // File sharing is the SMB port; every other configured port (RDP) belongs to the RDP permission.
    public static int[] TcpPorts(Settings settings, Perm perms) =>
        settings.TcpPorts.Where(p => p == FilesPort ? perms.HasFlag(Perm.Files) : perms.HasFlag(Perm.Rdp)).ToArray();
    public static int[] UdpPorts(Settings settings, Perm perms) =>
        settings.UdpPorts.Where(p => p == FilesPort ? perms.HasFlag(Perm.Files) : perms.HasFlag(Perm.Rdp)).ToArray();

    public static string Describe(Perm perms)
    {
        var parts = new List<string>();
        if (perms.HasFlag(Perm.Frd)) parts.Add("FRD");
        if (perms.HasFlag(Perm.Rdp)) parts.Add("RDP");
        if (perms.HasFlag(Perm.Files)) parts.Add("文件");
        return parts.Count == 0 ? "无" : string.Join(" / ", parts);
    }
}
