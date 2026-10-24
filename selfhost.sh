#!/usr/bin/env bash
# ArdUi self-hosting: the device directory (arduiserver) behind Caddy with automatic HTTPS.
#
#   curl -fsSL https://raw.githubusercontent.com/KateY07/ArdUi/main/selfhost.sh | sudo bash -s -- --domain ardui.example.com
#
# Options:
#   --domain NAME     public DNS name that already points at this machine (ports 80 and 443 must be reachable);
#                     a public IP address works too: Let's Encrypt then issues a short-lived (about 6 days) IP certificate that Caddy renews
#   --http-only       only run the directory on 127.0.0.1:PORT; you bring your own HTTPS reverse proxy
#   --port N          local port of the directory (default 8091)
#   --ref NAME        git branch or tag of KateY07/ArdUi to take arduiserver.py from (default main)
#   --file PATH       use this arduiserver.py instead of downloading it (servers that cannot reach GitHub)
#   --base-url URL    download arduiserver.py from URL/server/arduiserver.py instead of raw.githubusercontent.com
#   --pip-index URL   Python package index for uvicorn and cryptography (default: pip's own setting)
#   --wheels DIR      install the Python packages from wheel files in DIR, without any network (offline installs;
#                     fill DIR on a connected machine with: pip download pip uvicorn cryptography maxminddb -d DIR)
#   --caddy-binary P  use this Caddy binary instead of downloading one from caddyserver.com
#   --tls-internal    sign the certificate with Caddy's own local CA instead of Let's Encrypt (tests, private networks; clients will not trust it)
#   --no-probe        do not install the UDP network probe (the part of ArdUi's network diagnosis that needs the server)
#   --probe-port N    first UDP port of the probe (it uses N and N+1, default 7843); open both in your firewall / cloud security group
#   --probe-host H    the host name clients use for the probe (default: the --domain)
#   The whole M is installed: besides the device directory, M's ArdFerry directory and its signaling / address discovery,
#   from the M package of the ArdUi release (ardui-m-linux-x86_64.tar.gz, checked against the SHA-256 pinned below).
#   --m-package FILE  use this already downloaded M package (servers that cannot reach GitHub)
#   --m-bundle DIR    use the M parts from DIR instead: ard-relay, ard-relay-admission, master.py, ferry_identity.py
#   --no-m            install only the device directory (clients can then register but not connect; see ROLES.md)
#   --m-identity FILE use this M identity (PKCS#8 DER private key) instead of making a new one, when moving an existing M
#   --m-host H        host name or address that clients and ArdFerry nodes use for the parts above (default: the --domain)
#   --directory-port N  public TCP port of the ArdFerry directory (default 8080)
#   --control-port N  public TCP and UDP port of signaling and address discovery (default 7842)
#   --front-group G   group of the HTTPS front that serves the directory with M's key (default ardui-caddy; with --http-only,
#                     the group your own proxy runs as)
#   --name NAME       service name, user and directories (default arduiserver); lets a second instance run beside another one
#   --dry-run         print what would be done and change nothing
#   --uninstall       remove the services (data in /var/lib/arduiserver is kept)
set -euo pipefail

DOMAIN=; PORT=8091; REF=main; SRC_FILE=; BASE_URL=; PIP_INDEX=; WHEELS=; CADDY_BIN=; NAME=arduiserver; PROBE=true; PROBE_PORT=7843; PROBE_HOST=; TLS_INTERNAL=false; HTTP_ONLY=false; DRY=false; UNINSTALL=false
M_BUNDLE=; M_PACKAGE=; NO_M=false; M_IDENTITY=; M_HOST=; DIRECTORY_PORT=8080; CONTROL_PORT=7842; FRONT_GROUP=ardui-caddy

CONF=/etc/ardui; CADDY_DATA=/var/lib/ardui-caddy

