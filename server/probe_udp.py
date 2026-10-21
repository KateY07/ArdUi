#!/usr/bin/env python3
"""ArdUi network probe service (UDP): the part of the network diagnosis that needs a trusted server (M) to take part.

What it does for an ArdUi client (see docs/probe-protocol.md):
  * tells the client which public address and port it sees (from two server ports, so the client can tell the NAT mapping kind),
  * answers from the other server port (port-restricted or not),
  * echoes packets (loss, round trip, jitter, path MTU) and pushes a measured burst back (downlink),
  * sends an unsolicited packet to a port the client listens on (is a UPnP / NAT-PMP mapping, a public address or an open port really
    reachable from outside?), and tries a TCP connection to a port the client listens on.

Abuse protection (the service sends packets to addresses given by clients, so this matters):
  * a token minted by the directory server (arduiserver) over authenticated HTTPS for a registered device is needed for every packet;
  * the token does not name an address, because a machine behind several internet lines leaves through different addresses. Instead
    every source address has to PROVE itself: HELLO is answered with a cookie (an HMAC over the token, the address and the port, no
    larger than the request), and only a packet that returns the cookie from that same address (AUTH) makes the address usable. A spoofed
    address never sees the cookie. The service therefore only ever sends to addresses that answered a challenge;
  * replies are never larger than the request, except the measured PUSH burst, which is capped per request and per token;
  * per-token budgets (packets, bytes, probes, validated addresses), per-address and global rate limits, a short ban for repeated bad
    tokens, only ports >= 1024, and never private, loopback or reserved addresses.
Standard library only.
"""
import asyncio
import hashlib
import hmac
import ipaddress
import os
import secrets
import socket
import struct
import sys
import tempfile
import time

MAGIC = b'ARDP'
VERSION = 1
HEADER = 48                 # magic 4, version 1, type 1, flags 2, token 28, seq 4, timestamp 8
TOKEN_LEN = 28              # nonce 8, expiry 4 (unix seconds, big endian), mac 16
MIN_REQUEST = 96            # a reply is never longer than its request
MAX_PACKET = 1500
T_BIND, T_ECHO, T_OTHER, T_PUSH, T_PROBE_UDP, T_CONFIRM, T_PROBE_TCP, T_HELLO, T_AUTH = 1, 2, 3, 4, 5, 6, 7, 8, 9
REPLY = 0x80
PUSH_DATA = 0x84
PROBE_PACKET = 0x90

# budgets per token
MAX_PACKETS = 2000
MAX_OUT_BYTES = 4 * 1024 * 1024
MAX_PUSHES, MAX_UDP_PROBES, MAX_TCP_PROBES, MAX_ADDRESSES = 3, 8, 3, 6
PUSH_MAX_COUNT, PUSH_MAX_SIZE, PUSH_MAX_BYTES, PUSH_MIN_INTERVAL = 300, 1400, 300 * 1024, 0.002
ADDRESS_LIFETIME = 180      # seconds an address stays validated after its AUTH
COOKIE_WINDOW = 30
# per source address
IP_PPS, IP_BURST = 300, 600
GLOBAL_PPS = 30000
BAD_LIMIT, BAN_SECONDS = 50, 60
TCP_CONCURRENT = 10

ALLOW_PRIVATE = os.environ.get('ARDUI_PROBE_ALLOW_PRIVATE') == '1'   # tests only (loopback)


def ip16(text):
    address = ipaddress.ip_address(text.split('%')[0])
    if address.version == 6 and address.ipv4_mapped is not None:
        address = address.ipv4_mapped
    return b'\x00' * 10 + b'\xff\xff' + address.packed if address.version == 4 else address.packed


def ip_from16(raw):
    address = ipaddress.IPv6Address(raw)
    return str(address.ipv4_mapped) if address.ipv4_mapped is not None else str(address)


def token_mac(key, nonce, expiry):
    return hmac.new(key, b'ARDP-token2\0' + nonce + expiry, hashlib.sha256).digest()[:16]


def mint_token(key, lifetime=120, now=None):
    nonce = secrets.token_bytes(8)
    expiry = struct.pack('>I', int((now if now is not None else time.time()) + lifetime))
    return nonce + expiry + token_mac(key, nonce, expiry)


def verify_token(key, token, now=None):
    if len(token) != TOKEN_LEN: return False
    nonce, expiry, mac = token[:8], token[8:12], token[12:]
    if struct.unpack('>I', expiry)[0] < (now if now is not None else time.time()): return False
    return hmac.compare_digest(mac, token_mac(key, nonce, expiry))


