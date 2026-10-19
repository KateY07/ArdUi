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
        // The launcher chooses the data folder with an environment variable; the entry passes the same folder explicitly.
        var command = string.IsNullOrEmpty(root) ? $"\"{exe}\" --tray" : $"\"{exe}\" --data-root \"{root}\" --tray";
        key.SetValue(Name, command);
    }
}
