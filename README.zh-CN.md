# ArdUi

[English](README.md) · **简体中文**

> ## 角色与职责（请先读这里）
>
> **只有三种角色：M、ArdFerry、ArdUi。每个角色只有一项配置：一个 HTTPS 域名。** ArdUi 用的就是现有 `config.json` 里的 `server`，客户端配置不做任何改动。密钥由程序自己生成和保存，不算配置。
>
> | 角色 | 唯一的配置 | 职责 |
> | --- | --- | --- |
> | **M** | **自己的** HTTPS 域名 | 一个网络的中心：设备登记与 6 位机器编号、在线状态、在设备之间转交签名的连接请求、维护 ArdFerry 节点列表（探测每个入口，把可用的下发给客户端）、地址发现和网络诊断探测。看不到会话内容，也拿不到密码。 |
> | **ArdFerry** | **M 的** HTTPS 域名 | 两台设备无法直连时转发加密流量。运行未修改的官方 `iroh-relay`；有公网 IP，或路由器支持 UPnP / NAT-PMP 端口映射即可运行。**按不可信设计：任何人都可以运行并加入一个网络。** 它读不到也改不了任何内容；它能看到经过它的 IP 地址、时间和流量大小，也可能拖慢流量，所以客户端优先直连，并按实测质量挑选中继。 |
> | **ArdUi**（客户端） | **M 的** HTTPS 域名（`config.json` 里的 `server`） | 持有设备密钥（即设备身份）。点对点连接，不通时经 ArdFerry；端到端认证对方设备。谁能做什么由被控端在本机决定，审计录屏也只存在被控端本机。 |
>
> **一个网络 = 一个 M = 一个域名。** 自托管就是运行你自己的网络：Bob 的域名和 Li 的域名是两个独立的网络，彼此不知道对方，也不互通。官方网络也只是这样一个普通网络。所有人运行同一套程序，区别只在域名。
>
> 完整定义、密钥，以及这个设计与当前代码之间还有哪些差距，见 [ROLES.md](ROLES.md)。

用 6 位编号，安全地远程访问 Windows 电脑。端到端加密，没有 IP、端口、虚拟网卡要配置；被控端逐设备授权；内置对话、文件传输和被控端审计录屏。

