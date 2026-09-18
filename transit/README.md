# ArdTransit v2.pre1

独立、明确启用的候选转发节点。它只转发已授权 A/B 会话的内层密文，复用 ARD 建立两段连接；只有 A–C 与 B–C 都为 Direct 且实测质量改善，客户端才会选用。无需静态公网 IP，也不要求所有 ArdUi 客户端互相常驻连接。

Windows x64 一键安装（普通 PowerShell，无需 Python、.NET 或管理员权限）：

```powershell
irm https://f.visnova.cn/ardui/install-relay.ps1 | iex
```

Windows 10 1809 / Server 2019 或更新版本；当前构建 `v2.pre1-relay1`，协议保持 v2.pre1。安装到 `%LOCALAPPDATA%\ArdTransit`，立即后台启动并设置当前用户登录后自动运行；默认最多 2 对会话、节点收发合计 10 Mbps。它是独立转发程序，不开启本机远程桌面或文件共享。首次下载校验 SHA-256；重复执行时复用校验通过的缓存，保留 `data` 身份与 `config.json`。ARD 单独下载，也可复用本机 ArdUi 已校验的 ARD。

查看状态、停止并关闭登录自启：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\ArdTransit\Status-ArdTransit.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\ArdTransit\Stop-ArdTransit.ps1"
```

再次执行安装命令即可启动并恢复登录自启。修改 `config.json` 中 `capacity` / `mbps` 后停止并重新运行安装命令；运行日志在 `data\relay.log`，诊断包在 `data\diagnostics.zip`。网络中断时自动重试，注册和心跳成功后才显示在线。日志轮转保留约 8 MiB；停止时清理本实例的 ARD 子进程。

从源码运行需要 Python 3.10+、`cryptography` 与可信的 ARD 2.0.0-pre.6 可执行文件。不要从陌生来源替换 ARD：

```powershell
python -m pip install cryptography
python ardtransit.py --ard C:\ArdTransit\ard.exe --data C:\ArdTransit\data --capacity 2 --mbps 10 --diagnostics C:\ArdTransit\diagnostics.zip
```

Linux 示例（将 `/opt/ard/bin/ard` 换成实际 ARD 路径）：

```sh
python3 -m venv .venv
.venv/bin/pip install cryptography
.venv/bin/python ardtransit.py --ard /opt/ard/bin/ard --data ./data --capacity 2 --mbps 10 --diagnostics ./diagnostics.zip
```

默认目录服务为 `https://f.visnova.cn`，ArdRelay 为 `http://175.27.160.144:8080`，固定其公钥；需要自建环境时一起提供 `--server`、`--relay` 与 `--relay-key`。程序不会申请提权、修改防火墙或配置公网端口映射。

- 启动后自动注册并每 3 秒心跳，等待被授权的配对任务；无需用户手动填写客户端 IP。
- `--capacity` 限制并发配对数；`--mbps` 是节点收发转发量合计的平均限额，令牌桶允许约 1 秒突发。首次试用建议 2 对、10 Mbps；专用节点可按资源提高。
- NJ 签名票据绑定 A/B/C 和临时 ARD 公钥；转发服务不接受任意出口地址。会话路由由双方分别续租，过期、失联、空闲或退出时清理。
- `data` 包含永久身份、固定的目录签名公钥和临时会话。更新保留它；签名公钥变化会拒绝自动接受。同一目录只允许一个实例运行。
- 诊断 ZIP 每次心跳更新，包含节点资源、当前路由、两段网络类型和最近事件；IP 默认脱敏，不包含私钥、密码、票据令牌或业务载荷。请勿把 `data` 当成诊断包上传。
- 按 Ctrl+C 退出；直接终止进程后由租约到期淘汰。客户端保留原有 A–B 路径，并在节点失败时回退。

拥有 IPv6、动态公网 IPv4 或良好 NAT 只表示有潜力。最终资格针对特定 A/B 双端实测；节点在线并不保证被选择。v2.pre1 未将此角色自动启用在普通 ArdUi 客户端中。
