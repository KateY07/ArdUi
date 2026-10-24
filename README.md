# ArdUi

**English** · [简体中文](README.zh-CN.md)

> ## Roles — read this first
>
> **There are exactly three roles: M, ArdFerry and ArdUi. Each is configured with one thing only: an HTTPS domain.** For ArdUi this is the existing `server` setting in `config.json`; nothing about the client's configuration changes. Keys are generated and kept by the programs themselves; they are not configuration.
>
> | Role | Its one setting | Responsibility |
> | --- | --- | --- |
> | **M** | **its own** HTTPS domain | The centre of one network. Registers devices and hands out their 6-character IDs, presence, passes signed connection requests between devices, keeps the list of ArdFerry nodes (probes each entrance, gives clients the working ones), address discovery and the network probe. Sees neither session content nor passwords. |
> | **ArdFerry** | **M's** HTTPS domain | Forwards encrypted traffic when two devices cannot connect directly. Runs the unmodified official `iroh-relay`; works with a public IP, or behind a router with UPnP / NAT-PMP port mapping. **Untrusted by design: anyone may run one and join a network.** It cannot read or alter anything; it can see IP addresses, timing and traffic volume of what passes through it, and it could slow traffic down, so clients prefer direct paths and pick relays by measured quality. |
> | **ArdUi** (client) | **M's** HTTPS domain (`server` in `config.json`) | Holds the device key (the device's identity). Connects peer-to-peer, falling back to an ArdFerry; authenticates the other device end to end. The controlled machine decides who may do what, and keeps its audit recording locally. |
>
> **One network = one M = one domain.** Self-hosting means running your own network: Bob's domain and Li's domain are two separate networks that know nothing of each other and do not interoperate. The official network is just one such network. Everyone runs the same programs; only the domain differs.
>
> Full definitions, keys and the remaining gaps between this design and the current code: [ROLES.md](ROLES.md).

High-performance remote access for Windows. Peer-to-peer and end-to-end encrypted, connect by a short device ID with no IP addresses, ports or virtual adapters to set up, and the directory server can be self-hosted with one command.

**[Website](https://katey07.github.io/ArdUi/)** · **[Download](https://github.com/KateY07/ArdUi/releases)** · [Self-hosting](#self-hosting) · [Historical design notes (Chinese)](docs/DESIGN-v2.md)

Current version: v6.pre2 (preview). The official server refuses clients older than v6.pre2: since v6.pre2 both sides of a session meet on one ArdFerry chosen by the controller, which roughly halves the time to connect again on slow networks.

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
| Per-device permissions | The host allows FRD, RDP, chat, files and calls separately for each controlling device, can accept a connection **just once**, and can revoke or disconnect at any time. |
| Remote desktop | View and control the signed-in desktop with FRD, or use RDP. |
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
- **No domain?** Give the server's public IP address: `--domain 203.0.113.7`. Let's Encrypt then issues a short-lived (about 6 days) IP certificate, which Caddy renews on its own, and clients use `https://203.0.113.7/`.
- `--dry-run` previews without changing anything; `--http-only` runs only the directory service so you can put your own HTTPS reverse proxy in front; `--uninstall` removes the services and keeps the data in `/var/lib/arduiserver`.
- The script also installs the **UDP network probe** (ports 7843 and 7844 by default; `--probe-port N`, or `--no-probe`). Open both UDP ports in the firewall / cloud security group so that ArdUi's network diagnosis can use your server.
- Other options: `--name NAME` (service name, user and directories, to run beside another instance), `--port N`, `--pip-index URL`, `--caddy-binary PATH`, `--wheels DIR`. Run `bash selfhost.sh --help` for all of them.
- **Servers in mainland China** usually cannot reach GitHub or caddyserver.com. Download [`selfhost.sh`](selfhost.sh), [`server/arduiserver.py`](server/arduiserver.py) and [`server/probe_udp.py`](server/probe_udp.py) elsewhere, copy them over, and run `sudo bash selfhost.sh --domain … --file arduiserver.py` (add `--caddy-binary /path/to/caddy` if Caddy is not installed; `--pip-index` selects a PyPI mirror).
- **Fully offline:** on a connected machine with the same Python version run `pip download pip uvicorn cryptography maxminddb -d wheels`, copy the folder too, and add `--wheels wheels`.
- Then, on every client, set `"server": "https://your-domain/"` in `%LOCALAPPDATA%\ArdUi\data\config.json` and restart ArdUi. Check the server at `https://your-domain/api/health`.

**The whole M** is installed in one run. Besides the device directory, the script installs:
- the ArdFerry directory;
- signaling and address discovery;
- M's own key (made once, kept in `/var/lib/arduiserver-m`);
- `/etc/ardui/network.json`, which tells clients and ArdFerry nodes where all of this is.

Details:
- These parts come from the **M package** attached to the release, `ardui-m-linux-x86_64.tar.gz`: static builds of `ard-relay` and `ard-relay-admission` (any x86_64 Linux) with `master.py` and `ferry_identity.py`. The script downloads it and accepts it only if its SHA-256 is the one pinned in the script. Servers that cannot reach GitHub: download it elsewhere and add `--m-package FILE`.
- Open TCP 8080 and TCP+UDP 7842 as well.
- The processes facing the public never hold M's key; only `arduiserver-control` and the HTTPS front do (see [ROLES.md](ROLES.md)).
- ArdFerry nodes join your network in one step, with the installer your M hands out: `curl -fsSL https://your-domain/ardferry.sh | sudo bash` on Linux, `& ([scriptblock]::Create((irm https://your-domain/ardferry.ps1)))` on Windows. Anyone can add capacity to the official network the same way with `f.visnova.cn`.
- `--no-m` installs only the device directory (clients can then register but not connect).

Verified: the whole script, Caddy included, in an offline systemd container (OpenCloudOS 9): services under their own users in a hardened sandbox, HTTPS, the HTTP redirect, signed API calls, running it twice, an IP address, uninstall. A real Let's Encrypt IP certificate was issued for a public server and ArdUi clients completed a full end-to-end run against it. Not yet tried: a real certificate for a domain name on a fresh server.

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
