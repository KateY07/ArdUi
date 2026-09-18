"""Package the standalone Windows relay and pin its installer hashes."""
import hashlib
import importlib.metadata
from pathlib import Path
import re
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = 'v2.pre1-relay1'

def sha(data):
    return hashlib.sha256(data).hexdigest()

def main():
    exe = (ROOT/'dist/relay-build/output/ArdTransit.exe').read_bytes()
    notices = ['ArdTransit third-party runtime notices', f'Python {sys.version}']
    notices.append((Path(sys.base_prefix)/'LICENSE.txt').read_text(encoding='utf-8'))
    for name in ('cryptography', 'cffi', 'pyinstaller', 'pyinstaller-hooks-contrib', 'pywin32-ctypes'):
        dist = importlib.metadata.distribution(name)
        notices.append(f'\n{name} {dist.version}\n')
        found = []
        for file in dist.files or []:
            if file.name.lower().startswith(('license', 'copying')):
                found.append(file)
                notices.append(dist.locate_file(file).read_text(encoding='utf-8', errors='replace'))
        if not found:
            raise RuntimeError(f'Missing license text for {name}')
    payload = {'ArdTransit.exe': exe, 'THIRD-PARTY-NOTICES.txt': '\n'.join(notices).encode('utf-8')}
    for name in ('Start-ArdTransit.ps1', 'Stop-ArdTransit.ps1', 'Status-ArdTransit.ps1', 'README.md'):
        payload[name] = (ROOT/'transit'/name).read_bytes()
    archive = ROOT/'dist'/f'ArdTransit-{VERSION}-win-x64.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for name, data in payload.items():
            info = zipfile.ZipInfo(name, (2026, 9, 18, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            bundle.writestr(info, data)
    installer = ROOT/'install-relay.ps1'
    source = installer.read_text(encoding='utf-8')
    for variable, value in [('bundleSha256', sha(archive.read_bytes())), ('relaySha256', sha(exe))]:
        source, count = re.subn(rf"\${variable}='[^']+'", f"${variable}='{value}'", source)
        if count != 1:
            raise RuntimeError(f'Expected one {variable} pin')
    installer.write_text(source, encoding='utf-8')
    print(f'{archive.name}: {archive.stat().st_size} bytes; SHA256={sha(archive.read_bytes())}')
    print(f'ArdTransit.exe: {len(exe)} bytes; SHA256={sha(exe)}')

if __name__ == '__main__':
    main()
