#!/usr/bin/env bash
# ArdFerry: the traffic forwarding node of an ArdUi network, in one step. Get this script from the network's M and run it:
#
#   curl -fsSL https://M-DOMAIN/ardferry.sh | sudo bash
#
# The script fetched from an M joins that M's network: the M's domain is its only setting (ROLES.md). The node makes its own key,
# finds M through the domain and registers itself; anyone may run one, and M publishes it only after probing it. A copy of this
# script from GitHub has no M filled in: give --m M-DOMAIN.
#
# Options (curl ... | sudo bash -s -- OPTIONS):
#   --m DOMAIN         the network's M (default: the M this script came from)
#   --no-upnp          no UPnP / NAT-PMP port mapping on the router
#   --pip-index URL    a PyPI mirror for the one Python package (cryptography), e.g. https://mirrors.aliyun.com/pypi/simple/
#   --relay-archive F  an already downloaded official iroh-relay release archive
#   --relay-mirror URL download the official iroh-relay archive from URL/<archive name> only
#   --prefix DIR       install directory (default /opt/ardferry)
#   --uninstall        remove the service (the node key in the install directory is kept)
# The official iroh-relay 1.1.0 is downloaded from its GitHub release, or from the M when GitHub cannot be reached, and checked
# against the SHA-256 its release publishes either way. The node and iroh-relay run as user ardferry in a hardened sandbox.
set -euo pipefail

M='@M@'
case "$M" in @*) M=;; esac
PREFIX=/opt/ardferry
ARCHIVE=
MIRROR=
PIP_INDEX=
UPNP=true
UNINSTALL=false
GITHUB=https://github.com/n0-computer/iroh/releases/download/v1.1.0

while [ $# -gt 0 ]; do
  case "$1" in
    --m) M=${2#https://}; M=${M%%/*}; shift 2;;
    --relay-archive) ARCHIVE=$2; shift 2;;
    --relay-mirror) MIRROR=${2%/}; shift 2;;
    --pip-index) PIP_INDEX=$2; shift 2;;
    --prefix) PREFIX=$2; shift 2;;
    --no-upnp) UPNP=false; shift;;
    --uninstall) UNINSTALL=true; shift;;
    -h|--help) sed -n '2,20p' "$0" 2>/dev/null | sed 's/^# \{0,1\}//' || true; exit 0;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

[ "$(id -u)" -eq 0 ] || { echo "run as root (sudo)" >&2; exit 2; }
[ "$(uname -s)" = Linux ] && command -v systemctl >/dev/null || { echo "Linux with systemd is required" >&2; exit 2; }

if $UNINSTALL; then
  systemctl disable --now ardferry 2>/dev/null || true
  rm -f /etc/systemd/system/ardferry.service
  systemctl daemon-reload 2>/dev/null || true
  echo "Service removed. Files in $PREFIX are kept; delete them by hand (they contain the node key)."
  exit 0
fi

[ -n "$M" ] || { echo "give --m M-DOMAIN (the HTTPS domain of the network's M)" >&2; exit 2; }
printf '%s' "$M" | grep -qE '^[A-Za-z0-9.:-]+$' || { echo "not a domain: $M" >&2; exit 2; }
command -v python3 >/dev/null && python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' \
  || { echo "python3 (3.8 or newer) is required" >&2; exit 2; }
python3 -c 'import venv, ensurepip' 2>/dev/null || { echo "python3 venv is required (e.g. apt install python3-venv)" >&2; exit 2; }
command -v curl >/dev/null || { echo "curl is required" >&2; exit 2; }
echo "ArdFerry joins the network of $M"

# ---- the official relay: the static (musl) build for this CPU, verified against the SHA-256 its GitHub release publishes ----
case "$(uname -m)" in
  x86_64|amd64) NAME=iroh-relay-v1.1.0-x86_64-unknown-linux-musl.tar.gz; SUM=9a68108b824e4164ad2eec729cf0e8167e4cb50581cb745a242bace135df7614;;
  aarch64|arm64) NAME=iroh-relay-v1.1.0-aarch64-unknown-linux-musl.tar.gz; SUM=1b4261b6dd0d17ae9a7516aa6d122b296a77678bac174ce30262702c9f91cb00;;
  *) echo "no official iroh-relay build for $(uname -m)" >&2; exit 2;;
esac
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
fetch() { curl -fsSL --connect-timeout 15 --max-time 600 -o "$WORK/$NAME" "$1/$NAME"; }
verified() { [ -f "$WORK/$NAME" ] && [ "$(sha256sum "$WORK/$NAME" | cut -d' ' -f1)" = "$SUM" ]; }
if [ -n "$ARCHIVE" ]; then cp "$ARCHIVE" "$WORK/$NAME"
elif [ -n "$MIRROR" ]; then echo "Downloading $NAME from $MIRROR"; fetch "$MIRROR" || true
elif [ -f "$PREFIX/$NAME" ]; then cp "$PREFIX/$NAME" "$WORK/$NAME"   # kept by an earlier run; checked again below
fi
if [ -z "$ARCHIVE$MIRROR" ] && ! verified; then
  echo "Downloading $NAME"
  fetch "$GITHUB" || { echo "GitHub did not answer; trying the copy on $M"; fetch "https://$M/ardferry" || true; }
