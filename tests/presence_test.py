"""Server regression: computer name via poll, grant-gated presence, and database migration.

Run with a throw-away database:  ARDUI_DATABASE=/tmp/x.sqlite3 python3 presence_test.py
"""
import base64
import io
import json
import os
import secrets
import sqlite3
import sys
import tempfile
import time
from pathlib import Path

folder = tempfile.mkdtemp(prefix='ardui-presence-')
os.environ['ARDUI_DATABASE'] = os.path.join(folder, 'devices.sqlite3')
os.environ['ARDUI_TRANSIT_KEY'] = os.path.join(folder, 'transit-key')
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'server'))

# Database created by the first release has no "name" column; the new code must migrate it in place.
legacy = sqlite3.connect(os.environ['ARDUI_DATABASE'])
legacy.executescript('''CREATE TABLE devices(endpoint TEXT PRIMARY KEY, code TEXT UNIQUE NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 0, epoch INTEGER NOT NULL DEFAULT 0, seen INTEGER NOT NULL DEFAULT 0);''')
legacy.commit()
legacy.close()

import arduiserver  # noqa: E402
from cryptography.hazmat.primitives import serialization  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

assert 'name' in {row[1] for row in sqlite3.connect(os.environ['ARDUI_DATABASE']).execute('PRAGMA table_info(devices)')}


class Device:
    def __init__(self):
        self.key = Ed25519PrivateKey.generate()
        self.id = self.key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()

    def sign(self, path, payload_obj):
        payload = base64.b64encode(json.dumps(payload_obj, separators=(',', ':')).encode()).decode()
        issued, nonce = int(time.time()), secrets.token_hex(16)
        message = f'ArdUiServer/1\n{path}\n{self.id}\n{issued}\n{nonce}\n{payload}'.encode()
        return dict(endpoint=self.id, issuedAt=issued, nonce=nonce, payload=payload,
                    signature=base64.b64encode(self.key.sign(message)).decode())

    def call(self, path, data, client='v5.pre1'):
        body = json.dumps(self.sign(path, data)).encode()
        environ = {'PATH_INFO': path, 'REQUEST_METHOD': 'POST', 'CONTENT_LENGTH': str(len(body)),
                   'wsgi.input': io.BytesIO(body), 'REMOTE_ADDR': '127.0.0.1', 'HTTP_X_ARDUI_CLIENT': client}
        result = {}

        def start(status, headers):
            result['status'] = int(status.split()[0])
        result['body'] = json.loads(b''.join(arduiserver.application(environ, start)))
        return result['status'], result['body']

    def grant_to(self, controller):
        return self.sign('/grant/v1', dict(controllerEndpoint=controller.id, targetEndpoint=self.id))


controller, host, stranger = Device(), Device(), Device()
for device in (controller, host, stranger):
    assert device.call('/api/v1/register', {})[0] == 200
assert host.call('/api/v1/access', {'enabled': True})[0] == 200

# A poll without a name (older clients) still works and leaves the name empty.
assert host.call('/api/v1/poll', {'enabled': True})[0] == 200
status, answer = controller.call('/api/v1/presence', {'peers': [{'endpoint': host.id, 'grant': host.grant_to(controller)}]})
assert status == 200 and answer['peers'][host.id] == {'online': True, 'name': ''}, answer

# A poll with a computer name publishes it; a malformed name is rejected.
assert host.call('/api/v1/poll', {'enabled': True, 'name': '办公室-PC01'})[0] == 200
assert host.call('/api/v1/poll', {'enabled': True, 'name': 'bad\nname'})[0] == 400
assert host.call('/api/v1/poll', {'enabled': True, 'name': 'x' * 65})[0] == 400
assert host.call('/api/v1/poll', {'enabled': True, 'other': 1})[0] == 400
assert host.call('/api/v1/poll', {'name': 'x'})[0] == 400
answer = controller.call('/api/v1/presence', {'peers': [{'endpoint': host.id, 'grant': host.grant_to(controller)}]})[1]
assert answer['peers'][host.id] == {'online': True, 'name': '办公室-PC01'}, answer

# Nothing is revealed without the target's signed grant for exactly this controller.
answer = stranger.call('/api/v1/presence', {'peers': [{'endpoint': host.id, 'grant': host.grant_to(controller)}]})[1]
assert answer['peers'] == {}, answer
forged = host.grant_to(controller)
forged['signature'] = base64.b64encode(b'x' * 64).decode()
assert controller.call('/api/v1/presence', {'peers': [{'endpoint': host.id, 'grant': forged}]})[1]['peers'] == {}

# Offline: disabled access or a stale heartbeat reads as offline, but the entry is still returned to the grant holder.
assert host.call('/api/v1/offline', {})[0] == 200
answer = controller.call('/api/v1/presence', {'peers': [{'endpoint': host.id, 'grant': host.grant_to(controller)}]})[1]
assert answer['peers'][host.id]['online'] is False, answer

# Malformed requests.
assert controller.call('/api/v1/presence', {'peers': 'x'})[0] == 400
assert controller.call('/api/v1/presence', {'peers': [{'endpoint': host.id}]})[0] == 400
assert controller.call('/api/v1/presence', {'peers': [], 'extra': 1})[0] == 400
# Clients older than v5.pre1 are refused: no version header (every earlier release), an older one, or a malformed one.
for header in ('', 'v4.pre9', 'v3.pre17', 'v5.pre0', 'v5.pre', '5.0.0-pre.1', 'x' * 40):
    status, answer = controller.call('/api/v1/poll', {'enabled': True}, client=header)
    assert status == 426 and '版本过旧' in answer['error'], (header, status, answer)
for header in ('v5.pre1', 'v5.pre2', 'v5.pre10', 'v6.pre1'):
    assert controller.call('/api/v1/register', {}, client=header)[0] == 200, header
# The refusal comes before anything is checked or consumed: an unsigned body with an old header is 426, not 401.
environ = {'PATH_INFO': '/api/v1/poll', 'REQUEST_METHOD': 'POST', 'CONTENT_LENGTH': '2', 'wsgi.input': io.BytesIO(b'{}'), 'REMOTE_ADDR': '127.0.0.1'}
seen = {}
arduiserver.application(environ, lambda status, headers: seen.update(status=int(status.split()[0])))
assert seen['status'] == 426, seen
# The health page stays open and says what is required.
health = {}
environ = {'PATH_INFO': '/api/health', 'REQUEST_METHOD': 'GET', 'REMOTE_ADDR': '127.0.0.1'}
body = b''.join(arduiserver.application(environ, lambda status, headers: health.update(status=int(status.split()[0]))))
assert health['status'] == 200 and json.loads(body)['minClient'] == 'v5.pre1' and json.loads(body)['version'] == 'v5.pre1', body
# The connection request carries the controller's version, signed with the rest of the request.
req = {'code': 'AAAAAA', 'sessionId': 'a' * 64, 'targetEndpoint': 'b' * 64, 'requestId': 'c' * 32, 'expires': int(time.time()) + 200}
assert controller.call('/api/v1/connect', dict(req))[0] == 400, 'a request without the version is invalid'
assert controller.call('/api/v1/connect', dict(req, client='v4.pre9'))[0] == 426, 'an old version in the request is refused'
assert controller.call('/api/v1/connect', dict(req, client='v5.pre1'))[0] != 400, 'a request with the version passes validation'
print('PASS: client version gate (v5.pre1)')
print('PASS: name migration, computer name via poll, grant-gated presence, offline, malformed requests')
