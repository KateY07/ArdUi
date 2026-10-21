"""Probe service regression: tokens, the cookie proof of every source address, abuse limits and every packet type, over real loopback sockets.

Run with:  python3 tests/probe_test.py     (needs the 'cryptography' package, like the directory server itself)
"""
import asyncio
import os
import socket
import struct
import sys
import tempfile
import time
from pathlib import Path

folder = tempfile.mkdtemp(prefix='ardui-probe-')
os.environ['ARDUI_DATABASE'] = os.path.join(folder, 'devices.sqlite3')
os.environ['ARDUI_TRANSIT_KEY'] = os.path.join(folder, 'transit-key')
os.environ['ARDUI_PROBE_KEY'] = os.path.join(folder, 'probe-key')
os.environ['ARDUI_PROBE_ALLOW_PRIVATE'] = '1'     # the tests talk over loopback
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'server'))

import arduiserver  # noqa: E402
import probe_udp as probe  # noqa: E402

failures = 0


def check(ok, name):
    global failures
    if ok: print('PASS:', name)
    else: failures += 1; print('FAIL:', name)


def packet(kind, token, body=b'', seq=1, stamp=b'\x00' * 8, pad=probe.MIN_REQUEST):
    data = probe.MAGIC + bytes([1, kind]) + b'\x00\x00' + token + struct.pack('>I', seq) + stamp + body
    return data + b'\x00' * max(0, pad - len(data))


def free_pair():
    while True:
        a = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); a.bind(('127.0.0.1', 0)); port = a.getsockname()[1]
        b = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try: b.bind(('127.0.0.1', port + 1))
        except OSError: a.close(); b.close(); continue
        a.close(); b.close(); return port


def client(timeout=0.4, host='127.0.0.1'):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); sock.bind((host, 0)); sock.settimeout(timeout)
    return sock


async def receive(sock, timeout=0.5):
    loop = asyncio.get_running_loop(); sock.setblocking(False)
    try: return await asyncio.wait_for(loop.sock_recvfrom(sock, 2048), timeout)
    except (asyncio.TimeoutError, TimeoutError): return None


async def start(key):
    first = free_pair(); loop = asyncio.get_running_loop(); transports = []
    service = probe.Service(key, lambda index, data, address: transports[index].sendto(data, address))
    for index in (0, 1):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); sock.bind(('127.0.0.1', first + index))
        transport, _ = await loop.create_datagram_endpoint(lambda i=index: probe.Endpoint(service, i), sock=sock)
        transports.append(transport)
    service.ports = (first, first + 1)
    return service, first, transports


async def prove(sock, token, server):
    """HELLO, then AUTH with the cookie: afterwards the socket's address is usable with this token."""
    sock.sendto(packet(probe.T_HELLO, token), server)
    hello = await receive(sock)
    if hello is None or hello[0][5] != (probe.REPLY | probe.T_HELLO): return False
    sock.sendto(packet(probe.T_AUTH, token, hello[0][probe.HEADER:probe.HEADER + 16]), server)
    auth = await receive(sock)
    return auth is not None and auth[0][5] == (probe.REPLY | probe.T_AUTH) and auth[0][probe.HEADER] == 1