def cookie_for(key, nonce, source16, port, window):
    return hmac.new(key, b'ARDP-cookie\0' + nonce + source16 + struct.pack('>HI', port, window), hashlib.sha256).digest()[:16]


def load_key(path):
    """The shared secret with arduiserver. Created atomically if neither side has made it yet."""
    folder = os.path.dirname(os.path.abspath(path)) or '.'
    os.makedirs(folder, exist_ok=True)
    if not os.path.exists(path):
        fd, pending = tempfile.mkstemp(prefix='.probe-key-', dir=folder)
        try:
            with os.fdopen(fd, 'wb') as out:
                out.write(secrets.token_bytes(32)); out.flush(); os.fsync(out.fileno())
            try: os.link(pending, path)
            except FileExistsError: pass
        finally: os.unlink(pending)
    with open(path, 'rb') as source:
        return source.read()


def forbidden_target(text):
    if ALLOW_PRIVATE: return False
    address = ipaddress.ip_address(text.split('%')[0])
    if address.version == 6 and address.ipv4_mapped is not None: address = address.ipv4_mapped
    return not address.is_global or address.is_multicast


def header(kind, seq, stamp):
    return MAGIC + bytes([VERSION, kind]) + b'\x00\x00' + b'\x00' * TOKEN_LEN + seq + stamp


class Budget:
    __slots__ = ('packets', 'out_bytes', 'pushes', 'udp_probes', 'tcp_probes', 'pending', 'seen', 'valid')

    def __init__(self):
        self.packets = self.out_bytes = self.pushes = self.udp_probes = self.tcp_probes = 0
        self.pending = {}          # nonce -> time of unsolicited probes waiting for the client's confirmation
        self.valid = {}            # address (16 bytes) -> time until which it counts as proven
        self.seen = time.time()


