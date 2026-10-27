"""Accounts on M: only users an administrator made can sign a device in; only a signed-in device may start a session (an anonymous
controller's connection request is refused; the controlled side needs no account); passwords can be changed; a disabled user or a
signed-out device is anonymous again; the administration page."""
import base64
import json
import os
import secrets
import sys
import tempfile
import time
from pathlib import Path

folder = tempfile.mkdtemp(prefix='ardui-account-')
os.environ['ARDUI_DATABASE'] = os.path.join(folder, 'devices.sqlite3')
os.environ['ARDUI_NETWORK'] = os.path.join(folder, 'network.json')
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'server'))

import arduiserver  # noqa: E402
from cryptography.hazmat.primitives import serialization  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402


class Device:
    def __init__(self):
        self.key = Ed25519PrivateKey.generate()
        self.id = self.key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()

    def call(self, path, data):
        payload = base64.b64encode(json.dumps(data, separators=(',', ':')).encode()).decode()
        issued, nonce = int(time.time()), secrets.token_hex(16)
        message = f'ArdUiServer/1\n{path}\n{self.id}\n{issued}\n{nonce}\n{payload}'.encode()
        body = json.dumps(dict(endpoint=self.id, issuedAt=issued, nonce=nonce, payload=payload,
                               signature=base64.b64encode(self.key.sign(message)).decode())).encode()
        import io
        out = {}
        environ = {'REQUEST_METHOD': 'POST', 'PATH_INFO': path, 'CONTENT_LENGTH': str(len(body)), 'wsgi.input': io.BytesIO(body),
                   'HTTP_X_ARDUI_CLIENT': 'v6.pre7', 'REMOTE_ADDR': '198.51.100.' + str(secrets.randbelow(200) + 1)}
        reply = b''.join(arduiserver.application(environ, lambda status, headers: out.update(status=status)))
        return int(out['status'].split()[0]), json.loads(reply)


def request(controller, host, code, session):
    ticket = secrets.token_hex(16)
    data = {'code': code, 'sessionId': session, 'targetEndpoint': host.id, 'requestId': ticket, 'expires': int(time.time()) + 200,
            'client': 'v6.pre7', 'ferry': {'node': 'n1-m', 'key': 'spki:00', 'addresses': ['192.0.2.1:443']}}
    return controller.call('/api/v1/connect', data), ticket


host, member, anonymous = Device(), Device(), Device()
for device in (host, member, anonymous): assert device.call('/api/v1/register', {})[0] == 200
code = host.call('/api/v1/register', {})[1]['code']
assert host.call('/api/v1/access', {'enabled': True})[0] == 200

password = arduiserver.add_user('alice')
assert arduiserver.add_user('admin1', admin=True)
try: arduiserver.add_user('alice'); raise SystemExit('a second alice')
except arduiserver.ApiError as ex: assert ex.status == 409
print('PASS: users are made by an administrator; names are unique')

assert member.call('/api/v1/account', {})[1]['username'] is None
assert member.call('/api/v1/account/login', {'username': 'alice', 'password': 'wrong-password'})[0] == 403
status, answer = member.call('/api/v1/account/login', {'username': 'ALICE', 'password': password})
assert status == 200 and answer['username'] == 'alice', answer
assert member.call('/api/v1/account', {})[1]['username'] == 'alice'
print('PASS: a device signs in with name and password and is then that user')

(status, answer), _ = request(anonymous, host, code, secrets.token_hex(32))
assert status == 403 and '请先登录' in answer['error'], (status, answer)
(status, _), _ = request(member, host, code, secrets.token_hex(32))
assert status == 200, status
print('PASS: only a signed-in device may start a session; the controlled side needs no account')

assert member.call('/api/v1/account/password', {'old': 'wrong-password', 'new': 'new-password-1'})[0] == 403
assert member.call('/api/v1/account/password', {'old': password, 'new': 'short'})[0] == 400
assert member.call('/api/v1/account/password', {'old': password, 'new': 'new-password-1'})[0] == 200
assert anonymous.call('/api/v1/account/login', {'username': 'alice', 'password': password})[0] == 403
assert anonymous.call('/api/v1/account/login', {'username': 'alice', 'password': 'new-password-1'})[0] == 200
print('PASS: a user changes the password; the old one stops working')

