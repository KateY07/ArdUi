"""ArdUi console prototype. Python 3.11+, cryptography, and the existing ARD binary.
No TPM, virtual adapter, elevation, or GUI. See PROTOTYPE.md.
"""
import argparse
import asyncio
import base64
import contextlib
import getpass
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import socket
import statistics
import struct
import subprocess
import tempfile
import time
import urllib.request
import uuid
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey

DEFAULT_RELAY_KEY = 'spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f'


def encode(value):
    return json.dumps(value, separators=(',', ':')).encode()


def identity(root, create=False):
    root.mkdir(parents=True, exist_ok=True)
    path = root / 'identity'
    if create and not path.exists():
        # Exclusive create: an existing identity is never overwritten.
        try:
            with path.open('xb') as file:
                file.write(secrets.token_bytes(32)); file.flush(); os.fsync(file.fileno())
        except FileExistsError:
            pass
    seed = path.read_bytes()
    if len(seed) != 32:
        raise ValueError('Existing identity is invalid; left unchanged.')
    return Ed25519PrivateKey.from_private_bytes(seed)


def signed_bytes(route, value):
    return f"ArdUiServer/1\n{route}\n{value['endpoint']}\n{value['issuedAt']}\n{value['nonce']}\n{value['payload']}".encode()


def verify(envelope, route, endpoint, expiring=True):
    if envelope['endpoint'] != endpoint or (expiring and abs(time.time()-envelope['issuedAt']) > 360):
        raise ValueError('Identity mismatch or expired signature.')
    Ed25519PublicKey.from_public_bytes(bytes.fromhex(endpoint)).verify(
        base64.b64decode(envelope['signature'], validate=True), signed_bytes(route, envelope))
    return json.loads(base64.b64decode(envelope['payload'], validate=True))


async def read_json(reader):
    size, = struct.unpack('!I', await reader.readexactly(4))
    if not 0 < size <= 8192:
        raise ValueError('Invalid handshake size.')
    return json.loads(await reader.readexactly(size))


async def write_json(writer, value):
    data = encode(value)
    if len(data) > 8192:
        raise ValueError('Handshake too large.')
    writer.write(struct.pack('!I', len(data))+data); await writer.drain()


async def bridge(left, right):
    async def copy(reader, writer):
        while data := await reader.read(65536):
            writer.write(data); await writer.drain()
        if writer.can_write_eof():
            writer.write_eof(); await writer.drain()
    try:
        async with asyncio.TaskGroup() as group:
            group.create_task(copy(left[0], right[1]))
            group.create_task(copy(right[0], left[1]))
    finally:
        left[1].close(); right[1].close()


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); return sock.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('Directory redirect refused.')


