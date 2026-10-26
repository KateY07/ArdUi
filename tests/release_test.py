"""Guided updates on M: GET /api/v1/release announces the installer only when release.json matches the file itself (size and SHA-256),
also to clients the version gate refuses; GET /api/v1/release/<file> streams it through the asynchronous front, resumes from a byte
offset (206), refuses a range past the end (416), refuses other names (404) and too many downloads at once (503)."""
import asyncio
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

folder = tempfile.mkdtemp(prefix='ardui-release-')
os.environ['ARDUI_DATABASE'] = os.path.join(folder, 'devices.sqlite3')
os.environ['ARDUI_NETWORK'] = os.path.join(folder, 'network.json')
os.environ['ARDUI_CLIENT_FILES'] = os.path.join(folder, 'client')
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'server'))

import arduiserver  # noqa: E402

client = Path(os.environ['ARDUI_CLIENT_FILES']); client.mkdir()


async def request(path, headers=(), client_version=b'v5.5'):
    scope = {'type': 'http', 'method': 'GET', 'path': path, 'query_string': b'', 'client': ('127.0.0.1', 1),
             'headers': [(b'x-ardui-client', client_version), (b'x-real-ip', b'198.51.100.7'), *headers]}

    async def receive():
        return {'type': 'http.request', 'body': b'', 'more_body': False}
    sent = []

    async def send(message):
        sent.append(message)
    await arduiserver.asgi(scope, receive, send)
    head = sent[0]; body = b''.join(m.get('body', b'') for m in sent[1:])
    return head['status'], dict((k.decode(), v.decode()) for k, v in head['headers']), body, len(sent) - 1


def publish(version, data, digest=None, size=None):
    name = f'ArdUi-{version}_setup.exe'
    (client / name).write_bytes(data)
    (client / 'release.json').write_text(json.dumps({'version': version, 'file': name, 'size': len(data) if size is None else size,
                                                     'sha256': digest or hashlib.sha256(data).hexdigest()}))
    return name


async def main():
    status, _, body, _ = await request('/api/v1/release')
    assert status == 200 and json.loads(body) == {'available': False}, body
    print('PASS: nothing published, nothing announced')

    data = os.urandom(300_000)
    name = publish('v6.3', data)
    status, _, body, _ = await request('/api/v1/release', client_version=b'v4.pre9')
    release = json.loads(body)
    assert status == 200 and release == {'available': True, 'version': 'v6.3', 'file': name, 'size': len(data),
                                         'sha256': hashlib.sha256(data).hexdigest(), 'url': '/api/v1/release/' + name}, release
    print('PASS: the installer is announced with size and SHA-256, also to a client the version gate refuses')

    status, headers, body, pieces = await request(release['url'])
    assert status == 200 and body == data and headers['content-length'] == str(len(data)) and headers['accept-ranges'] == 'bytes', status
    assert pieces > 3, pieces
    print(f'PASS: the whole file is streamed in {pieces} pieces')

    status, headers, body, _ = await request(release['url'], [(b'range', b'bytes=123456-')])
    assert status == 206 and body == data[123456:] and headers['content-range'] == f'bytes 123456-{len(data) - 1}/{len(data)}', status
    status, _, body, _ = await request(release['url'], [(b'range', b'bytes=10-19')])
    assert status == 206 and body == data[10:20], status
    status, headers, _, _ = await request(release['url'], [(b'range', f'bytes={len(data)}-'.encode())])
    assert status == 416 and headers['content-range'] == f'bytes */{len(data)}', status
    print('PASS: a download resumes from where it stopped (206); a range past the end is refused (416)')

    for path in ('/api/v1/release/ArdUi-v6.2_setup.exe', '/api/v1/release/release.json', '/api/v1/release/..%2Fdevices.sqlite3'):
        assert (await request(path))[0] == 404, path
    print('PASS: only the announced installer is served')

    publish('v6.3', data, digest='0' * 64)
    assert json.loads((await request('/api/v1/release'))[2]) == {'available': False}
    assert (await request('/api/v1/release/' + name))[0] == 404
    publish('v6.3', data, size=len(data) + 1)
    assert json.loads((await request('/api/v1/release'))[2]) == {'available': False}
    publish('v6.3', data)
    (client / name).write_bytes(data[:-1] + bytes([data[-1] ^ 1]))   # replaced on disk after it was checked once
    os.utime(client / name, (1, 1))
    assert json.loads((await request('/api/v1/release'))[2]) == {'available': False}
    print('PASS: a file that does not match release.json (hash, size, changed later) is neither announced nor served')

    publish('v6.3', data)
    held = [arduiserver.client_download(name, {'REMOTE_ADDR': '203.0.113.%d' % i}, lambda *_: None) for i in range(arduiserver.CLIENT_DOWNLOADS)]
    status, _, body, _ = await request('/api/v1/release/' + name)
    assert status == 503, status
    for chunks in held: chunks.close()
    assert arduiserver._client_active[0] == 0, arduiserver._client_active
    assert (await request('/api/v1/release/' + name))[0] == 200 and arduiserver._client_active[0] == 0
    print('PASS: too many downloads at once are asked to come back later (503); finished downloads free their place')

asyncio.run(main())
