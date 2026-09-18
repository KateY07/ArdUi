"""ArdTransit v2.pre1: an explicitly enabled, opaque two-peer ARD forwarder."""
import argparse
import asyncio
import base64
import collections
import hashlib
import json
import logging
from logging.handlers import RotatingFileHandler
import os
import re
import secrets
import shutil
import struct
import subprocess
import tempfile
import time
import urllib.request
import urllib.parse
import zipfile
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey

VERSION = 'v2.pre1'
BUILD = 'relay1'
DEFAULT_RELAY_KEY = 'spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f'
events = collections.deque(maxlen=512)
runtime_log = logging.getLogger('ArdTransit')
runtime_log.setLevel(logging.INFO)
runtime_log.propagate = False

def configure_log(folder):
    handler = RotatingFileHandler(Path(folder) / 'relay.log', maxBytes=2*1024*1024, backupCount=3, encoding='utf-8')
    handler.setFormatter(logging.Formatter('%(message)s'))
    runtime_log.addHandler(handler)
    return handler

def log(event, **fields):
    row = dict(time=time.time(), event=event, **fields)
    events.append(row)
    text = json.dumps(row, ensure_ascii=False)
    runtime_log.info(text)
    try: print(text, flush=True)
    except (OSError,UnicodeError) as ex: runtime_log.error(json.dumps(dict(event='stdout-error', error=str(ex))))

