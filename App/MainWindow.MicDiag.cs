namespace ArdUi;

// Settings > 麦克风诊断 (see Core/MicDiag.cs). Everything stays on this machine; nothing is sent anywhere.
sealed partial class MainWindow
{
    readonly StackPanel micResults = new() { Spacing = 8 };
    readonly TextBlock micStatus = new() { FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };
    readonly Border micMeter = new() { Height = 10, CornerRadius = new CornerRadius(5), Background = Brush.Parse("#2F7D4F"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    string micText = "";

    Control BuildMicDiagTab()
    {
        var test = new Button { Content = Look.IconText(MicPath, "测试麦克风"), MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left }; test.Classes.Add("primary");
        var echo = new Button { Content = "录音并回放" };
        var tone = new Button { Content = "播放测试音" };
        var copy = new Button { Content = Look.IconText(Look.CopyPath, "复制结果"), IsEnabled = false };
        ToolTip.SetTip(echo, "录 4 秒再从扬声器放出来，用来确认别人听到的是什么。请戴耳机，避免回声。");
        var track = new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = Look.Line, Child = micMeter, ClipToBounds = true };
        var meterBox = new Grid { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Children = { track } };
        var busy = false;
        void Busy(bool value) { busy = value; test.IsEnabled = echo.IsEnabled = tone.IsEnabled = !value; }
        void Show(params DiagSection[] sections)
        {
            micResults.Children.Clear(); foreach (var s in sections) micResults.Children.Add(DiagCard(s));
            micText = string.Join(Environment.NewLine + Environment.NewLine, sections.Select(s => s.Title + "：" + s.Summary + Environment.NewLine + string.Join(Environment.NewLine, s.Lines.Select(l => "  " + l.Label + " " + l.Value))));
            copy.IsEnabled = true;
        }
        void Level(double level) => Dispatcher.UIThread.Post(() => micMeter.Width = Math.Min(1, level * 3) * 360);
        async Task Run(bool play)
        {
            if (busy) return; Busy(true); micMeter.Width = 0;
            try
            {
                micStatus.Text = play ? "正在录音 4 秒，请说话…" : "正在测试 5 秒，请对着麦克风说话（看上面的音量条是否跳动）…";
                var (result, error, samples) = await MicDiag.Listen(TimeSpan.FromSeconds(play ? 4 : 5), Level, CancellationToken.None, keep: play);
                micMeter.Width = 0;
                Show(MicDiag.Judge(result, error), MicDiag.Privacy(), MicDiag.Devices());
                micStatus.Text = "测试完成 " + DateTime.Now.ToString("HH:mm:ss") + "。";
                if (play && error == null && samples.Length != 0) { micStatus.Text += "正在回放…"; await MicDiag.Play(samples, CancellationToken.None); micStatus.Text = "测试完成，回放结束。如果回放里听不到自己，说明对方也听不到。"; }
            }
            catch (Exception ex) { micStatus.Text = "测试失败：" + ex.Message; }
            finally { Busy(false); }
        }
        test.Click += async (_, _) => await Run(false);
        echo.Click += async (_, _) => await Run(true);
        tone.Click += async (_, _) =>
        {
            Busy(true); micStatus.Text = "正在从默认播放设备播放测试音…";
            try { await MicDiag.Play(MicDiag.Tone(), CancellationToken.None); micStatus.Text = "听到两声提示音说明扬声器正常；没有声音请检查默认播放设备和音量。"; }
            catch (Exception ex) when (ex is IOException or NAudio.MmException) { micStatus.Text = "无法播放：" + ex.Message; }
            finally { Busy(false); }
        };
        copy.Click += async (_, _) => { if (Clipboard != null) { await Clipboard.SetTextAsync(micText); Toast("诊断结果已复制"); } };
        Show(MicDiag.Privacy(), MicDiag.Devices()); copy.IsEnabled = false;
        var intro = new TextBlock { Text = "检查语音通话要用的麦克风和扬声器：列出 Windows 里的音频设备，检查隐私设置是否允许使用麦克风，并实际听 5 秒，看有没有声音、音量够不够、是否爆音或全是静音。声音只在本机处理，不会发送出去。", Foreground = Look.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { test, echo, tone, copy } };
        return new StackPanel { Spacing = 10, Children = { Card(new StackPanel { Spacing = 10, Children = { intro, buttons, meterBox, micStatus } }), micResults } };
    }
}
