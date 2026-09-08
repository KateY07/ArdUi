# Python 控制台原型 v1.pre6

当前版本以最小控制台界面验证 ArdUi 的身份、授权、远程桌面、SMB、多设备和断线重连流程。客户端核心位于单个 `prototype.py`，协调服务位于 `server/arduiserver.py`。NJ 的 ARD Relay 保持 8080，下载服务保持 8090。

## 安装与启动

在普通 PowerShell 窗口执行：

```powershell
irm https://f.visnova.cn/ardui/install.ps1 | iex
```

脚本安装到 `%LOCALAPPDATA%\ArdUi`，不请求管理员权限，也不安装 Wintun。它下载经过固定 SHA-256 校验的发布包，内含 Python 3.13.15、cryptography 50.0.1、ARD 与原型。重复执行同一命令会验证并修复程序文件，保留 `data\identity`、EndpointId、机器编号、已授权设备、访问密码和“允许被控”设置。以后可运行 `%LOCALAPPDATA%\ArdUi\ArdUi.cmd`。

安装脚本将安装目录 ACL 限制为当前用户、SYSTEM 和 Administrators。身份文件不存在时才创建；无效文件会令安装停止，绝不自动覆盖。本阶段不使用 TPM，也不承诺系统重装后自动恢复身份。

## 控制台界面

启动后直接显示：

- 是否允许被控，以及当前正在访问本机的设备 ID 和 EndpointId；
- 开启被控时显示本机 6 位设备 ID 与完整 EndpointId；
- 已获得授权的远程设备列表，以及连接、重连或离线状态。
- 每条活动连接的网络类型（P2P 直连 IPv4/IPv6 或 ArdRelay 中继）、TCP/PsPing RTT 和 UDP 数据报往返延迟；路径切换、断开和自动重连会实时输出日志。

按设备前的数字进入操作菜单，可打开远程桌面、SMB 文件共享、连接或断开。主菜单还提供：

- `A`：添加设备，输入对方 6 位设备 ID 和访问密码；
- `B`：进入被控设置，开启/关闭被控或管理授权；
- `T`：在任意菜单直接信任当前待确认设备；
- `Q`：退出。

首次连接时双方必须通过独立渠道核对完整 EndpointId。收到申请后，被控端屏幕先显示申请方完整 EndpointId，然后可在任意菜单按 `T` 回车明确同意。密码只在经过身份验证的端到端连接中发送，中央服务器不接收密码或密码哈希。Windows RDP 和 SMB 账户仍由 Windows 自身验证。

RDP 使用每台设备固定的本机 `127.77.x.x` 地址；SMB 使用 `127.0.0.1` 和每台设备独立的动态 `TcpPort`。ARD 会话断开后，控制层重新协调签名会话，同时保留原来的本地监听地址和端口，以便 RDP/SMB 自身重连。

## 开发与回归

源码运行需要 Python 3.11+、cryptography 41+ 和已构建的 ARD 2 客户端：

```powershell
py prototype.py init
py prototype.py run
py prototype.py test
```

回归实际访问 NJ HTTPS 目录与 8080 Relay，验证稳定设备 ID、批准前不落授权、双方身份、签名授权、真实 TCP、本机 SMB、多设备隔离、相同本地端口上的断线重连、免密码重连、篡改拒绝、撤销、错误密码和关闭被控。

这是控制台预览版。跨两台真实 Windows 设备的 RDP 登录仍需继续验收，二维码、独立更新签名和 Avalonia 界面留给后续版本。
