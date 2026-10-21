# ArdUi

**English** · [简体中文](README.zh-CN.md)

High-performance remote access for Windows. Peer-to-peer and end-to-end encrypted, connect by a short device ID with no IP addresses, ports or virtual adapters to set up, and the directory server can be self-hosted with one command.

**[Website](https://katey07.github.io/ArdUi/)** · **[Download](https://github.com/KateY07/ArdUi/releases)** · [Self-hosting](#self-hosting) · [Historical design notes (Chinese)](docs/DESIGN-v2.md)

Current version: v5.pre1 (pre-release). From v5 the server refuses clients older than v5.pre1.

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
| Floating island | A small draggable pill near the tray: connection state, a 4-second message bubble, unread count and file progress; click to open the chat. Hidden over full-screen apps by default. |
| No admin rights | No drivers and no network changes; identity and settings are kept across upgrades. |

## Quick start

Requires Windows 10/11 x64 and the [.NET 8 and .NET 10 desktop runtimes](https://dotnet.microsoft.com/download).

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
- Then, on every client, set `"server": "https://your-domain/"` in `%LOCALAPPDATA%\ArdUi\data\config.json` and restart ArdUi. Check the server at `https://your-domain/api/health`.

Scope: the script deploys the directory service only. Relay nodes and the relay directory (M) still default to the public ones; to run your own see the [relay node guide](https://github.com/KateY07/ard2/tree/main/native_relay), which is a manual process for now. The complete self-hosting flow has not been verified end to end on a fresh server.

## Building from source

Needs the .NET 8 SDK.

```powershell
dotnet build ArdUi.csproj
dotnet run --project ArdUi.csproj                      # run
dotnet run --project ArdUi.csproj -- --self-test        # basic self-check
```

Built-in tests (no network needed): `--chat-test`, `--chat-ui-test`, `--perm-test`, `--connect-flow-test`, `--audit-test`. The directory server's tests are in `tests/presence_test.py`.

Packaging the installer needs [onesetup](https://github.com/KateY07/onesetup) v3.pre10 (at `.tools\onesetup\onesetup.exe` one level above the repo), `ard.exe` and an FRD release zip in `D:\pub\FRD`:

```powershell
powershell -File packaging\build-onesetup-release.ps1
```

Source layout: [ARCHITECTURE.md](ARCHITECTURE.md). Release notes: [docs/releases](docs/releases).

## Licenses and third-party components

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). FRD ships with the installer; the FFmpeg libraries it carries are distributed under their own (GPL) licence, whose text is in `ffmpeg\LICENSE.txt` in its install folder.
