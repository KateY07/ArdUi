"""One-time uploads to M: a token made on the server allows exactly one PUT of a release file into <data>/incoming, streamed in pieces;
the file is kept only if its SHA-256 matches; a used, wrong or expired token is refused, and so are other file names."""
import asyncio
import hashlib
import json
import os
import sys
import tempfile
import time
from pathlib import Path

folder = tempfile.mkdtemp(prefix='ardui-upload-')
os.environ['ARDUI_DATABASE'] = os.path.join(folder, 'devices.sqlite3')
os.environ['ARDUI_NETWORK'] = os.path.join(folder, 'network.json')
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'server'))

import arduiserver  # noqa: E402


async def put(path, data, sha=None, ip='198.51.100.9'):
    pieces = [data[i:i + 65536] for i in range(0, len(data), 65536)] or [b'']
    scope = {'type': 'http', 'method': 'PUT', 'path': path, 'query_string': b'', 'client': ('127.0.0.1', 1),
             'headers': [(b'x-real-ip', ip.encode()), (b'x-sha256', (sha or hashlib.sha256(data).hexdigest()).encode())]}
    sent_pieces = iter(pieces)

    async def receive():
        piece = next(sent_pieces, None)
        return {'type': 'http.request', 'body': piece or b'', 'more_body': piece is not None and piece is not pieces[-1]}
    out = []

    async def send(message):
        out.append(message)
    await arduiserver.asgi(scope, receive, send)
    return out[0]['status'], json.loads(out[1]['body'])


async def main():
    data = os.urandom(300_000); name = 'ArdUi-v6.6_setup.exe'
    token = arduiserver.make_upload_token()
    status, answer = await put(f'/api/v1/admin/upload/{token}/{name}', data)
    target = Path(arduiserver.UPLOAD_DIR) / name
    assert status == 200 and target.read_bytes() == data, (status, answer)
    print('PASS: a token allows one upload; the file arrives whole in incoming/')
    status, _ = await put(f'/api/v1/admin/upload/{token}/{name}', data)
    assert status == 403, status
    print('PASS: the same token does not work twice')

    token = arduiserver.make_upload_token(); target.unlink()
    status, _ = await put(f'/api/v1/admin/upload/{token}/{name}', data, sha='0' * 64)
    assert status == 400 and not target.exists() and not Path(str(target) + '.part').exists(), status
    print('PASS: a file whose SHA-256 does not match is thrown away')

    token = arduiserver.make_upload_token()
    status, _ = await put(f'/api/v1/admin/upload/{"f" * 64}/{name}', data)
    assert status == 403 and (await put(f'/api/v1/admin/upload/{token}/{name}', data))[0] == 403
    print('PASS: a wrong token is refused and uses the token up')

    arduiserver.make_upload_token()
    stored, until = open(arduiserver.UPLOAD_TOKEN).read().split()
    with open(arduiserver.UPLOAD_TOKEN, 'w') as out: out.write(f'{stored} {int(time.time()) - 1}\n')
    assert (await put(f'/api/v1/admin/upload/{stored}/{name}', data))[0] == 403
    print('PASS: an expired token is refused')

    token = arduiserver.make_upload_token()
    for bad in ('../devices.sqlite3', 'evil.exe', 'ArdUi-v6.6_setup.exe.bat'):
        assert (await put(f'/api/v1/admin/upload/{token}/{bad}', data))[0] == 404, bad
    print('PASS: only release file names are taken')

asyncio.run(main())
