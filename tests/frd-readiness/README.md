# FRD 启动就绪回归

在 Windows 上运行：`dotnet run --project tests/frd-readiness/FrdReadinessTests.csproj -c Release`。

测试直接编译生产文件 `Frd/FrdListener.cs`，启动不会读取桌面或注入输入的临时测试进程，使用随机回环端口。

覆盖 TCP 单独监听不能通过、同一进程的同端口 TCP/UDP 才可通过、其他进程占用同端口不能冒充就绪、静默子进程正常通过、延迟绑定 UDP、仅输出旧日志但未监听时拒绝、启动失败退出码和取消。

生产检查只查询系统的 IPv4 监听表与进程 ID，不连接 FRD、不解释业务协议、不读取 FRD 内部配置。FRD 启动参数固定绑定 `127.0.0.1`，因此这里仅接受该地址；stdout/stderr 仍由原有日志读取任务持续收集。

使用的公开系统接口：[GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)、[GetExtendedUdpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable)。
