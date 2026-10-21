# ArdUi

**English** · [简体中文](README.zh-CN.md)

High-performance remote access for Windows. Peer-to-peer and end-to-end encrypted, connect by a short device ID with no IP addresses, ports or virtual adapters to set up, and the directory server can be self-hosted with one command.

**[Website](https://katey07.github.io/ArdUi/)** · **[Download](https://github.com/KateY07/ArdUi/releases)** · [Self-hosting](#self-hosting) · [Historical design notes (Chinese)](docs/DESIGN-v2.md)

Current version: v5.3. From v5 the server refuses clients older than v5.pre1.

## Why ArdUi

- **High performance**: direct UDP paths between devices, automatic fallback to a relay when no direct path exists, and low-latency desktop streaming through [FRD](https://github.com/KateY07/frd).
- **Peer-to-peer**: data goes straight from one device to the other. The directory server only hands out device IDs and coordinates connections; it is never on the data path.
- **End-to-end encrypted**: every device has a permanent key whose public half is its identity. Identities are verified before any password is sent, and the server never receives passwords.
- **Self-hostable**: one command deploys your own directory server (Linux, HTTPS included). Everything is open source.

Built on [ARD](https://github.com/KateY07/ard2) (Iroh).

## Features

| Feature | Description |
| --- | --- |
| Device ID connect | Enter the 6-character ID and a one-time password, compare the full device fingerprint, and the host approves. Later connections use the device identity and need no password. |
| Per-device permissions | The host allows FRD, RDP and file access separately for each controlling device (FRD only by default), can accept a connection **just once**, and can revoke or disconnect at any time. |
| Remote desktop | View and control the signed-in desktop with FRD, or use RDP or an SMB file share. |
| Chat and file transfer | Text, images and files up to 2 GB, with speed and progress, cancellable; chat keeps working during a transfer. |
| Audit recording | While a device uses the screen it is recorded locally on the host (10 fps, fragmented MP4, up to 10 GB, oldest overwritten) with the H.264 encoder built into Windows. Always on and cannot be switched off; the host shows "recording". |
| English and Chinese | The interface speaks English or Chinese: automatic (follows the Windows display language) or chosen in Settings → General → Language; it takes effect after a restart. What you and the other side type (chat, device names) is never translated. |
| Voice calls | Call a connected device from the chat header: Opus at 24 kbps over the same encrypted link (peer-to-peer or relayed), ringing, answer / decline / mute / hang up from the window or the floating island, and a call record in the host's audit log (the sound itself is not recorded). A side without a working microphone or speaker can still join, and both sides are told. Use a headset (no echo cancellation yet). Can be switched off in Settings. |
| Microphone diagnosis | Settings → 麦克风诊断: lists audio devices, checks Windows' microphone privacy switches, measures the microphone (frames, level, clipping, all-zero), records and plays back, and plays a test tone. Nothing leaves the machine. |
| Network diagnosis | Settings → 网络诊断: this machine's addresses and exits (several public exits or a large intranet?), NAT type, hop-by-hop path, UPnP / NAT-PMP, and a verdict on whether a direct (peer-to-peer) connection is likely. The directory server (M) takes part: it sees your public address from two ports, echoes and pushes measured traffic, and sends an unsolicited packet to a port you listen on, which proves that a UPnP mapping or a public address really works from outside. Details and abuse protection: [docs/probe-protocol.md](docs/probe-protocol.md). |
| Floating island | A small draggable pill near the tray: connection state, a 4-second message bubble, unread count, file progress and the current call (answer, decline, mute, hang up); click to open the chat. Hidden over full-screen apps by default. |
| No admin rights | No drivers and no network changes; identity and settings are kept across upgrades. |

## Quick start

Requires Windows 10/11 x64 and the [.NET 8 desktop runtime](https://dotnet.microsoft.com/download/dotnet/8.0). FRD is Native AOT and needs no runtime.

1. **Install**: download the latest `ArdUi-vX.exe` from [Releases](https://github.com/KateY07/ArdUi/releases) and run it. It installs to `%LOCALAPPDATA%\ArdUi` and adds a Start menu entry.
2. **Host** (the computer to be reached): switch on "Allow control". You get a device ID, and clicking shows a first-connection password for 10 seconds. Give both to the person connecting.
3. **Controller**: enter the ID and password and press Connect. Compare the device fingerprint over a separate channel (for example a phone call); the host accepts, or accepts once, and chooses which features to allow.
4. **Use it**: authorized devices appear in the list. FRD opens the desktop, Desktop uses RDP, Files opens the share, and the chat icon sends messages and files.

## Self-hosting

By default ArdUi uses a public directory service. To run your own **device directory service** on a Linux machine with a domain name (Python + Caddy with automatic HTTPS):

```bash
curl -fsSL https://raw.githubusercontent.com/KateY07/ArdUi/main/selfhost.sh | sudo bash -s -- --domain ardui.example.com
```

- Needs Linux with systemd, root, a domain already pointing at the machine, and ports 80/443 open. The script is [selfhost.sh](selfhost.sh); read it before running.
- `--dry-run` previews without changing anything; `--http-only` runs only the directory service so you can put your own HTTPS reverse proxy in front; `--uninstall` removes the services and keeps the data in `/var/lib/arduiserver`.
- The script also installs the **UDP network probe** (ports 7843 and 7844 by default; `--probe-port N`, or `--no-probe`). Open both UDP ports in the firewall / cloud security group so that ArdUi's network diagnosis can use your server.
- Other options: `--name NAME` (service name, user and directories, to run beside another instance), `--port N`, `--pip-index URL`, `--caddy-binary PATH`.
- **Servers in mainland China** usually cannot reach GitHub or caddyserver.com. Download [`selfhost.sh`](selfhost.sh) and [`server/arduiserver.py`](server/arduiserver.py) elsewhere, copy them over, and run `sudo bash selfhost.sh --domain … --file arduiserver.py` (add `--caddy-binary /path/to/caddy` if Caddy is not installed; `--pip-index` selects a PyPI mirror).
- Then, on every client, set `"server": "https://your-domain/"` in `%LOCALAPPDATA%\ArdUi\data\config.json` and restart ArdUi. Check the server at `https://your-domain/api/health`.

Scope: the script deploys the directory service only. Relay nodes and the relay directory (M) still default to the public ones; to run your own see the [relay node guide](https://github.com/KateY07/ard2/tree/main/native_relay), which is a manual process for now. Verified so far: the full run without HTTPS (`--http-only`) in a smolvm VM and on a real systemd server (OpenCloudOS 9), and the refusal to run when ports 80/443 are taken. The Caddy/HTTPS step has not been exercised on a fresh server with a public domain.

## Building from source

Needs the .NET 8 SDK.

```powershell
dotnet build ArdUi.csproj
dotnet run --project ArdUi.csproj                      # run
dotnet run --project ArdUi.csproj -- --self-test        # basic self-check
```

Built-in tests (no network needed): `--chat-test`, `--chat-ui-test`, `--perm-test`, `--connect-flow-test`, `--audit-test`, `--voice-test`, `--i18n-test`. The directory server's tests are in `tests/presence_test.py`.

Packaging the installer needs [onesetup](https://github.com/KateY07/onesetup) v3.pre10 (at `.tools\onesetup\onesetup.exe` one level above the repo), `ard.exe` and an FRD release zip in `D:\pub\FRD`:

```powershell
powershell -File packaging\build-onesetup-release.ps1
```

Source layout: [ARCHITECTURE.md](ARCHITECTURE.md). Release notes: [release-notes](release-notes).

## Licenses and third-party components

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). FRD ships with the installer. FFmpeg is **not** included: FRD uses the H.264 codec built into Windows by default, and FFmpeg (GPL build) is an optional extension you install yourself.