say()  { printf '\033[1m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }
run()  { if $DRY; then echo "+ $*"; else "$@"; fi; }
# write_file PATH MODE  (content on stdin)
write_file() { if $DRY; then echo "+ write $1"; cat >/dev/null; else cat >"$1"; chmod "$2" "$1"; fi; }

while [ $# -gt 0 ]; do
  case "$1" in
    --domain) DOMAIN=${2:?}; shift 2;;
    --http-only) HTTP_ONLY=true; shift;;
    --port) PORT=${2:?}; shift 2;;
    --ref) REF=${2:?}; shift 2;;
    --file) SRC_FILE=${2:?}; shift 2;;
    --base-url) BASE_URL=${2:?}; shift 2;;
    --pip-index) PIP_INDEX=${2:?}; shift 2;;
    --wheels) WHEELS=${2:?}; shift 2;;
    --caddy-binary) CADDY_BIN=${2:?}; shift 2;;
    --m-bundle) M_BUNDLE=${2:?}; shift 2;;
    --m-package) M_PACKAGE=${2:?}; shift 2;;
    --no-m) NO_M=true; shift;;
    --m-identity) M_IDENTITY=${2:?}; shift 2;;
    --m-host) M_HOST=${2:?}; shift 2;;
    --directory-port) DIRECTORY_PORT=${2:?}; shift 2;;
    --control-port) CONTROL_PORT=${2:?}; shift 2;;
    --front-group) FRONT_GROUP=${2:?}; shift 2;;
    --name) NAME=${2:?}; shift 2;;
    --tls-internal) TLS_INTERNAL=true; shift;;
    --no-probe) PROBE=false; shift;;
    --probe-port) PROBE_PORT=${2:?}; shift 2;;
    --probe-host) PROBE_HOST=${2:?}; shift 2;;
    --dry-run) DRY=true; shift;;
    --uninstall) UNINSTALL=true; shift;;
    -h|--help) awk 'NR>1 && !/^#/{exit} NR>1' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "unknown option: $1";;
  esac
done

case "$NAME" in ''|*[!a-z0-9-]*|-*) die "--name may only contain lower-case letters, digits and '-'";; esac
[ ${#NAME} -le 32 ] || die "--name is too long (32 characters at most)"
APP=/opt/$NAME; DATA=/var/lib/$NAME
[ "$(id -u)" -eq 0 ] || $DRY || die "run as root (sudo)"
[ "$(uname -s)" = Linux ] || die "Linux only"
command -v systemctl >/dev/null || die "systemd is required"

if $UNINSTALL; then
  say "removing services"
  for unit in "$NAME" "$NAME-probe" "$NAME-relays" "$NAME-control" ardui-caddy; do
    run systemctl disable --now "$unit" 2>/dev/null || true
    run rm -f "/etc/systemd/system/$unit.service"
  done
  run systemctl daemon-reload
  echo "Kept: $DATA (device database), /var/lib/$NAME-m (M identity), $APP, $CONF. Delete them by hand if you really want to start over."
  exit 0
fi

$HTTP_ONLY || [ -n "$DOMAIN" ] || die "give --domain NAME (or --http-only to run behind your own proxy)"
case "$PORT" in ''|*[!0-9]*) die "--port must be a number";; esac
case "$PROBE_PORT" in ''|*[!0-9]*) die "--probe-port must be a number";; esac
case "$DIRECTORY_PORT$CONTROL_PORT" in *[!0-9]*) die "--directory-port and --control-port must be numbers";; esac
# loopback only: the ArdFerry directory behind the HTTPS front, and the signaling's local HTTP
RELAYS_PORT=$((PORT + 1)); CONTROL_LOCAL=$((PORT + 2))
[ -n "$PROBE_HOST" ] || PROBE_HOST=$DOMAIN
[ -n "$M_HOST" ] || M_HOST=$DOMAIN
if ! $NO_M; then
  [ -n "$M_HOST" ] || die "with --http-only, give --m-host (the name or address clients reach this machine at), or --no-m"
  [ -z "$M_IDENTITY" ] || [ -f "$M_IDENTITY" ] || die "--m-identity: no such file: $M_IDENTITY"
  [ -z "$M_PACKAGE" ] || [ -f "$M_PACKAGE" ] || die "--m-package: no such file: $M_PACKAGE"
fi
# An IP address instead of a name: Let's Encrypt only issues IP certificates with its short-lived profile.
IS_IP=false
if printf '%s' "$DOMAIN" | grep -qE '^[0-9]{1,3}(\.[0-9]{1,3}){3}$|^[0-9a-fA-F:]*:[0-9a-fA-F:.]*$'; then IS_IP=true; fi