with arduiserver.database() as db: db.execute("UPDATE users SET disabled=1 WHERE name='alice'")
assert member.call('/api/v1/account', {})[1]['username'] is None
assert request(member, host, code, secrets.token_hex(32))[0][0] == 403
assert member.call('/api/v1/account/login', {'username': 'alice', 'password': 'new-password-1'})[0] == 403
with arduiserver.database() as db: db.execute("UPDATE users SET disabled=0 WHERE name='alice'")
assert member.call('/api/v1/account/logout', {})[0] == 200 and member.call('/api/v1/account', {})[1]['username'] is None
print('PASS: a disabled user or a signed-out device is anonymous')


def panel(method, path, data=None, cookie='', marked=True):
    import io
    body = json.dumps(data).encode() if data is not None else b''
    out = {}
    environ = {'REQUEST_METHOD': method, 'PATH_INFO': path, 'CONTENT_LENGTH': str(len(body)), 'wsgi.input': io.BytesIO(body),
               'REMOTE_ADDR': '203.0.113.5', 'HTTP_COOKIE': cookie}
    if marked: environ['HTTP_X_ARDUI_ADMIN'] = '1'
    reply = b''.join(arduiserver.application(environ, lambda status, headers: out.update(status=status, headers=dict(headers))))
    return int(out['status'].split()[0]), (json.loads(reply) if out['headers'].get('Content-Type', '').startswith('application/json') else reply), out['headers']


admin_password = arduiserver.add_user('boss', 'boss-password-1', admin=True)
status, page, headers = panel('GET', '/api/admin', marked=False)
assert status == 200 and b'ArdUi' in page and headers.get('X-Frame-Options') == 'DENY'
assert panel('POST', '/api/admin/login', {'name': 'boss', 'password': 'boss-password-1'}, marked=False)[0] == 403
assert panel('POST', '/api/admin/login', {'name': 'alice', 'password': 'new-password-1'})[0] == 403
status, _, headers = panel('POST', '/api/admin/login', {'name': 'boss', 'password': 'boss-password-1'})
cookie = headers['Set-Cookie'].split(';')[0]
assert status == 200 and 'HttpOnly' in headers['Set-Cookie'] and 'SameSite=Strict' in headers['Set-Cookie'] and 'Secure' in headers['Set-Cookie']
assert panel('GET', '/api/admin/me', cookie=cookie, marked=False)[0] == 403 and panel('GET', '/api/admin/me')[0] == 401
assert panel('GET', '/api/admin/me', cookie=cookie)[1]['name'] == 'boss'
print('PASS: only administrators sign in to the panel; its session needs the cookie and the panel header')

status, made, _ = panel('POST', '/api/admin/users', {'name': 'carol', 'admin': False}, cookie=cookie)
assert status == 200 and made['password'] and panel('POST', '/api/admin/users', {'name': 'carol'}, cookie=cookie)[0] == 409
assert panel('POST', '/api/admin/users', {'name': ''}, cookie=cookie)[0] == 400
users = panel('GET', '/api/admin/users', cookie=cookie)[1]
carol = next(u for u in users['users'] if u['name'] == 'carol')
assert anonymous.call('/api/v1/account/login', {'username': 'carol', 'password': made['password']})[0] == 200
status, reset, _ = panel('POST', f"/api/admin/users/{carol['id']}", {'resetPassword': True}, cookie=cookie)
assert status == 200 and reset['password'] and anonymous.call('/api/v1/account', {})[1]['username'] is None
assert anonymous.call('/api/v1/account/login', {'username': 'carol', 'password': reset['password']})[0] == 200
me = users['me']
assert panel('DELETE', f'/api/admin/users/{me}', cookie=cookie)[0] == 400 and panel('POST', f'/api/admin/users/{me}', {'disabled': True}, cookie=cookie)[0] == 400
assert panel('DELETE', f"/api/admin/users/{carol['id']}", cookie=cookie)[0] == 200 and anonymous.call('/api/v1/account', {})[1]['username'] is None
assert panel('POST', '/api/admin/logout', cookie=cookie)[0] == 200 and panel('GET', '/api/admin/me', cookie=cookie)[0] == 401
print('PASS: the panel makes users (name required), resets passwords (signing them out), deletes; not itself')