**[网页介绍与演示](https://katey07.github.io/ArdUi/)** · **[下载](https://github.com/KateY07/ArdUi/releases)** · [自托管](#自托管) · [历史设计文档](docs/DESIGN-v2.md)

当前版本：v6.3。官方服务器拒绝 v6.2（v6.pre2）以下的客户端：从 v6.2 起，会话双方使用主控端选定的同一个 ArdFerry，在慢速网络上建立连接的时间再缩短约一半。

## 简介

ArdUi 基于 [ARD](https://github.com/KateY07/ard2)（Iroh）建立点对点的加密连接。每台设备有一对永久密钥，公钥就是设备身份；中央目录服务器只负责分配 6 位机器编号和协调连接，不接收密码，也不在数据路径上。

| 功能 | 说明 |
| --- | --- |
| 6 位编号连接 | 输入对方的编号和一次性密码，核对完整设备指纹后，由被控端同意授权。之后凭设备身份重连，不用再输密码。 |
| 账号 | 只有登录的设备可以连接（控制）其他设备；未登录时显示“匿名用户”，只能被控。登录、注册、修改密码都在服务器的账号网页中完成，注册的账号由管理员审核。 |
| 逐设备授权 | 被控端为每台控制设备单独开放 FRD、RDP、对话（含文件、图片和通话）、共享文件夹；支持“仅接受这一次”；可随时撤销或断开。 |
| 共享文件夹 | 在浏览器中浏览、上传（可续传）、下载对方的共享文件夹（WebDAV，仅限直连）；删除的内容进回收文件夹；每次操作记入被控端审计。 |
| 远程桌面 | 通过 [FRD](https://github.com/KateY07/frd) 查看和控制已登录的桌面，也可使用 RDP。 |
| 对话与文件传输 | 文字、图片、文件（最大 2 GB），显示速度与进度，可取消，传输时仍可聊天。 |
| 审计录屏 | 对方使用屏幕时，在被控端本地录制（10 帧/秒，保存为分片 MP4），最多占用 10 GB，循环覆盖。使用 Windows 自带的 H.264 编码器，不依赖 FFmpeg。录屏默认开启、不可关闭，被控端首页会显示“录屏中”。 |
| 中英文界面 | 界面支持中文和英文：自动（跟随 Windows 显示语言），或在 设置 → 通用 → 语言 中选择，重启后生效。你和对方输入的内容（对话、设备名）不会被翻译。 |
| 语音通话 | 在对话窗口顶部呼叫已连接的设备：沿用同一条加密链路（直连或中继），Opus 24 kbps；支持响铃、接听、拒绝、静音、挂断，窗口和灵动岛里都能操作；被控端的审计日志会记录通话（时间、时长、结果，不录声音）。一方没有可用的麦克风或扬声器也能通话，双方都会收到提示。目前没有回声消除，请戴耳机。可在设置里关闭。 |
| 麦克风诊断 | 设置 → 麦克风诊断：列出音频设备、检查 Windows 麦克风隐私开关、实测麦克风（帧数、音量、爆音、全静音）、录音回放和播放测试音。声音只在本机处理。 |
| 网络诊断 | 设置 → 网络诊断：本机地址与公网出口（是否有多个出口或处在大内网）、NAT 类型、逐跳路径、UPnP / NAT-PMP，并给出能否点对点直连的结论。目录服务器（M）会参与实测：从两个端口看你的公网地址，收发并统计流量，向你监听的端口主动发包——以此证明 UPnP 映射或公网地址从外部真的可达。协议与防滥用措施见 [docs/probe-protocol.md](docs/probe-protocol.md)。 |
| 灵动岛 | 屏幕右下角（托盘上方）的小胶囊，可拖动：显示连接状态、新消息气泡（4 秒）、未读数、文件传输进度和通话（接听、拒绝、静音、挂断），点击打开对话。全屏或演示时默认不显示。 |
| 无需管理员权限 | 不安装驱动，不改网络设置；设备身份和配置在升级时保留。 |

## 快速入门

需要 Windows 10/11 x64 和 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)；FRD 是 Native AOT，不需要运行时。

1. **安装**：到 [Releases](https://github.com/KateY07/ArdUi/releases) 下载最新的 `ArdUi-vX.exe`，双击运行，按安装窗口提示完成。安装在 `%LOCALAPPDATA%\ArdUi`，开始菜单里会有 ArdUi。
2. **被控端**（要被访问的电脑）：打开“允许被控”，得到 6 位编号；点击后显示 10 秒的首次密码。把编号和密码告诉对方。
3. **主控端**（你的电脑）：输入对方的编号和密码，点“连接”。双方用电话等独立渠道核对设备指纹，被控端点“接受”或“仅接受这一次”，并选择开放的功能。
4. **使用**：已授权的设备出现在列表里，点 FRD 看桌面，“桌面”用 RDP，“文件”访问共享，对话图标发消息和文件。

## 自托管

默认使用公共的设备目录服务。想自己掌握，可以在一台有域名的 Linux 服务器上用一条命令部署自己的**设备目录服务**（Python + Caddy 自动 HTTPS）：

```bash
curl -fsSL https://raw.githubusercontent.com/KateY07/ArdUi/main/selfhost.sh | sudo bash -s -- --domain ardui.example.com
```

- 需要：Linux（systemd）、root 权限、已指向本机的域名、开放 80/443 端口。
- **没有域名？** 直接填服务器的公网 IP：`--domain 203.0.113.7`。Let's Encrypt 会签发约 6 天有效的 IP 证书，Caddy 自动续期，客户端用 `https://203.0.113.7/`。
- 加 `--dry-run` 先预览；加 `--http-only` 只运行目录服务（你自己提供 HTTPS 反向代理）；`--uninstall` 卸载（数据保留在 `/var/lib/arduiserver`）。
- 脚本同时安装 **UDP 网络探测服务**（默认 UDP 7843 和 7844；`--probe-port N` 改端口，`--no-probe` 不装）。请在防火墙和云安全组里放行这两个 UDP 端口，ArdUi 的网络诊断才能使用你的服务器。
- 其他选项：`--name 名称`（服务名、用户和目录，可与另一个实例并存）、`--port N`、`--pip-index URL`、`--caddy-binary 路径`、`--wheels 目录`。全部选项见 `bash selfhost.sh --help`。
- **中国大陆的服务器**通常连不上 GitHub 和 caddyserver.com：请在别处下载 [`selfhost.sh`](selfhost.sh)、[`server/arduiserver.py`](server/arduiserver.py) 和 [`server/probe_udp.py`](server/probe_udp.py) 再拷过去，运行 `sudo bash selfhost.sh --domain … --file arduiserver.py`（没装 Caddy 时加 `--caddy-binary Caddy路径`；`--pip-index` 指定 PyPI 镜像）。
- **完全离线：** 在一台能联网、Python 版本相同的机器上运行 `pip download pip uvicorn cryptography maxminddb -d wheels`，把这个目录一起拷过去，加 `--wheels wheels`。
- 部署完成后，在每台客户端的 `%LOCALAPPDATA%\ArdUi\data\config.json` 里设置 `"server": "https://你的域名/"`，重启 ArdUi。验证：浏览器打开 `https://你的域名/api/health`。

**整个 M 一次装好**：除设备目录外，脚本还会装好：
- ArdFerry 目录；
- 信令与地址发现；
- M 自己的密钥（只生成一次，保存在 `/var/lib/arduiserver-m`）；
- `/etc/ardui/network.json`，告诉客户端和 ArdFerry 上述服务在哪里。

说明：
- 这些组件来自发布页附带的 **M 包** `ardui-m-linux-x86_64.tar.gz`：静态编译的 `ard-relay`、`ard-relay-admission`（任何 x86_64 Linux 都能运行），`master.py`、`ferry_identity.py`，以及同一版本的 ArdUi 安装包，由 M 提供给它的客户端：有新版本时，ArdUi 在后台从 M 下载，核对大小和 SHA-256，下载完询问用户是否安装（不会自行安装）。脚本会自动下载，只有 SHA-256 与脚本里固定的值一致才会安装。连不上 GitHub 的服务器：在别处下载后加 `--m-package 文件`。
- 防火墙需另外放行 TCP 8080，以及 TCP 和 UDP 7842。
- 面向公网的进程拿不到 M 的密钥；只有 `arduiserver-control` 和 HTTPS 前端持有（见 [ROLES.md](ROLES.md)）。
- ArdFerry 节点用你的 M 提供的一键脚本加入：Linux 上 `curl -fsSL https://你的域名/ardferry.sh | sudo bash`，Windows 上 `& ([scriptblock]::Create((irm https://你的域名/ardferry.ps1)))`。任何人也可以用 `f.visnova.cn` 以同样方式为官方网络增加 ArdFerry。
- `--no-m` 只装设备目录（客户端能登记，但连不上）。
- **账号**：只有登录的设备可以连接（控制）其他设备，被控不需要账号。部署后在服务器上创建第一个管理员：`sudo -u arduiserver /opt/arduiserver/venv/bin/python /opt/arduiserver/arduiserver.py --add-user 名字 --admin`（打印一次初始密码），再到 `https://你的域名/api/admin` 登录管理页面创建用户、审核注册申请。用户在 `https://你的域名/api/account` 注册、登录设备和修改密码。

已验证：整个脚本（含 Caddy）在断网的 systemd 容器（OpenCloudOS 9）里跑通，包括各服务以独立用户运行在加固沙箱中、HTTPS、HTTP 跳转、签名 API 调用、重复运行、IP 地址、卸载；在一台公网服务器上真实签发了 Let's Encrypt IP 证书，ArdUi 客户端对它完成了完整的端到端测试。尚未验证：在全新服务器上为域名签发真实证书。

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build ArdUi.csproj
dotnet run --project ArdUi.csproj                      # 运行
dotnet run --project ArdUi.csproj -- --self-test        # 基础自检
```

内置测试（无需网络）：`--chat-test`、`--chat-ui-test`、`--perm-test`、`--connect-flow-test`、`--audit-test`、`--voice-test`、`--i18n-test`。目录服务器的测试在 `tests/presence_test.py`。

打包安装程序需要 [onesetup](https://github.com/KateY07/onesetup) v3.pre10（放在仓库上一级的 `.tools\onesetup\onesetup.exe`）、`ard.exe` 和 `D:\pub\FRD` 下的 FRD 发布包：

```powershell
powershell -File packaging\build-onesetup-release.ps1
```

源码结构见 [ARCHITECTURE.md](ARCHITECTURE.md)，各版本变化见 [release-notes](release-notes)。

## 许可与第三方组件

第三方组件及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。FRD 随安装包提供。安装包**不含** FFmpeg：FRD 默认使用 Windows 自带的 H.264 编解码器，FFmpeg（GPL 构建）是需要时由你自己安装的可选扩展。
