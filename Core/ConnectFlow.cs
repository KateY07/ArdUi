namespace ArdUi;

// What the user sees while a connection is being made, in order. Technical detail goes to the log, not here.
enum ConnectStage { Lookup, Verify, Signal, Link, Done }

// Why a connection failed, reduced to what the user can act on.
enum ConnectFailure
{
    NoPermission,      // wrong / expired password, fingerprint refused, or remote access switched off on the other side: ask for the password again
    NotFound,          // no such device code
    Offline,           // the device is known but not reachable now
    SecurityWarning,   // the device's identity changed: never retry automatically
    Declined,          // the user did not confirm the fingerprint
    Yourself,          // own device code
    Network,           // our own network or the directory service
    Other,
}

sealed record ConnectProblem(ConnectFailure Kind, string Title, string Hint, bool AskPassword);

static class ConnectFlow
{
    // The host replies with one merged text; the controller cannot tell the three causes apart.
    public const string RejectedText = "密码错误、对端拒绝了首次指纹确认，或远程访问已关闭。";

    public static string Text(ConnectStage stage) => stage switch
    {
        ConnectStage.Lookup => "正在查找设备…",
        ConnectStage.Verify => "正在核对设备身份…",
        ConnectStage.Signal => "正在等待对方响应…",
        ConnectStage.Link => "正在建立加密连接…",
        _ => "已连接",
    };

    // Device codes are six letters / digits; anything else is rejected before touching the network.
    public static string? NormaliseCode(string? text)
    {
        var code = new string((text ?? "").Where(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z').ToArray()).ToUpperInvariant();   // ASCII only
        return code.Length == 6 && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9') ? code : null;
    }

    public static ConnectProblem Classify(Exception ex)
    {
        var text = ex.Message;
        if (ex is OperationCanceledException)
            return text.Contains("核对", StringComparison.Ordinal)
                ? new(ConnectFailure.Declined, "未核对设备身份", "没有确认对方的 EndpointId，连接已取消。", false)
                : new(ConnectFailure.Other, "连接已取消", "", false);
        if (text.Contains("EndpointId 已改变", StringComparison.Ordinal) || text.Contains("签名", StringComparison.Ordinal) && text.Contains("已阻止", StringComparison.Ordinal))
            return new(ConnectFailure.SecurityWarning, "设备身份与上次不一致", "这可能是对方重装了系统，也可能有人冒充。请通过电话等独立渠道核对后再处理，不要继续连接。", false);
        if (text.Contains(RejectedText, StringComparison.Ordinal) || text.Contains("访问未获批准", StringComparison.Ordinal) || text.Contains("未接受连接", StringComparison.Ordinal))
            return new(ConnectFailure.NoPermission, "没有权限", "密码不正确或已过期，或对方已关闭“允许被控”。请向对方索取新的连接密码。", true);
        if (text.Contains("编号不存在", StringComparison.Ordinal) || text.Contains("机器编号不匹配", StringComparison.Ordinal))
            return new(ConnectFailure.NotFound, "找不到这台设备", "请检查 6 位设备 ID 是否输入正确。", false);
        if (text.Contains("不能连接本机", StringComparison.Ordinal))
            return new(ConnectFailure.Yourself, "这是本机", "请输入另一台设备的 ID。", false);
        if (text.Contains("离线", StringComparison.Ordinal) || text.Contains("未开放远程访问", StringComparison.Ordinal))
            return new(ConnectFailure.Offline, "对方不在线", "对方的电脑可能已关机、没有联网，或没有运行 ArdUi。", false);
        if (ex is HttpRequestException or TimeoutException or System.Net.Sockets.SocketException || text.Contains("暂时不可用", StringComparison.Ordinal) || text.Contains("时间相差", StringComparison.Ordinal))
            return new(ConnectFailure.Network, "网络不通", "请检查本机网络；稍后再试。", false);
        return new(ConnectFailure.Other, "连接失败", text, false);
    }
}
