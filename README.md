# ArdUi

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
| 灵动岛 | 屏幕右下角（托盘上方）的小胶囊，可拖动：显示连接状态、新消息气泡（4 秒）、未读数和文件传输进度，点击打开对话。全屏或演示时默认不显示。 |
| 无需管理员权限 | 不安装驱动，不改网络设置；设备身份和配置在升级时保留。 |

## 快速入门

需要 Windows 10/11 x64，以及 [.NET 8 和 .NET 10 桌面运行时](https://dotnet.microsoft.com/download)。

1. **安装**：到 [Releases](https://github.com/KateY07/ArdUi/releases) 下载最新的 `ArdUi-vX.exe`，双击运行，按安装窗口提示完成。安装在 `%LOCALAPPDATA%\ArdUi`，开始菜单里会有 ArdUi。
2. **被控端**（要被访问的电脑）：打开“允许被控”，得到 6 位编号；点击后显示 10 秒的首次密码。把编号和密码告诉对方。
3. **主控端**（你的电脑）：输入对方的编号和密码，点“连接”。双方用电话等独立渠道核对设备指纹，被控端点“接受”或“仅接受这一次”，并选择开放的功能。
4. **使用**：已授权的设备出现在列表里，点 FRD 看桌面，“桌面”用 RDP，“文件”访问共享，对话图标发消息和文件。

## 自托管

默认使用公共的设备目录服务。想自己掌握，可以在一台有域名的 Linux 服务器上用一条命令部署自己的**设备目录服务**（Python + Caddy 自动 HTTPS）：

```bash
curl -fsSL https://katey07.github.io/ArdUi/selfhost.sh | sudo bash -s -- --domain ardui.example.com
```

- 需要：Linux（systemd）、root 权限、已指向本机的域名、开放 80/443 端口。
- 加 `--dry-run` 先预览；加 `--http-only` 只运行目录服务（你自己提供 HTTPS 反向代理）；`--uninstall` 卸载（数据保留在 `/var/lib/arduiserver`）。
- 部署完成后，在每台客户端的 `%LOCALAPPDATA%\ArdUi\data\config.json` 里设置 `"server": "https://你的域名/"`，重启 ArdUi。验证：浏览器打开 `https://你的域名/api/health`。

范围说明：脚本只部署设备目录服务。流量中继和中继目录（M）默认仍使用公共节点；自建它们请见 [ard2 的中继节点部署文档](https://github.com/KateY07/ard2/tree/main/native_relay)，这部分目前需要手动完成。完整的自托管流程还没有在全新服务器上做过端到端验收。

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build ArdUi.csproj
dotnet run --project ArdUi.csproj                      # 运行
dotnet run --project ArdUi.csproj -- --self-test        # 基础自检
```

内置测试（无需网络）：`--chat-test`、`--chat-ui-test`、`--perm-test`、`--connect-flow-test`、`--audit-test`。目录服务器的测试在 `tests/presence_test.py`。

打包安装程序需要 [onesetup](https://github.com/KateY07/onesetup) v3.pre10（放在仓库上一级的 `.tools\onesetup\onesetup.exe`）、`ard.exe` 和 `D:\pub\FRD` 下的 FRD 发布包：

```powershell
powershell -File packaging\build-onesetup-release.ps1
```

源码结构见 [ARCHITECTURE.md](ARCHITECTURE.md)，各版本变化见 [docs/releases](docs/releases)。

## 许可与第三方组件

第三方组件及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。FRD 随安装包提供，它携带的 FFmpeg 库按其自带的许可证（GPL）分发，授权文本在其安装目录的 `ffmpeg\LICENSE.txt`。
