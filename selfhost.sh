#!/usr/bin/env bash
# ArdUi self-hosting: the device directory (arduiserver) behind Caddy with automatic HTTPS.
#
#   curl -fsSL https://raw.githubusercontent.com/KateY07/ArdUi/main/selfhost.sh | sudo bash -s -- --domain ardui.example.com
#
# Options:
#   --domain NAME     public DNS name that already points at this machine (ports 80 and 443 must be reachable)
#   --http-only       only run the directory on 127.0.0.1:PORT; you bring your own HTTPS reverse proxy
#   --port N          local port of the directory (default 8091)
#   --ref NAME        git branch or tag of KateY07/ArdUi to take arduiserver.py from (default main)
#   --file PATH       use this arduiserver.py instead of downloading it (servers that cannot reach GitHub)
#   --base-url URL    download arduiserver.py from URL/server/arduiserver.py instead of raw.githubusercontent.com
#   --pip-index URL   Python package index for gunicorn and cryptography (default: pip's own setting)
#   --caddy-binary P  use this Caddy binary instead of downloading one from caddyserver.com
#   --tls-internal    sign the certificate with Caddy's own local CA instead of Let's Encrypt (tests, private networks; clients will not trust it)
#   --no-probe        do not install the UDP network probe (the part of ArdUi's network diagnosis that needs the server)
#   --probe-port N    first UDP port of the probe (it uses N and N+1, default 7843); open both in your firewall / cloud security group
#   --probe-host H    the host name clients use for the probe (default: the --domain)
#   --name NAME       service name, user and directories (default arduiserver); lets a second instance run beside another one
#   --dry-run         print what would be done and change nothing
#   --uninstall       remove the services (data in /var/lib/arduiserver is kept)
set -euo pipefail

DOMAIN=; PORT=8091; REF=main; SRC_FILE=; BASE_URL=; PIP_INDEX=; CADDY_BIN=; NAME=arduiserver; PROBE=true; PROBE_PORT=7843; PROBE_HOST=; TLS_INTERNAL=false; HTTP_ONLY=false; DRY=false; UNINSTALL=false
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
    --caddy-binary) CADDY_BIN=${2:?}; shift 2;;
    --name) NAME=${2:?}; shift 2;;
    --tls-internal) TLS_INTERNAL=true; shift;;
    --no-probe) PROBE=false; shift;;
    --probe-port) PROBE_PORT=${2:?}; shift 2;;
    --probe-host) PROBE_HOST=${2:?}; shift 2;;
    --dry-run) DRY=true; shift;;
    --uninstall) UNINSTALL=true; shift;;
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
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
  for unit in "$NAME" "$NAME-probe" ardui-caddy; do
    run systemctl disable --now "$unit" 2>/dev/null || true
    run rm -f "/etc/systemd/system/$unit.service"
  done
  run systemctl daemon-reload
  echo "Kept: $DATA (device database), $APP, $CONF. Delete them by hand if you really want to start over."
  exit 0
fi

$HTTP_ONLY || [ -n "$DOMAIN" ] || die "give --domain NAME (or --http-only to run behind your own proxy)"
case "$PORT" in ''|*[!0-9]*) die "--port must be a number";; esac
case "$PROBE_PORT" in ''|*[!0-9]*) die "--probe-port must be a number";; esac
[ -n "$PROBE_HOST" ] || PROBE_HOST=$DOMAIN

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
[ -x "$APP/venv/bin/python" ] || run python3 -m venv "$APP/venv"
run "$APP/venv/bin/pip" install --quiet --no-cache-dir ${PIP_INDEX:+--index-url "$PIP_INDEX"} --upgrade pip gunicorn cryptography

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
ExecStart=$APP/venv/bin/gunicorn --bind 127.0.0.1:$PORT --workers 1 --threads 8 --timeout 30 arduiserver:application
Restart=on-failure
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
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
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$DATA
MemoryMax=256M
TasksMax=64
[Install]
WantedBy=multi-user.target
EOF
fi

