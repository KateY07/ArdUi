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
import secrets
import socket
import struct
import subprocess
import tempfile
import time
import urllib.request
import uuid
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey


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
        self.listeners, self.tasks = [], set()
        self.token, self.port, self.ports = b'', 0, []
        self.closed = False
        self.address = '127.0.0.1'
        self.mappings = []

    def spawn(self, coroutine):
        task = asyncio.create_task(coroutine); self.tasks.add(task)
        task.add_done_callback(self.tasks.discard)
        return task

    async def start(self, mode, port, peer):
        env = dict(os.environ); env.pop('RUST_LOG', None); env['NO_COLOR'] = '1'
        self.process = await asyncio.create_subprocess_exec(str(self.node.ard), mode, str(port), 'to', peer,
            '--relay', self.node.relay, cwd=self.folder.name, env=env,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        ready = b'relay online' if mode == 'open' else b'READY:'
        async with asyncio.timeout(75):
            while line := await self.process.stdout.readline():
                if ready in line:
                    self.spawn(self.drain()); return
            raise RuntimeError('ARD exited before connection was ready.')

    async def drain(self):
        while await self.process.stdout.readline():
            pass

    async def open(self, target):
        if target not in self.ports:
            raise ValueError('Port not authorized.')
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        writer.write(b'AUI1'+self.token+b'\x01'+struct.pack('!H', target)); await writer.drain()
        if await reader.readexactly(1) != b'\0':
            writer.close(); raise ConnectionError('Remote service is not listening.')
        return reader, writer

    async def forward(self, target):
        async def serve(reader, writer):
            try:
                await bridge((reader, writer), await self.open(target))
            except (OSError, EOFError, ValueError, ExceptionGroup):
                writer.close()
        # Windows SMB accepts its own loopback name; 127.77 aliases are rejected
        # by the redirector before a connection is attempted. TcpPort still keeps
        # each remote mapping on its own listener.
        bind='127.0.0.1' if target==445 else self.address
        listener = await asyncio.start_server(lambda r, w: self.spawn(serve(r, w)), bind, 0)
        self.listeners.append(listener)
        return listener.sockets[0].getsockname()[1]

    async def close(self):
        if self.closed:
            return
        self.closed = True
        for drive,remote in self.mappings:
            with contextlib.suppress(Exception):
                await powershell("$m=Get-SmbMapping -LocalPath $d.drive -ErrorAction SilentlyContinue; if($m -and $m.RemotePath -eq $d.remote){Remove-SmbMapping -LocalPath $d.drive -Force -Confirm:$false}",dict(drive=drive,remote=remote))
        for listener in self.listeners:
            listener.close(); await listener.wait_closed()
        if self.process and self.process.returncode is None:
            self.process.kill(); await self.process.wait()
        tasks = list(self.tasks)
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self.folder.cleanup()


class Node:
    def __init__(self, root, ard, server='https://f.visnova.cn', relay='http://175.27.160.144:8080'):
        self.root, self.ard, self.server, self.relay = Path(root).resolve(), Path(ard).resolve(), server.rstrip('/'), relay
        if not self.server.startswith('https://'):
            raise ValueError('HTTPS directory required.')
        self.key = identity(self.root)
        self.endpoint = self.key.public_key().public_bytes_raw().hex()
        path = self.root/'state.json'
        self.state = json.loads(path.read_text()) if path.exists() else {'acl': {}, 'peers': {}, 'pins': {}}
        self.code, self.enabled, self.password = '', False, None
        if 'password' in self.state:
            self.password = tuple(bytes.fromhex(part) for part in self.state['password'])
        self.incoming, self.outgoing, self.jobs, self.seen = {}, {}, set(), set()
        self.desired, self.supervisors, self.connect_locks = set(), {}, {}
        self.confirm = None
        self.attempts = []
        self.control = asyncio.Lock()

    def save(self):
        path = self.root/'state.json'
        temporary = path.with_suffix('.tmp'); temporary.write_bytes(encode(self.state)); temporary.replace(path)

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
            self.state['acl'].clear(); self.save()
        if enabled and self.password is None:
            raise ValueError('Set a password before enabling host access.')
        await self.call('/api/v1/access', {'enabled': enabled}); self.enabled = enabled

    async def poll(self):
        while True:
            try:
                async with self.control:
                    reply = await self.call('/api/v1/poll', {'enabled': self.enabled})
                for ticket in reply['tickets']:
                    if ticket['id'] in self.seen:
                        continue
                    self.seen.add(ticket['id'])
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
                                        self.state['acl'][peer] = grant; self.save()
                            if not self.enabled or session.closed:
                                grant = None
                            await write_json(writer, dict(accepted=bool(grant), error=None if grant else 'Access denied',
                                token=session.token.hex() if grant else None, tcpPorts=session.ports if grant else [], udpPorts=[], grant=grant))
                            return
                        if peer not in self.state['acl'] or not hmac.compare_digest(header[4:20],session.token):
                            return
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
            await session.start('open',listener.sockets[0].getsockname()[1],ticket['clientSessionId'])
            await self.call('/api/v1/tickets/'+ticket['id']+'/ready',dict(sessionId=session.endpoint,requestId=ticket['id'],
                controllerEndpoint=peer,targetEndpoint=self.endpoint,clientSessionId=ticket['clientSessionId'],expires=ticket['expires']))
        except Exception as error:
            print('Incoming:',type(error).__name__, flush=True)
            if session:
                await session.close()
            with contextlib.suppress(Exception):
                await self.call('/api/v1/tickets/'+ticket['id']+'/reject',{})

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
                session.token = bytes.fromhex(reply['token']); session.ports = reply['tcpPorts']
                addresses={p.get('address') for p in self.state['peers'].values()}
                session.address=self.state['peers'].get(code,{}).get('address') or next(
                    f'127.77.{i//250}.{i%250+1}' for i in range(64000) if f'127.77.{i//250}.{i%250+1}' not in addresses)
                self.state['peers'][code] = dict(endpoint=peer,grant=reply['grant'],address=session.address); self.save()
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
                except Exception:
                    await asyncio.sleep(delay); delay=min(delay*2,30)

    async def disconnect(self,code):
        code=code.upper(); self.desired.discard(code)
        record=self.state['peers'].get(code)
        if record and (session:=self.outgoing.pop(record['endpoint'],None)):
            await session.close()

    async def revoke(self, peer):
        self.state['acl'].pop(peer,None); self.save()
        if peer in self.incoming:
            await self.incoming.pop(peer).close()

    async def close(self):
        self.enabled = False
        self.desired.clear()
        for task in self.supervisors.values(): task.cancel()
        await asyncio.gather(*self.supervisors.values(),return_exceptions=True)
        for task in list(self.jobs):
            task.cancel()
        await asyncio.gather(*self.jobs, return_exceptions=True)
        for session in list(self.incoming.values())+list(self.outgoing.values()):
            await session.close()
        with contextlib.suppress(Exception):
            await self.call('/api/v1/access', {'enabled':False})


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
    node = Node(args.data,args.ard,args.server,args.relay); node.allowed_ports=[3389,445]
    await node.register()
    print(f'Machine: {node.code}\nEndpointId: {node.endpoint}\nHost access: OFF',flush=True)
    approvals, background = {}, set()
    async def confirm(code, endpoint, incoming):
        token=secrets.token_hex(3); future=asyncio.get_running_loop().create_future(); approvals[token]=future
        print(f'\n{"Incoming" if incoming else "Target"}: {code}\nEndpointId: {endpoint}\nVerify independently, then: approve {token} (or deny {token})',flush=True)
        try:
            return await asyncio.wait_for(future,180)
        finally:
            approvals.pop(token,None)
    node.confirm=confirm
    poll=asyncio.create_task(node.poll())
    async def connect(code,password):
        try:
            await node.connect(code,password); print('\nAUTHORIZED:',code,flush=True)
        except Exception as error:
            print('\nConnect failed:',str(error),flush=True)
    print('Commands: on, off, add CODE, connect CODE, approve TOKEN, deny TOKEN, list, rdp CODE, smb CODE, disconnect CODE, revoke ENDPOINT, quit')
    try:
        while True:
            words=(await asyncio.to_thread(input,'ardui> ')).split()
            if not words:
                continue
            try:
                command=words[0]
                if command=='quit':
                    break
                if command=='on':
                    await node.access(True,(await asyncio.to_thread(getpass.getpass,'New password (resets grants); empty keeps existing: ')) or None)
                elif command=='off':
                    await node.access(False)
                elif command in ('approve','deny'):
                    approvals[words[1]].set_result(command=='approve')
                elif command in ('add','connect'):
                    password=await asyncio.to_thread(getpass.getpass,'Enrollment password: ') if command=='add' else None
                    task=asyncio.create_task(connect(words[1].upper(),password)); background.add(task); task.add_done_callback(background.discard)
                elif command=='list':
                    print('Authorized targets:',json.dumps({c:p['endpoint'] for c,p in node.state['peers'].items()},indent=2))
                    print('Allowed controllers:',*node.state['acl'],sep='\n')
                elif command=='revoke':
                    await node.revoke(words[1])
                elif command in ('rdp','smb','disconnect'):
                    peer=node.state['peers'][words[1].upper()]['endpoint']
                    if command=='disconnect':
                        await node.disconnect(words[1])
                        continue
                    session=node.outgoing.get(peer)
                    if not session: raise ValueError('Run connect CODE first.')
                    port=await session.forward(3389 if command=='rdp' else 445)
                    if command=='rdp':
                        subprocess.Popen(['mstsc',f'/v:{session.address}:{port}'])
                    else:
                        share=await asyncio.to_thread(input,'Share name: ')
                        if not share or any(c in share for c in '\\/\0'):
                            raise ValueError('Enter a share name without a path.')
                        user=await asyncio.to_thread(input,'Windows user (empty = current account): ')
                        password=await asyncio.to_thread(getpass.getpass,'Windows password: ') if user else ''
                        remote=f'\\\\{("127.0.0.1" if command=="smb" else session.address)}\\{share}'
                        script="""$drive=90..68 | ForEach-Object { [char]$_ } | Where-Object { -not (Get-PSDrive -Name $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1;
if(-not $drive){throw 'No drive letter available'}; $local=([string]$drive)+':';
$p=@{LocalPath=$local; RemotePath=$d.remote; TcpPort=[int]$d.port; Persistent=$false};
if($d.user){$p.Credential=[pscredential]::new($d.user,(ConvertTo-SecureString $d.password -AsPlainText -Force))};
New-SmbMapping @p | Out-Null; [Console]::Write($local)"""
                        drive=await powershell(script,dict(remote=remote,port=port,user=user,password=password))
                        session.mappings.append((drive,remote)); subprocess.Popen(['explorer.exe',drive+'\\'])
                else:
                    print('Unknown command.')
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
    args=parser.parse_args()
    if args.command=='init':
        print('EndpointId:',identity(args.data,True).public_key().public_bytes_raw().hex())
    elif args.command=='test':
        asyncio.run(regression(args))
    else:
        asyncio.run(console(args))


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
        nodes=[Node(root,args.ard,args.server,args.relay) for root in roots]
        host,other,client=nodes
        async def echo(reader,writer):
            try:
                while data:=await reader.read(65536): writer.write(data); await writer.drain()
            finally: writer.close()
        listener=await asyncio.start_server(echo,'127.0.0.1',0)
        port=listener.sockets[0].getsockname()[1]
        for node in nodes: node.allowed_ports=[port]
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
        loops=[]
        try:
            for node in nodes: await node.register()
            original=client.code; await client.register(); assert original==client.code
            print('PASS NJ HTTPS: stable six-character machine codes',flush=True)
            await host.access(True,'Prototype-Password-8362'); await other.access(True,'Other-Password-5318')
            loops=[asyncio.create_task(n.poll()) for n in (host,other)]
            connecting=asyncio.create_task(client.connect(host.code,'Prototype-Password-8362'))
            await asyncio.wait_for(pending.wait(),120)
            assert not connecting.done() and not client.state['peers']
            consent.set_result(True); first=await connecting
            print('PASS password + both fingerprints + explicit consent + signed grant',flush=True)
            async def check_flow(session):
                forwarded=await session.forward(port)
                reader,writer=await asyncio.open_connection(session.address,forwarded)
                payload=secrets.token_bytes(16384); writer.write(payload); await writer.drain()
                assert await asyncio.wait_for(reader.readexactly(len(payload)),15)==payload
                writer.close(); await writer.wait_closed()
            await check_flow(first)
            print('PASS actual ARD relay + loopback TCP forwarding',flush=True)
            if os.name=='nt':
                smb_port=await first.forward(445)
                remote='\\\\127.0.0.1\\scan'
                script="New-SmbMapping -RemotePath $d.remote -TcpPort ([int]$d.port) -Persistent $false | Out-Null; Get-ChildItem $d.remote -ErrorAction Stop | Out-Null; [Console]::Write('SMB_OK')"
                try:
                    assert await powershell(script,dict(remote=remote,port=smb_port))=='SMB_OK'
                finally:
                    with contextlib.suppress(Exception):
                        await powershell("Remove-SmbMapping -RemotePath $d.remote -Force -Confirm:$false",dict(remote=remote))
                print('PASS Windows SMB read through ARD and New-SmbMapping -TcpPort',flush=True)
            second=await client.connect(other.code,'Other-Password-5318')
            assert first.address!=second.address
            await asyncio.gather(check_flow(first),check_flow(second))
            print('PASS simultaneous remote devices with isolated loopback addresses',flush=True)
            before=len(checks); first=await client.connect(host.code)
            assert len(checks)==before
            await check_flow(first)
            print('PASS reconnect without enrollment password or repeated consent',flush=True)
            before=len(checks); broken=first
            broken.process.kill(); await broken.process.wait()
            async with asyncio.timeout(120):
                while client.outgoing.get(host.endpoint) in (None,broken): await asyncio.sleep(.25)
            recovered=client.outgoing[host.endpoint]
            assert len(checks)==before
            await check_flow(recovered)
            first=recovered
            print('PASS automatic reconnect after ARD connection loss; new flows work without prompts',flush=True)
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
            await other.access(False)
            try: await client.connect(other.code)
            except urllib.error.HTTPError as error: assert error.code==403
            else: raise AssertionError('Disabled host admitted connection')
            print('PASS disabled host denies new connections and stops incoming sessions',flush=True)
        finally:
            for task in loops: task.cancel()
            await asyncio.gather(*loops,return_exceptions=True)
            for node in nodes: await node.close()
            listener.close(); await listener.wait_closed()


if __name__=='__main__':
    try:
        main()
    except (KeyboardInterrupt,EOFError):
        pass
