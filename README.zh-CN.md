# ArdUi

[English](README.md) · **简体中文**

用 6 位编号，安全地远程访问 Windows 电脑。端到端加密，没有 IP、端口、虚拟网卡要配置；被控端逐设备授权；内置对话、文件传输和被控端审计录屏。

**[网页介绍与演示](https://katey07.github.io/ArdUi/)** · **[下载](https://github.com/KateY07/ArdUi/releases)** · [自托管](#自托管) · [历史设计文档](docs/DESIGN-v2.md)

当前版本：v5.pre1（预发布）。v5 起，服务端拒绝所有 v5.pre1 以下的旧客户端。

## 简介

ArdUi 基于 [ARD](https://github.com/KateY07/ard2)（Iroh）建立点对点的加密连接。每台设备有一对永久密钥，公钥就是设备身份；中央目录服务器只负责分配 6 位机器编号和协调连接，不接收密码，也不在数据路径上。

| 功能 | 说明 |
| --- | --- |
| 6 位编号连接 | 输入对方的编号和一次性密码，核对完整设备指纹后，由被控端同意授权。之后凭设备身份重连，不用再输密码。 |
| 逐设备授权 | 被控端为每台控制设备单独开放 FRD、RDP、文件，默认只开 FRD；支持“仅接受这一次”；可随时撤销或断开。 |
| 远程桌面 | 通过 [FRD](https://github.com/KateY07/frd) 查看和控制已登录的桌面，也可打开 RDP 或 SMB 文件共享。 |
| 对话与文件传输 | 文字、图片、文件（最大 2 GB），显示速度与进度，可取消，传输时仍可聊天。 |
| 审计录屏 | 对方使用屏幕时，在被控端本地录制（10 帧/秒，保存为分片 MP4），最多占用 10 GB，循环覆盖。使用 Windows 自带的 H.264 编码器，不依赖 FFmpeg。录屏默认开启、不可关闭，被控端首页会显示“录屏中”。 |
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
- 加 `--dry-run` 先预览；加 `--http-only` 只运行目录服务（你自己提供 HTTPS 反向代理）；`--uninstall` 卸载（数据保留在 `/var/lib/arduiserver`）。
- 脚本同时安装 **UDP 网络探测服务**（默认 UDP 7843 和 7844；`--probe-port N` 改端口，`--no-probe` 不装）。请在防火墙和云安全组里放行这两个 UDP 端口，ArdUi 的网络诊断才能使用你的服务器。
- 其他选项：`--name 名称`（服务名、用户和目录，可与另一个实例并存）、`--port N`、`--pip-index URL`、`--caddy-binary 路径`。
- **中国大陆的服务器**通常连不上 GitHub 和 caddyserver.com：请在别处下载 [`selfhost.sh`](selfhost.sh) 和 [`server/arduiserver.py`](server/arduiserver.py) 再拷过去，运行 `sudo bash selfhost.sh --domain … --file arduiserver.py`（没装 Caddy 时加 `--caddy-binary Caddy路径`；`--pip-index` 指定 PyPI 镜像）。
- 部署完成后，在每台客户端的 `%LOCALAPPDATA%\ArdUi\data\config.json` 里设置 `"server": "https://你的域名/"`，重启 ArdUi。验证：浏览器打开 `https://你的域名/api/health`。

范围说明：脚本只部署设备目录服务。流量中继和中继目录（M）默认仍使用公共节点；自建它们请见 [ard2 的中继节点部署文档](https://github.com/KateY07/ard2/tree/main/native_relay)，这部分目前需要手动完成。已验证：不带 HTTPS 的完整流程（`--http-only`）在 smolvm 虚拟机和一台真实的 systemd 服务器（OpenCloudOS 9）上跑通，80/443 被占用时会拒绝继续。Caddy/HTTPS 这一步还没有在带公网域名的全新服务器上跑过。

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build ArdUi.csproj
dotnet run --project ArdUi.csproj                      # 运行
dotnet run --project ArdUi.csproj -- --self-test        # 基础自检
```

内置测试（无需网络）：`--chat-test`、`--chat-ui-test`、`--perm-test`、`--connect-flow-test`、`--audit-test`、`--voice-test`。目录服务器的测试在 `tests/presence_test.py`。

打包安装程序需要 [onesetup](https://github.com/KateY07/onesetup) v3.pre10（放在仓库上一级的 `.tools\onesetup\onesetup.exe`）、`ard.exe` 和 `D:\pub\FRD` 下的 FRD 发布包：

```powershell
powershell -File packaging\build-onesetup-release.ps1
```

源码结构见 [ARCHITECTURE.md](ARCHITECTURE.md)，各版本变化见 [release-notes](release-notes)。

## 许可与第三方组件

第三方组件及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。FRD 随安装包提供。安装包**不含** FFmpeg：FRD 默认使用 Windows 自带的 H.264 编解码器，FFmpeg（GPL 构建）是需要时由你自己安装的可选扩展。
