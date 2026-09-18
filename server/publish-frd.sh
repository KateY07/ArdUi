#!/bin/sh
set -eu
stage=$(realpath "${1:?pass the verified staging directory}")
case "$stage" in /tmp/ardui-v2.pre2-*) ;; *) echo 'Unexpected staging path' >&2; exit 1;; esac
cd "$stage"
python3 - <<'PY'
import hashlib,json,pathlib
manifest=json.loads(pathlib.Path('manifest.json').read_text())
assert manifest['version']=='v2.pre2'
for name,digest in manifest['files'].items():
    assert pathlib.Path(name).name==name
    assert hashlib.sha256(pathlib.Path(name).read_bytes()).hexdigest()==digest,name
frd=manifest['frd'];path=pathlib.Path(frd['name'])
assert path.name==str(path)=='FRD-v1.pre6-win-x64.tar.xz'
assert path.stat().st_size==frd['size']
assert hashlib.sha256(path.read_bytes()).hexdigest()==frd['sha256']
print('All client and FRD payload hashes verified.')
PY
stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup=/var/backups/ardui-before-v2.pre2-$stamp
web=/var/www/f.visnova.cn/ardui
install -d -m 700 "$backup"
cp "$web/install.ps1" "$backup/install.ps1"
cp "$web/index.html" "$backup/index.html"
install -m 644 ArdUi-v2.pre2.exe "$web/ArdUi-v2.pre2.exe.next"
install -m 644 FRD-v1.pre6-win-x64.tar.xz "$web/FRD-v1.pre6-win-x64.tar.xz.next"
mv "$web/ArdUi-v2.pre2.exe.next" "$web/ArdUi-v2.pre2.exe"
mv "$web/FRD-v1.pre6-win-x64.tar.xz.next" "$web/FRD-v1.pre6-win-x64.tar.xz"
install -m 644 manifest.json "$web/manifest-v2.pre2.json"
install -m 644 install.ps1 "$web/install.ps1.next"
install -m 644 index.html "$web/index.html.next"
mv "$web/install.ps1.next" "$web/install.ps1"
mv "$web/index.html.next" "$web/index.html"
echo "Published v2.pre2; rollback files: $backup"
sha256sum "$web/ArdUi-v2.pre2.exe" "$web/FRD-v1.pre6-win-x64.tar.xz"