# ---- packages ---------------------------------------------------------------------------------------------------
say "checking packages"
need_install=false
command -v python3 >/dev/null && python3 -c 'import sys; sys.exit(0 if sys.version_info>=(3,8) else 1)' || need_install=true
command -v curl >/dev/null || need_install=true
python3 -c 'import venv, ensurepip' 2>/dev/null || need_install=true
if $need_install; then
  if   command -v apt-get >/dev/null; then run env DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-venv python3-pip curl ca-certificates
  elif command -v dnf     >/dev/null; then run dnf install -y python3 python3-pip curl ca-certificates
  elif command -v yum     >/dev/null; then run yum install -y python3 python3-pip curl ca-certificates
  elif command -v apk     >/dev/null; then die "Alpine is not supported (no systemd)"
  else die "install python3 (3.8+), python3-venv and curl, then run this again"; fi
fi

# ---- the M package -----------------------------------------------------------------------------------------------
# Static builds of ard-relay (signaling, address discovery) and ard-relay-admission (the ArdFerry probe), with master.py and
# ferry_identity.py, built from the ard2 sources of this release. The SHA-256 is pinned here, so a download is accepted only if it
# is exactly the published package.
M_RELEASE=v5.5
M_PACKAGE_NAME=ardui-m-linux-x86_64.tar.gz
M_PACKAGE_SHA256=bd84caa0fe34ca2b603c90fb617ef154082b1f1fac5b402077765a2d080a76be
if ! $NO_M && [ -z "$M_BUNDLE" ]; then
  case "$(uname -m)" in x86_64|amd64) ;; *) die "the M package is built for x86_64 only; on $(uname -m) give --m-bundle DIR (or --no-m)";; esac
  say "getting the M package ($M_PACKAGE_NAME)"
  MPKG=$(mktemp -d)
  if [ -n "$M_PACKAGE" ]; then run cp "$M_PACKAGE" "$MPKG/$M_PACKAGE_NAME"
  else run curl -fsSL --connect-timeout 15 --max-time 900 "https://github.com/KateY07/ArdUi/releases/download/$M_RELEASE/$M_PACKAGE_NAME" -o "$MPKG/$M_PACKAGE_NAME" \
    || die "could not download $M_PACKAGE_NAME (is GitHub reachable from here? download it elsewhere and give --m-package FILE)"; fi
  if ! $DRY; then
    [ "$(sha256sum "$MPKG/$M_PACKAGE_NAME" | cut -d' ' -f1)" = "$M_PACKAGE_SHA256" ] || die "$M_PACKAGE_NAME is not the published M package (SHA-256 mismatch)"
    tar -xzf "$MPKG/$M_PACKAGE_NAME" -C "$MPKG"
    echo "M package verified ($(cat "$MPKG/ardui-m/VERSION" 2>/dev/null))"
  fi
  M_BUNDLE=$MPKG/ardui-m
fi
if [ -n "$M_BUNDLE" ] && ! $DRY; then
  for f in ard-relay ard-relay-admission master.py ferry_identity.py; do [ -f "$M_BUNDLE/$f" ] || die "$M_BUNDLE/$f is missing"; done
fi

# ---- the directory service --------------------------------------------------------------------------------------
say "installing arduiserver"
id "$NAME" >/dev/null 2>&1 || run useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin "$NAME"
run install -d -m 755 "$APP"
run install -d -m 700 -o "$NAME" -g "$NAME" "$DATA"
tmp=$(mktemp)
if [ -n "$SRC_FILE" ]; then run cp "$SRC_FILE" "$tmp"
else run curl -fsSL --connect-timeout 15 "${BASE_URL:-https://raw.githubusercontent.com/KateY07/ArdUi/$REF}/server/arduiserver.py" -o "$tmp" || die "could not download arduiserver.py (is GitHub reachable from here? use --file or --base-url)"; fi
if ! $DRY; then grep -q 'def application' "$tmp" || die "that does not look like arduiserver.py"; echo "arduiserver.py sha256: $(sha256sum "$tmp" | cut -d' ' -f1)"; fi
run install -m 644 "$tmp" "$APP/arduiserver.py"; rm -f "$tmp"
if $PROBE; then
  ptmp=$(mktemp)
  if [ -n "$SRC_FILE" ] && [ -f "$(dirname "$SRC_FILE")/probe_udp.py" ]; then run cp "$(dirname "$SRC_FILE")/probe_udp.py" "$ptmp"
  else run curl -fsSL --connect-timeout 15 "${BASE_URL:-https://raw.githubusercontent.com/KateY07/ArdUi/$REF}/server/probe_udp.py" -o "$ptmp" || die "could not download probe_udp.py (use --no-probe, or put it next to the --file arduiserver.py)"; fi
  if ! $DRY; then grep -q 'class Service' "$ptmp" || die "that does not look like probe_udp.py"; fi
  run install -m 644 "$ptmp" "$APP/probe_udp.py"; rm -f "$ptmp"
