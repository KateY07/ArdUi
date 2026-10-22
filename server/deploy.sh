#!/bin/sh
# Update the official M (NJ, f.visnova.cn) the same way a self-hoster installs one: selfhost.sh in --http-only mode behind the Caddy
# that NJ already runs. M is one installation (ROLES.md): device directory, network probe, ArdFerry directory, signaling and address
# discovery. Copy selfhost.sh, server/arduiserver.py, server/probe_udp.py, native_relay/master.py and native_relay/ferry_identity.py
# to /tmp first (NJ cannot reach GitHub), then run this as root. Rollback: /root/m-rollback-<stamp>.sh (written below).
set -eu
health() { curl -fsS -m 10 https://f.visnova.cn/api/health; }
stamp=$(date -u +%Y%m%dT%H%M%S)
OLD=/opt/ard2/directory          # NJ's M before it was installed by selfhost.sh
NEW=/opt/arduiserver/m
cp -a /var/lib/arduiserver/devices.sqlite3 "/root/devices.sqlite3.before-$stamp" 2>/dev/null || true
cp -a /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.before-$stamp"
# the probe used to be installed as "ardui-probe"; selfhost.sh names it arduiserver-probe (same ports)
if [ -f /etc/systemd/system/ardui-probe.service ]; then
  systemctl disable --now ardui-probe; rm -f /etc/systemd/system/ardui-probe.service; systemctl daemon-reload
fi
# What NJ's M tells its clients about the relays (GET /api/v1/network). The address discovery listener is IPv4-only, so the
# addresses stay IPv4. selfhost.sh writes the same thing when it is missing.
if [ ! -f /etc/ardui/network.json ]; then
  install -d -m 755 /etc/ardui
  cat > /etc/ardui/network.json <<'EOF'
{"relayDirectory": "https://175.27.160.144:8080/directory", "master": "https://175.27.160.144:7842/", "masterKey": "spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f"}
EOF
  chmod 644 /etc/ardui/network.json
fi

# ---- M's relay part: from the hand-made units in /opt/ard2/directory to selfhost.sh -----------------------------------------
bundle=/tmp/m-bundle; rm -rf "$bundle"; mkdir -p "$bundle"
cp "$OLD/bin/ard-relay" "$OLD/bin/ard-relay-admission" /tmp/master.py /tmp/ferry_identity.py "$bundle/"
if systemctl list-unit-files ard-directory-master.service >/dev/null 2>&1 && [ -f /etc/systemd/system/ard-directory-master.service ]; then
  cat > "/root/m-rollback-$stamp.sh" <<EOF
#!/bin/sh
# Back to the M of $stamp: the hand-made directory units and the old Caddyfile (the device database is untouched).
systemctl disable --now arduiserver-relays arduiserver-control
cp -a /etc/caddy/Caddyfile.before-$stamp /etc/caddy/Caddyfile && systemctl reload caddy
cp -a $OLD/nj-node.json.before-$stamp $OLD/nj-node.json 2>/dev/null || true
systemctl enable --now ard-directory-master ard-directory-control
systemctl restart ardferry
EOF
  chmod 700 "/root/m-rollback-$stamp.sh"
  # the operator's official nodes are carried over (selfhost.sh keeps that list across runs)
  install -d -m 755 "$NEW"
  python3 - "$OLD/master.json" "$NEW/master.json" <<'PY'
import json, os, sys
old, new = sys.argv[1:3]
if not os.path.exists(new):
    json.dump(dict(official=json.load(open(old)).get('official', [])), open(new, 'w'))
PY
  systemctl disable --now ard-directory-master ard-directory-control   # frees 8080 for Caddy and 7842 for arduiserver-control
fi
bash /tmp/selfhost.sh --http-only --file /tmp/arduiserver.py \
  --m-bundle "$bundle" --m-identity "$OLD/pki-relay-identity/master.identity" --m-host 175.27.160.144 --front-group caddy

# Rough locations for the ArdFerry map: DB-IP's free city database (CC BY 4.0), refreshed when this month's is not there yet.
geo=/var/lib/arduiserver/dbip-city-lite.mmdb; month=$(date -u +%Y-%m)
if [ ! -f "$geo" ] || [ "$(date -u -r "$geo" +%Y-%m)" != "$month" ]; then
  if curl -fsSL --connect-timeout 20 --max-time 900 "https://download.db-ip.com/free/dbip-city-lite-$month.mmdb.gz" -o /tmp/dbip.mmdb.gz; then
    gunzip -c /tmp/dbip.mmdb.gz > "$geo.tmp" && mv "$geo.tmp" "$geo" && chown arduiserver:arduiserver "$geo" && chmod 640 "$geo"
  fi
  rm -f /tmp/dbip.mmdb.gz "$geo.tmp"
fi

# NJ's Caddy fronts the device directory API and, with M's key, the ArdFerry directory (as selfhost.sh's own Caddy would)
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
		respond "ArdUi directory" 200
	}
}
https://:8080 {
	tls /etc/ardui/m/m.pem /etc/ardui/m/m.key
	@relays path /directory /register
	handle @relays {
		reverse_proxy 127.0.0.1:8092 {
			header_up X-Real-IP {remote_host}
		}
	}
	handle {
		respond 404
	}
}
EOF
t=$(mktemp -d); env HOME="$t" XDG_DATA_HOME="$t" XDG_CONFIG_HOME="$t" /usr/local/bin/caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null; rm -rf "$t"
systemctl reload caddy

# NJ's own ArdFerry joins like everyone else's: configured with M's domain only (its certificate from M's old CA cannot pass the
# HTTPS front). Its new name is added to the official list.
if ! grep -q '"m"' "$OLD/nj-node.json"; then
  cp -a "$OLD/nj-node.json" "$OLD/nj-node.json.before-$stamp"
  python3 - "$OLD/nj-node.json" <<'PY'
import json, sys
path = sys.argv[1]; old = json.load(open(path))
json.dump(dict(m='f.visnova.cn', data=old['data'] + '-ferry', relay_port=old['relay_port'], official_relay_sha256=old['official_relay_sha256'],
               upnp=False, manual_endpoints=old.get('manual_endpoints', [])), open(path, 'w'), indent=1)
PY
  systemctl restart ardferry
  for _ in $(seq 1 30); do [ -f "$OLD/run/nj-ferry/identity/node.pem" ] && break; sleep 1; done
fi
node=
[ -f "$OLD/run/nj-ferry/identity/node.pem" ] && node=$(python3 -I -c "import sys; sys.path.insert(0, sys.argv[1]); import ferry_identity; print(ferry_identity.ensure(sys.argv[2])['node'])" \
  "$OLD/product" "$OLD/run/nj-ferry/identity")
if [ -n "$node" ]; then
  python3 - "$NEW/master.json" "$node" <<'PY'
import json, sys
path, node = sys.argv[1:3]; config = json.load(open(path))
if node not in config['official']:
    config['official'].append(node)
    json.dump(config, open(path, 'w'), indent=1)
    print('official ArdFerry nodes:', config['official'])
PY
  systemctl restart arduiserver-relays
fi
sleep 3
echo "health: $(health)"
curl -fsS -m 5 http://127.0.0.1:8092/directory | python3 -c 'import json,sys; print([(e["node"], e["family"], e["available"]) for e in json.load(sys.stdin)["entries"]])'
