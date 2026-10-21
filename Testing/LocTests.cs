using Avalonia.Headless;

namespace ArdUi;

// `ArdUi.exe --i18n-test`: the English table (no Chinese left in an English text, templates fill their values, phrases combine), and the
// hook that translates what is put on screen while leaving the user's own text alone.
static class LocTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    public static int Run()
    {
        Loc.Init(false);
        Check(Loc.T("连接") == "连接" && !Loc.English, "in Chinese mode every text stays as it is");
        Check(Loc.Resolve("en") && !Loc.Resolve("zh"), "the language can be chosen explicitly");

        Loc.Init(true);
        var cjk = new System.Text.RegularExpressions.Regex(@"[一-鿿]");
        var bad = Loc.Table.Where(p => cjk.IsMatch(p.En) && p.En != "简体中文").Select(p => p.Zh).ToList();
        Check(bad.Count == 0, "no English text still holds Chinese" + (bad.Count == 0 ? "" : ": " + bad[0]));
        Check(Loc.Table.Select(p => p.Zh).Distinct().Count() == Loc.Table.Length, "every Chinese text appears once in the table");
        Check(Loc.T("取消") == "Cancel", "an exact text is translated");
        Check(Loc.T("连接") == "Connect", "even a single word");
        Check(Loc.T("12 分钟前") == "12 min ago", "a template takes its value (" + Loc.T("12 分钟前") + ")");
        Check(Loc.T("已移除 K4DD0B，可在 8 秒内撤销。") == "K4DD0B removed; you can undo within 8 seconds.", "a template with a code in the middle (" + Loc.T("已移除 K4DD0B，可在 8 秒内撤销。") + ")");
        Check(Loc.T("在线 · 最近连接 3 分钟前") == "Online · last connected 3 min ago", "the values of a template are translated too (" + Loc.T("在线 · 最近连接 3 分钟前") + ")");
        Check(!cjk.IsMatch(Loc.T("正在连接 K4DD0B（首次授权）")), "pieces put together by the code are translated phrase by phrase (" + Loc.T("正在连接 K4DD0B（首次授权）") + ")");
        Check(Loc.T("A7B2K9 failed") == "A7B2K9 failed", "English text is returned as it is");
        Check(Loc.T("Windows 麦克风权限") == "Windows microphone permission", "a name with Latin letters in it");
        Check(Loc.T("连接失败：超时") == "Connection failed: 超时" || Loc.T("连接失败：超时").StartsWith("Connection failed:"), "a Chinese error text keeps its punctuation in English form (" + Loc.T("连接失败：超时") + ")");

        Environment.SetEnvironmentVariable("ARDUI_PREVIEW_HOST", "1");
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        Loc.Install();
        var block = new TextBlock { Text = "允许被控" };
        Check(block.Text == "Allow control", "a text block is translated when its text is set (" + block.Text + ")");
        block.Text = "对方不在线"; Check(block.Text == "The other side is offline", "and when it changes");
        var mine = new TextBlock { [Loc.KeepProperty] = true, Text = "你好，连接一下" };
        Check(mine.Text == "你好，连接一下", "text marked as the user's own is never touched");
        var button = new Button { Content = "断开" };
        var window = new Window { Content = button, Title = "设置" };
        window.Show();
        Check(window.Title == "Settings", "a window title is translated");
        ToolTip.SetTip(button, "关闭"); Check(ToolTip.GetTip(button) as string == "Close", "a tooltip is translated");
        var box = new TextBox { Watermark = "取消" }; Check(box.Watermark == "Cancel", "a watermark is translated");
        var run = new Avalonia.Controls.Documents.Run { Text = "在线" }; Check(run.Text == "Online", "a run inside a text block is translated");
        var keptRun = new Avalonia.Controls.Documents.Run { [Loc.KeepRunProperty] = true, Text = "在线" }; Check(keptRun.Text == "在线", "and a run with a name in it is not");
        window.Close();

        Console.WriteLine(failures == 0 ? "PASS: interface language." : failures + " failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
