# v2.pre2 验证记录

日期：2026-09-18。新增 FRD v1.pre6 支持及其自动部署；按用户要求拆分 C# 源码。ARD 引擎、锁定 README、NJ 目录与中继协议没有修改。

## 源码与成品

- 改动前源码存档：`dist/archives/before-frd-v2.pre2.zip`，SHA-256 `cecc1f6e50529232a90ccece6c10e501d5ebadc9f19e407fe00471d01baec10b`。
- 27 个生产 C# 文件按职责组织；`ArdUi.cs` 保留 41 行入口与版本声明。原有 59 个顶层类型完整迁移，项目显式包含生产目录，排除测试替身和输出目录。
- 最终 `dist/final-v2.pre2/ArdUi.exe`：25,362,217 字节，SHA-256 `9daf89bf0b98189e0f93fa3ef0bf7e632d067925280929eb52d87cfc298ea6e7`。
- FRD 使用用户提供的已发布 v1.pre6，主程序 SHA-256 `37c95ff75dceb75c222b5afdc769a7941d7c9a8c96ce5543e09a2927d0ea2286`。不重新编译 FRD。
- ARD 仍为 2.0.0-pre.6，SHA-256 `04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d`。

## 已验证

| 范围 | 结果 |
| --- | --- |
| 拆分与构建 | 零警告、零错误；.NET 8 框架依赖单 EXE。无头渲染保留紧凑单行设备布局，增加 FRD 按钮。 |
| 原有自检 | 身份创建、重复安装保留、损坏身份拒绝；加密、防重放、可靠窗口、TCP/UDP 多路复用、路径切换、回退及诊断脱敏通过。 |
| 真实 ARD | 三节点 Direct、候选中继、杀死 C 后原 TCP 完整继续；基础 ARD 重启后约 17.3 秒恢复原端口。此数值是本次观测，不是承诺时限。 |
| 授权 | 14 项目录用例通过；首次批准、免密码重连、撤销拒绝、连续三轮添加/移除及关闭被控通过。 |
| FRD 协议 | 生产代理的握手改写、视频/诊断 UDP、原来源端口反馈、附属 TCP、错误 token/未知类型拒绝和清理通过。 |
| FRD 实际桌面 | pre6 基础路径 8 秒收到 388 帧、GPU 确认 381 帧；候选路径 8 秒收到 374 帧、GPU 确认 371 帧；两份报告 Passed=true、Errors=[]。 |
| FRD 权限与撤销 | 只有已授权主控能启动被控 FRD；一般端口白名单不扩张；撤销后关闭 FRD 两端进程、代理及临时端口授权。 |
| 安装器 | 最终 EXE 与 pre6 包通过 Windows PowerShell 5.1 Restricted 入口：缓存零下载、重装保留身份/授权/被控状态、缺失或损坏 DLL 修复、完整本机 pre6 目录复用、坏缓存一次重下、错误 hash 三次拒绝。下载在此用例中由本地夹具提供已校验制品。 |

真实 pre6 证据：`dist/v2-integration-20260918-230307/client.log`、同目录 `frd-base.json`、`frd-transit.json` 和诊断 ZIP；测试没有键鼠或剪贴板操作。最终成品原有三节点回归：`dist/v2-integration-20260918-230513`；授权回归：`dist/v2-integration-20260918-230551`。

此前 pre4 一轮 GPU 超时发生于用户手动关闭程序，按用户说明记为人为中断，不作为 FRD 故障结论；另一轮关闭期间 EndOfStream 原因未确认。后续 pre6 完整流程通过。

## 分发校验

FRD 最终压缩包为 `FRD-v1.pre6-win-x64.tar.xz`，98,648,752 字节，SHA-256 `6aee2eb0912d1b0915eef79156c06bcf64c8c008181d2e6b26845137da09944d`；只含 11 个运行与许可文件。安装器自身 SHA-256 为 `8929fb36dbed45bdf66bcbf7d45a078ef9e3a639a7733495675538a0866e01ab`。

NJ 准备时从固定官方版本下载 FFmpeg，逐文件匹配用户提供的 FRD 运行依赖；本机只上传约 34.2 MiB 的 FRD 自有组件压缩包。SHA-256 `1abab426182adb7e19eaef30d3ab1e82f1ad30d2b2101ed64ad8b384af32f082`，35,848,476 字节。客户端下载方式仍是经完整哈希校验的独立 FRD 包。

NJ 重组出的完整压缩包与本地包 SHA-256 完全一致，11 个成员再次验证通过。2026-09-18 15:29 UTC 已发布；旧页面与安装器备份于 `/var/backups/ardui-before-v2.pre2-20260918T152910Z`。经实际 HTTPS 请求验证 EXE 与 FRD 包的完整内容哈希；本机公网验证安装脚本哈希一致，`/ardui`、`/ardui/`、EXE 和 FRD 包均返回 200，文件长度符合清单。既有三项服务持续 active，没有重启。

上传末段遇到单连接降速时，校验并保留已上传前缀，只将剩余 1.74 MB 拆为 128 KiB 小块续传；合并后验证完整 seed。上传工具支持可调分块及进度、近期速度、剩余时间输出。仅推送 GitHub 源码，没有上传 GitHub Release。

## 边界

FRD 保持原生 UDP 视频和反馈，未将其改为 TCP。此次验证不等同于 Windows mstsc 已协商 RDP UDP，也不证明跨 NAT、公网 IPv6 的所有场景。Windows SMB 测试按用户要求暂缓。FRD 需要已登录的交互式桌面；网络全部路径中断超过 FRD 自身超时时，需要重新打开 FRD。
