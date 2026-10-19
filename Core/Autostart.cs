using Microsoft.Win32;

namespace ArdUi;

// "Start with Windows": a per-user Run entry that starts ArdUi hidden in the tray, keeping the data folder this copy uses.
static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "ArdUi";

    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) is string; }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!on) { key.DeleteValue(Name, false); return; }
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径。");
        var root = Environment.GetEnvironmentVariable("ARDUI_DATA_ROOT");
        // The data folder is chosen by an environment variable in the launcher, so the entry sets it the same way.
        var command = string.IsNullOrEmpty(root)
            ? $"\"{exe}\" --tray"
            : $"cmd.exe /c \"set \"ARDUI_DATA_ROOT={root}\"&& start \"\" \"{exe}\" --tray\"";
        key.SetValue(Name, command);
    }
}
