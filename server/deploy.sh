#!/bin/sh
set -eu
id arduiserver >/dev/null 2>&1 || useradd --system --home-dir /var/lib/arduiserver --shell /sbin/nologin arduiserver
install -d -m 755 /opt/arduiserver
install -d -m 700 -o arduiserver -g arduiserver /var/lib/arduiserver
install -m 644 /tmp/arduiserver.py /opt/arduiserver/arduiserver.py
cat > /etc/systemd/system/arduiserver.service <<'EOF'
[Unit]
Description=ArdUi device directory
After=network-online.target
[Service]
User=arduiserver
Group=arduiserver
WorkingDirectory=/opt/arduiserver
Environment=ARDUI_DATABASE=/var/lib/arduiserver/devices.sqlite3
ExecStart=/usr/bin/gunicorn --bind 127.0.0.1:8091 --workers 1 --threads 8 --timeout 30 arduiserver:application
Restart=on-failure
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=/var/lib/arduiserver
[Install]
WantedBy=multi-user.target
EOF
cp -n /etc/caddy/Caddyfile /etc/caddy/Caddyfile.pre-ardui || true
cat > /etc/caddy/Caddyfile <<'EOF'
f.visnova.cn {
    encode zstd gzip
    header X-Content-Type-Options nosniff
    handle /api/* {
        reverse_proxy 127.0.0.1:8091 {
            header_up X-Real-IP {remote_host}
        }
    }
    handle {
        root * /var/www/f.visnova.cn
        file_server
    }
}
EOF
/usr/local/bin/caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
systemctl daemon-reload
systemctl enable --now arduiserver
systemctl restart arduiserver
systemctl reload caddy
