# FRD 透明端口转发回归

运行 `dotnet run --project tests/frd-protocol/FrdProtocolTests.csproj --configuration Release`。

测试直接编译生产文件 `Frd/FrdForwarder.cs`、`Transport/LocalUdpFlow.cs` 和 `Transport/Wire.cs`。仅将已认证的 Session 替换为本地测试端点，不模拟或解析 FRD 协议。

验证服务端先发送数据、8 条同时保持的独立 TCP 连接、超限仅拒绝新连接、单连接半关闭与重连不关闭其他连接、任意二进制字节流、TCP 半关闭后的反向数据、0–1482 字节内多种长度的 UDP 原样往返、来源映射隔离，以及取消与关闭后的 TCP/UDP 端口回收。

不启动真实 FRD，不读取屏幕，不操作键鼠或剪贴板。此测试不替代加密覆盖层和真实 FRD 的端到端验证。
