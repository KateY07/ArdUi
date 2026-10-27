"""Waiting for news (long polling) through the asynchronous front: a host's waiting poll gets a connection request at once, a
controller's waiting look gets the host's answer at once, nothing new means an empty answer after WAIT_SECONDS, and waiting
requests hold no thread (other requests are not slowed down by many of them)."""
import asyncio
import base64
import json
import os
import secrets
import sys
import tempfile
import time
from pathlib import Path

folder = tempfile.mkdtemp(prefix='ardui-longpoll-')
os.environ['ARDUI_DATABASE'] = os.path.join(folder, 'devices.sqlite3')
os.environ['ARDUI_NETWORK'] = os.path.join(folder, 'network.json')
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'server'))

import arduiserver  # noqa: E402
from cryptography.hazmat.primitives import serialization  # noqa: E402
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

arduiserver.WAIT_SECONDS = 2


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

    async def call(self, path, data, ip='127.0.0.1'):
        body = json.dumps(self.sign(path, data)).encode()
        return await request('POST', path, body, ip)


async def request(method, path, body=b'', ip='127.0.0.1'):
    scope = {'type': 'http', 'method': method, 'path': path, 'query_string': b'', 'client': ('127.0.0.1', 1),
             'headers': [(b'content-length', str(len(body)).encode()), (b'x-ardui-client', b'v6.pre6'), (b'x-real-ip', ip.encode())]}
    given = False

    async def receive():
        nonlocal given
        if not given:
            given = True
            return {'type': 'http.request', 'body': body, 'more_body': False}
        await asyncio.sleep(3600)

    sent = []

    async def send(message):
        sent.append(message)
    await arduiserver.asgi(scope, receive, send)
    return sent[0]['status'], json.loads(sent[1]['body'])


async def timed(coroutine):
    started = time.monotonic()
    result = await coroutine
    return result, time.monotonic() - started


async def main():
    host, controller = Device(), Device()
    status, registered = await host.call('/api/v1/register', {})
    assert status == 200, registered
    assert (await controller.call('/api/v1/register', {}))[0] == 200
    assert (await host.call('/api/v1/access', {'enabled': True}))[0] == 200

    # 1. the host waits; a connection request reaches it at once, not at the end of the wait
    poll = asyncio.create_task(timed(host.call('/api/v1/poll', {'enabled': True, 'known': []})))
    await asyncio.sleep(0.3)
    assert not poll.done(), 'a poll with nothing new waits'
    ticket = secrets.token_hex(16)
    request_body = {'code': registered['code'], 'sessionId': secrets.token_hex(32), 'targetEndpoint': host.id,
                    'requestId': ticket, 'expires': int(time.time()) + 200, 'client': 'v6.pre6',
                    'ferry': {'node': '805c40bca7e89af2', 'key': 'spki:3059', 'addresses': ['175.27.160.144:18443']}}
    status, answer = await controller.call('/api/v1/connect', request_body)
    assert status == 200, answer
    (status, reply), waited = await poll
    assert status == 200 and [t['id'] for t in reply['tickets']] == [ticket], reply
    assert waited < 1.0, f'the request was delivered after {waited:.2f} s'
    print(f'PASS: a waiting host gets a connection request at once ({waited:.2f} s after it was polled, request at 0.3 s)')

    # 2. a request the host already handles is not news: the poll waits again (and returns empty after WAIT_SECONDS)
    (status, reply), waited = await timed(host.call('/api/v1/poll', {'enabled': True, 'known': [ticket]}))
    assert status == 200 and arduiserver.WAIT_SECONDS - 0.2 < waited < arduiserver.WAIT_SECONDS + 1.5, waited
    print(f'PASS: nothing new: the poll returns after the wait ({waited:.2f} s), with what is still pending')

    # 3. the controller waits for the host's answer and learns it at once
    look = asyncio.create_task(timed(controller.call(f'/api/v1/tickets/{ticket}', {})))
    await asyncio.sleep(0.3)
    assert not look.done(), 'a pending request makes the controller wait'
    assert (await host.call(f'/api/v1/tickets/{ticket}/reject', {}))[0] == 200
    (status, reply), waited = await look
    assert status == 200 and reply['status'] == 'rejected' and waited < 1.0, (reply, waited)
    print(f'PASS: a waiting controller learns the host\'s answer at once ({waited:.2f} s)')

    # 4. switching access off wakes the host's own poll and every controller waiting for one of its requests
    second = secrets.token_hex(16)
    await controller.call('/api/v1/connect', dict(request_body, requestId=second, sessionId=secrets.token_hex(32)))
    look = asyncio.create_task(timed(controller.call(f'/api/v1/tickets/{second}', {})))
    poll = asyncio.create_task(timed(host.call('/api/v1/poll', {'enabled': True, 'known': [second]})))
    await asyncio.sleep(0.3)
    assert (await host.call('/api/v1/access', {'enabled': False}))[0] == 200
    (status, reply), waited = await look
    assert status == 410 and waited < 1.0, (status, reply, waited)
    (status, reply), waited = await poll
    assert status == 200 and reply['enabled'] is False and waited < 1.0, (reply, waited)
    print('PASS: switching access off ends the waits at once (the controller\'s request is gone, the host\'s poll sees "off")')

    # 5. many waiting hosts hold no thread: other requests are answered right away
    hosts = [Device() for _ in range(80)]
    for i, device in enumerate(hosts):
        assert (await device.call('/api/v1/register', {}, ip=f'10.0.{i // 200}.{i % 200}'))[0] == 200
        await device.call('/api/v1/access', {'enabled': True})
    arduiserver.WAIT_SECONDS = 30
    started = time.monotonic()
    waiting = [asyncio.create_task(device.call('/api/v1/poll', {'enabled': True, 'known': []})) for device in hosts]
    deadline = time.monotonic() + 20
    while sum(len(v) for v in arduiserver._news_waiters.values()) < len(hosts) and time.monotonic() < deadline:
        await asyncio.sleep(0.05)   # until every poll has been handled and is waiting (no thread held)
    (status, health), waited = await timed(request('GET', '/api/health'))
    assert status == 200 and waited < 0.5, waited
    parked = time.monotonic() - started
    assert sum(1 for task in waiting if not task.done()) == len(hosts), 'all of them are still waiting'
    arduiserver.notify(*('poll:' + device.id for device in hosts))   # let them go
    await asyncio.gather(*waiting)
    print(f'PASS: {len(hosts)} waiting hosts (more than the 32 worker threads; all waiting after {parked:.2f} s) '
          f'do not slow other requests ({waited * 1000:.0f} ms)')


asyncio.run(main())
