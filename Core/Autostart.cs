using Microsoft.Win32;

namespace ArdUi;

// "Start with Windows". Two things can start ArdUi at sign-in: the per-user Run entry this setting writes (it starts ArdUi hidden in
// the tray and keeps the data folder this copy uses), and the Startup-folder shortcut that older installers created unconditionally.
// The setting reflects both, so what the switch shows is what really happens; switching it off removes both.
static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "ArdUi";

    static string LegacyShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ArdUi.lnk");

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string || File.Exists(LegacyShortcut);
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!on)
        {
            key.DeleteValue(Name, false);
            DeleteLegacyShortcut();
            return;
        }
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径。");
        var root = Environment.GetEnvironmentVariable("ARDUI_DATA_ROOT");
        var command = string.IsNullOrEmpty(root) ? $"\"{exe}\" --tray" : $"\"{exe}\" --data-root \"{root}\" --tray";
        key.SetValue(Name, command);
        DeleteLegacyShortcut();   // one mechanism only, so ArdUi is not started twice
    }

    static void DeleteLegacyShortcut()
    {
        try { if (File.Exists(LegacyShortcut)) File.Delete(LegacyShortcut); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
