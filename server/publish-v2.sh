#!/bin/sh
set -eu
stage=$(realpath "${1:?pass the verified staging directory}")
case "$stage" in /tmp/ardui-v2.pre1-*) ;; *) echo 'Unexpected staging path' >&2; exit 1;; esac
cd "$stage"
python3 - <<'PY'
import hashlib,json,pathlib
manifest=json.loads(pathlib.Path('manifest.json').read_text())
assert manifest['version']=='v2.pre1'
for name,digest in manifest['files'].items():
    assert pathlib.Path(name).name==name
    assert hashlib.sha256(pathlib.Path(name).read_bytes()).hexdigest()==digest,name
print('All staged payload hashes verified.')
PY
python3 -m py_compile arduiserver.py
stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup=/var/backups/ardui-before-v2.pre1-$stamp
install -d -m 700 "$backup"
cp /opt/arduiserver/arduiserver.py "$backup/arduiserver.py"
cp /var/www/f.visnova.cn/ardui/install.ps1 "$backup/install.ps1"
cp /var/www/f.visnova.cn/ardui/index.html "$backup/index.html"
python3 - "$backup" <<'PY'
import pathlib,sqlite3,sys
root=pathlib.Path(sys.argv[1])
with sqlite3.connect('/var/lib/arduiserver/devices.sqlite3') as source:
    with sqlite3.connect(root/'devices.sqlite3') as target: source.backup(target)
print('Directory backup completed.')
PY
install -m 644 arduiserver.py /opt/arduiserver/arduiserver.py.next
mv /opt/arduiserver/arduiserver.py.next /opt/arduiserver/arduiserver.py
systemctl restart arduiserver
healthy=0
for attempt in 1 2 3 4 5; do
    if curl --noproxy '*' --max-time 4 -fsS http://127.0.0.1:8091/api/health | python3 -c 'import json,sys; assert json.load(sys.stdin)["version"]=="v2.pre1"'; then healthy=1; break; fi
    sleep 1
done
if test "$healthy" -ne 1; then
    install -m 644 "$backup/arduiserver.py" /opt/arduiserver/arduiserver.py
    systemctl restart arduiserver
    echo 'Health check failed; previous directory implementation restored.' >&2
    exit 1
fi
web=/var/www/f.visnova.cn/ardui
install -m 644 ArdUi-v2.pre1.exe "$web/ArdUi-v2.pre1.exe"
install -m 644 ArdTransit-v2.pre1.zip "$web/ArdTransit-v2.pre1.zip"
install -m 644 manifest.json "$web/manifest-v2.pre1.json"
install -m 644 index.html "$web/index.html.next"
install -m 644 install.ps1 "$web/install.ps1.next"
mv "$web/index.html.next" "$web/index.html"
mv "$web/install.ps1.next" "$web/install.ps1"
echo "Published v2.pre1; rollback files: $backup"
sha256sum "$web/ArdUi-v2.pre1.exe" "$web/ard-v2.0.0-pre.6.exe"
