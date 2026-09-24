# ARD / ArdUi 本机 P2P 性能对照（2026-09-19）

本次确认存在明显的本机单流吞吐上限损失：原生约 9.38–11.48 Gbps，纯 ARD 约 1.01–1.12 Gbps，完整 ArdUi 约 0.80–0.87 Gbps。空载应用回显 RTT 的附加开销是亚毫秒级，没有复现之前 RDP 报告的数百毫秒额外延迟。

这是 localhost 对照，不能据此计算此前公网 IPv6 RDP 的损失，也不能把“本机吞吐上限降低约 90%”直接套用到任意公网线路。这些数值包含转发、封装、调度、内存复制、协议和加密的总成本，不是单独的密码算法成本。

两轮按相反顺序运行，业务实际路径均为 IPv4 127.0.0.1 P2P。每组 TCP 上传、下载各发送 8 秒，等待接收端字节计数确认及缓冲区排空。TCP/UDP RTT 每组预热 50 次后采样 1000 次，表中为中位数 p50 和 p95，单位 ms；吞吐为 Mbps。TCP RTT 是已建立连接的应用数据回显，不是本地转发端口的 TCP connect 时间。

| 轮次 | 链路 | TCP 64B p50 / p95 (ms) | UDP 64B p50 (ms) | UDP 1200B p50 / p95 (ms) | TCP 上传 (Mbps) | TCP 下载 (Mbps) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 原生 TCP/UDP | 0.0270 / 0.0570 | 0.0386 | 0.0386 / 0.0712 | 11477.8 | 10223.6 |
| 1 | 纯 ARD P2P | 0.1939 / 0.3477 | 0.2123 | 0.2636 / 0.4382 | 1005.5 | 1021.7 |
| 1 | 仅 ArdUi 覆盖层（无 ARD） | 0.2889 / 0.4356 | 0.1996 | 0.2037 / 0.3647 | 1687.3 | 1687.4 |
| 1 | 完整 ArdUi 传输链路 | 0.5672 / 0.7642 | 0.7158 | 0.6490 / 1.0646 | 831.6 | 871.8 |
| 2 | 完整 ArdUi 传输链路 | 0.6292 / 0.8999 | 0.5186 | 0.4977 / 0.7522 | 816.7 | 803.8 |
| 2 | 纯 ARD P2P | 0.1824 / 0.2922 | 0.2231 | 0.2746 / 0.4451 | 1022.9 | 1116.7 |
| 2 | 原生 TCP/UDP | 0.0654 / 0.1331 | 0.0618 | 0.0601 / 0.1111 | 9380.4 | 11324.2 |

按同一轮、同一方向比较：

- 纯 ARD 比原生：TCP RTT 中位数增加 **0.1170–0.1669 ms**；单流吞吐上限下降 **89.1%–91.2%**。
- 完整 ArdUi 比原生：TCP RTT 中位数增加 **0.5402–0.5638 ms**；1200B UDP RTT 中位数增加 **0.4376–0.6104 ms**；TCP 吞吐上限下降 **91.3%–92.9%**。
- 完整 ArdUi 比纯 ARD：TCP RTT 中位数再增加 **0.3733–0.4468 ms**；TCP 吞吐再下降 **14.7%–28.0%**。
- 两轮 raw/ard/full 各自的 64B 和 1200B UDP 均为 **2000/2000 成功回显、0 次超时**。这只代表串行回显测试，不代表满载 UDP 的丢包率。
- 完整 ArdUi TCP p95 为 **0.7642–0.8999 ms**，p99 为 **0.8753–1.0806 ms**。采样中的最大值为 **3.2520 / 6.0542 ms**；中位数不能视为最大延迟保证。

第一轮顺序 raw → ard → overlay → full；第二轮 full → ard → raw。仅覆盖层一组用于定位，不能把它的时间或吞吐与另一组简单相加。完整链路使用实际 Engine、DirectoryClient、ARD、Gateway、Overlay、LocalForwarder；正常后台探测开启，未启动实际 RDP/FRD 桌面或 Avalonia 窗口。因此结果量化传输链路，不含桌面捕获、编码、解码、呈现，也未测试其他业务同时传输的情况。

