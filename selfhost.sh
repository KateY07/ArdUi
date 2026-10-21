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
#   --file PATH       use this arduiserver.py instead of downloading it
#   --dry-run         print what would be done and change nothing
#   --uninstall       remove the services (data in /var/lib/arduiserver is kept)
set -euo pipefail

DOMAIN=; PORT=8091; REF=main; SRC_FILE=; HTTP_ONLY=false; DRY=false; UNINSTALL=false
APP=/opt/arduiserver; DATA=/var/lib/arduiserver; CONF=/etc/ardui; CADDY_DATA=/var/lib/ardui-caddy

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
    --dry-run) DRY=true; shift;;
    --uninstall) UNINSTALL=true; shift;;
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "unknown option: $1";;
  esac
done

[ "$(id -u)" -eq 0 ] || $DRY || die "run as root (sudo)"
[ "$(uname -s)" = Linux ] || die "Linux only"
command -v systemctl >/dev/null || die "systemd is required"

if $UNINSTALL; then
  say "removing services"
  for unit in arduiserver ardui-caddy; do
    run systemctl disable --now "$unit" 2>/dev/null || true
    run rm -f "/etc/systemd/system/$unit.service"
  done
  run systemctl daemon-reload
  echo "Kept: $DATA (device database), $APP, $CONF. Delete them by hand if you really want to start over."
  exit 0
fi

$HTTP_ONLY || [ -n "$DOMAIN" ] || die "give --domain NAME (or --http-only to run behind your own proxy)"
case "$PORT" in ''|*[!0-9]*) die "--port must be a number";; esac

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
id arduiserver >/dev/null 2>&1 || run useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin arduiserver
run install -d -m 755 "$APP"
run install -d -m 700 -o arduiserver -g arduiserver "$DATA"
tmp=$(mktemp)
if [ -n "$SRC_FILE" ]; then run cp "$SRC_FILE" "$tmp"
else run curl -fsSL "https://raw.githubusercontent.com/KateY07/ArdUi/$REF/server/arduiserver.py" -o "$tmp"; fi
if ! $DRY; then grep -q 'def application' "$tmp" || die "that does not look like arduiserver.py"; echo "arduiserver.py sha256: $(sha256sum "$tmp" | cut -d' ' -f1)"; fi
run install -m 644 "$tmp" "$APP/arduiserver.py"; rm -f "$tmp"
[ -x "$APP/venv/bin/python" ] || run python3 -m venv "$APP/venv"
run "$APP/venv/bin/pip" install --quiet --upgrade pip gunicorn cryptography

write_file /etc/systemd/system/arduiserver.service 644 <<EOF
[Unit]
Description=ArdUi device directory
After=network-online.target
[Service]
User=arduiserver
Group=arduiserver
WorkingDirectory=$APP
Environment=ARDUI_DATABASE=$DATA/devices.sqlite3
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
    run curl -fsSL "https://caddyserver.com/api/download?os=linux&arch=$arch" -o /usr/local/bin/caddy
    run chmod 755 /usr/local/bin/caddy
  fi
  id ardui-caddy >/dev/null 2>&1 || run useradd --system --home-dir "$CADDY_DATA" --shell /usr/sbin/nologin ardui-caddy
  run install -d -m 755 "$CONF"
  run install -d -m 700 -o ardui-caddy -g ardui-caddy "$CADDY_DATA"
  write_file "$CONF/Caddyfile" 644 <<EOF
$DOMAIN {
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
run systemctl enable arduiserver
run systemctl restart arduiserver
$HTTP_ONLY || { run systemctl enable ardui-caddy; run systemctl restart ardui-caddy; }
$DRY && { echo "(dry run: nothing was changed)"; exit 0; }

for _ in $(seq 1 20); do curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 && break; sleep 1; done
health=$(curl -fsS "http://127.0.0.1:$PORT/api/health" 2>/dev/null) || { journalctl -u arduiserver --no-pager -n 20; die "the directory did not start"; }
echo "local health: $health"
url="http://127.0.0.1:$PORT/"
if ! $HTTP_ONLY; then
  url="https://$DOMAIN/"
  say "waiting for the certificate (needs DNS for $DOMAIN to point here and ports 80/443 open)"
  ok=false
  for _ in $(seq 1 45); do curl -fsS "https://$DOMAIN/api/health" >/dev/null 2>&1 && { ok=true; break; }; sleep 2; done
  $ok && echo "public health: $(curl -fsS "https://$DOMAIN/api/health")" || { warn "https://$DOMAIN/api/health is not answering yet; see: journalctl -u ardui-caddy"; }
fi

cat <<EOF

Done. The directory is running at $url

Point ArdUi at it (on every machine), in  %LOCALAPPDATA%\\ArdUi\\data\\config.json  set:
    "server": "$url"
then restart ArdUi. Back up $DATA/ (it holds the device database).
Logs:  journalctl -u arduiserver -f
EOF
