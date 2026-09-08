# Python 控制台原型

先验证实际端到端链路，再开发 Avalonia 界面和正式一键安装。客户端全部在 `prototype.py`；协调服务在 `server/arduiserver.py`，已部署 NJ。既有 ARD 8080/8090 端口保持不变。

## 运行

普通用户直接运行唯一安装命令，无需预装 Python、.NET 或 ARD，也不请求管理员权限：

```powershell
irm https://f.visnova.cn/ardui/install.ps1 | iex
```

脚本安装到当前用户的 `%LOCALAPPDATA%\ArdUi`，创建或保留 `%LOCALAPPDATA%\ArdUi\data\identity`，随后打开控制台。以后可运行 `%LOCALAPPDATA%\ArdUi\ArdUi.cmd`。重复执行安装命令用于修复或升级，不覆盖 `data`。

源码开发需要 Python 3.11+、`cryptography`（41+）和已构建的 ARD 2 客户端。默认读取 `tools/ard.exe`，也可用 `--ard` 指定路径。

```powershell
py -m pip install cryptography
py prototype.py init
py prototype.py run
```

`init` 仅在 `prototype-data/identity` 不存在时创建密钥，已有文件绝不覆盖。`run` 只读取已有密钥。该目录与程序放在一起；重复初始化和升级保持身份。删除或丢失密钥会产生新身份；本阶段无 TPM 恢复。不要将该目录提交到 Git 或分享给他人。

## 两端操作

1. 两端分别执行 `init`、`run`，各自显示机器码及完整 EndpointId，默认关闭被控功能。
2. 被控端输入 `on`，设置至少 8 位的访问密码。以后 `on` 时密码留空可保留原密码及授权；输入新密码会撤销已有被控授权。
3. 主控端输入 `add 对方机器码`，输入访问密码。先独立核对显示的被控端 EndpointId，再输入 `approve 提示中的令牌`；拒绝用 `deny 令牌`。
4. 被控端只有密码验证成功才显示主控端完整 EndpointId。通过独立渠道核对后输入 `approve 令牌`。主控端收到签名授权后，设备才加入 `list`。
5. 主控端输入 `rdp 机器码` 打开远程桌面，或 `smb 机器码` 输入共享名和 Windows 账户，映射空闲盘符并打开资源管理器。Windows 自身远程桌面和 SMB 服务须已经启用；ArdUi 密码不代替 Windows 凭据。

| 命令 | 功能 |
| --- | --- |
| `list` | 显示已授权的目标及允许访问本机的公钥 |
| `connect 机器码` | 已授权设备重新建连，不重复输入 ArdUi 密码或确认 |
| `disconnect 机器码` | 断开此目标并清理该会话创建的 SMB 映射 |
| `revoke 完整EndpointId` | 被控端撤销该主控端并断开已有连接 |
| `off` | 关闭被控功能、断开所有入站连接；保留授权，不影响出站连接 |
| `quit` | 退出并清理会话及本原型创建的 SMB 映射 |

多设备的 RDP 连接使用不同的本机 `127.77.x.x` 回环地址及动态端口；SMB 因 Windows 重定向器会拒绝非标准回环别名，使用 `127.0.0.1` 加每台设备独立的动态 `TcpPort` 隔离映射。程序不创建虚拟网卡、虚拟 IP 或系统路由。SMB 使用 Windows 11 24H2 / Server 2025 的 `New-SmbMapping -TcpPort`。不用管理员权限，不自动修改系统服务或防火墙。

ARD 自身在 Iroh Connection 关闭时退出，控制台原型监视该进程并重新向双方协调一个签名会话；已有授权重连不重复询问密码或指纹。底层路径迁移由 Iroh 处理。已经中断的 TCP 流不会由 ArdUi 伪造续传，RDP/SMB 按各自协议恢复；重连后的新流继续使用原设备身份和授权。

## 回归

```powershell
py prototype.py test
```

回归创建三个临时身份，实际访问 NJ HTTPS 目录与 ARD Relay，验证稳定机器码、批准前不落授权、双方身份、签名授权、真实 TCP 转发、本机 SMB 共享读取、多设备隔离、ARD 进程断线自动重连、免密码重连、签名篡改、撤销、错误密码、关闭被控。临时私钥在结束后清理。

2026-09-08 首次实测：上述回归全部通过，使用 NJ 真实 HTTPS 服务及现有 8080 ARD Relay。

这属于 `v1pre.1-console` 开发原型，不替代完整安全审查。自动回归使用 TCP 回显服务，并已通过本机真实 SMB 共享读取；实际远端 RDP 登录仍需两台具备相应 Windows 服务和账户的机器完成验收。暂不做二维码、独立更新签名或 UI 布局。
