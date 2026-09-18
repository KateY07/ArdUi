"""Publish verified standalone relay files without restarting the directory or ARD."""
import datetime
import hashlib
import json
from pathlib import Path
import shutil
import sys

stage = Path(sys.argv[1]).resolve()
if stage.parent != Path('/tmp') or not stage.name.startswith('ardui-relay1-'):
    raise ValueError('Unexpected staging directory')
manifest = json.loads((stage/'relay-manifest.json').read_text())
names = ['ArdTransit-v2.pre1-relay1-win-x64.zip', 'install-relay.ps1', 'index.html']
if manifest['version'] != 'v2.pre1-relay1' or set(manifest['files']) != set(names):
    raise ValueError('Unexpected release manifest')
for name in names:
    if hashlib.sha256((stage/name).read_bytes()).hexdigest() != manifest['files'][name]:
        raise ValueError('Staged hash mismatch: '+name)
web = Path('/var/www/f.visnova.cn/ardui')
backup = Path('/var/backups')/('ardui-before-relay1-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
backup.mkdir(mode=0o700)
for name in names+['relay-manifest.json']:
    if (web/name).exists():
        shutil.copy2(web/name, backup/name)
for name in names+['relay-manifest.json']:
    pending = web/(name+'.next')
    shutil.copyfile(stage/name, pending)
    pending.chmod(0o644)
    pending.replace(web/name)
print('Published standalone relay; directory and ARD services unchanged.')
print('Backup:', backup)