本次直连证明由实际 TCP 业务日志的 tunnel ID 关联同 ID 的 CONNECTED / network path selected，避免使用可能被辅助连接影响的 UI 标签。第一轮完整日志显示：引导期 Relay → direct 后，测量期间没有路径切换。第二轮自动检查整个区间的新增路径事件、前后 PID/tunnel 及两端 Session.Process 实例，均无变更、无日志丢失、无退出；所有 intervalPathEvents 为空。Overlay selected 始终为 base，Switches=0。

源码和本次快照还确认了后续定位方向，尚未逐项做因果验证：

- [状态解析](D:/1/ard2/ArdUi/Transport/ArdProcess.cs:60) 没有按业务 tunnel 筛选，辅助连接也可能更新 UI 的 P2P 标签。
- [界面带宽探测](D:/1/ard2/ArdUi/Core/Session.cs:159) 用 64 KiB 除以包含请求往返的时间，不能代表持续吞吐。它的结果不能与本报告或 RDP 自身的带宽估计混为同一指标。
- [覆盖层传输](D:/1/ard2/ArdUi/Overlay/OverlaySession.cs:98) 把多条业务 TCP 放入一条基础流；[顺序分发](D:/1/ard2/ArdUi/Overlay/OverlaySession.cs:237) 会等待慢业务的有界接收队列，存在跨业务阻塞风险，本轮单业务测量没有验证该风险的程度。
- [维护重发](D:/1/ard2/ArdUi/Overlay/OverlaySession.cs:340) 每到间隔会重发尚未确认的部分帧；两轮完整链路主控侧分别记录 **513 / 498** 次覆盖层重发、**385 / 496** 次重复帧。这是覆盖层计数，不能据此断言网络丢包或 QUIC 重传。
- 两轮完整模式 C# 进程总分配约 **9.96 / 9.48 GiB**，包含测试接收端与生产传输代码，不是常驻内存。生产中逐帧创建数组和 AES-GCM 对象的成本值得单独分析；尚未证明它占吞吐损失的具体比例。
- 底层每条 QUIC 流默认接收窗口为 1,250,000 字节，长 RTT 可能约束单流吞吐；本机数据不能验证 WAN 的窗口瓶颈。

环境：Intel Core Ultra 9 285H，Windows 11 Enterprise 26200，.NET 8（实际运行时版本见原始 JSON）。两个端点在同一机器上，共享 CPU/调度资源；无公网 IPv6，未做 LAN/WAN/IPv6 或真实 RDP 的同目标对照，未测满载期间 RTT、UDP 持续吞吐以及多业务公平性。测试运行期间没有编译任务并行。

版本：ARD 2.0.0-pre.6，提交 dc9acb915a915777577597f66ec55117fd8d81a2；ArdUi v2.pre2，提交 a7e9f27305f6989bbef0fb80291b49d7c455c737。ARD 二进制 SHA256：04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d。fixture.json 留存被链接生产文件 SHA256 与测试程序集 SHA256；本次未修改生产代码或锁定 README。

原始记录：[第一轮 JSON](D:/1/ard2/ArdUi/dist/performance-20260919-091753/measurements.json)、[第二轮 JSON](D:/1/ard2/ArdUi/dist/performance-20260919-092107/measurements.json)。[测试说明与复现方式](D:/1/ard2/ArdUi/tests/performance/README.md)。

此前 FRD 的 388 / 374 是先后两次独立测试的 ReceivedFrames：先等待 frd-base 测试结束，再选择候选中继并运行 frd-transit，见 [FrdTest.cs](D:/1/ard2/ArdUi/Testing/FrdTest.cs:38)。GPU 确认呈现分别是 381 / 371。它们不是多个路径同时收到同一批画面的计数，也不是有效的吞吐或延迟比较。
