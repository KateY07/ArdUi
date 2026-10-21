# ArdUi v3.pre1 本机测试发布

日期：2026-09-22。离线交付位置：`D:\pub\ArdUi\v3.pre1.zip`，相邻 `.zip.sha256` 用于完整性校验。解压后运行 `install.bat`，由它调用 `install.ps1`。运行时由用户预先安装，不随包下载。

## 范围与兼容性

- ArdUi：v3.pre1；本次修正连接操作隔离与取消、普通 UDP 单流错误隔离、ARD 退出恢复、Overlay 流信用窗口、半帧写失败处理、路径日志消费和吞吐测量。
- ARD：沿用 v3.pre1；FRD：沿用开发组正式二进制 v1.pre13。没有修改两者源码，也没有访问 FRD 源码。
- Overlay 增加版本 3 和流信用帧；连接双方必须同时升级 ArdUi v3。不能与 ArdUi v2 建立新 Overlay 会话。升级保留身份、授权和设备列表。
- 问题 05（更新验签接入）按用户要求暂不修复。SHA-256 仅校验完整性，不替代可信签名。
- ARD 的依赖、结构化诊断、自定义 QAD 端口请求见 [ARD 团队请求](ARD-TEAM-REQUEST-v3pre1.md)。本版不等待、不代改外部组件。

## 验证证据

| 检查 | 结果与边界 | 日志 |
|---|---|---|
| 最终单 EXE 自检 | 身份创建/保留、损坏身份拒绝、FRD 安装指针、Avalonia 无头渲染通过 | `dist/v3-self-test.log` |
| v3 独立回归 | 操作取消及提交、路径事件、慢 TCP 流隔离、恢复后数据完整、半关闭、部分写失败、吞吐计时通过 | `dist/v3-regression.log` |
| 通用端口转发回归 | 透明 TCP/UDP、故障流隔离及重复 Reset 检查通过 | `dist/v3-forwarding.log` |
| 候选中继回归 | 同一 TCP socket 切换路径、UDP 分片与重放拒绝、备用路径重连、C 故障回退通过 | `dist/v3-transit-test.log` |
| 最终 EXE 与真实 ARD 集成 | 首次确认取消、身份核对、授权、ARD 子进程退出恢复、永久授权重连、撤销及三轮添加/移除通过 | `dist/v3-prototype-final.log` |
| FRD pre13 黑盒集成 | 基础路径 400 帧、候选路径 399 帧，撤销与显式断开清理通过；未注入键鼠/剪贴板操作。此测试早于最后的 Gateway 立即关闭和恢复宽限调整，不能称为最终 EXE 的完整 FRD 重测 | `dist/v3-frd.log` |
| 最终离线包升级及重复安装 | 从 v2.0.0 安装到隔离目录再升级；access.json、peers.json、identity、config.json 字节不变；启动入口及 FRD 指针正确；重复安装复用；零网络请求 | `dist/v3-offline-upgrade.log` |

离线安装回归验证保存文件的字节保留，不把测试中种入的占位设备记录当作真实设备的功能联调。首次请求取消测试验证被控关闭、迟到批准不生效，并显式取消主控请求；不声明远端 TCP 在三秒内必然自动结束。测试没有更改用户真实安装，没有部署 alipc/NJ，没有触发 Actions 或上传 GitHub/NuGet。

最终 ArdUi.exe SHA-256：`e67ba7a672f29d2a7ed15aaa5f2eab8268eba11bf45592f0e26d1978affd6aca`。

## 状态

这是供测试的预发布包。通过上述回归不等于所有静态审查项通过；剩余问题和职责见 [发布后静态及边界审查](AUDIT-v3.pre1-20260922.md)。已有同名发行不得覆盖，后续修正必须使用新版本。
