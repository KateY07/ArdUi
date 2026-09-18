"""Package the FRD client update without changing directory or relay services."""
import hashlib
import json
from pathlib import Path
import zipfile

from delta_v2 import make

repo=Path(__file__).resolve().parents[1]
version='v2.pre2'
binary=repo/f'dist/final-{version}/ArdUi.exe'
digest=hashlib.sha256(binary.read_bytes()).hexdigest()
installer=(repo/'install.ps1').read_text(encoding='ascii')
assert f"$version='{version}'" in installer
assert f"$expectedSha256='{digest}'" in installer
assert digest in (repo/'site/index.html').read_text(encoding='utf-8')
frd=repo/'dist/FRD-v1.pre6-win-x64.tar.xz'
frd_digest=hashlib.sha256(frd.read_bytes()).hexdigest()
assert frd_digest in installer
payloads={f'ArdUi-{version}.exe':binary,'install.ps1':repo/'install.ps1',
          'index.html':repo/'site/index.html','publish-frd.sh':repo/'server/publish-frd.sh'}
files={name:path.read_bytes() for name,path in payloads.items()}
files['publish-frd.sh']=files['publish-frd.sh'].replace(b'\r\n',b'\n')
manifest={'version':version,'files':{name:hashlib.sha256(data).hexdigest() for name,data in files.items()},
          'frd':{'name':frd.name,'sha256':frd_digest,'size':frd.stat().st_size}}
files['manifest.json']=json.dumps(manifest,indent=2).encode()
bundle=repo/f'dist/upload-{version}.zip'
with zipfile.ZipFile(bundle,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
    for name,data in files.items(): archive.writestr(name,data)
make(repo,previous='v2.pre1',version=version)