def atomic_json(path, value):
    fd, pending = tempfile.mkstemp(prefix='.'+path.name+'-', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as out:
            json.dump(value, out, ensure_ascii=False)
            out.flush()
            os.fsync(out.fileno())
        os.replace(pending, path)
    finally:
        if os.path.exists(pending): os.unlink(pending)

class DirectoryTrustError(ValueError):
    pass

class ChildJob:
    def __init__(self):
        self.handle = None
        if os.name != 'nt': return
        import ctypes
        from ctypes import wintypes
        class BasicLimits(ctypes.Structure):
            _fields_ = [('PerProcessUserTimeLimit',ctypes.c_int64),('PerJobUserTimeLimit',ctypes.c_int64),
                ('LimitFlags',wintypes.DWORD),('MinimumWorkingSetSize',ctypes.c_size_t),('MaximumWorkingSetSize',ctypes.c_size_t),
                ('ActiveProcessLimit',wintypes.DWORD),('Affinity',ctypes.c_size_t),('PriorityClass',wintypes.DWORD),('SchedulingClass',wintypes.DWORD)]
        class IoCounters(ctypes.Structure):
            _fields_ = [(name,ctypes.c_uint64) for name in ('ReadOperationCount','WriteOperationCount','OtherOperationCount',
                'ReadTransferCount','WriteTransferCount','OtherTransferCount')]
        class ExtendedLimits(ctypes.Structure):
            _fields_ = [('BasicLimitInformation',BasicLimits),('IoInfo',IoCounters),('ProcessMemoryLimit',ctypes.c_size_t),
                ('JobMemoryLimit',ctypes.c_size_t),('PeakProcessMemoryUsed',ctypes.c_size_t),('PeakJobMemoryUsed',ctypes.c_size_t)]
        self.ctypes, self.api = ctypes, ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.CreateJobObjectW.argtypes = [wintypes.LPVOID,wintypes.LPCWSTR]
        self.api.CreateJobObjectW.restype = wintypes.HANDLE
        self.api.SetInformationJobObject.argtypes = [wintypes.HANDLE,ctypes.c_int,wintypes.LPVOID,wintypes.DWORD]
        self.api.SetInformationJobObject.restype = wintypes.BOOL
        self.api.OpenProcess.argtypes = [wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.AssignProcessToJobObject.argtypes = [wintypes.HANDLE,wintypes.HANDLE]
        self.api.AssignProcessToJobObject.restype = wintypes.BOOL
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.CloseHandle.restype = wintypes.BOOL
        self.handle = self.api.CreateJobObjectW(None,None)
        if not self.handle: raise ctypes.WinError(ctypes.get_last_error())
        limits = ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = 0x2000
        if not self.api.SetInformationJobObject(self.handle,9,ctypes.byref(limits),ctypes.sizeof(limits)):
            error = ctypes.WinError(ctypes.get_last_error())
            self.close()
            raise error

    def assign(self, pid):
        if self.handle is None: return
        process = self.api.OpenProcess(0x0101,False,pid)
        if not process: raise self.ctypes.WinError(self.ctypes.get_last_error())
        try:
            if not self.api.AssignProcessToJobObject(self.handle,process):
                raise self.ctypes.WinError(self.ctypes.get_last_error())
        finally:
            if not self.api.CloseHandle(process): log('process-handle-close-error',error=str(self.ctypes.WinError(self.ctypes.get_last_error())))

    def close(self):
        if self.handle is not None:
            handle, self.handle = self.handle, None
            if not self.api.CloseHandle(handle): log('job-close-error',error=str(self.ctypes.WinError(self.ctypes.get_last_error())))

def redact(text):
    text = re.sub(r'\b(?:\d{1,3}\.){3}\d{1,3}\b', '[IPv4]', str(text))
    return re.sub(r'(?<![\w])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]+', '[IPv6]', text)

def signed_bytes(route, value):
    return f"ArdUiServer/1\n{route}\n{value['endpoint']}\n{value['issuedAt']}\n{value['nonce']}\n{value['payload']}".encode()

def verify(value, route, endpoint):
    if value['endpoint'] != endpoint or abs(time.time()-value['issuedAt']) > 360:
        raise ValueError('signature identity or expiry')
    Ed25519PublicKey.from_public_bytes(bytes.fromhex(endpoint)).verify(base64.b64decode(value['signature'], validate=True), signed_bytes(route, value))
    return json.loads(base64.b64decode(value['payload'], validate=True))

class Instance:
    def __init__(self, folder):
        Path(folder).mkdir(parents=True, exist_ok=True)
        self.file = open(Path(folder) / 'instance.lock', 'a+b')
        if self.file.seek(0, os.SEEK_END) == 0:
            self.file.write(b'\0')
            self.file.flush()
        self.file.seek(0)
        try:
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.file.close()
            raise RuntimeError('ArdTransit is already using this data directory.')

    def __enter__(self): return self
    def __exit__(self, *args): self.file.close()

def memory_bytes():
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)]+[(name,ctypes.c_size_t) for name in (
                'PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage',
                'QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage')]
        value = Counters()
        value.cb = ctypes.sizeof(value)
        query = ctypes.WinDLL('psapi', use_last_error=True).GetProcessMemoryInfo
        query.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        query.restype = wintypes.BOOL
        if not query(wintypes.HANDLE(-1), ctypes.byref(value), value.cb): raise ctypes.WinError(ctypes.get_last_error())
        return value.WorkingSetSize
    return int(Path('/proc/self/statm').read_text().split()[1])*os.sysconf('SC_PAGE_SIZE')