fi
verified || { echo "could not get $NAME matching the official SHA-256 (use --relay-archive FILE or --relay-mirror URL)" >&2; exit 2; }
tar -xzf "$WORK/$NAME" -C "$WORK"
RELAY=$(find "$WORK" -type f -name iroh-relay | head -1)
[ -n "$RELAY" ] || { echo "no iroh-relay inside $NAME" >&2; exit 2; }
echo "official iroh-relay verified ($NAME)"

# ---- the node: its Python code is part of this script ----
base64 -d > "$WORK/node.tar.gz" <<'PAYLOAD'
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
PAYLOAD
mkdir -p "$WORK/node" && tar -xzf "$WORK/node.tar.gz" -C "$WORK/node"

# Before v5.5 the node was installed as "ard-relay-node": stop it so the two do not fight over the relay port and the mapping.
if [ -f /etc/systemd/system/ard-relay-node.service ]; then
  systemctl disable --now ard-relay-node 2>/dev/null || true
  rm -f /etc/systemd/system/ard-relay-node.service
  echo "Stopped the old ard-relay-node service (its files in /opt/ard-relay-node are kept)."
fi

umask 077
mkdir -p "$PREFIX/data"
cp "$WORK"/node/*.py "$PREFIX/"
install -m 755 "$RELAY" "$PREFIX/iroh-relay"
cp "$WORK/$NAME" "$PREFIX/$NAME"   # re-runs need no download
# its own Python with the one package it needs (cryptography: its key, certificate and signed registrations)
[ -x "$PREFIX/venv/bin/python" ] || python3 -m venv "$PREFIX/venv" || { echo "python3 -m venv failed" >&2; exit 2; }
"$PREFIX/venv/bin/pip" install --quiet --no-cache-dir --disable-pip-version-check ${PIP_INDEX:+--index-url "$PIP_INDEX"} cryptography \
  || { echo "could not install the Python package cryptography (try --pip-index URL)" >&2; exit 2; }
"$PREFIX/venv/bin/python" - "$PREFIX" "$UPNP" "$M" <<'PY'
import hashlib, json, sys
from pathlib import Path
prefix, upnp, m = Path(sys.argv[1]), sys.argv[2] == 'true', sys.argv[3]
config = dict(m=m, data=str(prefix / 'data'), relay_port=18443, upnp=upnp,
              official_relay_sha256=hashlib.sha256((prefix / 'iroh-relay').read_bytes()).hexdigest())
(prefix / 'node.json').write_text(json.dumps(config, indent=1))
PY

# The node runs as its own user: it reads the program and its configuration (root's, group-readable) and writes only its data
# (its own key and the relay's state). The stock iroh-relay it supervises faces the internet; it gets nothing more.
id ardferry >/dev/null 2>&1 || useradd --system --home-dir "$PREFIX/data" --shell /usr/sbin/nologin ardferry
chown -R root:ardferry "$PREFIX"; chmod -R g+rX,g-w,o-rwx "$PREFIX"
chown -R ardferry:ardferry "$PREFIX/data"; chmod 700 "$PREFIX/data"

cat > /etc/systemd/system/ardferry.service <<EOF
[Unit]
Description=ArdFerry traffic forwarding node of $M (managed stock iroh-relay)
After=network-online.target
Wants=network-online.target
[Service]
User=ardferry
Group=ardferry
ExecStart=$PREFIX/venv/bin/python -u $PREFIX/node.py --config $PREFIX/node.json --official-relay $PREFIX/iroh-relay
Restart=always
RestartSec=3
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$PREFIX/data
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
ProtectProc=invisible
CapabilityBoundingSet=
# netlink: the node reads its addresses and default routes with ip(8)
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
RestrictNamespaces=true
RestrictRealtime=true
RestrictSUIDSGID=true
LockPersonality=true
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallFilter=~@privileged @resources
# ip(8) drops its capabilities at start and gives up if it cannot. With no capabilities and NoNewPrivileges, capset can only drop.
SystemCallFilter=capset
SystemCallErrorNumber=EPERM
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --quiet ardferry
started=$(date '+%Y-%m-%d %H:%M:%S')
systemctl restart ardferry   # a re-run applies the new files and configuration
echo "Waiting for the start-up self-check (up to 100 s)..."
for _ in $(seq 1 50); do
  journalctl -u ardferry --since "$started" --no-pager 2>/dev/null | grep -q 'SELF-CHECK RESULT' && break
  sleep 2
done
journalctl -u ardferry --since "$started" --no-pager -o cat | grep -E 'SELF-CHECK|STATE' | tail -6 || true
cat <<EOF

ArdFerry is installed. Inbound TCP 18443 must be open (firewall / cloud security group) for M and clients to reach it.
Status any time:  $PREFIX/venv/bin/python $PREFIX/node.py --config $PREFIX/node.json --status
Logs:             journalctl -u ardferry -f
Remove:           curl -fsSL https://$M/ardferry.sh | sudo bash -s -- --uninstall
EOF