fi
# The one-step ArdFerry installers this M hands out (https://DOMAIN/ardferry.sh and .ps1, with this M's domain filled in), and a copy of the
# official iroh-relay archives for servers that cannot reach GitHub; arduiserver serves an archive only if its SHA-256 is the official one.
run install -d -m 755 "$APP/ardferry"
src_dir=$( [ -n "$SRC_FILE" ] && cd "$(dirname "$SRC_FILE")" && pwd || true )
for script in ardferry.sh ardferry.ps1; do
  ftmp=$(mktemp)
  if [ -n "$src_dir" ] && [ -f "$src_dir/$script" ]; then run cp "$src_dir/$script" "$ftmp"
  elif [ -n "$src_dir" ] && [ -f "$src_dir/../$script" ]; then run cp "$src_dir/../$script" "$ftmp"
  else run curl -fsSL --connect-timeout 15 "${BASE_URL:-https://raw.githubusercontent.com/KateY07/ArdUi/$REF}/$script" -o "$ftmp" \
    || warn "could not get $script: this M will not hand it out (put it next to the --file arduiserver.py)"; fi
  if ! $DRY && [ -s "$ftmp" ]; then
    grep -qF "'@M@'" "$ftmp" || die "that does not look like $script"
    install -m 644 "$ftmp" "$APP/ardferry/$script"
  fi
  rm -f "$ftmp"