class Api:
    def __init__(self, args):
        self.args = args
        self.root = Path(args.data).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        identity = self.root / 'identity'
        if not identity.exists():
            fd, pending = tempfile.mkstemp(prefix='.identity-', dir=self.root)
            try:
                with os.fdopen(fd, 'wb') as out:
                    out.write(secrets.token_bytes(32))
                    out.flush()
                    os.fsync(out.fileno())
                try: os.link(pending, identity)
                except FileExistsError: log('identity-already-created')
            finally: os.unlink(pending)
        self.key = Ed25519PrivateKey.from_private_bytes(identity.read_bytes())
        self.endpoint = self.key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()
        self.rtt = None
        self.server_key = None

    async def call(self, route, data):
        payload = base64.b64encode(json.dumps(data, separators=(',', ':')).encode()).decode()
        envelope = dict(endpoint=self.endpoint, issuedAt=int(time.time()), nonce=secrets.token_hex(16), payload=payload)
        envelope['signature'] = base64.b64encode(self.key.sign(signed_bytes(route, envelope))).decode()
        def request():
            req = urllib.request.Request(self.args.server.rstrip('/')+route, data=json.dumps(envelope).encode(), headers={'Content-Type':'application/json'})
            # Never forward a signed API body to a redirected endpoint.
            class NoRedirect(urllib.request.HTTPRedirectHandler):
                def redirect_request(self, req, fp, code, msg, headers, newurl): return None
            with urllib.request.build_opener(NoRedirect).open(req, timeout=15) as response:
                body = response.read(65537)
                if len(body)>65536: raise ValueError('directory response too large')
                return json.loads(body)
        start = time.monotonic()
        result = await asyncio.to_thread(request)
        self.rtt = (time.monotonic()-start)*1000
        return result

    async def register(self):
        await self.call('/api/v1/register', {})
        value = await self.call('/api/v2/transit/key', {})
        key = value['endpoint']
        if not re.fullmatch('[0-9a-f]{64}', key): raise DirectoryTrustError('invalid directory public key')
        path = self.root / 'directory-public-key'
        if path.exists() and path.read_text().strip() != key:
            raise DirectoryTrustError('directory signing key changed; inspect before replacing the saved public key')
        path.write_text(key)
        self.server_key = key

class Budget:
    def __init__(self, mbps):
        self.rate = mbps*1_000_000/8
        self.credit = self.rate
        self.updated = time.monotonic()
        self.lock = asyncio.Lock()

    def take(self, size):
        now = time.monotonic()
        self.credit = min(self.rate, self.credit+(now-self.updated)*self.rate)
        self.updated = now
        if self.credit < size: return False
        self.credit -= size
        return True

    async def wait(self, size):
        async with self.lock:
            while not self.take(size): await asyncio.sleep(max(.001, size/self.rate))

class Datagram(asyncio.DatagramProtocol):
    def __init__(self, pair, side):
        self.pair, self.side = pair, side
        self.remote = None
        self.transport = None

    def connection_made(self, transport): self.transport = transport

    def datagram_received(self, data, addr):
        pair = self.pair
        if addr[0] != '127.0.0.1' or not 41 <= len(data) <= 1500 or not secrets.compare_digest(data[:16], pair.tokens[self.side]):
            pair.drops += 1
            return
        self.remote = addr
        other = pair.udp[1-self.side]
        if other is None or other.remote is None: return
        if not pair.budget.take(len(data)) or not pair.agent.budget.take(len(data)):
            pair.drops += 1
            return
        pair.touched = time.monotonic()
        pair.udp_bytes += len(data)-16
        other.transport.sendto(pair.tokens[1-self.side]+data[16:], other.remote)

    def error_received(self, exc): log('udp-error', error=redact(exc))

