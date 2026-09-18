# FRD 协议适配回归

运行 `dotnet run --project tests/frd-protocol/FrdProtocolTests.csproj --configuration Release`。

测试直接编译生产文件 `Frd/FrdProtocol.cs`，以本地假 FRD 和传输桩检查 TCP 协商、附属通道、UDP 视频/诊断、反馈源端口、错误请求拒绝及生命周期清理。

不启动真实 FRD，不读取屏幕，不操作键鼠或剪贴板。此测试不替代加密覆盖层和真实 FRD 的端到端验证。