# ---- HTTPS (Caddy) ----------------------------------------------------------------------------------------------
if ! $HTTP_ONLY; then
  say "setting up HTTPS for $DOMAIN"
  if ss -ltn 2>/dev/null | grep -qE ':(80|443)\s' && ! systemctl is-active --quiet ardui-caddy; then
    warn "ports 80/443 are already in use by something else. Re-run with --http-only and add this to your own reverse proxy:"
    warn "    $DOMAIN { handle /api/* { reverse_proxy 127.0.0.1:$PORT { header_up X-Real-IP {remote_host} } } }"
    die "refusing to fight over ports 80/443"
  fi
  if ! command -v caddy >/dev/null; then
    case "$(uname -m)" in x86_64|amd64) arch=amd64;; aarch64|arm64) arch=arm64;; armv7l) arch=arm;; *) die "no Caddy build for $(uname -m)";; esac
    if [ -n "$CADDY_BIN" ]; then run install -m 755 "$CADDY_BIN" /usr/local/bin/caddy
    else run curl -fsSL --connect-timeout 15 "https://caddyserver.com/api/download?os=linux&arch=$arch" -o /usr/local/bin/caddy || die "could not download Caddy; install it yourself or pass --caddy-binary PATH"; fi
    run chmod 755 /usr/local/bin/caddy
  fi
  id ardui-caddy >/dev/null 2>&1 || run useradd --system --home-dir "$CADDY_DATA" --shell /usr/sbin/nologin ardui-caddy
  run install -d -m 755 "$CONF"
  run install -d -m 700 -o ardui-caddy -g ardui-caddy "$CADDY_DATA"
  write_file "$CONF/Caddyfile" 644 <<EOF
$DOMAIN {
$($TLS_INTERNAL && echo "    tls internal")
    encode zstd gzip
    header X-Content-Type-Options nosniff
    handle /api/* {
        reverse_proxy 127.0.0.1:$PORT {
            header_up X-Real-IP {remote_host}
        }
    }
    handle {
        respond "ArdUi directory" 200
    }
}
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
  $DRY || "$(command -v caddy)" validate --config "$CONF/Caddyfile" --adapter caddyfile >/dev/null || die "Caddyfile did not validate"
fi

# ---- start and check --------------------------------------------------------------------------------------------
say "starting"
run systemctl daemon-reload
run systemctl enable "$NAME"
run systemctl restart "$NAME"
$PROBE && { run systemctl enable "$NAME-probe"; run systemctl restart "$NAME-probe"; }
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
  say "waiting for the certificate (needs DNS for $DOMAIN to point here and ports 80/443 open)"
  ok=false
  k=; $TLS_INTERNAL && k=-k   # a certificate from Caddy's own CA is not trusted by curl
  for _ in $(seq 1 45); do curl -fsS $k --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/api/health" >/dev/null 2>&1 && { ok=true; break; }; sleep 2; done
  $ok && echo "public health: $(curl -fsS $k --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/api/health")" || { warn "https://$DOMAIN/api/health is not answering yet; see: journalctl -u ardui-caddy"; }
fi

client_url=$url; proxy_note=
if $HTTP_ONLY; then
  client_url="https://YOUR-DOMAIN/"
  proxy_note="(--http-only: put your HTTPS reverse proxy in front, /api/* -> 127.0.0.1:$PORT, and use its https:// address below.)"
fi
cat <<EOF

Done. The directory is running at $url
$proxy_note

Point ArdUi at it (on every machine), in  %LOCALAPPDATA%\\ArdUi\\data\\config.json  set:
    "server": "$client_url"
then restart ArdUi.
$($PROBE && echo "Network diagnosis: open UDP $PROBE_PORT and $((PROBE_PORT + 1)) in the firewall / cloud security group so that ArdUi's probe can reach the server.") Back up $DATA/ (it holds the device database).
Logs:  journalctl -u $NAME -f
EOF