class Pair:
    def __init__(self, agent, ticket):
        self.agent, self.ticket = agent, ticket
        self.route = ticket['route']
        self.tokens = [secrets.token_bytes(16), secrets.token_bytes(16)]
        self.udp = [None, None]
        self.servers, self.children, self.readers = [], [], []
        self.writers = [None, None]
        self.write_locks = [asyncio.Lock(), asyncio.Lock()]
        self.tasks = set()
        self.paths = ['connecting', 'connecting']
        self.rtts = [None, None]
        self.budget = Budget(min(agent.args.mbps, ticket['mbps']))
        self.created = self.touched = time.monotonic()
        self.tcp_bytes = self.udp_bytes = self.drops = 0
        self.lease = ticket['expires']
        self.closed = False
        self.root = agent.api.root / 'sessions' / self.route

    async def start(self):
        self.root.mkdir(parents=True, exist_ok=False)
        identities = []
        for side in range(2):
            cwd = self.root / str(side)
            cwd.mkdir()
            creation = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name=='nt' else {}
            proc = await self.agent.spawn(self.agent.args.ard, '--quiet', 'id', cwd=cwd,
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, **creation)
            try: out, err = await asyncio.wait_for(proc.communicate(), 15)
            except BaseException:
                if proc.returncode is None: proc.kill()
                await proc.wait()
                raise
            identity = out.decode().strip()
            if proc.returncode or not re.fullmatch('[0-9a-f]{64}', identity): raise ValueError('ARD identity creation failed: '+redact(err.decode())[:120])
            identities.append(identity)
            async def handler(reader, writer, index=side): await self.accept(index, reader, writer)
            server = await asyncio.start_server(handler, '127.0.0.1', 0, limit=65536)
            self.servers.append(server)
            port = server.sockets[0].getsockname()[1]
            transport, protocol = await asyncio.get_running_loop().create_datagram_endpoint(lambda: Datagram(self, side), local_addr=('127.0.0.1',port))
            self.udp[side] = protocol
            peer = self.ticket['aSession' if side==0 else 'bSession']
            child = await self.agent.spawn(self.agent.args.ard, 'open', str(port), 'to', peer,
                    '--relay', self.agent.args.relay, '--relay-key', self.agent.args.relay_key, cwd=cwd,
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, **creation)
            self.children.append(child)
            self.readers.extend([asyncio.create_task(self.pump(child.stdout,side)), asyncio.create_task(self.pump(child.stderr,side))])
        await self.agent.api.call('/api/v2/transit/routes/'+self.route+'/ready', dict(route=self.route,
              aRelaySession=identities[0], bRelaySession=identities[1], aToken=self.tokens[0].hex(), bToken=self.tokens[1].hex()))
        log('route-ready', route=self.route)

    async def pump(self, reader, side):
        while line := await reader.readline():
            text = line.decode(errors='replace').strip()
            match = re.search(r'transport="(direct|relay)"', text)
            if match: self.paths[side] = match[1]
            match = re.search(r'rtt_ms=(?:Some\()?([\d.]+)', text)
            if match: self.rtts[side] = float(match[1])
            log('ard', route=self.route, side=side, message=redact(text)[:1000])

    async def accept(self, side, reader, writer):
        task = asyncio.current_task()
        self.tasks.add(task)
        try:
            if self.closed or len(self.tasks)>4: raise ValueError('route connection limit')
            size = struct.unpack('!I', await asyncio.wait_for(reader.readexactly(4),10))[0]
            if not 1<=size<=1024: raise ValueError('invalid join size')
            join = json.loads(await asyncio.wait_for(reader.readexactly(size),10))
            if join.get('route') != self.route or not secrets.compare_digest(join.get('token',''), self.tokens[side].hex()):
                raise ValueError('invalid join capability')
            if self.writers[side] is not None: self.writers[side].close()
            self.writers[side] = writer
            writer.write(b'\x00')
            await writer.drain()
            while not self.closed:
                header = await asyncio.wait_for(reader.readexactly(4),45)
                size = struct.unpack('!I', header)[0]
                if not 25<=size<=65536: raise ValueError('invalid opaque frame size')
                data = await asyncio.wait_for(reader.readexactly(size),15)
                self.touched = time.monotonic()
                other = self.writers[1-side]
                if other is None: continue
                await self.budget.wait(size)
                await self.agent.budget.wait(size)
                async with self.write_locks[1-side]:
                    other.write(header+data)
                    await asyncio.wait_for(other.drain(),15)
                self.tcp_bytes += size
        except asyncio.CancelledError:
            log('leg-cancelled', route=self.route, side=side)
        except (ValueError, OSError, TimeoutError, asyncio.IncompleteReadError) as ex:
            log('leg-ended', route=self.route, side=side, error=redact(ex))
        finally:
            if self.writers[side] is writer: self.writers[side] = None
            writer.close()
            try: await writer.wait_closed()
            except OSError as ex: log('leg-close', error=redact(ex))
            self.tasks.discard(task)

    def snapshot(self):
        return dict(route=self.route, paths=self.paths, rttMs=self.rtts, tcpBytes=self.tcp_bytes,
                    udpBytes=self.udp_bytes, droppedDatagrams=self.drops, ageSeconds=round(time.monotonic()-self.created))

    async def close(self):
        if self.closed: return
        self.closed = True
        for server in self.servers: server.close()
        for server in self.servers: await server.wait_closed()
        for udp in self.udp:
            if udp is not None: udp.transport.close()
        for writer in self.writers:
            if writer is not None: writer.close()
        for task in list(self.tasks): task.cancel()
        await asyncio.gather(*list(self.tasks), return_exceptions=True)
        for child in self.children:
            if child.returncode is None: child.terminate()
        for child in self.children:
            try: await asyncio.wait_for(child.wait(),5)
            except TimeoutError:
                child.kill()
                await child.wait()
        await asyncio.gather(*self.readers)
        if self.root.exists(): shutil.rmtree(self.root)
        log('route-closed', **self.snapshot())

