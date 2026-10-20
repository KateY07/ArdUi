using System.Reflection;
using Avalonia.Headless;
using Avalonia.Input;

namespace ArdUi;

// `ArdUi.exe --chat-ui-test`: the keys of the conversation box in a real (headless) window.
static class ChatUiTests
{
    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }
    static void Pump() { for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } }

    // The island's rules with a fake clock: hidden with nothing to show, a message is a bubble for exactly four seconds, then only a count.
    static void IslandRules()
    {
        var clock = DateTimeOffset.Now; var model = new IslandModel(() => clock);
        Check(!model.Visible, "island hidden when no device is connected");
        model.SetConnected(1); Check(model.Visible && !model.BubbleVisible, "island shows a resting pill when connected");
        model.SetUnread(1, "p"); model.MessageArrived("p", "K4DD0B", "hi");
        Check(model.BubbleVisible, "a message opens the bubble");
        clock += TimeSpan.FromMilliseconds(3900); model.Tick(); Check(model.BubbleVisible, "bubble still up at 3.9 s");
        clock += TimeSpan.FromMilliseconds(200); model.Tick(); Check(!model.BubbleVisible && model.Unread == 1, "bubble gone after 4 s, unread stays");
        model.MessageArrived("p", "K4DD0B", "again"); model.DismissBubble(); Check(!model.BubbleVisible, "opening the chat dismisses the bubble");
        model.SetConnected(0); Check(model.Visible, "unread keeps the island up after the device left");
        model.SetUnread(0); Check(!model.Visible, "island hides when nothing is left");
        model.SetConnected(1); model.SetSuppressed(true); Check(!model.Visible, "full screen hides the island");
    }

    public static int Run()
    {
        IslandRules();
        Environment.SetEnvironmentVariable("ARDUI_PREVIEW_HOST", "1");
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        var window = new MainWindow(true); window.Show();
        try
        {
            Pump();
            var type = typeof(MainWindow); const BindingFlags all = BindingFlags.NonPublic | BindingFlags.Instance;
            var sent = new List<string>();
            var channel = new ChatChannel(Path.Combine(Path.GetTempPath(), "ArdUi-chatui-" + Guid.NewGuid().ToString("N")), () => (method, value, ct) =>
            { sent.Add(method + ":" + JsonSerializer.Serialize(value)); return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true })); });
            var input = (TextBox)type.GetField("chatInput", all)!.GetValue(window)!;
            type.GetField("openChat", all)!.SetValue(window, channel);
            ((Border)type.GetField("chatOverlay", all)!.GetValue(window)!).IsVisible = true;
            Pump(); input.Focus(); Pump();

            input.Text = "你好";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
            Check(channel.Messages.Count == 1 && channel.Messages[0].Text == "你好" && sent.Count == 1, "Enter sends the message");
            Check(string.IsNullOrEmpty(input.Text), "and empties the box");

            input.Text = "第一行"; input.CaretIndex = input.Text.Length;
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null); Pump();
            Check(channel.Messages.Count == 1 && (input.Text ?? "").Contains('\n'), "Shift+Enter makes a new line and sends nothing");

            // a picture or file is staged first and only goes out when the message is sent
            var file = Path.Combine(Path.GetTempPath(), "ArdUi-chatui-" + Guid.NewGuid().ToString("N") + ".bin"); File.WriteAllBytes(file, new byte[300_000]);
            type.GetMethod("Stage", all)!.Invoke(window, [file]); Pump();
            var tray = (WrapPanel)type.GetField("attachTray", all)!.GetValue(window)!;
            Check(tray.IsVisible && tray.Children.Count == 1 && !sent.Any(m => m.StartsWith("chat-file-begin", StringComparison.Ordinal)), "a staged file is shown under the box and not sent yet");
            input.Text = "看看这个文件";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
            for (var i = 0; i < 100 && !sent.Any(m => m.StartsWith("chat-file-end", StringComparison.Ordinal)); i++) { Thread.Sleep(20); Pump(); }
            Check(sent.Any(m => m.StartsWith("chat-file-begin", StringComparison.Ordinal)) && sent.Any(m => m.StartsWith("chat-file-end", StringComparison.Ordinal)), "Enter sends the text and then the staged file");
            Check(!tray.IsVisible && channel.Messages.Any(m => m.Kind == ChatKind.File), "the tray is empty afterwards and the file is in the conversation");
            File.Delete(file);

            var before = channel.Messages.Count; input.Text = "  ";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
            Check(channel.Messages.Count == before, "an empty message is not sent");
        }
        finally { window.Close(); }
        return failures == 0 ? 0 : 1;
    }
}