# the account page in a browser: register (pending until an administrator approves), sign a device in, change the password
def web(method, path, data=None, marked=True, query=''):
    import io
    body = json.dumps(data).encode() if data is not None else b''
    out = {}
    environ = {'REQUEST_METHOD': method, 'PATH_INFO': path, 'QUERY_STRING': query, 'CONTENT_LENGTH': str(len(body)), 'wsgi.input': io.BytesIO(body),
               'REMOTE_ADDR': '203.0.113.' + str(secrets.randbelow(200) + 1)}
    if marked: environ['HTTP_X_ARDUI_WEB'] = '1'
    reply = b''.join(arduiserver.application(environ, lambda status, headers: out.update(status=status, headers=dict(headers))))
    return int(out['status'].split()[0]), (json.loads(reply) if out['headers'].get('Content-Type', '').startswith('application/json') else reply)


status, page = web('GET', '/api/account', marked=False)
assert status == 200 and b'ArdUi' in page
assert web('POST', '/api/account/register', {'name': 'dave', 'email': 'dave@example.com', 'password': 'dave-password-1'}, marked=False)[0] == 403
assert web('POST', '/api/account/register', {'name': 'dave', 'email': 'not-an-email', 'password': 'dave-password-1'})[0] == 400
assert web('POST', '/api/account/register', {'name': 'dave', 'email': 'dave@example.com', 'password': 'short'})[0] == 400
assert web('POST', '/api/account/register', {'name': 'dave', 'email': 'dave@example.com', 'password': 'dave-password-1'}) == (200, {'pending': True})
assert web('POST', '/api/account/register', {'name': 'Dave', 'email': 'x@example.com', 'password': 'dave-password-1'})[0] == 409
print('PASS: anyone may register on the account page (name, e-mail, password); names stay unique')

newcomer = Device(); assert newcomer.call('/api/v1/register', {})[0] == 200
status, start = newcomer.call('/api/v1/account/login-start', {})
assert status == 200 and start['url'].startswith('/api/account?login='), start
token = start['token']
status, shown = web('GET', '/api/account/device', query='login=' + token)
assert status == 200 and shown['code'] == newcomer.call('/api/v1/register', {})[1]['code'] and not shown['done'], shown
assert newcomer.call('/api/v1/account/login-wait', {'token': token}) == (200, {'pending': True})
status, answer = web('POST', '/api/account/login', {'login': token, 'name': 'dave', 'password': 'dave-password-1'})
assert status == 403 and '等待管理员审核' in answer['error'], answer
print('PASS: a pending account cannot sign a device in')

status, _, headers = panel('POST', '/api/admin/login', {'name': 'boss', 'password': 'boss-password-1'})
cookie = headers['Set-Cookie'].split(';')[0]
dave = next(u for u in panel('GET', '/api/admin/users', cookie=cookie)[1]['users'] if u['name'] == 'dave')
assert dave['pending'] == 1 and dave['email'] == 'dave@example.com'
assert panel('POST', f"/api/admin/users/{dave['id']}", {'approve': True}, cookie=cookie)[0] == 200
assert web('POST', '/api/account/login', {'login': 'wrong-token', 'name': 'dave', 'password': 'dave-password-1'})[0] == 410
assert web('POST', '/api/account/login', {'login': token, 'name': 'dave', 'password': 'dave-password-1'}) == (200, {'name': 'dave'})
assert newcomer.call('/api/v1/account/login-wait', {'token': token}) == (200, {'username': 'dave', 'admin': False})
assert newcomer.call('/api/v1/account', {})[1]['username'] == 'dave'
assert web('POST', '/api/account/login', {'login': token, 'name': 'dave', 'password': 'dave-password-1'})[0] == 409
assert anonymous.call('/api/v1/account/login-wait', {'token': token})[0] == 410
print('PASS: once approved, signing in on the page signs the waiting device in; the link works once, for that device only')

assert web('POST', '/api/account/password', {'name': 'dave', 'old': 'wrong-password', 'new': 'dave-password-2'})[0] == 403
assert web('POST', '/api/account/password', {'name': 'dave', 'old': 'dave-password-1', 'new': 'dave-password-2'})[0] == 200
start2 = newcomer.call('/api/v1/account/login-start', {})[1]
assert web('POST', '/api/account/login', {'login': start2['token'], 'name': 'dave', 'password': 'dave-password-2'})[0] == 200
print('PASS: the password is changed on the page')
