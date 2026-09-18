"""Read public release checksums and exercise a full, non-selectable test candidate."""
import asyncio
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import urllib.request
import zipfile

repo=Path(__file__).resolve().parents[1]
base='https://f.visnova.cn'
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
def read(path):
    with opener.open(base+path,timeout=30) as response: return response.read()

health=json.loads(read('/api/health'))
assert health['version']=='v2.pre1' and health['ok']
assert b'ArdUi v2.pre1' in read('/ardui')
with zipfile.ZipFile(repo/'dist/upload-v2.pre1.zip') as archive:
    manifest=json.loads(archive.read('manifest.json'))
for name in ('install.ps1','index.html','ArdTransit-v2.pre1.zip'):
    assert hashlib.sha256(read('/ardui/'+name)).hexdigest()==manifest['files'][name],name
with opener.open(urllib.request.Request(base+'/ardui/ArdUi-v2.pre1.exe',method='HEAD'),timeout=30) as response:
    assert int(response.headers['Content-Length'])==25292585
print('PASS: public HTTPS health, /ardui page, exact installer/node checksums and EXE availability.')

spec=importlib.util.spec_from_file_location('transit',repo/'transit/ardtransit.py')
transit=importlib.util.module_from_spec(spec)
spec.loader.exec_module(transit)
async def run():
    with tempfile.TemporaryDirectory(prefix='ArdTransit-deployment-smoke-') as folder:
        api=transit.Api(SimpleNamespace(data=folder,server=base))
        await api.register()
        reply=await api.call('/api/v2/transit/heartbeat',dict(capacity=1,active=1,mbps=1,metrics=dict(version=transit.VERSION,smoke=True)))
        assert reply['version']=='v2.pre1' and reply['jobs']==[]
        print('PASS: production signed registration, directory-key pin and candidate heartbeat; test candidate advertises no spare capacity.')
asyncio.run(run())