class Agent:
    def __init__(self, args):
        self.args, self.api = args, Api(args)
        self.pairs = {}
        self.budget = Budget(args.mbps)
        self.started = time.monotonic()
        self.errors = collections.deque(maxlen=10)
        self.last_heartbeat = 0
        self.state = 'starting'
        self.job = ChildJob()

    async def spawn(self, *command, **kwargs):
        process = await asyncio.create_subprocess_exec(*command, **kwargs)
        try: self.job.assign(process.pid)
        except Exception:
            if process.returncode is None:
                try: process.kill()
                except ProcessLookupError as ex: log('child-already-ended',error=str(ex))
            await process.wait()
            raise
        return process

    def stopping(self): return (self.api.root / 'stop.request').exists()

    async def pause(self, seconds):
        until = time.monotonic()+seconds
        while not self.stopping():
            remaining = until-time.monotonic()
            if remaining <= 0: return
            await asyncio.sleep(min(.2,remaining))

    def status(self, state):
        self.state = state
        try:
            atomic_json(self.api.root / 'status.json', dict(version=VERSION,build=BUILD,pid=os.getpid(),endpoint=self.api.endpoint,
                state=state,lastHeartbeat=self.last_heartbeat,capacity=self.args.capacity,active=len(self.pairs),mbps=self.args.mbps))
        except OSError as ex: log('status-write-error',error=redact(ex))

    def metrics(self, heartbeat=False):
        return dict(version=VERSION, uptimeSeconds=round(time.monotonic()-self.started),
                    directoryRttMs=self.api.rtt, cpuSeconds=time.process_time(), memoryBytes=memory_bytes(), pid=os.getpid(),
                    tcpBytes=sum(p.tcp_bytes for p in self.pairs.values()), udpBytes=sum(p.udp_bytes for p in self.pairs.values()),
                    routes=[p.snapshot() for p in list(self.pairs.values())[:4 if heartbeat else 32]],
                    errors=[message[:120] for message in list(self.errors)[-3:]] if heartbeat else list(self.errors))

    def diagnostics(self, path):
        pending = None
        try:
            path = Path(path)
            fd, pending = tempfile.mkstemp(prefix='.'+path.name+'-', dir=path.parent)
            os.close(fd)
            with zipfile.ZipFile(pending, 'w', zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('status.json', json.dumps(dict(endpoint=self.api.endpoint, **self.metrics()),ensure_ascii=False,indent=2))
                archive.writestr('events.jsonl', '\n'.join(json.dumps(row,ensure_ascii=False) for row in events))
            os.replace(pending,path)
            return True
        except Exception as ex:
            log('diagnostics-error',error=redact(ex))
            return False
        finally:
            if pending is not None and os.path.exists(pending):
                try: os.unlink(pending)
                except OSError as ex: log('diagnostics-cleanup-error',error=redact(ex))

    async def run(self):
        self.status('starting')
        try:
            delay = 1
            while not self.stopping():
                try:
                    await self.api.register()
                    log('registered', endpoint=self.api.endpoint, version=VERSION, capacity=self.args.capacity)
                    break
                except DirectoryTrustError as ex:
                    self.status('offline')
                    log('directory-trust-error',error=redact(ex))
                    raise
                except Exception as ex:
                    message = redact(ex)[:250]
                    self.errors.append(message)
                    self.status('offline')
                    log('register-retry',error=message,retrySeconds=delay)
                    await self.pause(delay)
                    delay = min(30,delay*2)
            while not self.stopping():
                try:
                    reply = await self.api.call('/api/v2/transit/heartbeat', dict(capacity=self.args.capacity,
                          active=len(self.pairs), mbps=self.args.mbps, metrics=self.metrics(heartbeat=True)))
                    self.last_heartbeat = int(time.time())
                    if self.state != 'online': log('online',endpoint=self.api.endpoint,version=VERSION,build=BUILD)
                    self.status('online')
                    for route, pair in list(self.pairs.items()):
                        lease = reply['leases'].get(route)
                        if lease is None:
                            await pair.close()
                            del self.pairs[route]
                        else: pair.lease = lease
                    for envelope in reply['jobs']:
                        ticket = verify(envelope, '/transit-ticket/v2', self.api.server_key)
                        route = ticket['route']
                        if ticket['c']!=self.api.endpoint or ticket['expires']<=time.time() or not re.fullmatch('[0-9a-f]{32}',route):
                            raise ValueError('invalid transit ticket binding')
                        for name in ('a','b','aSession','bSession'):
                            if not re.fullmatch('[0-9a-f]{64}',ticket[name]): raise ValueError('invalid ticket peer')
                        if route in self.pairs or len(self.pairs)>=self.args.capacity: continue
                        pair = Pair(self,ticket)
                        self.pairs[route] = pair
                        try: await pair.start()
                        except Exception:
                            await pair.close()
                            del self.pairs[route]
                            raise
                except Exception as ex:
                    message=redact(ex)[:250]
                    self.errors.append(message)
                    self.status('offline')
                    log('heartbeat-error', error=message)
                for route,pair in list(self.pairs.items()):
                    if pair.lease<time.time() or time.monotonic()-pair.touched>90 or any(p.returncode is not None for p in pair.children):
                        await pair.close()
                        del self.pairs[route]
                self.status(self.state)
                if self.args.diagnostics: self.diagnostics(self.args.diagnostics)
                await self.pause(3)
        finally:
            try:
                results = await asyncio.gather(*(p.close() for p in self.pairs.values()),return_exceptions=True)
                for result in results:
                    if isinstance(result,BaseException): log('route-shutdown-error',error=redact(result))
                self.pairs.clear()
                if self.args.diagnostics: self.diagnostics(self.args.diagnostics)
            finally:
                self.job.close()
                self.status('stopped')
                log('stopped',endpoint=self.api.endpoint)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ard', required=True, help='Trusted ARD pre.6 executable (absolute path)')
    parser.add_argument('--server', default='https://f.visnova.cn')
    parser.add_argument('--relay', default='http://175.27.160.144:8080')
    parser.add_argument('--relay-key', default=DEFAULT_RELAY_KEY)
    parser.add_argument('--data', default='ardtransit-data')
    parser.add_argument('--capacity', type=int, default=8)
    parser.add_argument('--mbps', type=int, default=50)
    parser.add_argument('--diagnostics', help='Write a redacted diagnostic ZIP on every heartbeat and shutdown')
    parser.add_argument('--test-local-directory', action='store_true', help=argparse.SUPPRESS)
    args=parser.parse_args()
    uri=urllib.parse.urlparse(args.server)
    if uri.username or uri.scheme!='https' and not (args.test_local_directory and uri.scheme=='http' and uri.hostname in ('127.0.0.1','localhost','::1')):
        parser.error('directory must be trusted HTTPS')
    if not 1<=args.capacity<=32 or not 1<=args.mbps<=10000: parser.error('invalid capacity or bandwidth')
    try:
        with Instance(args.data):
            handler = configure_log(args.data)
            try:
                args.ard=str(Path(args.ard).resolve(strict=True))
                asyncio.run(Agent(args).run())
            except Exception as ex:
                log('fatal',error=redact(ex))
                raise
            finally:
                runtime_log.removeHandler(handler)
                handler.close()
    except KeyboardInterrupt: log('stopped')

if __name__=='__main__': main()