class Session:
    def __init__(self, node, peer):
        self.node, self.peer = node, peer
        self.folder = tempfile.TemporaryDirectory(prefix='session-', dir=node.root)
        self.key = identity(Path(self.folder.name), True)
        self.endpoint = self.key.public_key().public_bytes_raw().hex()
        self.process = None
        self.listeners, self.datagrams, self.tasks = [], [], set()
        self.token, self.port, self.ports, self.udp_ports = b'', 0, [], []
        self.udp_targets, self.udp_flow_lock = {}, asyncio.Lock()
        self.network_type, self.tcp_rtt_ms, self.udp_rtt_ms = '建立中', None, None
        self.udp_socket = None
        self.probe_lock = asyncio.Lock()
        self.closed = False
        self.admitted = False

    def spawn(self, coroutine):
        task = asyncio.create_task(coroutine); self.tasks.add(task)
        task.add_done_callback(self.tasks.discard)
        return task

    def network_log(self, line):
        text = line.decode('utf-8', errors='replace').strip()
        transport=re.search(r'transport="?([^"\s]+)',text)
        network=re.search(r'network="?([^"\s]+)',text)
        if transport:
            if transport.group(1)=='relay': kind='ArdRelay 中继'
            elif transport.group(1)=='direct':
                family={'ipv4':'IPv4','ipv6':'IPv6'}.get(network.group(1) if network else '', '未知网络')
                kind='P2P 直连 / '+family
            else: kind=transport.group(1)
            self.network_type=kind
            event='路径已断开' if 'closed' in text else ('路径已切换' if 'selected' in text else '已连接')
            print(f'\n[ARD 网络] {event}：{kind} | {text}',flush=True)
        elif 'relay online' in text or 'READY:' in text:
            print('\n[ARD 网络] 转发入口已就绪 | '+text,flush=True)
        elif 'Connection closed' in text:
            print('\n[ARD 网络] 连接已断开 | '+text,flush=True)

    async def start(self, mode, port, peer):
        env = dict(os.environ); env.pop('RUST_LOG', None); env['NO_COLOR'] = '1'
        self.process = await asyncio.create_subprocess_exec(str(self.node.ard), mode, str(port), 'to', peer,
            '--relay', self.node.relay, '--relay-key', self.node.relay_key,
            cwd=self.folder.name, env=env,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        ready = b'relay online' if mode == 'open' else b'READY:'
        async with asyncio.timeout(75):
            while line := await self.process.stdout.readline():
                self.network_log(line)
                if ready in line:
                    self.spawn(self.drain()); return
            raise RuntimeError('ARD exited before connection was ready.')

    async def drain(self):
        while line := await self.process.stdout.readline():
            self.network_log(line)
        await self.process.wait()
        if not self.closed:
            print(f'\n[ARD 网络] 会话已断开，退出码 {self.process.returncode}；正在自动重连。', flush=True)

    async def open(self, target):
        if target not in self.ports:
            raise ValueError('Port not authorized.')
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        writer.write(b'AUI1'+self.token+b'\x01'+struct.pack('!H', target)); await writer.drain()
        if await reader.readexactly(1) != b'\0':
            writer.close(); raise ConnectionError('Remote service is not listening.')
        return reader, writer

    async def tcp_ping(self):
        nonce=secrets.token_bytes(2); began=time.perf_counter_ns()
        reader,writer=await asyncio.open_connection('127.0.0.1',self.port)
        try:
            writer.write(b'AUI1'+self.token+b'\x03'+nonce); await writer.drain()
            if await asyncio.wait_for(reader.readexactly(3),3)!=b'\0'+nonce:
                raise ConnectionError('TCP latency probe was rejected.')
            return (time.perf_counter_ns()-began)/1e6
        finally:
            writer.close(); await writer.wait_closed()

    async def udp_ping(self):
        if self.udp_socket is None:
            self.udp_socket=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
            self.udp_socket.setblocking(False); self.udp_socket.bind(('127.0.0.1',0))
        nonce=secrets.token_bytes(8); request=b'AUI1'+self.token+b'\x03'+nonce
        loop=asyncio.get_running_loop(); began=time.perf_counter_ns()
        await loop.sock_sendto(self.udp_socket,request,('127.0.0.1',self.port))
        async with asyncio.timeout(3):
            while True:
                reply,_=await loop.sock_recvfrom(self.udp_socket,64)
                if reply==b'AUP1'+nonce:
                    return (time.perf_counter_ns()-began)/1e6

    async def measure_latency(self):
        async with self.probe_lock:
            tcp=[]; udp=[]
            for _ in range(3):
                tcp.append(await self.tcp_ping())
                try: udp.append(await self.udp_ping())
                except (OSError,TimeoutError): pass
            self.tcp_rtt_ms=statistics.median(tcp)
            self.udp_rtt_ms=statistics.median(udp) if udp else None
            udp_text=f'{self.udp_rtt_ms:.2f} ms' if self.udp_rtt_ms is not None else '不可用'
            print(f'\n[ARD 延迟] {self.network_type} | TCP/PsPing {self.tcp_rtt_ms:.2f} ms | UDP 往返 {udp_text}',flush=True)

    async def monitor_latency(self):
        while not self.closed:
            try: await self.measure_latency()
            except (OSError,EOFError,ConnectionError,TimeoutError): pass
            await asyncio.sleep(10)

    async def close(self):
        if self.closed:
            return
        self.closed = True
        for listener in self.listeners:
            listener.close(); await listener.wait_closed()
        for transport in self.datagrams: transport.close()
        if self.udp_socket: self.udp_socket.close()
        for udp, _ in self.udp_targets.values(): udp.close()
        self.udp_targets.clear()
        if self.process and self.process.returncode is None:
            self.process.kill(); await self.process.wait()
        tasks = list(self.tasks)
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self.folder.cleanup()


class Forward:
    def __init__(self,node,code,peer,target,address):
        self.node,self.code,self.peer,self.target,self.address=node,code,peer,target,address
        self.listener=None
        self.datagram=None
        self.udp_flows={}
        self.udp_lock=asyncio.Lock()
        self.tasks=set()
        self.mappings=[]
        self.closed=False

    def spawn(self,coroutine):
        task=asyncio.create_task(coroutine); self.tasks.add(task)
        task.add_done_callback(self.tasks.discard)
        return task

    async def start(self):
        async def serve(reader,writer):
            deadline=time.monotonic()+150
            try:
                while not self.closed and self.code in self.node.desired:
                    session=self.node.outgoing.get(self.peer)
                    if session and not session.closed and session.process and session.process.returncode is None:
                        try:
                            await bridge((reader,writer),await session.open(self.target)); return
                        except (OSError,EOFError,ConnectionError):
                            pass
                    if time.monotonic()>=deadline:
                        break
                    await asyncio.sleep(.25)
            except (ValueError,ExceptionGroup):
                pass
            finally:
                writer.close()
        bind='127.0.0.1' if self.target==445 else self.address
        self.listener=await asyncio.start_server(lambda r,w:self.spawn(serve(r,w)),bind,0)
        session=self.node.outgoing.get(self.peer)
        if session and self.target in session.udp_ports:
            class LocalUdp(asyncio.DatagramProtocol):
                def connection_made(protocol_self,transport): self.datagram=protocol_self.transport=transport
                def datagram_received(protocol_self,data,address): self.spawn(self.send_udp(address,data))
            await asyncio.get_running_loop().create_datagram_endpoint(
                LocalUdp,local_addr=(bind,self.listener.sockets[0].getsockname()[1]))
        return self

    async def send_udp(self,address,payload):
        if not payload or len(payload)>1500 or self.closed: return
        session=self.node.outgoing.get(self.peer)
        if not session or session.closed or self.target not in session.udp_ports: return
        async with self.udp_lock:
            flow=self.udp_flows.get(address)
            if flow is None:
                if len(self.udp_flows)>=64: return
                udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); udp.setblocking(False)
                udp.bind(('127.0.0.1',0)); udp.connect(('127.0.0.1',session.port))
                async def replies():
                    try:
                        while not self.closed:
                            reply=await asyncio.get_running_loop().sock_recv(udp,1501)
                            if reply and len(reply)<=1500 and self.datagram: self.datagram.sendto(reply,address)
                    except (OSError,asyncio.CancelledError): pass
                flow=[udp,session.port,self.spawn(replies())]; self.udp_flows[address]=flow
            elif flow[1]!=session.port:
                flow[0].connect(('127.0.0.1',session.port)); flow[1]=session.port
        await asyncio.get_running_loop().sock_sendall(flow[0],payload)

    @property
    def port(self):
        return self.listener.sockets[0].getsockname()[1]

    async def close(self):
        if self.closed:
            return
        self.closed=True
        for drive,remote in self.mappings:
            with contextlib.suppress(Exception):
                await powershell("$m=Get-SmbMapping -LocalPath $d.drive -ErrorAction SilentlyContinue; if($m -and $m.RemotePath -eq $d.remote){Remove-SmbMapping -LocalPath $d.drive -Force -Confirm:$false}",dict(drive=drive,remote=remote))
        if self.listener:
            self.listener.close(); await self.listener.wait_closed()
        if self.datagram: self.datagram.close()
        for udp,_,_ in self.udp_flows.values(): udp.close()
        self.udp_flows.clear()
        for task in list(self.tasks): task.cancel()
        await asyncio.gather(*self.tasks,return_exceptions=True)


