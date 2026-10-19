namespace ArdUi;

// `ArdUi.exe --connect-flow-test`: the failure classes and the code parser behave as the UI relies on.
static class ConnectFlowTests
{
    public static int Run()
    {
        var failures = 0;
        void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }
        ConnectFailure Kind(Exception ex) => ConnectFlow.Classify(ex).Kind;

        Check(Kind(new IOException(ConnectFlow.RejectedText)) == ConnectFailure.NoPermission, "rejected → NoPermission");
        Check(ConnectFlow.Classify(new IOException(ConnectFlow.RejectedText)).AskPassword, "NoPermission asks for the password again");
        Check(Kind(new IOException("对端未接受连接，请检查允许访问开关。")) == ConnectFailure.NoPermission, "ticket rejected → NoPermission");
        Check(Kind(new IOException("机器编号不存在。")) == ConnectFailure.NotFound, "unknown code → NotFound");
        Check(Kind(new IOException("对端离线或未开放远程访问。")) == ConnectFailure.Offline, "offline → Offline");
        Check(!ConnectFlow.Classify(new IOException("对端离线或未开放远程访问。")).AskPassword, "Offline does not ask for the password");
        Check(Kind(new InvalidDataException("该机器的 EndpointId 已改变！已阻止连接，请通过独立渠道核对。")) == ConnectFailure.SecurityWarning, "endpoint changed → SecurityWarning");
        Check(Kind(new OperationCanceledException("尚未核对对端 EndpointId。")) == ConnectFailure.Declined, "fingerprint refused → Declined");
        Check(Kind(new InvalidOperationException("不能连接本机。")) == ConnectFailure.Yourself, "own code → Yourself");
        Check(Kind(new HttpRequestException("x")) == ConnectFailure.Network, "http error → Network");
        Check(Kind(new IOException("可信服务器暂时不可用。")) == ConnectFailure.Network, "directory down → Network");
        Check(Kind(new Exception("boom")) == ConnectFailure.Other, "unknown → Other");
        Check(!ConnectFlow.Classify(new Exception("boom")).Title.Contains("可信服务器"), "no technical wording in titles");

        Check(ConnectFlow.NormaliseCode("a7b2k9") == "A7B2K9", "code upper-cased");
        Check(ConnectFlow.NormaliseCode(" A7B 2K9 ") == "A7B2K9", "code spaces dropped");
        Check(ConnectFlow.NormaliseCode("A7B2K") == null, "short code rejected");
        Check(ConnectFlow.NormaliseCode("A7B2K9X") == null, "long code rejected");
        Check(ConnectFlow.NormaliseCode("A7B2K!") == null, "symbols are not part of a code");
        Check(ConnectFlow.NormaliseCode("ＡＢＣ１２３") == null, "full-width letters are not a code");
        Check(ConnectFlow.NormaliseCode("设备A7B2K9") == "A7B2K9", "non-ASCII characters are ignored");
        return failures == 0 ? 0 : 1;
    }
}
