namespace ArdUi;

// Legacy text compatibility: only selected-path events can publish a current path.
sealed class ArdPathState
{
    public string Network{get;set;}="正在连接";
    public double? Rtt{get;set;}
    public void Observe(string line)
    {
        if(line.Contains("selected network path closed",StringComparison.Ordinal)||line.Contains("network path log events were dropped",StringComparison.Ordinal)||line.Contains("Lagged",StringComparison.Ordinal))
        {Network="路径恢复中 / 状态待确认";Rtt=null;return;}
        if(!line.Contains("CONNECTED",StringComparison.Ordinal)&&!line.Contains("network path selected",StringComparison.Ordinal))return;
        var transport=Regex.Match(line,"transport=\"(?<v>direct|relay)\"");
        var kind=Regex.Match(line,"network=\"(?<v>ipv4|ipv6|relay)\"");
        if(!transport.Success)return;
        Network=transport.Groups["v"].Value=="direct"?"P2P 直连 / "+(kind.Success?kind.Groups["v"].Value.ToUpperInvariant():"IP"):"ArdRelay 中继";
        var rtt=Regex.Match(line,@"rtt_ms=(?:Some\()?(?<v>[0-9]+(?:\.[0-9]+)?)");
        Rtt=rtt.Success&&double.TryParse(rtt.Groups["v"].Value,System.Globalization.CultureInfo.InvariantCulture,out var value)?value:null;
    }
}