async def main():
    key = probe.load_key(os.environ['ARDUI_PROBE_KEY'])
    now = time.time()

    # ---- tokens: minted by arduiserver, verified here ----------------------------------------------------------------
    token = arduiserver.probe_token()
    check(len(token) == probe.TOKEN_LEN and probe.verify_token(key, token), 'the directory server mints a 28-byte token the probe service accepts')
    check(not probe.verify_token(key, token, now + 500), 'an expired token fails')
    check(not probe.verify_token(key, token[:-1] + bytes([token[-1] ^ 1])), 'a tampered token fails')
    check(not probe.verify_token(key, token[:8] + bytes(4) + token[12:]), 'a token with a changed expiry fails')
    check(arduiserver.probe_token() != arduiserver.probe_token(), 'every token is different')
    check(probe.verify_token(key, probe.mint_token(key)), 'the probe service can mint the same kind of token (tests)')

    # ---- the HTTPS side: registered devices only, rate limited ----------------------------------------------------------
    arduiserver.initialize()
    with arduiserver.database() as db:
        db.execute("INSERT INTO devices(endpoint,code,enabled,epoch,seen) VALUES(?,?,0,0,0)", ('ab' * 32, 'TESTAB'))
    reply = arduiserver.dispatch('/api/v1/probe/start', 'ab' * 32, {}, '203.0.113.5', {})
    check(reply['v'] == 2 and reply['ip'] == '203.0.113.5' and reply['ports'] == [7843, 7844] and probe.verify_token(key, bytes.fromhex(reply['token'])), 'probe/start returns a token, the address it saw and the ports')
    try: arduiserver.dispatch('/api/v1/probe/start', 'cd' * 32, {}, '203.0.113.5', {}); refused = False
    except arduiserver.ApiError as error: refused = error.status == 403
    check(refused, 'an unregistered device gets no token')
    try: arduiserver.dispatch('/api/v1/probe/start', 'ab' * 32, {'x': 1}, '203.0.113.5', {}); refused = False
    except arduiserver.ApiError as error: refused = error.status == 400
    check(refused, 'parameters are rejected')
    limited = False
    for _ in range(14):
        try: arduiserver.dispatch('/api/v1/probe/start', 'ab' * 32, {}, '203.0.113.5', {})
        except arduiserver.ApiError as error: limited = limited or error.status == 429
    check(limited, 'too many tokens per device per hour are refused (429)')
    try: arduiserver.dispatch('/api/v1/probe/start', 'ab' * 32, {}, 'not-an-address', {}); refused = False
    except arduiserver.ApiError as error: refused = error.status in (400, 429)
    check(refused, 'an unknown source address gets no token')

    # ---- UDP over loopback ---------------------------------------------------------------------------------------------
    service, first, transports = await start(key)
    server0, server1 = ('127.0.0.1', first), ('127.0.0.1', first + 1)
    good = probe.mint_token(key)
    me = client()

    me.sendto(packet(probe.T_BIND, good), server0)
    check(await receive(me) is None and service.stats['unvalidated'] == 1, 'an address that has not proven itself gets nothing (not even for a valid token)')
    me.sendto(packet(probe.T_HELLO, good), server0)
    hello = await receive(me)
    check(hello is not None and hello[0][5] == (probe.REPLY | probe.T_HELLO) and len(hello[0]) <= probe.MIN_REQUEST, 'HELLO is answered with a cookie, no larger than the request')
    cookie = hello[0][probe.HEADER:probe.HEADER + 16] if hello else b''
    me.sendto(packet(probe.T_AUTH, good, bytes(16)), server0)
    wrong = await receive(me)
    check(wrong is not None and wrong[0][probe.HEADER] == 0, 'AUTH with a wrong cookie fails')
    other = client(); other.sendto(packet(probe.T_AUTH, good, cookie), server0)
    stolen = await receive(other)
    check(stolen is not None and stolen[0][probe.HEADER] == 0, 'a cookie cannot be used from another port')
    me.sendto(packet(probe.T_AUTH, good, cookie), server0)
    ok = await receive(me)
    check(ok is not None and ok[0][probe.HEADER] == 1, 'AUTH with the cookie proves the address')

    me.sendto(packet(probe.T_BIND, good), server0)
    answer = await receive(me)
    check(answer is not None and answer[0][5] == (probe.REPLY | probe.T_BIND), 'BIND is answered once the address is proven')
    if answer:
        body = answer[0][probe.HEADER:]
        seen_port = struct.unpack('>H', body[17:19])[0]; server_port = struct.unpack('>H', body[19:21])[0]
        check(seen_port == me.getsockname()[1] and body[0] == 4 and probe.ip_from16(body[1:17]) == '127.0.0.1', 'BIND reports the address and port the server saw')
        check(server_port == first and answer[1] == server0 and len(answer[0]) <= probe.MIN_REQUEST, 'the answer names and leaves from the server port that was asked, and is not longer than the request')

    other2 = client()   # another port of the same address: the address is proven, so it works without a new cookie
    other2.sendto(packet(probe.T_BIND, good), server0)
    check(await receive(other2) is not None, 'a proven address works from any of its ports')

    me.sendto(packet(probe.T_OTHER, good), server0)
    answer = await receive(me)
    check(answer is not None and answer[1] == server1 and answer[0][5] == (probe.REPLY | probe.T_OTHER), 'OTHER answers from the second server port (port-restriction test)')

    payload = bytes(range(200))
    me.sendto(packet(probe.T_ECHO, good, payload, seq=7), server1)
    answer = await receive(me)
    check(answer is not None and answer[0][probe.HEADER:probe.HEADER + 200] == payload and answer[0][36:40] == struct.pack('>I', 7) and len(answer[0]) == len(packet(probe.T_ECHO, good, payload)), 'ECHO returns the payload with the same sequence number and length')

    # abuse: nothing comes back for these
    me.sendto(packet(probe.T_BIND, b'\x00' * probe.TOKEN_LEN), server0)
    check(await receive(me) is None, 'a made-up token gets no answer')
    me.sendto(packet(probe.T_BIND, good, pad=40), server0)
    check(await receive(me) is None, 'a datagram shorter than the minimum request is ignored')
    me.sendto(b'GET / HTTP/1.1\r\n\r\n' + b'\x00' * 100, server0)
    check(await receive(me) is None and service.stats['bad_token'] >= 1 and service.stats['dropped'] >= 2, 'foreign traffic is ignored and bad tokens are counted')

    # downlink burst
    me.sendto(packet(probe.T_PUSH, good, struct.pack('>HHH', 20, 1200, 5)), server0)
    got = 0
    while (item := await receive(me, 0.5)) is not None:
        if item[0][5] == probe.PUSH_DATA: got += 1
    check(got == 20, 'PUSH delivers the requested burst (%d of 20)' % got)
    me.sendto(packet(probe.T_PUSH, good, struct.pack('>HHH', 300, 1400, 2)), server0)
    check(await receive(me, 0.6) is None, 'a burst over the byte cap is refused')

    # unsolicited UDP probe and confirmation: the heart of the UPnP / NAT-PMP / public-entrance check
    listener = client(); port = listener.getsockname()[1]
    nonce = b'NONCE-01'
    me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', port, 0) + nonce), server0)
    ack = await receive(me)
    arrived = await receive(listener)
    check(ack is not None and ack[0][5] == (probe.REPLY | probe.T_PROBE_UDP), 'the probe request is acknowledged to the requester')
    check(arrived is not None and arrived[0][5] == probe.PROBE_PACKET and arrived[0][probe.HEADER:probe.HEADER + 8] == nonce and arrived[1] == server0, 'the unsolicited packet reaches the listening port from the contacted server port')
    listener.sendto(packet(probe.T_CONFIRM, good, nonce), server0)
    confirmed = await receive(listener)
    check(confirmed is not None and confirmed[0][probe.HEADER] == 1 and struct.unpack('>H', confirmed[0][probe.HEADER + 18:probe.HEADER + 20])[0] == port, 'CONFIRM from the listening socket is acknowledged with the address the server saw')
    listener.sendto(packet(probe.T_CONFIRM, good, nonce), server0)
    again = await receive(listener)
    check(again is not None and again[0][probe.HEADER] == 0, 'a nonce can be confirmed only once')
    listener.sendto(packet(probe.T_CONFIRM, good, b'WRONG!!!'), server0)
    wrong = await receive(listener)
    check(wrong is not None and wrong[0][probe.HEADER] == 0, 'a confirmation with the wrong nonce fails')

    me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', port, 1) + b'NONCE-02'), server0)
    await receive(me)
    arrived = await receive(listener)
    check(arrived is not None and arrived[1] == server1, 'with from_other the unsolicited packet comes from the second server port')

    # a second address of the same token (a machine with two internet lines): probes may go to it once it has proven itself
    second = client(host='127.0.0.2'); second_port = second.getsockname()[1]
    target = probe.ip16('127.0.0.2')
    me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', second_port, 0) + b'NONCE-05' + target), server0)
    await receive(me)
    check(await receive(second, 0.4) is None, 'an address that has not proven itself is not probed, even when the token is valid')
    prover = client(host='127.0.0.2')
    check(await prove(prover, good, server0), 'a second address proves itself with the same token')
    me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', second_port, 0) + b'NONCE-06' + target), server0)
    await receive(me)
    got_second = await receive(second, 0.5)
    check(got_second is not None and got_second[0][probe.HEADER:probe.HEADER + 8] == b'NONCE-06', 'once proven, the second address can be probed on request from the first')
    other_token = probe.mint_token(key)
    me.sendto(packet(probe.T_PROBE_UDP, other_token, struct.pack('>HB', second_port, 0) + b'NONCE-07' + target), server0)
    check(await receive(me, 0.3) is None and await receive(second, 0.3) is None, 'another token cannot use an address proven for this one')

    me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', 80, 0) + b'NONCE-03'), server0)
    check(await receive(me) is None and await receive(listener) is None, 'probing a port below 1024 is refused')
    probe.ALLOW_PRIVATE = False
    me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', port, 0) + b'NONCE-04'), server0)
    check(await receive(me) is None and await receive(listener) is None, 'in production a private or loopback address is never probed')
    probe.ALLOW_PRIVATE = True
    for index in range(12):
        me.sendto(packet(probe.T_PROBE_UDP, good, struct.pack('>HB', port, 0) + b'NONCE-%02d' % (10 + index)), server0)
    sent = 0
    while await receive(listener, 0.3) is not None: sent += 1
    check(sent <= probe.MAX_UDP_PROBES, 'at most %d unsolicited probes per token (%d more seen after the earlier ones)' % (probe.MAX_UDP_PROBES, sent))

    # TCP probe against a listening port
    accepted = []
    async def handle(reader, writer):
        banner = await reader.readexactly(16); accepted.append(banner); writer.write(banner[8:]); await writer.drain(); writer.close()
    tcp = await asyncio.start_server(handle, '127.0.0.1', 0); tcp_port = tcp.sockets[0].getsockname()[1]
    fresh = probe.mint_token(key); me2 = client()
    await prove(me2, fresh, server0)
    me2.sendto(packet(probe.T_PROBE_TCP, fresh, struct.pack('>H', tcp_port) + b'TCPNONCE'), server0)
    result = await receive(me2, 3)
    check(result is not None and result[0][probe.HEADER] == 1 and result[0][probe.HEADER + 1] == 1 and accepted and accepted[0] == b'ARDPTCP1TCPNONCE', 'the TCP probe connects, sends its nonce and reads it back')
    tcp.close()
    me2.sendto(packet(probe.T_PROBE_TCP, fresh, struct.pack('>H', 9) + b'TCPNONCE'), server0)
    check(await receive(me2, 0.5) is None, 'a TCP probe to a port below 1024 is refused')
    closed = socket.socket(); closed.bind(('127.0.0.1', 0)); dead_port = closed.getsockname()[1]; closed.close()
    me2.sendto(packet(probe.T_PROBE_TCP, fresh, struct.pack('>H', dead_port) + b'TCPNONCE'), server0)
    result = await receive(me2, 3)
    check(result is not None and result[0][probe.HEADER] == 0, 'a closed TCP port is reported as not connected')

    for transport in transports: transport.close()

    # ---- limits ----------------------------------------------------------------------------------------------------------
    service, first, transports = await start(key)
    server0 = ('127.0.0.1', first)
    saved = probe.MAX_PACKETS; probe.MAX_PACKETS = 28
    budget_token = probe.mint_token(key); spender = client(0.2); answered = 0
    await prove(spender, budget_token, server0)
    for _ in range(60):
        spender.sendto(packet(probe.T_BIND, budget_token), server0)
        if await receive(spender, 0.05) is not None: answered += 1
    probe.MAX_PACKETS = saved
    check(20 <= answered <= 26, 'a token stops being served after its packet budget (%d answered)' % answered)
    for transport in transports: transport.close()

    service, first, transports = await start(key)
    server0 = ('127.0.0.1', first)
    spammer = client(0.05)
    for _ in range(probe.BAD_LIMIT + 5):
        spammer.sendto(packet(probe.T_BIND, b'\x01' * probe.TOKEN_LEN), server0)
        await asyncio.sleep(0.004)   # the Windows event loop reads one datagram per turn; a tight burst would overrun it
    await asyncio.sleep(0.2)
    spammer.sendto(packet(probe.T_HELLO, probe.mint_token(key)), server0)
    check(await receive(spammer, 0.3) is None and bool(service.banned), 'repeated bad tokens ban the address for a while, even for a good token')
    for transport in transports: transport.close()

    saved = (probe.IP_BURST, probe.IP_PPS); probe.IP_BURST, probe.IP_PPS = 10, 1
    service, first, transports = await start(key)
    server0 = ('127.0.0.1', first); rate_token = probe.mint_token(key); flood = client(0.05); answered = 0
    for _ in range(40):
        flood.sendto(packet(probe.T_HELLO, rate_token), server0)
        if await receive(flood, 0.02) is not None: answered += 1
    probe.IP_BURST, probe.IP_PPS = saved
    check(answered <= 14 and service.stats['rate_limited'] > 0, 'a source address is rate limited (%d of 40 answered)' % answered)
    for transport in transports: transport.close()

    # the number of proven addresses per token is capped
    service, first, transports = await start(key)
    server0 = ('127.0.0.1', first); capped = probe.mint_token(key); proven = 0
    for last in range(2, 2 + probe.MAX_ADDRESSES + 2):
        try: sock = client(host='127.0.0.%d' % last)
        except OSError: continue
        if await prove(sock, capped, server0): proven += 1
    check(proven == probe.MAX_ADDRESSES, 'a token can prove at most %d addresses (%d did)' % (probe.MAX_ADDRESSES, proven))
    for transport in transports: transport.close()


asyncio.run(main())
print('failures:', failures)
sys.exit(1 if failures else 0)