class Node:
    def __init__(self, root, ard, server='https://f.visnova.cn', relay='http://175.27.160.144:8080', relay_key=DEFAULT_RELAY_KEY):
        self.root, self.ard, self.server, self.relay = Path(root).resolve(), Path(ard).resolve(), server.rstrip('/'), relay
        self.relay_key = relay_key
        if not self.server.startswith('https://'):
            raise ValueError('HTTPS directory required.')
        if not self.relay_key.startswith('spki:') or len(self.relay_key) <= 5:
            raise ValueError('ArdRelay public key is invalid.')
        self.key = identity(self.root)
        self.endpoint = self.key.public_key().public_bytes_raw().hex()
        path = self.root/'state.json'
        self.state = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {'acl': {}, 'peers': {}, 'pins': {}, 'controllers': {}, 'enabled': False}
        if not isinstance(self.state,dict) or any(not isinstance(self.state.get(name),dict) for name in ('acl','peers','pins')):
            raise ValueError('state.json is invalid; restore state.json.bak or remove the corrupt state file.')
        self.state.setdefault('controllers',{})
        if not isinstance(self.state['controllers'],dict):
            raise ValueError('state.json controllers are invalid; restore state.json.bak.')
        if type(self.state.setdefault('enabled',False)) is not bool:
            raise ValueError('state.json enabled setting is invalid; restore state.json.bak.')
        self.code, self.enabled, self.password = '', self.state['enabled'], None
        if 'password' in self.state:
            self.password = tuple(bytes.fromhex(part) for part in self.state['password'])
        if self.enabled and self.password is None:
            raise ValueError('state.json enables host access without a password.')
        self.incoming, self.outgoing, self.forwards, self.jobs, self.seen = {}, {}, {}, set(), {}
        self.allowed_udp_ports=[]
        self.desired, self.supervisors, self.connect_locks = set(), {}, {}
        self.confirm = None
        self.attempts = []
        self.control = asyncio.Lock()

    def save(self):
        path = self.root/'state.json'
        temporary = path.with_suffix('.tmp')
        with temporary.open('wb') as file:
            file.write(encode(self.state)); file.flush(); os.fsync(file.fileno())
        if path.exists():
            path.with_suffix('.json.bak').write_bytes(path.read_bytes())
        temporary.replace(path)

    def sign(self, route, data):
        envelope = dict(endpoint=self.endpoint, issuedAt=int(time.time()), nonce=uuid.uuid4().hex,
                        payload=base64.b64encode(encode(data)).decode())
        envelope['signature'] = base64.b64encode(self.key.sign(signed_bytes(route, envelope))).decode()
        return envelope

    async def call(self, route, data):
        envelope = self.sign(route, data)
        def send():
            request = urllib.request.Request(self.server+route, data=encode(envelope), headers={'Content-Type':'application/json'})
            with urllib.request.build_opener(NoRedirect).open(request, timeout=20) as response:
                raw = response.read(65537)
                if len(raw) > 65536:
                    raise ValueError('Directory response too large.')
                return json.loads(raw)
        return await asyncio.to_thread(send)

    async def register(self):
        result = await self.call('/api/v1/register', {})
        if result['endpoint'] != self.endpoint or (self.state.get('code') and self.state['code'] != result['code']):
            raise ValueError('Machine identity changed.')
        self.code = self.state['code'] = result['code']; self.save()
        await self.call('/api/v1/access', {'enabled': self.enabled})

    async def access(self, enabled, password=None):
        async with self.control:
            await self.set_access(enabled,password)

    async def set_access(self, enabled, password=None):
        self.enabled = False
        for session in list(self.incoming.values()):
            await session.close()
        self.incoming.clear()
        if password is not None:
            if len(password) < 8:
                raise ValueError('Use at least 8 password characters.')
            salt = secrets.token_bytes(16)
            self.password = (salt, hashlib.pbkdf2_hmac('sha256', password.encode(), salt, 600000))
            self.state['password'] = [part.hex() for part in self.password]
            self.state['acl'].clear(); self.state['controllers'].clear(); self.save()
        if enabled and self.password is None:
            raise ValueError('Set a password before enabling host access.')
        self.state['enabled']=bool(enabled); self.save()
        await self.call('/api/v1/access', {'enabled': enabled}); self.enabled = enabled

    async def poll(self):
        while True:
            try:
                async with self.control:
                    reply = await self.call('/api/v1/poll', {'enabled': self.enabled})
                now=time.time(); self.seen={key:expires for key,expires in self.seen.items() if expires>=now}
                for ticket in reply['tickets']:
                    if ticket['id'] in self.seen:
                        continue
                    self.seen[ticket['id']]=ticket['expires']
                    task = asyncio.create_task(self.accept(ticket)); self.jobs.add(task)
                    task.add_done_callback(self.jobs.discard)
            except Exception as error:
                print('Directory:', type(error).__name__, flush=True)
            await asyncio.sleep(2)

    async def accept(self, ticket):
        session = None
        try:
            peer = ticket['controllerEndpoint']
            proof = verify(ticket['proof'], '/api/v1/connect', peer)
            expected = dict(code=self.code, targetEndpoint=self.endpoint, sessionId=ticket['clientSessionId'],
                            requestId=ticket['id'], expires=ticket['expires'])
            if proof != expected or not self.enabled or ticket['expires'] < time.time():
                raise ValueError('Invalid signed request.')
            if peer in self.incoming:
                await self.incoming.pop(peer).close()
            session = Session(self, peer); self.incoming[peer] = session
            session.token = secrets.token_bytes(16); session.ports = self.allowed_ports
            session.udp_ports = self.allowed_udp_ports
            async def route_udp(source,payload,outer):
                if not payload or len(payload)>1500 or not session.udp_ports: return
                async with session.udp_flow_lock:
                    flow=session.udp_targets.get(source)
                    if flow is None:
                        if len(session.udp_targets)>=64: return
                        udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); udp.setblocking(False)
                        udp.bind(('127.0.0.1',0)); udp.connect(('127.0.0.1',session.udp_ports[0]))
                        async def replies():
                            try:
                                while not session.closed:
                                    reply=await asyncio.get_running_loop().sock_recv(udp,1501)
                                    if reply and len(reply)<=1500: outer.sendto(reply,source)
                            except (OSError,asyncio.CancelledError): pass
                        flow=(udp,session.spawn(replies())); session.udp_targets[source]=flow
                await asyncio.get_running_loop().sock_sendall(flow[0],payload)
            async def serve(reader, writer):
                try:
                    async with asyncio.timeout(300):
                        header = await reader.readexactly(23)
                        if header[:4] != b'AUI1' or not self.enabled:
                            return
                        command, port = header[20], int.from_bytes(header[21:], 'big')
                        if command == 2:
                            request = await read_json(reader)
                            grant = self.state['acl'].get(peer)
                            if request['enroll']:
                                self.attempts = [t for t in self.attempts if time.time()-t < 60]
                                valid = False
                                if len(self.attempts) < 10 and isinstance(request['password'],str) and len(request['password']) <= 128:
                                    self.attempts.append(time.time())
                                    salt, expected_hash = self.password
                                    actual = await asyncio.to_thread(hashlib.pbkdf2_hmac,'sha256',request['password'].encode(),salt,600000)
                                    valid = hmac.compare_digest(actual, expected_hash)
                                if not valid:
                                    grant = None
                                elif not grant and self.confirm and await self.confirm(ticket['controllerCode'],peer,True):
                                    if self.enabled and not session.closed:
                                        grant = self.sign('/grant/v1',dict(controllerEndpoint=peer,targetEndpoint=self.endpoint,grantId=uuid.uuid4().hex))
                                        self.state['acl'][peer] = grant; self.state['controllers'][peer]=ticket['controllerCode']; self.save()
                            if not self.enabled or session.closed:
                                grant = None
                            session.admitted=bool(grant)
                            await write_json(writer, dict(accepted=bool(grant), error=None if grant else 'Access denied',
                                token=session.token.hex() if grant else None, tcpPorts=session.ports if grant else [],
                                udpPorts=session.udp_ports if grant else [], grant=grant))
                            return
                        if peer not in self.state['acl'] or not hmac.compare_digest(header[4:20],session.token):
                            return
                        if command == 3:
                            writer.write(b'\0'+header[21:23]); await writer.drain(); return
                        if command != 1 or port not in session.ports:
                            return
                        try:
                            target = await asyncio.open_connection('127.0.0.1',port)
                        except OSError:
                            writer.write(b'\x01'); await writer.drain(); return
                        writer.write(b'\0'); await writer.drain()
                    await bridge((reader,writer),target)
                except (OSError,EOFError,ValueError,TimeoutError,ExceptionGroup):
                    pass
                finally:
                    writer.close()
            listener = await asyncio.start_server(lambda r,w: session.spawn(serve(r,w)), '127.0.0.1',0)
            session.listeners.append(listener)
            class PingProtocol(asyncio.DatagramProtocol):
                def connection_made(self,transport): self.transport=transport
                def datagram_received(self,data,address):
                    if (len(data)==29 and data[:4]==b'AUI1' and data[20]==3 and session.admitted
                            and peer in self_node.state['acl'] and hmac.compare_digest(data[4:20],session.token)):
                        self.transport.sendto(b'AUP1'+data[21:],address)
                    elif session.admitted and peer in self_node.state['acl']:
                        session.spawn(route_udp(address,data,self.transport))
            self_node=self
            transport,_=await asyncio.get_running_loop().create_datagram_endpoint(
                PingProtocol,local_addr=('127.0.0.1',listener.sockets[0].getsockname()[1]))
            session.datagrams.append(transport)
            await session.start('open',listener.sockets[0].getsockname()[1],ticket['clientSessionId'])
            await self.call('/api/v1/tickets/'+ticket['id']+'/ready',dict(sessionId=session.endpoint,requestId=ticket['id'],
                controllerEndpoint=peer,targetEndpoint=self.endpoint,clientSessionId=ticket['clientSessionId'],expires=ticket['expires']))
            task=asyncio.create_task(self.expire_incoming(peer,session,ticket['expires'])); self.jobs.add(task)
            task.add_done_callback(self.jobs.discard)
        except Exception as error:
            print('Incoming:',type(error).__name__, flush=True)
            if session:
                await session.close()
            with contextlib.suppress(Exception):
                await self.call('/api/v1/tickets/'+ticket['id']+'/reject',{})

    async def expire_incoming(self,peer,session,expires):
        while session.process and session.process.returncode is None:
            if not session.admitted and time.time()>=expires:
                break
            await asyncio.sleep(.5)
        if self.incoming.get(peer) is session:
            self.incoming.pop(peer,None); await session.close()

    async def connect(self, code, password=None, supervise=True):
        code=code.upper()
        if supervise:
            self.desired.add(code)
        lock=self.connect_locks.setdefault(code,asyncio.Lock())
        async with lock:
            session=await self.connect_once(code,password)
        if supervise and code not in self.supervisors:
            task=asyncio.create_task(self.supervise(code,session.peer)); self.supervisors[code]=task
            task.add_done_callback(lambda done,c=code:self.supervisors.pop(c,None))
        return session

    async def connect_once(self, code, password=None):
        target = await self.call('/api/v1/lookup', {'code': code})
        peer = target['endpoint']; pinned = self.state['pins'].get(code)
        if pinned and pinned != peer:
            raise ValueError('PINNED FINGERPRINT CHANGED; connection blocked.')
        if not pinned:
            if not self.confirm or not await self.confirm(code,peer,False):
                raise PermissionError('Fingerprint not confirmed.')
            self.state['pins'][code] = peer; self.save()
        if peer in self.outgoing:
            await self.outgoing.pop(peer).close()
        session = Session(self,peer)
        try:
            ticket = uuid.uuid4().hex
            request = dict(code=code,targetEndpoint=peer,sessionId=session.endpoint,requestId=ticket,expires=int(time.time())+300)
            await self.call('/api/v1/connect', request)
            async with asyncio.timeout(150):
                while True:
                    await asyncio.sleep(1)
                    result = await self.call('/api/v1/tickets/'+ticket,{})
                    if result['status']=='rejected':
                        raise PermissionError('Host rejected request.')
                    if result['status']=='ready':
                        break
                offer = verify(result['offer'],'/api/v1/tickets/'+ticket+'/ready',peer)
                expected = dict(sessionId=offer['sessionId'],requestId=ticket,controllerEndpoint=self.endpoint,
                    targetEndpoint=peer,clientSessionId=session.endpoint,expires=request['expires'])
                if offer != expected:
                    raise ValueError('Session binding mismatch.')
                session.port = free_port(); await session.start('forward',session.port,offer['sessionId'])
                reader,writer = await asyncio.open_connection('127.0.0.1',session.port)
                try:
                    writer.write(b'AUI1'+bytes(16)+b'\x02\0\0'); await writer.drain()
                    await write_json(writer,dict(enroll=password is not None,password=password or ''))
                    reply = await read_json(reader)
                finally:
                    writer.close()
                if not reply['accepted']:
                    raise PermissionError('Password incorrect, consent declined, or authorization revoked.')
                grant = verify(reply['grant'],'/grant/v1',peer,False)
                if grant['controllerEndpoint']!=self.endpoint or grant['targetEndpoint']!=peer:
                    raise ValueError('Grant identity mismatch.')
                session.token = bytes.fromhex(reply['token']); session.ports = reply['tcpPorts']; session.udp_ports=reply['udpPorts']
                session.spawn(session.monitor_latency())
                addresses={p.get('address') for p in self.state['peers'].values()}
                address=self.state['peers'].get(code,{}).get('address') or next(
                    f'127.77.{i//250}.{i%250+1}' for i in range(64000) if f'127.77.{i//250}.{i%250+1}' not in addresses)
                self.state['peers'][code] = dict(endpoint=peer,grant=reply['grant'],address=address); self.save()
                self.outgoing[peer] = session
                return session
        except BaseException:
            await session.close(); raise

    async def supervise(self, code, peer):
        delay=1
        while code in self.desired:
            session=self.outgoing.get(peer)
            if session is None:
                return
            await session.process.wait()
            if code not in self.desired:
                return
            # ARD deliberately exits when its Iroh Connection closes. The upper
            # layer creates a fresh signed session; the permanent ACL avoids a
            # repeated password or consent prompt.
            while code in self.desired:
                lock=self.connect_locks.setdefault(code,asyncio.Lock())
                try:
                    async with lock:
                        current=self.outgoing.get(peer)
                        if current is not session:
                            session=current
                            break
                        await session.close(); self.outgoing.pop(peer,None)
                        replacement=await self.connect_once(code,None)
                    print(f'\nRECONNECTED: {code}',flush=True)
                    session=replacement; delay=1; break
                except (PermissionError,ValueError):
                    self.desired.discard(code); print(f'\nRECONNECT STOPPED: {code} authorization changed',flush=True); return
                except urllib.error.HTTPError as error:
                    if error.code in (403,404,410):
                        self.desired.discard(code); print(f'\nRECONNECT STOPPED: {code} host unavailable',flush=True); return
                    await asyncio.sleep(delay); delay=min(delay*2,30)
                except Exception:
                    await asyncio.sleep(delay); delay=min(delay*2,30)

    async def forward(self,code,target):
        code=code.upper(); record=self.state['peers'].get(code)
        if not record or record['endpoint'] not in self.outgoing:
            raise ValueError('Run connect CODE first.')
        key=(code,target)
        if key not in self.forwards:
            forward=Forward(self,code,record['endpoint'],target,record['address'])
            self.forwards[key]=await forward.start()
        return self.forwards[key]

    async def close_forwards(self,code):
        selected=[key for key in self.forwards if key[0]==code]
        for key in selected:
            await self.forwards.pop(key).close()

    async def disconnect(self,code):
        code=code.upper(); self.desired.discard(code)
        await self.close_forwards(code)
        record=self.state['peers'].get(code)
        if record and (session:=self.outgoing.pop(record['endpoint'],None)):
            await session.close()

    async def revoke(self, peer):
        self.state['acl'].pop(peer,None); self.state['controllers'].pop(peer,None); self.save()
        if peer in self.incoming:
            await self.incoming.pop(peer).close()

    async def close(self):
        self.desired.clear()
        for task in self.supervisors.values(): task.cancel()
        await asyncio.gather(*self.supervisors.values(),return_exceptions=True)
        for task in list(self.jobs):
            task.cancel()
        await asyncio.gather(*self.jobs, return_exceptions=True)
        for forward in list(self.forwards.values()):
            await forward.close()
        self.forwards.clear()
        for session in list(self.incoming.values())+list(self.outgoing.values()):
            await session.close()
        with contextlib.suppress(Exception):
            await self.call('/api/v1/offline', {})