done
for spec in iroh-relay-v1.1.0-x86_64-unknown-linux-musl.tar.gz:9a68108b824e4164ad2eec729cf0e8167e4cb50581cb745a242bace135df7614 \
            iroh-relay-v1.1.0-aarch64-unknown-linux-musl.tar.gz:1b4261b6dd0d17ae9a7516aa6d122b296a77678bac174ce30262702c9f91cb00 \
            iroh-relay-v1.1.0-x86_64-pc-windows-msvc.zip:052bd32704afcb4492a0cd8e619e1bb47c69cbe8ecaa89907c22033634b3da1c; do
  archive=${spec%%:*}; sum=${spec##*:}; target="$APP/ardferry/$archive"
  $DRY && { echo "+ fetch $archive"; continue; }
  [ -f "$target" ] && [ "$(sha256sum "$target" | cut -d' ' -f1)" = "$sum" ] && continue
  atmp=$(mktemp)
  if [ -n "$src_dir" ] && [ -f "$src_dir/$archive" ]; then cp "$src_dir/$archive" "$atmp"
  else curl -fsSL --connect-timeout 15 --max-time 300 "https://github.com/n0-computer/iroh/releases/download/v1.1.0/$archive" -o "$atmp" || true; fi
  if [ "$(sha256sum "$atmp" | cut -d' ' -f1)" = "$sum" ]; then install -m 644 "$atmp" "$target"
  else warn "no official $archive for the ArdFerry installer to fall back on (GitHub unreachable? put it next to the --file arduiserver.py)"; fi
  rm -f "$atmp"
done
[ -x "$APP/venv/bin/python" ] || run python3 -m venv "$APP/venv"
if [ -n "$WHEELS" ]; then
  [ -d "$WHEELS" ] || die "--wheels: no such directory: $WHEELS"
  run "$APP/venv/bin/pip" install --quiet --no-cache-dir --no-index --find-links "$WHEELS" --upgrade pip uvicorn cryptography maxminddb
else
  run "$APP/venv/bin/pip" install --quiet --no-cache-dir ${PIP_INDEX:+--index-url "$PIP_INDEX"} --upgrade pip uvicorn cryptography maxminddb
fi

# The sandbox both Python services run in: no privileges, no devices, a read-only system, only IP and Unix sockets.
# (No MemoryDenyWriteExecute: cryptography's cffi callbacks need writable-executable memory.)
HARDEN='NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectHome=true
ProtectSystem=strict
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
ProtectProc=invisible
ProcSubset=pid
CapabilityBoundingSet=
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
RestrictNamespaces=true
RestrictRealtime=true
RestrictSUIDSGID=true
LockPersonality=true
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallFilter=~@privileged @resources'

write_file "/etc/systemd/system/$NAME.service" 644 <<EOF
[Unit]
Description=ArdUi device directory ($NAME)
After=network-online.target
[Service]
User=$NAME
Group=$NAME
WorkingDirectory=$APP
Environment=ARDUI_DATABASE=$DATA/devices.sqlite3
Environment=ARDUI_PROBE_PORT=$PROBE_PORT
$($PROBE && [ -n "$PROBE_HOST" ] && echo "Environment=ARDUI_PROBE_HOST=$PROBE_HOST")
$([ -n "$M_BUNDLE" ] && echo "Environment=ARDUI_M_STATUS=http://127.0.0.1:$RELAYS_PORT/status")
ExecStart=$APP/venv/bin/uvicorn --host 127.0.0.1 --port $PORT --workers 1 --app-dir $APP --no-access-log --timeout-keep-alive 30 --lifespan on arduiserver:asgi
Restart=on-failure
UMask=0077
$HARDEN
ReadWritePaths=$DATA
[Install]
WantedBy=multi-user.target
EOF

if $PROBE; then
  write_file "/etc/systemd/system/$NAME-probe.service" 644 <<EOF
[Unit]
Description=ArdUi network probe, UDP $PROBE_PORT-$((PROBE_PORT + 1)) ($NAME)
After=network-online.target
[Service]
User=$NAME
Group=$NAME
WorkingDirectory=$APP
Environment=ARDUI_DATABASE=$DATA/devices.sqlite3
Environment=ARDUI_PROBE_PORT=$PROBE_PORT
ExecStart=$APP/venv/bin/python $APP/probe_udp.py
Restart=on-failure
UMask=0077
$HARDEN
ReadWritePaths=$DATA
MemoryMax=256M
TasksMax=64
[Install]
WantedBy=multi-user.target
EOF
fi

# ---- M: the ArdFerry directory, signaling and address discovery ---------------------------------------------------
# Two processes with different users (ROLES.md): $NAME-relays (master.py, user $NAME) faces the public behind the HTTPS front and
# never sees M's key; $NAME-control (ard-relay, user $NAME-m) holds it. The front gets a PEM copy to serve the directory with it,
# because ard and ArdFerry recognise the directory by that key.
MKEY=/var/lib/$NAME-m; MCONF=$CONF/m; M_KEY=
if [ -n "$M_BUNDLE" ]; then
  say "installing M (ArdFerry directory, signaling, address discovery)"
  id "$NAME-m" >/dev/null 2>&1 || run useradd --system --home-dir "$MKEY" --shell /usr/sbin/nologin "$NAME-m"
  if [ "$FRONT_GROUP" = ardui-caddy ] && ! id ardui-caddy >/dev/null 2>&1; then
    run useradd --system --home-dir "$CADDY_DATA" --shell /usr/sbin/nologin ardui-caddy
  fi
  getent group "$FRONT_GROUP" >/dev/null || $DRY || die "--front-group: no such group: $FRONT_GROUP"
  run install -d -m 755 "$APP/m" "$APP/m/bin" "$CONF"
  run install -d -m 700 -o "$NAME-m" -g "$NAME-m" "$MKEY"
  run install -d -m 750 -o root -g "$FRONT_GROUP" "$MCONF"
  run install -d -m 700 -o "$NAME" -g "$NAME" "$DATA/relays"
  for f in master.py ferry_identity.py; do run install -m 644 "$M_BUNDLE/$f" "$APP/m/$f"; done
  for f in ard-relay ard-relay-admission; do run install -m 755 "$M_BUNDLE/$f" "$APP/m/bin/$f"; done
  if ! $DRY; then
    # M's identity: made once (or taken over with --m-identity) and kept; the front's certificate is derived from it every run
    M_KEY=$("$APP/venv/bin/python" -I - "$MKEY/identity" "$MCONF/m.pem" "$MCONF/m.key" "$M_IDENTITY" <<'PY'
import os, sys
from datetime import datetime, timedelta, timezone
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID
identity, pem, key_file, imported = sys.argv[1:5]
DER, PKCS8, NONE = serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()
def write(path, data, mode):
    pending = path + '.new'
    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
    with os.fdopen(fd, 'wb') as out:
        out.write(data); out.flush(); os.fsync(out.fileno())
    os.replace(pending, path)
current = open(identity, 'rb').read() if os.path.exists(identity) else None
if imported:
    der = open(imported, 'rb').read()
    if current is not None and current != der:
        write(identity + '.replaced', current, 0o600)
        print('the previous M identity was kept as ' + identity + '.replaced', file=sys.stderr)
    if current != der:
        write(identity, der, 0o600)
elif current is None:
    write(identity, ec.generate_private_key(ec.SECP256R1()).private_bytes(DER, PKCS8, NONE), 0o600)
key = serialization.load_der_private_key(open(identity, 'rb').read(), None)
if not isinstance(key, ec.EllipticCurvePrivateKey) or key.curve.name != 'secp256r1':
    sys.exit('the M identity must be a P-256 key')
name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'ArdUi M')])
now = datetime.now(timezone.utc)
cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now - timedelta(days=1))
        .not_valid_after(now + timedelta(days=3650)).sign(key, hashes.SHA256()))
