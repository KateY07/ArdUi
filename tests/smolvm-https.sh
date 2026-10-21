#!/bin/sh
# End-to-end run of selfhost.sh with Caddy HTTPS (--tls-internal, no Let's Encrypt) inside a smolvm microVM. The VM has no systemd, so a small systemctl stand-in starts the
# service from the unit file the script wrote; this checks the packages, venv, download, unit file, start-up and health check.
# Needs a linux/amd64 Caddy binary at /vm/caddy. Run from a terminal outside the agent harness:
#   smolvm machine run --dns <host DNS> -v D:/1/ard2/ArdUi:/src:ro -v D:/1/ard2/_vmtest:/vm:ro -- sh /src/tests/smolvm-https.sh
set -eu
echo "== apk"; apk add bash curl openssl python3 py3-pip shadow iproute2
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
    case "$unit" in ardui-caddy) pkill -x caddy 2>/dev/null || true;; *) pkill -f gunicorn 2>/dev/null || true;; esac
    [ "$unit" = ardui-caddy ] && user=root   # the real unit grants CAP_NET_BIND_SERVICE; the stand-in just runs it as root
    su -s /bin/sh "$user" -c "cd ${dir:-/}; env $env_line nohup $exec_line >/tmp/$unit.log 2>&1 &"
    exit 0;;
  *) exit 0;;
esac
EOF
chmod +x /tmp/shim/systemctl
export PATH=/tmp/shim:$PATH

echo "== run with Caddy (internal CA)"
bash /src/selfhost.sh --domain test.example.com --tls-internal --file /src/server/arduiserver.py --caddy-binary /vm/caddy 2>&1 | tail -12

echo "== https through Caddy (the local CA is not trusted here, hence -k)"
ok=
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
  body=$(curl -sk -m 4 --resolve test.example.com:443:127.0.0.1 https://test.example.com/api/health) && ok=1 && break
  sleep 1
done
[ -n "$ok" ] || { echo "FAIL: no https answer"; exit 1; }
echo "health: $body"
echo "== certificate issuer"
echo | openssl s_client -connect 127.0.0.1:443 -servername test.example.com 2>/dev/null | openssl x509 -noout -issuer -subject 2>/dev/null || echo "(openssl not available)"
echo "== the root page"
curl -sk --resolve test.example.com:443:127.0.0.1 https://test.example.com/; echo
echo "== plain http is redirected to https"
curl -s -o /dev/null -w "%{http_code} -> %{redirect_url}
" --resolve test.example.com:80:127.0.0.1 http://test.example.com/api/health
echo "== uninstall"
bash /src/selfhost.sh --uninstall 2>&1 | tail -2
echo "https checks done"
