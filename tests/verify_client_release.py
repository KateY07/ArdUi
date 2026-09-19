"""Read-only public HTTPS verification of the locally packaged client release."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--version',default='v2.pre3')
parser.add_argument('--base',default='https://f.visnova.cn/ardui')
args=parser.parse_args()
repo=Path(__file__).resolve().parents[1]
with zipfile.ZipFile(repo/f'dist/upload-{args.version}.zip') as archive:
    manifest=json.loads(archive.read('manifest.json'))
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
def fetch(url,method='GET',headers=None):
    return opener.open(urllib.request.Request(url,method=method,headers=headers or {'Cache-Control':'no-cache'}),timeout=30)
for name in ('install.ps1','index.html'):
    with fetch(args.base+'/'+name) as response:
        data=response.read()
    if hashlib.sha256(data).hexdigest()!=manifest['files'][name]:
        raise ValueError('Public content differs from packaged release: '+name)
    if name=='install.ps1' and (not data.isascii() or data.startswith(b'\xef\xbb\xbf')):
        raise ValueError('Installer encoding is not ASCII without BOM')
    print('PASS: public exact hash '+name,flush=True)
with fetch(args.base) as response:
    if ('ArdUi '+args.version).encode() not in response.read(): raise ValueError('Landing-page version mismatch')
with fetch(args.base+'/manifest-'+args.version+'.json') as response:
    if json.loads(response.read())!=manifest: raise ValueError('Public manifest differs')
for name,path,expected in (
    (f'ArdUi-{args.version}.exe',repo/f'dist/final-{args.version}/ArdUi.exe',manifest['files'][f'ArdUi-{args.version}.exe']),
    (manifest['frd']['name'],repo/'dist'/manifest['frd']['name'],manifest['frd']['sha256'])):
    with fetch(args.base+'/'+name,method='HEAD') as response:
        if int(response.headers['Content-Length'])!=path.stat().st_size: raise ValueError('Public payload size mismatch: '+name)
    with fetch(args.base+'/'+name,headers={'Range':'bytes=0-63','Cache-Control':'no-cache'}) as response:
        if response.status!=206 or response.read()!=path.open('rb').read(64): raise ValueError('Public byte-range mismatch: '+name)
    print('PASS: payload HEAD/Range '+name+'; full remote file hash is checked by publisher: '+expected,flush=True)
print('PASS: HTTPS client release verification; no installer executed.',flush=True)