write(pem, cert.public_bytes(serialization.Encoding.PEM), 0o640)
write(key_file, key.private_bytes(serialization.Encoding.PEM, PKCS8, NONE), 0o640)
print('spki:' + key.public_key().public_bytes(DER, serialization.PublicFormat.SubjectPublicKeyInfo).hex())
PY
) || die "could not prepare the M identity"
    chown "$NAME-m:$NAME-m" "$MKEY/identity"; chown "root:$FRONT_GROUP" "$MCONF/m.pem" "$MCONF/m.key"
    echo "M key: ${M_KEY:0:24}…${M_KEY: -16}"
    # the directory's configuration; the operator's list of official nodes (shown as such on the panel) is kept across runs
    "$APP/venv/bin/python" -I - "$APP/m/master.json" "$DATA/relays" "$APP/m/bin/ard-relay-admission" "$RELAYS_PORT" <<'PY'
import hashlib, json, os, sys
path, data, admission, port = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
try:
    official = json.load(open(path)).get('official', [])
except (OSError, ValueError):
    official = []
config = dict(http_listen=['127.0.0.1', port], events=data + '/events.ndjson', open_ca_dir=data + '/ardferry-ca',
              admission_probe=admission, admission_probe_sha256=hashlib.sha256(open(admission, 'rb').read()).hexdigest(),
              nodes={}, official=official)
with open(path + '.new', 'w') as out:
    json.dump(config, out, indent=1)
os.replace(path + '.new', path)
PY
    chmod 644 "$APP/m/master.json"
  fi

  write_file "/etc/systemd/system/$NAME-relays.service" 644 <<EOF
[Unit]
Description=ArdUi M: ArdFerry directory ($NAME)
After=network-online.target
[Service]
User=$NAME
Group=$NAME
WorkingDirectory=$APP/m
ExecStart=$APP/venv/bin/python -u $APP/m/master.py --config $APP/m/master.json
Restart=always
RestartSec=3
UMask=0077
$HARDEN
ReadWritePaths=$DATA/relays
[Install]
WantedBy=multi-user.target
EOF
  write_file "/etc/systemd/system/$NAME-control.service" 644 <<EOF
[Unit]
Description=ArdUi M: signaling and address discovery, TCP and UDP $CONTROL_PORT; holds M's key ($NAME)
After=network-online.target
[Service]
User=$NAME-m
Group=$NAME-m
ExecStart=$APP/m/bin/ard-relay --control-only --listen 127.0.0.1:$CONTROL_LOCAL --qad-listen 0.0.0.0:$CONTROL_PORT --identity $MKEY/identity
Restart=always
RestartSec=3
UMask=0077
$HARDEN
MemoryDenyWriteExecute=true
[Install]
WantedBy=multi-user.target
EOF
fi

