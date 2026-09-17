"""Package only changed NJ payloads after build.ps1 has passed."""
import hashlib
import json
from pathlib import Path
import re
import zipfile

repo=Path(__file__).resolve().parents[1]
binary=repo/'dist/final-v2.pre1/ArdUi.exe'
digest=hashlib.sha256(binary.read_bytes()).hexdigest()
installer=repo/'install.ps1'
text=installer.read_text(encoding='utf-8')
assert "$version='v2.pre1'" in text
text,count=re.subn(r"\$expectedSha256='[0-9a-f]{64}'", "$expectedSha256='"+digest+"'",text)
assert count==1
installer.write_text(text,encoding='utf-8',newline='\n')
site=repo/'site/index.html'
text,count=re.subn(r'ArdUi SHA-256 <code>[0-9a-f]{64}</code>','ArdUi SHA-256 <code>'+digest+'</code>',site.read_text(encoding='utf-8'))
assert count==1
site.write_text(text,encoding='utf-8',newline='\n')
transit=repo/'dist/ArdTransit-v2.pre1.zip'
with zipfile.ZipFile(transit,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
    for name in ('ardtransit.py','README.md'): archive.write(repo/'transit'/name,name)
payloads={'ArdUi-v2.pre1.exe':binary,'ArdTransit-v2.pre1.zip':transit,'install.ps1':installer,
          'index.html':site,'arduiserver.py':repo/'server/arduiserver.py','publish-v2.sh':repo/'server/publish-v2.sh'}
files={name:path.read_bytes() for name,path in payloads.items()}
files['publish-v2.sh']=files['publish-v2.sh'].replace(b'\r\n',b'\n')
manifest={'version':'v2.pre1','files':{name:hashlib.sha256(data).hexdigest() for name,data in files.items()}}
files['manifest.json']=json.dumps(manifest,indent=2).encode()
bundle=repo/'dist/upload-v2.pre1.zip'
with zipfile.ZipFile(bundle,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
    for name,data in files.items(): archive.writestr(name,data)
print(json.dumps({'binarySha256':digest,'bundle':str(bundle),'bytes':bundle.stat().st_size,
                  'sha256':hashlib.sha256(bundle.read_bytes()).hexdigest(),'files':list(files)},indent=2))
