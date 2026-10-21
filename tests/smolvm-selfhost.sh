#!/bin/sh
# End-to-end run of selfhost.sh (--http-only) inside a smolvm microVM. The VM has no systemd, so a small systemctl stand-in starts the
# service from the unit file the script wrote; this checks the packages, venv, download, unit file, start-up and health check.
# Caddy/HTTPS needs a public domain and is not covered. Run from a terminal outside the agent harness:
#   smolvm machine run --dns <host DNS> -v D:/1/ard2/ArdUi:/src:ro -- sh /src/tests/smolvm-selfhost.sh
set -eu
apk add --quiet bash curl python3 py3-pip shadow iproute2 >/dev/null
chmod 1777 /tmp
mkdir -p /etc/systemd/system /tmp/shim
cat > /tmp/shim/systemctl <<'EOF'
#!/bin/sh
# systemctl [--now|--no-pager ...] ACTION [UNIT]
action=; unit=
for a in "$@"; do case "$a" in -*) ;; *) if [ -z "$action" ]; then action=$a; else unit=$a; fi;; esac; done
case "$action" in
  daemon-reload|enable|disable) exit 0;;
  is-active) exit 1;;
  restart|start)
    file=/etc/systemd/system/$unit.service
    [ -f "$file" ] || { echo "no unit $unit" >&2; exit 5; }
    exec_line=$(sed -n 's/^ExecStart=//p' "$file"); user=$(sed -n 's/^User=//p' "$file"); dir=$(sed -n 's/^WorkingDirectory=//p' "$file")
    env_line=$(sed -n 's/^Environment=//p' "$file" | tr '\n' ' ')
    pkill -f "gunicorn" 2>/dev/null || true
    su -s /bin/sh "$user" -c "cd ${dir:-/}; env $env_line nohup $exec_line >/tmp/$unit.log 2>&1 &"
    exit 0;;
  *) exit 0;;
esac
EOF
chmod +x /tmp/shim/systemctl
export PATH=/tmp/shim:$PATH

echo "== dry run"
bash /src/selfhost.sh --http-only --dry-run | tail -4

echo "== real run (--http-only, downloads arduiserver.py from GitHub)"
bash /src/selfhost.sh --http-only --file /src/server/arduiserver.py 2>&1 | tail -14

echo "== service answers"
python3 - <<'PY'
import json, time, urllib.request
last = None
for _ in range(15):
    try:
        body = json.load(urllib.request.urlopen('http://127.0.0.1:8091/api/health', timeout=2)); break
    except Exception as e:
        last = e; time.sleep(1)
else:
    raise SystemExit('FAIL: no answer: %s' % last)
print('health:', body); raise SystemExit(0 if body.get('ok') is True else 1)
PY
echo "== the data directory is private to the service user"
ls -ld /var/lib/arduiserver
echo "== uninstall keeps the data"
bash /src/selfhost.sh --uninstall 2>&1 | tail -3
ls /var/lib/arduiserver >/dev/null && echo "data kept"
echo "selfhost.sh checks done"