# ---- HTTPS (Caddy) ----------------------------------------------------------------------------------------------
# What the HTTPS front serves on the directory port: only the directory and node registration, with M's own key.
directory_site() {
  cat <<EOF
https://:$DIRECTORY_PORT {
    tls $MCONF/m.pem $MCONF/m.key
    @relays path /directory /register
    handle @relays {
        reverse_proxy 127.0.0.1:$RELAYS_PORT {
            header_up X-Real-IP {remote_host}
        }
    }
    handle {
        respond 404
    }
}
EOF
}
if ! $HTTP_ONLY; then
  say "setting up HTTPS for $DOMAIN"
  if ss -ltn 2>/dev/null | grep -qE ':(80|443)\s' && ! systemctl is-active --quiet ardui-caddy; then
    warn "ports 80/443 are already in use by something else. Re-run with --http-only and add this to your own reverse proxy:"
    warn "    $DOMAIN { @api path /api/* /ardferry.sh /ardferry.ps1 /ardferry/*; handle @api { reverse_proxy 127.0.0.1:$PORT { header_up X-Real-IP {remote_host} } } }"
    die "refusing to fight over ports 80/443"
  fi
  if ! command -v caddy >/dev/null; then
    case "$(uname -m)" in x86_64|amd64) arch=amd64;; aarch64|arm64) arch=arm64;; armv7l) arch=arm;; *) die "no Caddy build for $(uname -m)";; esac
    if [ -n "$CADDY_BIN" ]; then run install -m 755 "$CADDY_BIN" /usr/local/bin/caddy
    else run curl -fsSL --connect-timeout 15 --max-time 1800 "https://caddyserver.com/api/download?os=linux&arch=$arch" -o /usr/local/bin/caddy || { rm -f /usr/local/bin/caddy; die "could not download Caddy (caddyserver.com unreachable or too slow); download it elsewhere and pass --caddy-binary PATH"; }; fi
    run chmod 755 /usr/local/bin/caddy
  fi
  id ardui-caddy >/dev/null 2>&1 || run useradd --system --home-dir "$CADDY_DATA" --shell /usr/sbin/nologin ardui-caddy
  run install -d -m 755 "$CONF"
  run install -d -m 700 -o ardui-caddy -g ardui-caddy "$CADDY_DATA"
  # Clients reaching an IP address send no SNI (Windows / .NET never do), and without a default Caddy aborts the handshake.
  write_file "$CONF/Caddyfile" 644 <<EOF
