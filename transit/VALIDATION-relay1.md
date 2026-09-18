# ArdTransit v2.pre1-relay1 验证记录

日期：2026-09-18。仅新增独立中继的一键安装、运行保障及说明页；ArdUi v2.pre1 客户端、NJ 目录协议、ARD pre.6 和锁定 README 均未修改。

## 发布文件

- `ArdTransit-v2.pre1-relay1-win-x64.zip`：13,813,562 字节，SHA-256 `149c975f92e0ded715b619613fb052821968321e0cca5a48b8fa661b14162f8b`。
- `ArdTransit.exe`：14,024,670 字节，SHA-256 `4e9db896c80d37e07a11a675ed93fd84338c5ab373a53ecaeb36a30566ef25f3`。包含 Python 及 cryptography 运行依赖；不依赖用户安装的 Python 或 .NET。
- ARD 单独复用/下载，SHA-256 `04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d`。
- 新入口 `https://f.visnova.cn/ardui/install-relay.ps1`；普通用户目录 `%LOCALAPPDATA%\ArdTransit`。当前用户登录后自启，不是系统启动前的服务。

## 已通过

| 验证 | 结果 |
| --- | --- |
| 运行时 12 项回归 | 离线启动重试、心跳恢复、停止信号、目录公钥变化拒绝、状态文件、诊断失败不终止服务、日志轮转、ARD 子进程 Job 管理及进程被杀后的清理；不影响无关进程。 |
| Windows PowerShell 5.1 安装 | `Restricted` 策略的脚本块入口可用；含空格目录，全缓存不下载，重复安装不重写 EXE，身份/config 保留，坏 ZIP 下载修复，坏 ARD 缓存复用已安装文件，错误哈希三次后拒绝并保留旧安装，无进程停止成功。下载由本地真实制品替身提供。 |
| 打包 EXE 三节点实测 | 真实 C# A/B 与真实 ARD，两条 C 链路为 Direct；TCP 和 RDP 大小 UDP 经 C；强杀 C 后原 TCP 套接字回退并传输完整 1 MiB，UDP 继续；强杀基础 ARD 后约 17.9 秒恢复原业务连接；撤销授权销毁会话。 |
| 真实 NJ 注册与安装启动 | 在隔离目录用独立 EXE 注册、心跳上线，验证隐藏登录自启项；重装保留进程，停止删除该测试自启项，重启 EndpointId 与私钥字节不变，再次上线并生成诊断 ZIP。结束后停止测试节点并恢复原有注册表值。 |
| 公开安装入口 | Windows PowerShell 5.1 从 HTTPS 获取安装脚本，实际下载完整中继 ZIP 并校验；ARD 复用本机已校验安装。注册上线、缓存重装、停止重启全部通过。最终关闭下载进度渲染并限定单次下载 600 秒；缓存/损坏下载回归再次通过。 |
| 静态检查 | Python 语法编译、PowerShell 解析、`git diff --check` 通过。未以此声称独立安全审计。 |

三节点证据：`dist/v2-integration-20260918-153125/client.log`。NJ 安装启动证据：`dist/relay smoke 120fd47b8dff477094b82b33fbfc447a/data/relay.log`。

公开入口测试证据：`dist/relay smoke 334e611b5dc64f12b799f379f04dd951/data/relay.log`。NJ 发布前备份：`/var/backups/ardui-before-relay1-20260918T074054Z`。仅上传新增中继 ZIP、安装脚本、说明页及清单；未重新上传 ArdUi/ARD 二进制，未创建 GitHub Release。

本地直连本身更快，三节点回归显式选择 C 验证转发与切换，不代表真实质量选择器会以 C 替换 Direct。此轮未验证跨 NAT/公网 IPv6 的质量收益、长期负载、实际 mstsc UDP 协商或 Windows SMB。

## 复现

```powershell
./build-relay.ps1 -Python python
powershell -NoProfile -ExecutionPolicy Bypass -File tests/install_relay.ps1
python tests/test_transit_runtime.py
python tests/integration_v2.py --dll dist/final-v2.pre1/ArdUi.exe --transit-exe dist/relay-build/output/ArdTransit.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tests/smoke_relay_install.ps1
```

最后一项短暂向真实 NJ 注册隔离候选，测试后停止并恢复原有登录自启项；不修改用户正式安装。使用 `-Public -DownloadPayloads` 可从公开入口与安装包执行同样的完整流程。
