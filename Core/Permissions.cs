namespace ArdUi;

// What a controlling device may use on this machine, and what either side may do inside the session (the same set applies both ways:
// a host that does not allow files neither receives nor sends them). Bit 4 was SMB file sharing (removed in v5.5) and is never reused.
[Flags]
enum Perm { None = 0, Frd = 1, Rdp = 2, Chat = 8, Files = 16, Voice = 32, All = Frd | Rdp | Chat | Files | Voice }

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
    // RDP is the only port forward left. The SMB port stays closed even when an old config.json still lists it.
    public const int RetiredFilesPort = 445;
    // New authorisations: the desktop through FRD, and the conversation, files and calls as before.
    public const Perm Default = Perm.Frd | Perm.Chat | Perm.Files | Perm.Voice;
    // Stored values carry this bit since v5.5. Without it a value comes from an older version that knew only FRD / RDP / SMB and never
    // restricted the conversation, so the conversation, files and calls stay allowed for it.
    const int Explicit = 0x100;
    public const int Legacy = 7;   // what a grant or reply from before permissions existed means: everything that version knew

    public static Perm Parse(int value) =>
        (value & Explicit) != 0 ? (Perm)(value & (int)Perm.All) : (Perm)(value & (int)(Perm.Frd | Perm.Rdp)) | Perm.Chat | Perm.Files | Perm.Voice;
    public static int Store(Perm perms) => (int)(perms & Perm.All) | Explicit;

    public static int[] TcpPorts(Settings settings, Perm perms) =>
        perms.HasFlag(Perm.Rdp) ? settings.TcpPorts.Where(p => p != RetiredFilesPort).ToArray() : [];
    public static int[] UdpPorts(Settings settings, Perm perms) =>
        perms.HasFlag(Perm.Rdp) ? settings.UdpPorts.Where(p => p != RetiredFilesPort).ToArray() : [];

    public static string Describe(Perm perms)
    {
        var parts = new List<string>();
        if (perms.HasFlag(Perm.Frd)) parts.Add("FRD");
        if (perms.HasFlag(Perm.Rdp)) parts.Add("RDP");
        if (perms.HasFlag(Perm.Chat)) parts.Add("对话");
        if (perms.HasFlag(Perm.Files)) parts.Add("文件");
        if (perms.HasFlag(Perm.Voice)) parts.Add("通话");
        return parts.Count == 0 ? "无" : string.Join(" / ", parts);
    }
}