$($IS_IP && printf '{\n    default_sni %s\n}\n' "$DOMAIN")
$DOMAIN {
$($TLS_INTERNAL && echo "    tls internal")
$(! $TLS_INTERNAL && $IS_IP && printf '    tls {\n        issuer acme {\n            profile shortlived\n        }\n    }')
    encode zstd gzip
    header X-Content-Type-Options nosniff
    @api path /api/* /ardferry.sh /ardferry.ps1 /ardferry/*
    handle @api {
        reverse_proxy 127.0.0.1:$PORT {
            header_up X-Real-IP {remote_host}
        }
    }
    handle {
        respond "ArdUi directory" 200
    }
}
$([ -n "$M_BUNDLE" ] && directory_site)
EOF
  write_file /etc/systemd/system/ardui-caddy.service 644 <<EOF
[Unit]
Description=ArdUi HTTPS front (Caddy)
After=network-online.target
Wants=network-online.target
[Service]
User=ardui-caddy
Group=ardui-caddy
Environment=XDG_DATA_HOME=$CADDY_DATA XDG_CONFIG_HOME=$CADDY_DATA
ExecStart=$(command -v caddy || echo /usr/local/bin/caddy) run --config $CONF/Caddyfile --adapter caddyfile
Restart=on-failure
AmbientCapabilities=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
EOF
  if ! $DRY; then
    "$(command -v caddy)" fmt --overwrite "$CONF/Caddyfile" 2>/dev/null || true
    # Validating provisions the TLS setup (with --tls-internal: a whole CA). Do it in a throw-away place, not in root's home or the
    # current directory; the service keeps its own data in $CADDY_DATA.
    vtmp=$(mktemp -d)
    env HOME="$vtmp" XDG_DATA_HOME="$vtmp" XDG_CONFIG_HOME="$vtmp" "$(command -v caddy)" validate --config "$CONF/Caddyfile" --adapter caddyfile >"$vtmp/out" 2>&1 \
      || { tail -3 "$vtmp/out" >&2; rm -rf "$vtmp"; die "Caddyfile did not validate"; }
    rm -rf "$vtmp"
  fi
fi

# ---- start and check --------------------------------------------------------------------------------------------
say "starting"
run systemctl daemon-reload
run systemctl enable "$NAME"
run systemctl restart "$NAME"
$PROBE && { run systemctl enable "$NAME-probe"; run systemctl restart "$NAME-probe"; }
[ -n "$M_BUNDLE" ] && for unit in "$NAME-control" "$NAME-relays"; do run systemctl enable "$unit"; run systemctl restart "$unit"; done
$HTTP_ONLY || { run systemctl enable ardui-caddy; run systemctl restart ardui-caddy; }
$DRY && { echo "(dry run: nothing was changed)"; exit 0; }

for _ in $(seq 1 20); do curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 && break; sleep 1; done
health=$(curl -fsS "http://127.0.0.1:$PORT/api/health" 2>/dev/null) || { ! command -v journalctl >/dev/null || journalctl -u "$NAME" --no-pager -n 20; die "the directory did not start"; }
echo "local health: $health"
if $PROBE; then
  sleep 1
  if ss -lun 2>/dev/null | grep -qE ":$PROBE_PORT\\s"; then echo "probe: listening on UDP $PROBE_PORT and $((PROBE_PORT + 1))"; else warn "the probe is not listening yet: journalctl -u $NAME-probe"; fi
fi
url="http://127.0.0.1:$PORT/"
if ! $HTTP_ONLY; then
  url="https://$DOMAIN/"
  $IS_IP && url="https://$( case "$DOMAIN" in *:*) echo "[$DOMAIN]";; *) echo "$DOMAIN";; esac )/"
  say "waiting for the certificate (needs $($IS_IP && echo "$DOMAIN to be this machine's public address" || echo "DNS for $DOMAIN to point here") and ports 80/443 open)"
  ok=false
  k=; $TLS_INTERNAL && k=-k   # a certificate from Caddy's own CA is not trusted by curl
  # a name is checked through this machine (DNS may still be on its way); an IP address is checked as clients will reach it
  check() { if $IS_IP; then curl -fsS $k "${url}api/health"; else curl -fsS $k --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/api/health"; fi; }
  for _ in $(seq 1 45); do check >/dev/null 2>&1 && { ok=true; break; }; sleep 2; done
  $ok && echo "public health: $(check)" || { warn "${url}api/health is not answering yet; see: journalctl -u ardui-caddy"; }
fi

if [ -n "$M_BUNDLE" ]; then
  sleep 1
  curl -fsS "http://127.0.0.1:$RELAYS_PORT/directory" >/dev/null 2>&1 && echo "ArdFerry directory: running" \
    || warn "the ArdFerry directory is not answering yet: journalctl -u $NAME-relays"
  ss -lun 2>/dev/null | grep -qE ":$CONTROL_PORT\\s" && echo "signaling / address discovery: listening on TCP and UDP $CONTROL_PORT" \
    || warn "signaling is not listening yet: journalctl -u $NAME-control"
  # What this M tells its clients and ArdFerry nodes (GET /api/v1/network): written once, then left to the operator.
  m_host=$M_HOST; case "$m_host" in *:*) m_host="[$m_host]";; esac
  if [ ! -f "$CONF/network.json" ]; then
    printf '{"relayDirectory": "https://%s:%s/directory", "master": "https://%s:%s/", "masterKey": "%s"}\n' \
      "$m_host" "$DIRECTORY_PORT" "$m_host" "$CONTROL_PORT" "$M_KEY" > "$CONF/network.json"
    chmod 644 "$CONF/network.json"
    echo "wrote $CONF/network.json"
  elif ! grep -q "\"$M_KEY\"" "$CONF/network.json"; then
    warn "$CONF/network.json names another M key than this M's ($M_KEY); clients will refuse this M until it is corrected"
  fi
  if $HTTP_ONLY; then
    echo
    echo "--http-only: your HTTPS front must serve the ArdFerry directory with M's key. For Caddy (running as group $FRONT_GROUP):"
    directory_site | sed 's/^/    /'
  fi
elif [ ! -f "$CONF/network.json" ]; then
  warn "$CONF/network.json is missing: this M does not yet tell its clients where its ArdFerry directory is,"
  warn "so clients can register but cannot connect. Run this again without --no-m (see ROLES.md)."
fi
client_url=$url; proxy_note=
if $HTTP_ONLY; then
  client_url="https://YOUR-DOMAIN/"
  proxy_note="(--http-only: put your HTTPS reverse proxy in front, /api/*, /ardferry.sh, /ardferry.ps1 and /ardferry/* -> 127.0.0.1:$PORT, and use its https:// address below.)"
fi
cat <<EOF

Done. The directory is running at $url
$proxy_note

Point ArdUi at it (on every machine), in  %LOCALAPPDATA%\\ArdUi\\data\\config.json  set:
    "server": "$client_url"
then restart ArdUi.
$($PROBE && echo "Network diagnosis: open UDP $PROBE_PORT and $((PROBE_PORT + 1)) in the firewall / cloud security group so that ArdUi's probe can reach the server.")
$([ -n "$M_BUNDLE" ] && echo "M: open TCP $DIRECTORY_PORT and TCP+UDP $CONTROL_PORT as well. ArdFerry nodes join with: curl -fsSL https://${DOMAIN:-YOUR-DOMAIN}/ardferry.sh | sudo bash")
Back up $DATA/ (device database) and $MKEY/identity (M's key: a new one makes every client and node re-learn this M).
Logs:  journalctl -u $NAME -f
EOF