async def powershell(script, data):
    if os.name!='nt':
        raise OSError('Native Windows action requires Windows.')
    # Credentials travel only on standard input, never in process arguments.
    prefix="$ErrorActionPreference='Stop'; $d=[Console]::In.ReadToEnd() | ConvertFrom-Json; "
    command=base64.b64encode((prefix+script).encode('utf-16le')).decode()
    process=await asyncio.create_subprocess_exec('powershell.exe','-NoProfile','-NonInteractive','-EncodedCommand',command,
        stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,
        creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        output,error=await asyncio.wait_for(process.communicate(encode(data)),40)
    except BaseException:
        if process.returncode is None: process.kill(); await process.wait()
        raise
    if process.returncode:
        detail=error.decode(errors='replace').strip().splitlines()
        raise OSError('Windows SMB mapping failed: '+(detail[-1] if detail else 'unknown Windows error'))
    return output.decode().strip()


async def console(args):
    node = Node(args.data,args.ard,args.server,args.relay,args.relay_key); node.allowed_ports=[3389,445]; node.allowed_udp_ports=[3389]
    await node.register()
    approvals, background = {}, set()

    async def trust_pending():
        pending=next(iter(approvals.items()),None)
        if pending is None:
            print('\n目前没有待确认申请。',flush=True); return
        token,(future,code,endpoint)=pending
        print(f'\n已信任申请设备：{code} | EndpointId: {endpoint}',flush=True)
        if not future.done(): future.set_result(True)

    async def ask(prompt):
        while True:
            answer=(await asyncio.to_thread(input,prompt)).strip()
            if answer.upper()=='T':
                await trust_pending(); continue
            return answer

    async def confirm(code, endpoint, incoming):
        if not incoming:
            print(f'\n核对远程设备 {code} | EndpointId: {endpoint}')
            answer=await ask('已通过独立渠道核对一致？[y/N] ')
            return answer.strip().lower()=='y'
        token=secrets.token_hex(3); future=asyncio.get_running_loop().create_future(); approvals[token]=(future,code,endpoint)
        print(f'\n收到被控申请：{code} | EndpointId: {endpoint} | 在任意菜单按 T 后回车即可信任。',flush=True)
        try:
            return await asyncio.wait_for(future,180)
        finally:
            approvals.pop(token,None)
    node.confirm=confirm
    poll=asyncio.create_task(node.poll())
    async def connect(code,password):
        try:
            await node.connect(code,password); print('\n已授权并连接：',code,flush=True)
        except Exception as error:
            print('\n连接失败：',str(error),flush=True)

    def show():
        print('\n'+'='*66+'\nArdUi v1.pre7 | 被控：'+('允许' if node.enabled else '关闭')+f' | 正在被控：{sum(s.admitted and not s.closed for s in node.incoming.values())} 台')
        if node.enabled:
            print('设备 ID：'+node.code+' | EndpointId：'+node.endpoint)
            active=[(node.state['controllers'].get(peer,'未知设备'),peer) for peer,session in node.incoming.items() if session.admitted and not session.closed]
            if active:
                print('正在访问本机：'+'；'.join(f'{code} | {endpoint} | {node.incoming[endpoint].network_type}' for code,endpoint in active))
        print('已授权设备（本机可主动访问）：')
        peers=list(node.state['peers'].items())
        if not peers:
            print('  （暂无）')
        for index,(code,record) in enumerate(peers,1):
            session=node.outgoing.get(record['endpoint'])
            status='已连接' if session and not session.closed else ('重连中' if code in node.desired else '离线')
            detail=''
            if session and not session.closed:
                tcp=f'{session.tcp_rtt_ms:.2f} ms' if session.tcp_rtt_ms is not None else '测量中'
                udp=f'{session.udp_rtt_ms:.2f} ms' if session.udp_rtt_ms is not None else '测量中'
                detail=f' | {session.network_type} | TCP/PsPing {tcp} | UDP {udp}'
            print(f'  [{index}] {code}  {status}{detail}')
        settings='被控设置'+(f'（{len(approvals)} 个待确认）' if approvals else '')
        options='[A] 添加设备  [B] '+(settings if node.enabled else '开启被控')
        print(options+('  [T] 信任待确认' if approvals else '')+'  [Q] 退出')
        return peers

    async def open_device(code,record):
        while True:
            session=node.outgoing.get(record['endpoint'])
            status='已连接' if session and not session.closed else ('重连中' if code in node.desired else '离线')
            diagnostics=''
            if session and not session.closed:
                tcp=f'{session.tcp_rtt_ms:.2f} ms' if session.tcp_rtt_ms is not None else '测量中'
                udp=f'{session.udp_rtt_ms:.2f} ms' if session.udp_rtt_ms is not None else '测量中'
                diagnostics=f' | {session.network_type} | TCP/PsPing {tcp} | UDP {udp}'
            print(f'\n{code} | {status}{diagnostics} | EndpointId: {record["endpoint"]}')
            print('[1] 远程桌面  [2] 文件共享  [0] 返回')
            choice=await ask('选择：')
            if choice=='0': return
            if choice not in ('1','2'):
                print('无效选择。'); continue
            if record['endpoint'] not in node.outgoing:
                print('正在连接…'); await node.connect(code)
            forward=await node.forward(code,3389 if choice=='1' else 445)
            if choice=='1':
                subprocess.Popen(['mstsc',f'/v:{forward.address}:{forward.port}']); print('已打开远程桌面。')
                continue
            share=await ask('共享名：')
            if not share or any(c in share for c in '\\/\0'):
                raise ValueError('共享名不能包含路径。')
            user=await ask('Windows 用户（留空使用当前账户）：')
            password=await asyncio.to_thread(getpass.getpass,'Windows 密码：') if user else ''
            remote=f'\\\\localhost\\{share}'
            script="""$drive=90..68 | ForEach-Object { [char]$_ } | Where-Object { -not (Get-PSDrive -Name $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1;
if(-not $drive){throw 'No drive letter available'}; $local=([string]$drive)+':';
$p=@{LocalPath=$local; RemotePath=$d.remote; TcpPort=[int]$d.port; Persistent=$false};
if($d.user){$p.Credential=[pscredential]::new($d.user,(ConvertTo-SecureString $d.password -AsPlainText -Force))};
New-SmbMapping @p | Out-Null; [Console]::Write($local)"""
            drive=await powershell(script,dict(remote=remote,port=forward.port,user=user,password=password))
            forward.mappings.append((drive,remote)); subprocess.Popen(['explorer.exe',drive+'\\'])
            print('已打开文件共享：'+drive)

    async def host_settings():
        if not node.enabled:
            password=await asyncio.to_thread(getpass.getpass,'设置访问密码（至少 8 位）：')
            await node.access(True,password); print('已开启被控。'); return
        while True:
            active=sum(s.admitted and not s.closed for s in node.incoming.values())
            print(f'\n被控设置 | 正在被控：{active} 台 | 已授权主控：{len(node.state["acl"])} 台')
            print('[1] 处理待确认申请  [2] 管理主控授权  [3] 修改访问密码  [4] 关闭被控  [0] 返回')
            choice=await ask('选择：')
            if choice=='0': return
            if choice=='1':
                pending=list(approvals.items())
                if not pending: print('目前没有待确认申请。'); continue
                for index,(token,(_,code,endpoint)) in enumerate(pending,1): print(f'  [{index}] {code} | EndpointId: {endpoint}')
                index=int(await ask('申请编号（0 返回）：'))
                if not index: continue
                token,(future,_,_)=pending[index-1]
                answer=(await ask('已通过独立渠道核对完整 EndpointId？[y/N] ')).lower()
                if not future.done(): future.set_result(answer=='y')
            elif choice=='2':
                allowed=list(node.state['acl'])
                if not allowed: print('没有已授权主控。'); continue
                for index,endpoint in enumerate(allowed,1): print(f'  [{index}] {node.state["controllers"].get(endpoint,"未知设备")} | {endpoint}')
                index=int(await ask('输入编号撤销，0 返回：'))
                if index: await node.revoke(allowed[index-1]); print('已撤销。')
            elif choice=='3':
                password=await asyncio.to_thread(getpass.getpass,'新访问密码（修改后撤销全部主控授权）：')
                await node.access(True,password); print('密码已修改，原主控授权已撤销。'); continue
            elif choice=='4': await node.access(False); print('已关闭被控。'); return
            elif choice not in ('1','2','3','4'): print('无效选择。')

    try:
        while True:
            peers=show(); choice=(await ask('选择：')).upper()
            try:
                if choice=='Q': break
                if choice=='B':
                    await host_settings()
                elif choice=='A':
                    code=(await ask('对方 6 位设备 ID：')).upper()
                    if len(code)!=6: raise ValueError('设备 ID 必须为 6 位。')
                    password=await asyncio.to_thread(getpass.getpass,'对方访问密码：')
                    await connect(code,password)
                elif choice.isdigit() and 1<=int(choice)<=len(peers):
                    await open_device(*peers[int(choice)-1])
                else:
                    print('无效选择。')
            except Exception as error:
                print(type(error).__name__+': '+str(error))
    finally:
        poll.cancel()
        for task in background: task.cancel()
        await asyncio.gather(poll,*background,return_exceptions=True); await node.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command',choices=['init','run','test'])
    parser.add_argument('--data',type=Path,default=Path(__file__).parent/'prototype-data')
    parser.add_argument('--ard',type=Path,default=Path(__file__).parent/'tools'/'ard.exe')
    parser.add_argument('--server',default='https://f.visnova.cn')
    parser.add_argument('--relay',default='http://175.27.160.144:8080')
    parser.add_argument('--relay-key',default=DEFAULT_RELAY_KEY)
    args=parser.parse_args()
    if args.command=='init':
        print('EndpointId:',identity(args.data,True).public_key().public_bytes_raw().hex())
    elif args.command=='test':
        asyncio.run(regression(args))
    else:
        try:
            asyncio.run(console(args))
        except Exception as error:
            print('启动失败：'+str(error))
            with contextlib.suppress(EOFError): input('按 Enter 关闭…')
            raise SystemExit(1)


async def regression(args):
    with tempfile.TemporaryDirectory(prefix='ardui-console-test-') as temporary:
        roots=[Path(temporary)/str(i) for i in range(3)]
        for root in roots: identity(root,True)
        initial=(roots[0]/'identity').read_bytes(); identity(roots[0],True)
        assert (roots[0]/'identity').read_bytes()==initial
        broken=Path(temporary)/'broken'; broken.mkdir(); (broken/'identity').write_bytes(b'bad')
        try: identity(broken,True)
        except ValueError: pass
        else: raise AssertionError('Invalid identity accepted')
        assert (broken/'identity').read_bytes()==b'bad'
        print('PASS identity: create, reuse, no corrupt overwrite',flush=True)
        nodes=[Node(root,args.ard,args.server,args.relay,args.relay_key) for root in roots]
        host,other,client=nodes
        async def echo(reader,writer):
            try:
                while data:=await reader.read(65536): writer.write(data); await writer.drain()
            finally: writer.close()
        listener=await asyncio.start_server(echo,'127.0.0.1',0)
        port=listener.sockets[0].getsockname()[1]
        class UdpEcho(asyncio.DatagramProtocol):
            def connection_made(self,transport): self.transport=transport
            def datagram_received(self,data,address): self.transport.sendto(data,address)
        udp_echo_transport,_=await asyncio.get_running_loop().create_datagram_endpoint(
            UdpEcho,local_addr=('127.0.0.1',port))
        for node in nodes: node.allowed_ports=[port]
        for node in nodes: node.allowed_udp_ports=[port]
        host.allowed_ports.append(445)
        pending=asyncio.Event(); consent=asyncio.get_running_loop().create_future(); checks=[]
        async def host_confirm(code,endpoint,incoming):
            assert incoming and endpoint==client.endpoint
            checks.append('host'); pending.set(); return await consent
        async def client_confirm(code,endpoint,incoming):
            assert not incoming and endpoint in (host.endpoint,other.endpoint)
            checks.append(endpoint); return True
        async def other_confirm(code,endpoint,incoming):
            assert incoming and endpoint==client.endpoint
            return True
        host.confirm=host_confirm; other.confirm=other_confirm; client.confirm=client_confirm
        loops=[]; persistence_ready=False; disabled_ready=False
        try:
            for node in nodes: await node.register()
            original=client.code; await client.register(); assert original==client.code
            print('PASS NJ HTTPS: stable six-character machine codes',flush=True)
            await host.access(True,'Prototype-Password-8362'); await other.access(True,'Other-Password-5318')
            restored=Node(roots[0],args.ard,args.server,args.relay,args.relay_key)
            assert restored.enabled and restored.password==host.password
            persistence_ready=True
            print('PASS enabled setting and access password survive restart',flush=True)
            loops=[asyncio.create_task(n.poll()) for n in (host,other)]
            connecting=asyncio.create_task(client.connect(host.code,'Prototype-Password-8362'))
            await asyncio.wait_for(pending.wait(),120)
            assert not connecting.done() and not client.state['peers']
            consent.set_result(True); first=await connecting
            print('PASS password + both fingerprints + explicit consent + signed grant',flush=True)
            async with asyncio.timeout(45):
                while first.tcp_rtt_ms is None or first.udp_rtt_ms is None: await asyncio.sleep(.1)
            assert first.tcp_rtt_ms >= 0 and first.udp_rtt_ms >= 0
            print(f'PASS TCP/PsPing {first.tcp_rtt_ms:.2f} ms + UDP round-trip {first.udp_rtt_ms:.2f} ms',flush=True)
            async def check_flow(code,forward=None):
                forward=forward or await client.forward(code,port)
                reader,writer=await asyncio.open_connection(forward.address,forward.port)
                payload=secrets.token_bytes(16384); writer.write(payload); await writer.drain()
                assert await asyncio.wait_for(reader.readexactly(len(payload)),15)==payload
                writer.close(); await writer.wait_closed()
                return forward
            stable=await check_flow(host.code)
            print('PASS actual ARD relay + loopback TCP forwarding',flush=True)
            def udp_roundtrip():
                udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); udp.settimeout(15)
                try:
                    payload=secrets.token_bytes(1200); udp.sendto(payload,(stable.address,stable.port))
                    assert udp.recvfrom(1501)[0]==payload
                finally: udp.close()
            await asyncio.to_thread(udp_roundtrip)
            print('PASS authorized UDP business flow on the RDP TCP port',flush=True)
            if os.name=='nt':
                smb=await client.forward(host.code,445)
                remote='\\\\localhost\\scan'
                script="New-SmbMapping -RemotePath $d.remote -TcpPort ([int]$d.port) -Persistent $false | Out-Null; Get-ChildItem $d.remote -ErrorAction Stop | Out-Null; [Console]::Write('SMB_OK')"
                try:
                    assert await powershell(script,dict(remote=remote,port=smb.port))=='SMB_OK'
                finally:
                    with contextlib.suppress(Exception):
                        await powershell("Remove-SmbMapping -RemotePath $d.remote -Force -Confirm:$false",dict(remote=remote))
                print('PASS Windows SMB read through ARD and New-SmbMapping -TcpPort',flush=True)
            second=await client.connect(other.code,'Other-Password-5318')
            assert client.state['peers'][host.code]['address']!=client.state['peers'][other.code]['address']
            await asyncio.gather(check_flow(host.code),check_flow(other.code))
            print('PASS simultaneous remote devices with isolated loopback addresses',flush=True)
            before=len(checks); first=await client.connect(host.code)
            assert len(checks)==before
            await check_flow(host.code,stable)
            print('PASS reconnect without enrollment password or repeated consent',flush=True)
            before=len(checks); broken=first
            broken.process.kill(); await broken.process.wait()
            async with asyncio.timeout(120):
                while client.outgoing.get(host.endpoint) in (None,broken): await asyncio.sleep(.25)
            recovered=client.outgoing[host.endpoint]
            assert len(checks)==before
            assert await client.forward(host.code,port) is stable
            await check_flow(host.code,stable)
            first=recovered
            print('PASS automatic reconnect keeps the same local forwarding endpoint',flush=True)
            # Forged directory session data cannot pass the endpoint signature check.
            envelope=host.sign('/test',{'ok':True}); envelope['payload']=base64.b64encode(b'{"ok":false}').decode()
            try: verify(envelope,'/test',host.endpoint)
            except Exception: pass
            else: raise AssertionError('Forged signature accepted')
            print('PASS tampered signed message rejected',flush=True)
            await host.revoke(client.endpoint)
            try: await client.connect(host.code)
            except PermissionError: pass
            else: raise AssertionError('Revoked client admitted')
            assert not host.state['acl']
            before=len(checks)
            try: await client.connect(host.code,'incorrect-password')
            except PermissionError: pass
            else: raise AssertionError('Wrong password admitted')
            assert len(checks)==before
            print('PASS revoke rejects reconnect; wrong password never prompts host',flush=True)
            await client.disconnect(other.code); await other.access(False)
            disabled_ready=True
            assert not Node(roots[1],args.ard,args.server,args.relay,args.relay_key).enabled
            try: await client.connect(other.code)
            except urllib.error.HTTPError as error: assert error.code==403
            else: raise AssertionError('Disabled host admitted connection')
            print('PASS disabled host denies new connections and stops incoming sessions',flush=True)
        finally:
            for task in loops: task.cancel()
            await asyncio.gather(*loops,return_exceptions=True)
            for node in nodes: await node.close()
            if persistence_ready:
                assert Node(roots[0],args.ard,args.server,args.relay,args.relay_key).enabled
                if disabled_ready: assert not Node(roots[1],args.ard,args.server,args.relay,args.relay_key).enabled
                print('PASS normal shutdown preserves configured host-access state',flush=True)
            udp_echo_transport.close(); listener.close(); await listener.wait_closed()


if __name__=='__main__':
    try:
        main()
    except (KeyboardInterrupt,EOFError):
        pass