class Service:
    def __init__(self, key, send, clock=time.time):
        self.key, self.send, self.clock = key, send, clock    # send(index, data, address): index 0 or 1 = which server port
        self.budgets = {}
        self.buckets = {}
        self.bad = {}
        self.banned = {}
        self.global_count, self.global_window = 0, int(clock())
        self.tcp_slots = asyncio.Semaphore(TCP_CONCURRENT)
        self.ports = (0, 0)
        self.stats = dict(packets=0, dropped=0, bad_token=0, unvalidated=0, bad_cookie=0, rate_limited=0, out_bytes=0, pushes=0, udp_probes=0, tcp_probes=0)

    # ---- limits -----------------------------------------------------------------------------------------------------
    def allowed(self, source):
        now = self.clock()
        if self.banned.get(source, 0) > now: return False
        window = int(now)
        if window != self.global_window: self.global_window, self.global_count = window, 0
        self.global_count += 1
        if self.global_count > GLOBAL_PPS: return False
        tokens, last = self.buckets.get(source, (IP_BURST, now))
        tokens = min(IP_BURST, tokens + (now - last) * IP_PPS)
        if tokens < 1: self.buckets[source] = (tokens, now); return False
        self.buckets[source] = (tokens - 1, now)
        return True

    def note_bad(self, source):
        count = self.bad.get(source, 0) + 1
        self.bad[source] = count
        if count >= BAD_LIMIT: self.banned[source] = self.clock() + BAN_SECONDS; self.bad[source] = 0

    def budget_for(self, token):
        nonce = token[:8]
        budget = self.budgets.get(nonce)
        if budget is None:
            if len(self.budgets) > 20000: self.prune()
            budget = self.budgets[nonce] = Budget()
        budget.seen = self.clock()
        return budget

    def prune(self):
        now = self.clock()
        for nonce in [n for n, b in self.budgets.items() if now - b.seen > 300]: del self.budgets[nonce]
        for source in [s for s, (_, last) in self.buckets.items() if now - last > 60]: del self.buckets[source]
        for source in [s for s, until in self.banned.items() if until < now]: del self.banned[source]
        if len(self.budgets) > 20000: self.budgets.clear()

    def proven(self, budget, raw):
        until = budget.valid.get(raw)
        if until is None: return False
        if until < self.clock(): del budget.valid[raw]; return False
        return True

    # ---- one datagram -----------------------------------------------------------------------------------------------
    def packet(self, data, address, index):
        """Handles one datagram that arrived on server port `index`. Returns nothing; replies go through self.send."""
        if len(data) < MIN_REQUEST or len(data) > MAX_PACKET or data[:4] != MAGIC or data[4] != VERSION:
            self.stats['dropped'] += 1; return
        source = address[0].split('%')[0]
        if not self.allowed(source): self.stats['rate_limited'] += 1; return
        token = data[8:8 + TOKEN_LEN]
        if not verify_token(self.key, token, self.clock()):
            self.stats['bad_token'] += 1; self.note_bad(source); return
        budget = self.budget_for(token)
        budget.packets += 1
        if budget.packets > MAX_PACKETS or budget.out_bytes > MAX_OUT_BYTES: return
        kind, seq, stamp, body = data[5], data[36:40], data[40:48], data[HEADER:]
        raw = ip16(source)
        if kind == T_HELLO:
            window = int(self.clock() // COOKIE_WINDOW)
            self.stats['packets'] += 1
            self.reply(budget, REPLY | kind, seq, stamp, cookie_for(self.key, token[:8], raw, address[1], window), index, address, len(data)); return
        if kind == T_AUTH:
            window = int(self.clock() // COOKIE_WINDOW); given = body[:16]
            ok = len(given) == 16 and any(hmac.compare_digest(given, cookie_for(self.key, token[:8], raw, address[1], w)) for w in (window, window - 1))
            if ok and raw not in budget.valid and len(budget.valid) >= MAX_ADDRESSES: ok = False
            if ok: budget.valid[raw] = self.clock() + ADDRESS_LIFETIME
            else: self.stats['bad_cookie'] += 1
            self.stats['packets'] += 1
            self.reply(budget, REPLY | kind, seq, stamp, bytes([1 if ok else 0]) + self.endpoint_body(address), index, address, len(data)); return
        if not self.proven(budget, raw): self.stats['unvalidated'] += 1; return
        self.stats['packets'] += 1
        if kind == T_BIND: self.reply(budget, REPLY | kind, seq, stamp, self.endpoint_body(address), index, address, len(data))
        elif kind == T_ECHO: self.reply(budget, REPLY | kind, seq, stamp, body, index, address, len(data))
        elif kind == T_OTHER: self.reply(budget, REPLY | kind, seq, stamp, self.endpoint_body(address), 1 - index, address, len(data))
        elif kind == T_PUSH: self.push(budget, seq, stamp, body, index, address)
        elif kind == T_PROBE_UDP: self.probe_udp(budget, seq, stamp, body, index, address, source)
        elif kind == T_CONFIRM: self.confirm(budget, seq, stamp, body, index, address, len(data))
        elif kind == T_PROBE_TCP: self.probe_tcp(budget, seq, stamp, body, index, address, source, len(data))

    @staticmethod
    def endpoint_body(address):
        raw = ip16(address[0])
        return bytes([4 if raw[:12] == b'\x00' * 10 + b'\xff\xff' else 6]) + raw + struct.pack('>HH', address[1], 0)

    def reply(self, budget, kind, seq, stamp, body, index, address, request_length):
        data = header(kind, seq, stamp) + body
        if kind == REPLY | T_BIND or kind == REPLY | T_OTHER:   # the port this answer left from
            data = data[:-2] + struct.pack('>H', self.local_port(index))
        if len(data) > request_length: return      # never answer with more than was asked with
        budget.out_bytes += len(data); self.stats['out_bytes'] += len(data)
        self.send(index, data, address)

    def local_port(self, index): return self.ports[index]

    def target(self, budget, body, offset, source):
        """The address an unsolicited probe goes to: the sender's own, or another address of this token that proved itself."""
        raw = body[offset:offset + 16]
        if len(raw) == 16 and raw != b'\x00' * 16:
            if not self.proven(budget, raw): return None
            return ip_from16(raw)
        return source

    # ---- the bigger operations ----------------------------------------------------------------------------------------
    def push(self, budget, seq, stamp, body, index, address):
        if len(body) < 6 or budget.pushes >= MAX_PUSHES: return
        count, size, interval = struct.unpack('>HHH', body[:6])
        size = max(HEADER + 8, min(size, PUSH_MAX_SIZE)); count = min(count, PUSH_MAX_COUNT)
        interval = max(PUSH_MIN_INTERVAL, interval / 1000.0)
        if count == 0 or count * size > PUSH_MAX_BYTES or budget.out_bytes + count * size > MAX_OUT_BYTES: return
        budget.pushes += 1; budget.out_bytes += count * size; self.stats['pushes'] += 1; self.stats['out_bytes'] += count * size
        async def run():
            for number in range(count):
                self.send(index, header(PUSH_DATA, struct.pack('>I', number), stamp) + struct.pack('>HH', count, size) + b'\x00' * (size - HEADER - 4), address)
                await asyncio.sleep(interval)
        asyncio.ensure_future(run())

    def probe_udp(self, budget, seq, stamp, body, index, address, source):
        if len(body) < 11 or budget.udp_probes >= MAX_UDP_PROBES: return
        port, from_other = struct.unpack('>HB', body[:3]); nonce = body[3:11]
        target = self.target(budget, body, 11, source)
        if port < 1024 or target is None or forbidden_target(target): return
        budget.udp_probes += 1; self.stats['udp_probes'] += 1
        budget.pending[nonce] = self.clock()
        packet = header(PROBE_PACKET, seq, stamp) + nonce
        budget.out_bytes += len(packet) + HEADER + 10
        destination = (target, port) if len(address) == 2 else ((target, port, 0, 0) if ':' in target else ('::ffff:' + target, port, 0, 0))   # IPv4 through a dual-stack socket
        self.send(1 - index if from_other else index, packet, destination)
        self.reply(budget, REPLY | T_PROBE_UDP, seq, stamp, nonce + struct.pack('>H', port), index, address, HEADER + 11)

    def confirm(self, budget, seq, stamp, body, index, address, request_length):
        if len(body) < 8: return
        sent = budget.pending.pop(body[:8], None)
        ok = sent is not None and self.clock() - sent < 15
        self.reply(budget, REPLY | T_CONFIRM, seq, stamp, bytes([1 if ok else 0]) + self.endpoint_body(address), index, address, request_length)

    def probe_tcp(self, budget, seq, stamp, body, index, address, source, request_length):
        if len(body) < 10 or budget.tcp_probes >= MAX_TCP_PROBES: return
        port, nonce = struct.unpack('>H', body[:2])[0], body[2:10]
        target = self.target(budget, body, 10, source)
        if port < 1024 or target is None or forbidden_target(target): return
        budget.tcp_probes += 1; self.stats['tcp_probes'] += 1
        async def run():
            connected = acked = 0
            async with self.tcp_slots:
                try:
                    reader, writer = await asyncio.wait_for(asyncio.open_connection(target, port), 3)
                    connected = 1
                    try:
                        writer.write(b'ARDPTCP1' + nonce); await writer.drain()
                        acked = 1 if await asyncio.wait_for(reader.readexactly(8), 2) == nonce else 0
                    except (asyncio.TimeoutError, asyncio.IncompleteReadError, OSError): pass
                    finally: writer.close()
                except (asyncio.TimeoutError, OSError): pass
            self.reply(budget, REPLY | T_PROBE_TCP, seq, stamp, bytes([connected, acked]) + nonce, index, address, request_length)
        asyncio.ensure_future(run())


class Endpoint(asyncio.DatagramProtocol):
    def __init__(self, service, index): self.service, self.index, self.transport = service, index, None
    def connection_made(self, transport): self.transport = transport
    def datagram_received(self, data, address):
        try: self.service.packet(data, address, self.index)
        except Exception as error:   # a malformed packet must never stop the service
            print('probe: error handling a packet:', error, file=sys.stderr, flush=True)


async def serve(key_path, first_port, bind='::'):
    loop = asyncio.get_running_loop()
    transports = []
    service = Service(load_key(key_path), lambda index, data, address: transports[index].sendto(data, address))
    for index in (0, 1):
        sock = socket.socket(socket.AF_INET6 if ':' in bind else socket.AF_INET, socket.SOCK_DGRAM)
        if ':' in bind: sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        sock.bind((bind, first_port + index))
        transport, _ = await loop.create_datagram_endpoint(lambda i=index: Endpoint(service, i), sock=sock)
        transports.append(transport)
    service.ports = (first_port, first_port + 1)
    print(f'probe: listening on UDP {first_port} and {first_port + 1}', flush=True)
    while True:
        await asyncio.sleep(60)
        service.prune()
        print('probe: ' + ' '.join(f'{k}={v}' for k, v in service.stats.items()), flush=True)


if __name__ == '__main__':
    path = os.environ.get('ARDUI_PROBE_KEY') or os.environ.get('ARDUI_DATABASE', '/var/lib/arduiserver/devices.sqlite3') + '.probe-key'
    try: asyncio.run(serve(path, int(os.environ.get('ARDUI_PROBE_PORT', '7843')), os.environ.get('ARDUI_PROBE_BIND', '::')))
    except KeyboardInterrupt: pass
