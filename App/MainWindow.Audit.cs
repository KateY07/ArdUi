namespace ArdUi;

// Settings > "被控与授权" > 审计记录: what is kept of the sessions of controlling devices (read only; the budget manages itself).
sealed partial class MainWindow
{
    readonly TextBlock auditUsage = new() { FontSize = 12, Foreground = Look.Muted };
    readonly Border auditFill = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = Look.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Border auditTrack = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = Look.Ghost };
    readonly StackPanel auditRows = new() { Spacing = 6 };

    Control BuildAuditCard()
    {
        var open = new Button { Content = Look.IconText(Look.FolderPath, "打开审计文件夹"), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => { if (engine != null) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + engine.Audit.Store.Root + "\"") { UseShellExecute = true }); };
        var bar = new Grid { Children = { auditTrack, auditFill } };
        var note = new TextBlock { Text = "有设备控制本机期间自动录屏（10 帧/秒、主显示器），连同对话一起保存在本机，总量上限 10 GB，超出后自动覆盖最旧的内容。此功能不可关闭。", FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };
        return Card(new StackPanel { Spacing = 10, Children = { note, bar, auditUsage, auditRows, open } });
    }

    void RefreshAudit()
    {
        if (engine == null) return;
        var store = engine.Audit.Store;
        _ = Task.Run(() =>
        {
            long used = 0; var rows = new List<(string Text, bool Failed)>();
            try
            {
                used = store.UsedBytes();
                var index = Path.Combine(store.Root, "index.jsonl");
                if (File.Exists(index))
                    foreach (var line in File.ReadAllLines(index).TakeLast(8).Reverse())
                    {
                        using var json = JsonDocument.Parse(line); var e = json.RootElement;
                        var started = e.GetProperty("started").GetDateTimeOffset().ToLocalTime(); var ended = e.GetProperty("ended").GetDateTimeOffset().ToLocalTime();
                        var span = ended - started; var duration = span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分" : $"{Math.Max(1, (int)span.TotalMinutes)} 分钟";
                        var once = e.TryGetProperty("once", out var o) && o.GetBoolean(); var failed = e.TryGetProperty("failed", out var f) && f.GetBoolean();
                        rows.Add(($"{started:MM-dd HH:mm}  {e.GetProperty("code").GetString()}  {duration}  {SizeText(e.GetProperty("bytes").GetInt64())}" + (once ? "  · 仅此一次" : "") + (failed ? "  · 录屏中断" : ""), failed));
                    }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            Dispatcher.UIThread.Post(() =>
            {
                auditUsage.Text = $"已用 {used / 1073741824.0:F2} GB / {AuditStore.MaxBytes / 1073741824} GB";
                auditFill.Width = Math.Max(0, Math.Min(1, (double)used / AuditStore.MaxBytes)) * Math.Max(0, auditTrack.Bounds.Width);
                auditRows.Children.Clear();
                foreach (var (text, failed) in rows) auditRows.Children.Add(new TextBlock { Text = text, FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = failed ? Brush.Parse("#B3432B") : Look.Ink });
                if (rows.Count == 0) auditRows.Children.Add(new TextBlock { Text = "还没有被控记录。", FontSize = 12, Foreground = Look.Muted });
            });
        });
    }
}
