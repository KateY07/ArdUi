namespace ArdUi;

// Settings > 网络诊断: this machine's addresses and exits, the NAT type, what a direct (peer-to-peer) connection can expect, and what the
// router offers (UPnP, NAT-PMP). The run is read-only; the UPnP test mapping is a separate button and removes what it adds.
sealed partial class MainWindow
{
    readonly StackPanel netResults = new() { Spacing = 8 };
    readonly TextBlock netStatus = new() { FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };
    NetDiagReport? netReport;
    CancellationTokenSource? netRun;

    static readonly IBrush LevelGood = Brush.Parse("#2F7D4F"), LevelWarn = Brush.Parse("#D79A3B"), LevelBad = Brush.Parse("#D64545");
    // Asks the directory server for a probe token (bound to this machine's address); null when the directory is not available.
    Func<CancellationToken, Task<ProbeTicket?>>? ProbeSource()
    {
        var api = engine?.DirectoryApi; if (api == null) return null;
        return async ct =>
        {
            var reply = await api.Call<ProbeReply>("/api/v1/probe/start", new { }, ct);
            return new ProbeTicket(reply.Token, reply.Host, reply.Ports, reply.Ip, reply.Expires);
        };
    }

    static IBrush LevelBrush(DiagLevel level) => level switch { DiagLevel.Good => LevelGood, DiagLevel.Warn => LevelWarn, DiagLevel.Bad => LevelBad, _ => Look.Idle };

    Control BuildNetDiagTab()
    {
        var start = new Button { Content = Look.IconText(Look.DownloadPath, "开始检测"), MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
        start.Classes.Add("primary");
        var copy = new Button { Content = Look.IconText(Look.CopyPath, "复制结果"), MinWidth = 120, IsEnabled = false };
        var mapping = new Button { Content = "测试 UPnP 端口映射", IsEnabled = false };
        ToolTip.SetTip(mapping, "在路由器上临时添加一条 UDP 映射（租约 60 秒），读回确认后立即删除。");
        start.Click += async (_, _) =>
        {
            netRun?.Cancel(); var run = netRun = new CancellationTokenSource();
            start.IsEnabled = false; copy.IsEnabled = false; mapping.IsEnabled = false;
            netStatus.Text = "正在检测（约 30 秒）：本机地址、公网出口、NAT 类型、M 实测、路径、连通性、UPnP / NAT-PMP（路由器上会临时添加一条映射，M 从外部探测后立即删除）…";
            netResults.Children.Clear();
            try
            {
                var settings = engine?.Settings ?? new Settings();
                var report = await Task.Run(() => NetDiag.Run(new NetDiagOptions { DirectoryUrl = settings.Server, RelayHostPort = new Uri(settings.Relay).Authority, Probe = ProbeSource(), ProbeFallbackHost = new Uri(settings.Server).Host }, run.Token));
                if (run.IsCancellationRequested) return;
                netReport = report; Diagnostics.LastNetwork = report;
                foreach (var section in report.Sections) netResults.Children.Add(DiagCard(section));
                netStatus.Text = "检测完成 " + report.At.ToString("HH:mm:ss") + "。绿色表示正常，黄色需要留意，红色表示不可用。";
                copy.IsEnabled = true; mapping.IsEnabled = report.Gateways.Any(g => g.Control != null) || report.NatPmp.Count != 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { netStatus.Text = "检测失败：" + ex.Message; }
            finally { start.IsEnabled = true; }
        };
        copy.Click += async (_, _) => { if (netReport != null && Clipboard != null) { await Clipboard.SetTextAsync(netReport.ToText()); Toast("检测结果已复制（含本机 IP 地址，发给别人前请留意）"); } };
        mapping.Click += async (_, _) =>
        {
            var gateway = netReport?.Gateways.FirstOrDefault(g => g.Control != null); var pmp = netReport is { NatPmp.Count: > 0 } ? netReport.NatPmp[0] : default;
            if (gateway == null && pmp.Gateway == null) return;
            mapping.IsEnabled = false; netStatus.Text = "正在测试端口映射（添加映射 → M 从外部探测 → 删除 → 再探测，约 15 秒）…";
            try
            {
                var host = new Uri((engine?.Settings ?? new Settings()).Server).Host;
                var summaries = new List<string>();
                // NAT-PMP first, so that the UPnP card ends up on top (the more common of the two)
                if (pmp.Gateway != null)
                {
                    var section = await Task.Run(() => NetDiag.TestNatPmpMapping(pmp.Gateway, pmp.External, CancellationToken.None, ProbeSource(), host));
                    netResults.Children.Insert(0, DiagCard(section)); summaries.Add(section.Summary);
                }
                if (gateway != null)
                {
                    var section = await Task.Run(() => NetDiag.TestPortMapping(gateway, CancellationToken.None, ProbeSource(), host));
                    netResults.Children.Insert(0, DiagCard(section)); summaries.Add(section.Summary);
                }
                netStatus.Text = "端口映射测试完成：" + string.Join("；", summaries);
            }
            catch (Exception ex) { netStatus.Text = "端口映射测试失败：" + ex.Message; }
            finally { mapping.IsEnabled = true; }
        };
        var intro = new TextBlock
        {
            Text = "用来判断两台设备能否直接连接（点对点）：本机有几个公网出口、是什么类型的 NAT、路由器是否开放 UPnP 端口映射，以及到公网要经过几层内网。检测会访问公共的 STUN 服务器、几个“查询公网 IP”的网页和 ArdUi 的 M；如果路由器提供 UPnP 或 NAT-PMP，会临时添加一条 UDP 映射（租约 60 秒），让 M 从外部发包验证后立即删除。",
            Foreground = Look.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, copy, mapping } };
        return new StackPanel { Spacing = 10, Children = { Card(new StackPanel { Spacing = 10, Children = { intro, buttons, netStatus }, }), netResults } };
    }

    internal static Control DiagCard(DiagSection section)
    {
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new Ellipse { Width = 10, Height = 10, Fill = LevelBrush(section.Level), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = section.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(head);
        if (section.Summary.Length != 0) body.Children.Add(new TextBlock { Text = section.Summary, FontSize = 13, Foreground = Look.Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 0, 4) });
        var lines = new StackPanel { Spacing = 1, Margin = new Thickness(18, 0, 0, 0) };
        foreach (var line in section.Lines)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            var label = new TextBlock { Text = line.Label, FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
            var value = new SelectableTextBlock { Text = line.Value, FontSize = 12, Foreground = line.Level == DiagLevel.Info ? Look.Ink : LevelBrush(line.Level), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI") };
            Grid.SetColumn(value, 1); row.Children.Add(label); row.Children.Add(value); lines.Children.Add(row);
        }
        body.Children.Add(lines);
        return Card(body);
    }
}
