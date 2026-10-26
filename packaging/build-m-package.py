"""Build the M package ardui-m-linux-x86_64.tar.gz (reproducible: fixed order, owners and times), which selfhost.sh downloads and checks
against its pinned SHA-256. It holds M's parts (static ard-relay and ard-relay-admission, master.py, ferry_identity.py) and the ArdUi
installer of the same release in client/, which M offers its clients for guided updates.

  python packaging/build-m-package.py --client D:/pub/ArdUi/ArdUi-v6.3_setup.exe --out D:/pub/ArdUi/m-v6.3

Afterwards put the printed SHA-256 into selfhost.sh (M_PACKAGE_SHA256) and attach the package and its .sha256 to the release."""
import argparse
import gzip
import hashlib
import io
import re
import tarfile
from pathlib import Path

ard2 = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument('--client', required=True, help='the ArdUi installer of this release (ArdUi-vX_setup.exe)')
parser.add_argument('--out', required=True, help='directory for the package and its .sha256')
parser.add_argument('--relay', default=ard2 / '_mtarget/x86_64-unknown-linux-musl/release/ard-relay')
parser.add_argument('--admission', default=ard2 / '_mbuild/native_relay/admission/target/x86_64-unknown-linux-musl/release/ard-relay-admission')
parser.add_argument('--native-relay', default=ard2 / 'native_relay', help='where master.py and ferry_identity.py are')
parser.add_argument('--note', action='append', default=[], help='a line for VERSION saying where a part was built from (repeatable)')
args = parser.parse_args()

client = Path(args.client)
match = re.fullmatch(r'ArdUi-(v\d{1,3}\.(?:pre)?\d{1,3})_setup\.exe', client.name)
if not match: raise SystemExit(f'{client.name}: not an ArdUi installer name (ArdUi-vX_setup.exe)')
native = Path(args.native_relay)
files = [('ard-relay', Path(args.relay), 0o755), ('ard-relay-admission', Path(args.admission), 0o755),
         ('master.py', native / 'master.py', 0o644), ('ferry_identity.py', native / 'ferry_identity.py', 0o644),
         ('client/' + client.name, client, 0o644)]
data = {name: (path.read_bytes().replace(b'\r\n', b'\n') if name.endswith('.py') else path.read_bytes(), mode) for name, path, mode in files}
if data['ard-relay'][0][:4] != b'\x7fELF' or data['ard-relay-admission'][0][:4] != b'\x7fELF': raise SystemExit('ard-relay and ard-relay-admission must be Linux builds')
if data['client/' + client.name][0][:2] != b'MZ': raise SystemExit(f'{client.name} is not a Windows program')
version = f'ArdUi M package {match.group(1)}\n' + ''.join(line + '\n' for line in args.note) + \
          ''.join(f'{hashlib.sha256(body).hexdigest()}  {name}\n' for name, (body, _) in data.items())
data['VERSION'] = (version.encode(), 0o644)

raw = io.BytesIO()
with tarfile.open(fileobj=raw, mode='w', format=tarfile.USTAR_FORMAT) as tar:
    def directory(name):
        entry = tarfile.TarInfo(name); entry.type, entry.mode, entry.mtime, entry.uname, entry.gname = tarfile.DIRTYPE, 0o755, 0, 'root', 'root'
        tar.addfile(entry)
    directory('ardui-m'); directory('ardui-m/client')
    for name in sorted(data):
        body, mode = data[name]
        entry = tarfile.TarInfo('ardui-m/' + name); entry.size, entry.mode, entry.mtime, entry.uname, entry.gname = len(body), mode, 0, 'root', 'root'
        tar.addfile(entry, io.BytesIO(body))
packed = io.BytesIO()
with gzip.GzipFile(fileobj=packed, mode='wb', mtime=0, compresslevel=9) as stream: stream.write(raw.getvalue())
out = Path(args.out); out.mkdir(parents=True, exist_ok=True)
package = out / 'ardui-m-linux-x86_64.tar.gz'
package.write_bytes(packed.getvalue())
digest = hashlib.sha256(packed.getvalue()).hexdigest()
(out / (package.name + '.sha256')).write_text(f'{digest} *{package.name}\n', encoding='ascii', newline='\n')
print(version, end='')
print(f'{package}  {len(packed.getvalue())} bytes\nSHA-256 {digest}  (selfhost.sh: M_PACKAGE_SHA256={digest})')
