"""ArdUi trusted directory and short-lived connection authorization (WSGI).
Production: uvicorn --host 127.0.0.1 --port 8091 --workers 1 arduiserver:asgi (one process: waiting requests are woken in memory)
"""
import asyncio
import base64
from concurrent.futures import ThreadPoolExecutor
import hashlib
import io
import json
import os
import re
import secrets
import sqlite3
import string
import tempfile
import threading
import time
from http import HTTPStatus
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
from cryptography.hazmat.primitives import serialization

DB = os.environ.get('ARDUI_DATABASE', '/var/lib/arduiserver/devices.sqlite3')
CODE = re.compile(r'[A-Z0-9]{6}\Z')
ENDPOINT = re.compile(r'[0-9a-f]{64}\Z')
TICKET = re.compile(r'[0-9a-f]{32}\Z')
COMPUTER_NAME = re.compile(r'[^\x00-\x1f\x7f]{1,64}\Z')
VERSION = 'v6.pre2'
# Network probe (see probe_udp.py): this server only mints short-lived tokens, each bound to the caller's IP address.
PROBE_KEY = os.environ.get('ARDUI_PROBE_KEY', DB + '.probe-key')
PROBE_HOST = os.environ.get('ARDUI_PROBE_HOST', '')
PROBE_PORT = int(os.environ.get('ARDUI_PROBE_PORT', '7843'))
# Clients older than this are refused with 426 (the header is "v<major>.pre<n>", sent by ArdUi since v5.pre1; older clients send none).
# v6 waits for news instead of polling every few seconds (WAIT_SECONDS below); since v6.pre2 the controller names the session's
# ArdFerry in its signed request (both sides use it). Older clients are not supported. ARDUI_MIN_CLIENT=v<major>.pre<n> overrides it (e.g. while a release is rolling out).
CLIENT = re.compile(r'v(\d{1,3})\.pre(\d{1,3})\Z')
_min = CLIENT.fullmatch(os.environ.get('ARDUI_MIN_CLIENT', 'v6.pre2'))
MIN_CLIENT = (int(_min.group(1)), int(_min.group(2))) if _min else (6, 2)
MIN_TEXT = 'v%d.pre%d' % MIN_CLIENT
# What this network's clients need besides this server: where M's relay directory and address discovery are, and M's key.
# Written by the operator (selfhost.sh / deploy.sh); see ROLES.md.
NETWORK_FILE = os.environ.get('ARDUI_NETWORK', '/etc/ardui/network.json')

def check_client(environ):
    match = CLIENT.fullmatch(environ.get('HTTP_X_ARDUI_CLIENT', ''))
    if match is None or (int(match.group(1)), int(match.group(2))) < MIN_CLIENT:
        raise ApiError(426, f'客户端版本过旧，请升级到 ArdUi {MIN_TEXT} 或更高版本。')

# ---- waiting for news (long polling) -----------------------------------------------------------------------------------
# A host's poll and a controller's look at its connection request are answered at once when there is something new for them,
# otherwise as soon as something arrives (or after WAIT_SECONDS; then they ask again): no fixed polling interval stands between a
# connection request and its delivery. A waiting request holds no thread, only its connection and a future (see asgi below).
WAIT_SECONDS = 12
_news_lock = threading.Lock()
_news = {}             # key -> version, bumped by notify(); any thread
_news_waiters = {}     # key -> set of asyncio futures; event loop thread only
_news_loop = None

def news(key):
    with _news_lock:
        return _news.get(key, 0)

def notify(*keys):
    with _news_lock:
        if len(_news) > 100000:
            _news.clear()   # a waiting request then sees a changed version and simply asks again
        for key in keys:
            _news[key] = _news.get(key, 0) + 1
    loop = _news_loop
    if loop is not None:
        loop.call_soon_threadsafe(_wake, keys)

def _wake(keys):
    for key in keys:
        for future in _news_waiters.pop(key, ()):
            if not future.done():
                future.set_result(None)

class Wait(Exception):
    """Raised by a handler with nothing new to report: the asynchronous front calls `again` once `key` has news after `version`
    (read before the handler looked), or after WAIT_SECONDS. Under a plain WSGI server (the tests) `again` is called at once."""
    def __init__(self, key, version, again):
        super().__init__(key)
        self.key, self.version, self.again = key, version, again

def network():
    try:
        with open(NETWORK_FILE, encoding='utf-8') as source:
            value = json.load(source)
    except FileNotFoundError:
        raise ApiError(503, '此网络尚未配置中继目录。')
    except (OSError, ValueError):
        raise ApiError(503, '此网络的中继配置无效。')
    if (not isinstance(value, dict) or set(value) != {'relayDirectory', 'master', 'masterKey'} or
            not all(isinstance(value[k], str) and value[k].startswith('https://') and len(value[k]) <= 300 for k in ('relayDirectory', 'master')) or
            not isinstance(value['masterKey'], str) or not re.fullmatch(r'spki:[0-9a-f]{2,512}', value['masterKey'])):
        raise ApiError(503, '此网络的中继配置无效。')
    return value

# The one-step ArdFerry installers (ardferry.sh, ardferry.ps1, built in ard2's native_relay/deploy) and a copy of the official iroh-relay archives
# for servers that cannot reach GitHub. The script fetched from this M joins this M's network: its domain is filled in from the host
# the request was made to. The archives are served only if they match the SHA-256 published with the iroh release.
FERRY_FILES = os.environ.get('ARDUI_FERRY_FILES', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ardferry'))
FERRY_ARCHIVES = {
    'iroh-relay-v1.1.0-x86_64-unknown-linux-musl.tar.gz': '9a68108b824e4164ad2eec729cf0e8167e4cb50581cb745a242bace135df7614',
    'iroh-relay-v1.1.0-aarch64-unknown-linux-musl.tar.gz': '1b4261b6dd0d17ae9a7516aa6d122b296a77678bac174ce30262702c9f91cb00',
    'iroh-relay-v1.1.0-x86_64-pc-windows-msvc.zip': '052bd32704afcb4492a0cd8e619e1bb47c69cbe8ecaa89907c22033634b3da1c',
}
FERRY_SCRIPTS = {'/ardferry.sh': 'text/x-shellscript; charset=utf-8', '/ardferry.ps1': 'text/plain; charset=utf-8'}
_ferry_checked = {}

def ferry_download(path, environ, start_response):
    limit('ardferry:' + (environ.get('HTTP_X_REAL_IP') or environ.get('REMOTE_ADDR', 'unknown')), 30, 60)
    if path in FERRY_SCRIPTS:
        host = environ.get('HTTP_HOST', '')
        if not re.fullmatch(r'[A-Za-z0-9.-]{1,253}(:[0-9]{1,5})?', host):
            raise ApiError(400, '无法确定 M 的域名。')
        try:
            with open(os.path.join(FERRY_FILES, path[1:]), encoding='utf-8', newline='') as source:
                script = source.read()
        except OSError:
            raise ApiError(404, '此 M 未提供 ArdFerry 安装脚本。')
        body = script.replace("'@M@'", "'%s'" % host.removesuffix(':443'), 1).encode()
        start_response('200 OK', [('Content-Type', FERRY_SCRIPTS[path]), ('Content-Length', str(len(body))),
                                  ('Cache-Control', 'no-cache'), ('X-Content-Type-Options', 'nosniff')])
        return [body]
    name = path[len('/ardferry/'):]
    if name not in FERRY_ARCHIVES:
        raise ApiError(404, '没有这个文件。')
    file = os.path.join(FERRY_FILES, name)
    try:
        stat = os.stat(file)
        if _ferry_checked.get(name) != (stat.st_size, stat.st_mtime):
            with open(file, 'rb') as source:
                if hashlib.sha256(source.read()).hexdigest() != FERRY_ARCHIVES[name]:
                    raise ApiError(404, '没有这个文件。')
            _ferry_checked[name] = (stat.st_size, stat.st_mtime)
        source = open(file, 'rb')
    except OSError:
        raise ApiError(404, '没有这个文件。')
    start_response('200 OK', [('Content-Type', 'application/zip' if name.endswith('.zip') else 'application/gzip'), ('Content-Length', str(stat.st_size)),
                              ('Cache-Control', 'public, max-age=86400'), ('X-Content-Type-Options', 'nosniff')])
    wrapper = environ.get('wsgi.file_wrapper')
    if wrapper:
        return wrapper(source, 1 << 16)
    with source:
        return [source.read()]

def check_grant(value, controller, target):
    try:
        if value['endpoint'] != target: raise ValueError('grant signer')
        message = f"ArdUiServer/1\n/grant/v1\n{target}\n{value['issuedAt']}\n{value['nonce']}\n{value['payload']}".encode()
        Ed25519PublicKey.from_public_bytes(bytes.fromhex(target)).verify(base64.b64decode(value['signature'], validate=True), message)
        grant = json.loads(base64.b64decode(value['payload'], validate=True))
        if grant['controllerEndpoint'] != controller or grant['targetEndpoint'] != target: raise ValueError('grant pair')
    except (ValueError, KeyError, TypeError, InvalidSignature):
        raise ApiError(403, '转发需要被控端签名的永久授权。')

class ApiError(Exception):
    def __init__(self, status, message): self.status, self.message = status, message

class Connection(sqlite3.Connection):
    def __exit__(self, *args):
        try: return super().__exit__(*args)
        finally: self.close()

def database():
    db = sqlite3.connect(DB, timeout=15, factory=Connection)
    db.row_factory = sqlite3.Row
    db.execute('PRAGMA foreign_keys=ON')
    return db

def initialize():
    os.makedirs(os.path.dirname(os.path.abspath(DB)), exist_ok=True)
    with database() as db:
        db.execute('PRAGMA journal_mode=WAL')
        db.executescript('''
        CREATE TABLE IF NOT EXISTS devices(
          endpoint TEXT PRIMARY KEY, code TEXT UNIQUE NOT NULL,
          enabled INTEGER NOT NULL DEFAULT 0,
          epoch INTEGER NOT NULL DEFAULT 0, seen INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS requests(
          id TEXT PRIMARY KEY, target TEXT NOT NULL REFERENCES devices(endpoint) ON DELETE CASCADE,
          caller TEXT NOT NULL REFERENCES devices(endpoint) ON DELETE CASCADE,
          client_session TEXT NOT NULL, epoch INTEGER NOT NULL,
          expires INTEGER NOT NULL, status TEXT NOT NULL, proof TEXT NOT NULL, offer TEXT);
        CREATE INDEX IF NOT EXISTS requests_target ON requests(target, status, expires);
        CREATE TABLE IF NOT EXISTS nonces(endpoint TEXT, nonce TEXT, expires INTEGER,
          PRIMARY KEY(endpoint,nonce));
        CREATE TABLE IF NOT EXISTS limits(name TEXT, window INTEGER, count INTEGER,
          PRIMARY KEY(name,window));
        DROP TABLE IF EXISTS transit_routes;
        DROP TABLE IF EXISTS transit_nodes;
        CREATE TABLE IF NOT EXISTS ferry_reports(
          minute INTEGER NOT NULL, node TEXT NOT NULL, device TEXT NOT NULL,
          probes INTEGER NOT NULL, ok INTEGER NOT NULL, rtt REAL, sessions INTEGER NOT NULL, session_rtt REAL, mbps REAL,
          PRIMARY KEY(minute,node,device));
        CREATE INDEX IF NOT EXISTS ferry_reports_minute ON ferry_reports(minute);
        ''')
        # Computer name shown to controllers that hold a signed grant; added after the first release.
        if 'name' not in {row[1] for row in db.execute('PRAGMA table_info(devices)')}:
            db.execute("ALTER TABLE devices ADD COLUMN name TEXT NOT NULL DEFAULT ''")
    os.chmod(DB, 0o600)

def limit(name, count, seconds):
    now = int(time.time())
    window = now // seconds * seconds
    with database() as db:
        db.execute('DELETE FROM limits WHERE window < ?', (now-7200,))
        db.execute('INSERT INTO limits VALUES(?,?,1) ON CONFLICT(name,window) DO UPDATE SET count=count+1', (name,window))
        value = db.execute('SELECT count FROM limits WHERE name=? AND window=?', (name,window)).fetchone()[0]
    if value > count: raise ApiError(429, '请求过于频繁，请稍后再试。')

def text(data, key, pattern):
    value = data.get(key)
    if not isinstance(value,str) or not pattern.fullmatch(value): raise ApiError(400, '请求格式无效。')
    return value

def authenticate(path, body, ip):
    limit('ip:'+ip, 1200, 60)
    try:
        envelope = json.loads(body)
        endpoint = text(envelope, 'endpoint', ENDPOINT)
        nonce = text(envelope, 'nonce', TICKET)
        issued = envelope['issuedAt']
        if type(issued) is not int or abs(time.time()-issued)>120:
            raise ApiError(401, '设备时间与服务器相差超过两分钟，请同步系统时间。')
        payload = envelope['payload']
        if not isinstance(payload,str) or len(payload)>20000: raise ValueError()
        signed = f'ArdUiServer/1\n{path}\n{endpoint}\n{issued}\n{nonce}\n{payload}'.encode()
        signature = base64.b64decode(envelope['signature'], validate=True)
        Ed25519PublicKey.from_public_bytes(bytes.fromhex(endpoint)).verify(signature, signed)
        data = json.loads(base64.b64decode(payload,validate=True))
        if not isinstance(data,dict): raise ValueError()
    except ApiError: raise
    except (ValueError, KeyError, TypeError, InvalidSignature): raise ApiError(401, '设备签名无效。')
    limit('device:'+endpoint, 240, 60)
    try:
        with database() as db:
            db.execute('DELETE FROM nonces WHERE expires < ?', (int(time.time()),))
            db.execute('INSERT INTO nonces VALUES(?,?,?)',(endpoint,nonce,issued+240))
    except sqlite3.IntegrityError: raise ApiError(401, '请求已经使用，请重试。')
    return endpoint,data,envelope

def probe_key():
    folder = os.path.dirname(os.path.abspath(PROBE_KEY))
    os.makedirs(folder, exist_ok=True)
    if not os.path.exists(PROBE_KEY):
        fd, pending = tempfile.mkstemp(prefix='.probe-key-', dir=folder)
        try:
            with os.fdopen(fd, 'wb') as out:
                out.write(secrets.token_bytes(32)); out.flush(); os.fsync(out.fileno())
            try: os.link(pending, PROBE_KEY)
            except FileExistsError: pass
        finally: os.unlink(pending)
    with open(PROBE_KEY, 'rb') as source: return source.read()

def probe_ip16(text):
    import ipaddress
    try: address = ipaddress.ip_address(text.split('%')[0])
    except ValueError: raise ApiError(400, '无法确定来源地址。')
    if address.version == 6 and address.ipv4_mapped is not None: address = address.ipv4_mapped
    return (b'\x00' * 10 + b'\xff\xff' + address.packed if address.version == 4 else address.packed), str(address)

def probe_token(lifetime=120):
    # nonce + expiry + HMAC(key, "ARDP-token2" + nonce + expiry): it names no address (a machine behind several internet lines leaves through
    # different ones); the probe service makes every source address prove itself with a cookie round trip before it sends anything there.
    import hashlib, hmac, struct
    nonce = secrets.token_bytes(8); expiry = struct.pack('>I', int(time.time()) + lifetime)
    return nonce + expiry + hmac.new(probe_key(), b'ARDP-token2\0' + nonce + expiry, hashlib.sha256).digest()[:16]

def registered(db, endpoint):
    row = db.execute('SELECT * FROM devices WHERE endpoint=?',(endpoint,)).fetchone()
    if row is None: raise ApiError(403, '请先注册本机。')
    return row

# ---- ArdFerry quality, as the clients see it ----------------------------------------------------------------------------------
# ArdFerry nodes are untrusted and report nothing about themselves; the clients that use them report what they measured, a few
# hundred bytes every REPORT_SECONDS (the server can raise it to lighten its load). Kept per minute for a day, shown only in summary.
REPORT_SECONDS = int(os.environ.get('ARDUI_FERRY_REPORT_SECONDS', '30'))
FERRY_NAME = re.compile(r'[A-Za-z0-9_-]{1,64}\Z')
M_STATUS = os.environ.get('ARDUI_M_STATUS', 'https://127.0.0.1:8080/status')
ADMIN_TOKEN = os.environ.get('ARDUI_ADMIN_TOKEN', '')

def number(value, low, high, integer=False):
    if value is None and not integer: return None
    if isinstance(value, bool) or not isinstance(value, int if integer else (int, float)) or not low <= value <= high:
        raise ApiError(400, 'ArdFerry 报告格式无效。')
    return value

def ferry_report(endpoint, data, now):
    ferries = data.get('ferries')
    if set(data) != {'ferries'} or not isinstance(ferries, list) or len(ferries) > 16:
        raise ApiError(400, 'ArdFerry 报告格式无效。')
    rows = []
    for entry in ferries:
        if not isinstance(entry, dict) or set(entry) != {'node', 'probes', 'ok', 'rttMs', 'sessions', 'sessionRttMs', 'mbps'}:
            raise ApiError(400, 'ArdFerry 报告格式无效。')
        node = text(entry, 'node', FERRY_NAME)
        probes = number(entry['probes'], 0, 1000, True); ok = number(entry['ok'], 0, probes, True)
        rows.append((now // 60, node, endpoint, probes, ok, number(entry['rttMs'], 0, 60000), number(entry['sessions'], 0, 100, True),
                     number(entry['sessionRttMs'], 0, 60000), number(entry['mbps'], 0, 100000)))
    limit('ferry-report:' + endpoint, 4, 60)   # every 30 s, plus a report when a relayed session ends
    with database() as db:
        registered(db, endpoint)
        db.execute('DELETE FROM ferry_reports WHERE minute < ?', (now // 60 - 24 * 60,))
        db.executemany('INSERT OR REPLACE INTO ferry_reports VALUES(?,?,?,?,?,?,?,?,?)', rows)
    return {'ok': True, 'next': REPORT_SECONDS}

def median(values):
    values = sorted(v for v in values if v is not None)
    if not values: return None
    middle = len(values) // 2
    return round(values[middle] if len(values) % 2 else (values[middle - 1] + values[middle]) / 2, 1)

# Where an ArdFerry roughly is, for the map: looked up on this server in DB-IP's free database (CC BY 4.0; no address leaves the
# server). Public: country and region, the point rounded to whole degrees, so a node at someone's home does not give away the city.
GEOIP = os.environ.get('ARDUI_GEOIP', os.path.join(os.path.dirname(os.path.abspath(DB)), 'dbip-city-lite.mmdb'))
_geo = {'reader': None, 'mtime': None}

CN_REGIONS = {'Beijing': '北京', 'Tianjin': '天津', 'Hebei': '河北', 'Shanxi': '山西', 'Inner Mongolia': '内蒙古', 'Liaoning': '辽宁',
              'Jilin': '吉林', 'Heilongjiang': '黑龙江', 'Shanghai': '上海', 'Jiangsu': '江苏', 'Zhejiang': '浙江', 'Anhui': '安徽',
              'Fujian': '福建', 'Jiangxi': '江西', 'Shandong': '山东', 'Henan': '河南', 'Hubei': '湖北', 'Hunan': '湖南',
              'Guangdong': '广东', 'Guangxi': '广西', 'Hainan': '海南', 'Chongqing': '重庆', 'Sichuan': '四川', 'Guizhou': '贵州',
              'Yunnan': '云南', 'Tibet': '西藏', 'Shaanxi': '陕西', 'Gansu': '甘肃', 'Qinghai': '青海', 'Ningxia': '宁夏',
              'Xinjiang': '新疆', 'Taiwan': '台湾', 'Hong Kong': '香港', 'Macao': '澳门', 'Macau': '澳门'}

def geo_reader():
    try:
        mtime = os.path.getmtime(GEOIP)
    except OSError:
        return None
    if _geo['mtime'] != mtime:   # picked up again when the monthly file is replaced
        try:
            import maxminddb
            _geo['reader'], _geo['mtime'] = maxminddb.open_database(GEOIP), mtime
        except Exception:
            _geo['reader'], _geo['mtime'] = None, mtime
    return _geo['reader']

def locate(address, admin):
    import ipaddress
    reader = geo_reader()
    try:
        ip = ipaddress.ip_address(address.strip('[]'))
    except ValueError:
        return None
    if reader is None or not ip.is_global:
        return None
    record = reader.get(str(ip)) or {}
    name = lambda part: (part or {}).get('names', {}).get('zh-CN') or (part or {}).get('names', {}).get('en')
    point = record.get('location') or {}
    if point.get('latitude') is None:
        return None
    regions = record.get('subdivisions') or []
    region = name(regions[0]) if regions else None
    if (record.get('country') or {}).get('iso_code') in ('CN', 'TW', 'HK', 'MO'):
        region = CN_REGIONS.get(region, region)   # DB-IP names Chinese provinces in English only
    found = dict(country=name(record.get('country')), region=region,
                 lat=round(point['latitude']), lon=round(point['longitude']))
    if admin:
        found.update(city=name(record.get('city')), lat=point['latitude'], lon=point['longitude'])
    return found

def node_location(node, admin):
    # the address M sees the node at, else the hosts of its published entrances (IPv4 first)
    import urllib.parse
    hosts = [node.get('address')] + [urllib.parse.urlsplit(u).hostname for f in ('ipv4', 'ipv6')
                                     for u in (node.get('families', {}).get(f, {}).get('urls') or [])]
    for host in hosts:
        if host and (found := locate(host, admin)):
            return found
    return None

def ferry_view(admin=False):
    """M's own measurements of each ArdFerry next to what the clients measured. The public view carries no addresses or errors."""
    import ssl, urllib.request
    measured, note = [], None
    try:
        context = ssl.create_default_context(); context.check_hostname = False; context.verify_mode = ssl.CERT_NONE   # this host only
        with urllib.request.urlopen(M_STATUS, timeout=3, context=context) as response:
            measured = json.loads(response.read(1 << 20))['nodes']
    except Exception:
        note = 'M 的测量暂时取不到。'
    now = int(time.time())
    with database() as db:
        day = db.execute('SELECT * FROM ferry_reports WHERE minute >= ?', (now // 60 - 24 * 60,)).fetchall()
    recent = [r for r in day if r['minute'] >= now // 60 - 2]
    clients = {}
    for row in day:
        c = clients.setdefault(row['node'], dict(devices_24h=set(), probes_24h=0, ok_24h=0, rtt=[], session_rtt=[], mbps=[]))
        c['devices_24h'].add(row['device']); c['probes_24h'] += row['probes']; c['ok_24h'] += row['ok']
        c['rtt'].append(row['rtt']); c['session_rtt'].append(row['session_rtt']); c['mbps'].append(row['mbps'])
    latest = {}
    for row in recent:   # each device's latest report in the last two minutes says how many of its sessions run over the node now
        key = (row['node'], row['device'])
        if key not in latest or row['minute'] > latest[key]['minute']: latest[key] = row
    nodes = {n['node']: dict(n, clients=None) for n in measured}
    for name, c in clients.items():
        now_rows = [r for (node, _), r in latest.items() if node == name]
        summary = dict(devices_now=len(now_rows), sessions_now=sum(r['sessions'] for r in now_rows), devices_24h=len(c['devices_24h']),
                       client_rtt_ms_median=median(c['rtt']),
                       client_success_rate_24h=round(c['ok_24h'] / c['probes_24h'], 4) if c['probes_24h'] else None,
                       session_rtt_ms_median=median(c['session_rtt']), session_mbps_median=median(c['mbps']))
        # The page lists the ArdFerry nodes M knows now (M forgets a silent one after 10 minutes). Client reports about any other
        # node are history; only when M's own measurements cannot be had are nodes reported in the last 10 minutes shown.
        if name in nodes:
            nodes[name]['clients'] = summary
        elif note and any(r['node'] == name and r['minute'] >= now // 60 - 10 for r in day):
            nodes[name] = dict(node=name, families={}, clients=summary)
    result = []
    for node in sorted(nodes.values(), key=lambda n: n['node']):
        node['location'] = node_location(node, admin)
        if not admin:
            node = {k: v for k, v in node.items() if k not in ('address', 'first_seen')}
            node['families'] = {f: {k: v for k, v in fam.items() if k not in ('urls', 'last_error')} for f, fam in node.get('families', {}).items()}
        result.append(node)
    devices_now = len({device for (_, device) in latest})   # one device reports on every node: count it once
    return dict(nodes=result, devices_now=devices_now, report_seconds=REPORT_SECONDS, note=note, generated_at=now,
                located=any(n.get('location') for n in result))

FERRY_PAGE = r'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ArdFerry 状态</title>
<style>
:root{--landdot:#d5d9e0;--bg:#f6f7f9;--card:#fff;--ink:#16181d;--muted:#6b7280;--line:#e6e8ec;--good:#16a34a;--warn:#d97706;--bad:#dc2626;--accent:#ea580c;--track:#eef0f3;--shadow:0 1px 2px rgba(16,24,40,.04),0 4px 16px rgba(16,24,40,.06)}
@media (prefers-color-scheme:dark){:root{--landdot:#2c313c;--bg:#0f1115;--card:#171a21;--ink:#e8eaed;--muted:#9aa1ad;--line:#262a33;--track:#232733;--shadow:0 1px 2px rgba(0,0,0,.4)}}
*{box-sizing:border-box}html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 system-ui,-apple-system,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif}
main{max-width:1180px;margin:0 auto;padding:28px 16px 48px}
header{display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;gap:8px 16px;margin-bottom:20px}
h1{font-size:22px;margin:0;letter-spacing:.2px}h1 span{color:var(--accent)}
.updated{color:var(--muted);font-size:12px}.updated i{display:inline-block;width:7px;height:7px;border-radius:50%;background:var(--good);margin-right:6px;vertical-align:1px}
.note{background:color-mix(in srgb,var(--warn) 12%,var(--card));border:1px solid color-mix(in srgb,var(--warn) 35%,transparent);color:var(--ink);padding:10px 14px;border-radius:10px;margin-bottom:16px}
.hero{position:relative;margin-bottom:22px}.summary{display:grid;grid-template-columns:repeat(4,auto);gap:4px 22px;justify-content:start;background:var(--card);border:1px solid var(--line);border-radius:16px;box-shadow:var(--shadow);padding:12px 16px}.summary .tile{background:none;border:0;box-shadow:none;padding:0}.summary .tile b{font-size:20px}.hero.with-map .summary{position:absolute;top:12px;left:12px;background:color-mix(in srgb,var(--card) 82%,transparent);backdrop-filter:blur(8px);-webkit-backdrop-filter:blur(8px);z-index:1}@media (max-width:640px){.hero.with-map .summary{position:static;margin-top:10px;backdrop-filter:none;background:var(--card)}.summary{grid-template-columns:repeat(4,1fr);gap:4px 10px}.summary .tile b{font-size:18px}}
.tile{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 16px;box-shadow:var(--shadow)}
.tile b{display:block;font-size:26px;line-height:1.2;font-variant-numeric:tabular-nums}.tile span{color:var(--muted);font-size:12px}
.map{background:var(--card);border:1px solid var(--line);border-radius:16px;box-shadow:var(--shadow);padding:10px 12px}.map svg{display:block;width:100%;height:auto}.land{fill:var(--landdot)}.mark{stroke:var(--card);stroke-width:2}.halo{opacity:.25;animation:pulse 2.4s ease-out infinite;transform-box:fill-box;transform-origin:center}@keyframes pulse{0%{transform:scale(.6);opacity:.45}100%{transform:scale(2.4);opacity:0}}@media (prefers-reduced-motion:reduce){.halo{animation:none}}.where{color:var(--muted);font-size:12px;margin-top:-8px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:16px}
.node{background:var(--card);border:1px solid var(--line);border-radius:16px;box-shadow:var(--shadow);padding:18px;display:flex;flex-direction:column;gap:14px}
.node.down{opacity:.78}
.top{display:flex;align-items:center;justify-content:space-between;gap:10px}
.name{font:600 15px ui-monospace,SFMono-Regular,Consolas,monospace;word-break:break-all}
.badge{font-size:11px;color:var(--muted);border:1px solid var(--line);border-radius:999px;padding:1px 8px;white-space:nowrap}
.fams{display:flex;gap:6px;flex-wrap:wrap}
.pill{font-size:12px;font-weight:600;border-radius:999px;padding:2px 10px;display:inline-flex;align-items:center;gap:6px}
.pill::before{content:"";width:7px;height:7px;border-radius:50%;background:currentColor}
.on{color:var(--good);background:color-mix(in srgb,var(--good) 12%,transparent)}.off{color:var(--muted);background:var(--track)}
.cols{display:grid;grid-template-columns:1fr 1fr;gap:14px}
@media (max-width:420px){.cols{grid-template-columns:1fr}}
.col h3{margin:0 0 8px;font-size:11px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:var(--muted)}
.row{display:flex;justify-content:space-between;gap:8px;padding:3px 0;font-variant-numeric:tabular-nums}.row span{color:var(--muted)}
.bar{height:6px;border-radius:999px;background:var(--track);overflow:hidden;margin:2px 0 6px}.bar i{display:block;height:100%;border-radius:inherit}
.empty{color:var(--muted);text-align:center;padding:40px 0}
.map{position:relative}.credit{position:absolute;right:14px;bottom:8px;font-size:10px;color:var(--muted);text-decoration:none;opacity:.7}
</style></head><body><main>
<header><h1><span>ArdFerry</span> 状态</h1><div class="updated"><i></i><span id="updated"></span></div></header>
<div id="note"></div><section class="hero" id="hero"><section class="map" id="map" hidden></section><section class="summary" id="summary"></section></section><section class="grid" id="nodes"></section>

</main>
<script id="data" type="application/json">__DATA__</script>
<script>
const $=id=>document.getElementById(id);
const esc=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const v=(x,u='')=>x==null?'—':`${x}${u}`;
const pct=x=>x==null?'—':`${(x*100).toFixed(x>=.999?0:1)}%`;
const tone=x=>x==null?'var(--track)':x>=.98?'var(--good)':x>=.9?'var(--warn)':'var(--bad)';
const bar=x=>`<div class="bar"><i style="width:${x==null?0:Math.max(2,x*100)}%;background:${tone(x)}"></i></div>`;
const MASK={cols:180,rows:90,step:2,bits:'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAf4AP/AAAAAAAAAAAAAAAAAAAAAAAAX/z///+AAAAAAAABAAAAAAAAAAAAAYd8P///wAA+AAAAAA8AAAAAAAAAAAwAnw////4AAIAAAAAAGAAAAAAAAAAADivwAf//wAAAAADAAf/wAHYAAAAAADoi3sAP//gAAAAAMAD///sAAAAgBgACfwz/AD/+gAAAwAEHf///+/8gBAP/7/nJdjwD/+AAAH/AA7f///////f4P//////h8H/gAAAf/6//f////////Mf/////9H4D8AeAA+ev///////////AP/////4A0B4AAAD5/////////////Af3////gHgA4AAAH5///////////LwAHgH///gHkAAAAAH4/////////+CIAABAB///4D+AAAAGCx/////////4A8AAIAAf///n/gAAAOCD/////////wA4AAAAAf///n/wAAAbP///////////AgAAAAAP/////wAAADf//////////9AAAAAAAF////0YAAAB///////////9AAAAAAAD////8EAAAB///////////5AAAAAAAD////2AAAAB/f5fP//////wAAAAAAAD////gAAAAfxnwPP//////jAAAAAAAD////AAAAAPCb3/n/////+CAAAAAAAD///8AAAAAfALf/n////+ECAAAAAAAB///8AAAAAGHQP/n/////mMAAAAAAAA///8AAAAAH+Ai///////E8AAAAAAAAf//wAAAAAP/AA///////BgAAAAAAAAP//gAAAAAf/73///////gAAAAAAAAAD/AQAAAAAf////f/////gAAAAAAAAAF+AQAAAAB///+/n/////AAAAAAAAAAC+AAAAAAB///+f0H////AAAAAAAAAAAeAwAAAAD////f/B///8gAAAAAAAAAAeGEAAAAH////v+B/z/AAAAAAAAAAAAPMAgAAAD////n+A/B+gAAAAAAAAAAAD8AAAAAD////n4AeB/AgAAAAAAAAAAAPAAAAAH////3gAcAfAgAAAAAAAAAAADAAAAAD////6AAcAfggAAAAAAAAAAABDwAAAD////8wAMATAIAAAAAAAAAAAAr/AAAB/////gAKASAAAAAAAAAAAAAAH/gAAA/////gACAAAIAAAAAAAAAAAAH/8AAAaH///AAAAsGAAAAAAAAAAAAAH/+AAAAB//+AAAAUOAAAAAAAAAAAAAP/+AAAAB//8AAAAYegAAAAAAAAAAAAP//gAAAD//4AAAAMeBgAAAAAAAAAAAP//8AAAB//wAAAAGdiuAAAAAAAAAAAf///AAAA//wAAAACAQHgAAAAAAAAAAP///gAAA//wAAAABwAHwgAAAAAAAAAH///AAAA//wAAAAACIDQIAAAAAAAAAH//+AAAAf/wAAAAAAAAAAAAAAAAAAAD//+AAAA//wgAAAAABxAAAAAAAAAAAD//+AAAA//wgAAAAAPxgBAAAAAAAAAA//8AAAA//jgAAAAAf5gAAAAAAAAAAAf/8AAAA//DgAAAAAf/gAAAAAAAAAAAf/8AAAAf/DAAAAAD//4CAAAAAAAAAAf/wAAAAf/DAAAAAH//4AAAAAAAAAAAf/AAAAAf+CAAAAAH//8AAAAAAAAAAAf/AAAAAP8AAAAAAH//+AAAAAAAAAAA/+AAAAAP8AAAAAAH//+AAAAAAAAAAA/+AAAAAH4AAAAAAD//+AAAAAAAAAAA/8AAAAAHwAAAAAADwf8AAAAAAAAAAA/gAAAAAAAAAAAAACAH4AIAAAAAAAAB/wAAAAAAAAAAAAAAAD4AEAAAAAAAAB+AAAAAAAAAAAAAAAAAAAGAAAAAAAAB6AAAAAAAAAAAAAAAAAwAMAAAAAAAAA8AAAAAAAAAAAAAAAAAQAYAAAAAAAAB4AAAAAAAAAAAAAAAAAAAwAAAAAAAAB4AAAAAAAAAAAAAAAAAAAAAAAAAAAADwAAAAAAAAAACAAAAAAAAAAAAAAAAADgAAAAAAAAAAAAAAAAAAAAAAAAAAAABwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMAAAAAAAAAeAAIP+f/gAAAAAAAAAAAMAAAAAAABP/+H//////AAAAAAAAAAA+AAAAAf////8////////AAAAAAAOEAPAAAB///////////////gAAAP//T//8AAAH//////////////+AAAH/////4AAAH///////////////8AAE//////4ABw////////////////8AAAD//////gCA////////////////wAAAf/////////////////////////+A/4A///////////////////////////////////////////////////////////////////////////////////////'};
const maskBits=Uint8Array.from(atob(MASK.bits),c=>c.charCodeAt(0));
const TOP=84,BOTTOM=-58,C=6;   // latitudes shown, and the size of one 2-degree cell in the drawing
const px=lon=>(lon+180)/MASK.step*C, py=lat=>(TOP-lat)/MASK.step*C;
let landDots=null;
function land(){
  if(landDots)return landDots;
  const out=[];const r0=Math.round((90-TOP)/MASK.step),r1=Math.round((90-BOTTOM)/MASK.step);
  for(let r=r0;r<r1;r++)for(let c=0;c<MASK.cols;c++){const i=r*MASK.cols+c;
    if(maskBits[i>>3]&(0x80>>(i&7)))out.push(`<circle class="land" cx="${c*C+C/2}" cy="${(r-r0)*C+C/2}" r="1.7"/>`);}
  return landDots=out.join('');
}
function drawMap(nodes){
  const placed=nodes.filter(n=>n.location);
  $('map').hidden=!placed.length;$('hero').classList.toggle('with-map',!!placed.length);if(!placed.length)return;
  const w=MASK.cols*C,h=(TOP-BOTTOM)/MASK.step*C;
  const marks=placed.map(n=>{const up=Object.values(n.families||{}).some(f=>f.available);const col=up?'var(--good)':'var(--muted)';
    const x=px(n.location.lon),y=py(n.location.lat);const where=[n.location.country,n.location.region].filter(Boolean).join(' · ');
    return `<g><title>${esc(n.node)}${where?' — '+esc(where):''}${up?'':'（不可用）'}</title>${up?`<circle class="halo" cx="${x}" cy="${y}" r="9" fill="${col}"/>`:''}<circle class="mark" cx="${x}" cy="${y}" r="6" fill="${col}"/></g>`}).join('');
  $('map').innerHTML=`<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="ArdFerry 节点分布">${land()}${marks}</svg>`+
    '<a class="credit" href="https://db-ip.com" target="_blank" rel="noopener" title="IP Geolocation by DB-IP (CC BY 4.0)">© DB-IP</a>';   // the licence asks for this credit
}
function render(view){
  $('updated').textContent='更新于 '+new Date(view.generated_at*1000).toLocaleTimeString();
  $('note').innerHTML=view.note?`<div class="note">${esc(view.note)}</div>`:'';
  const nodes=view.nodes||[];
  const up=nodes.filter(n=>Object.values(n.families||{}).some(f=>f.available)).length;
  const sum=(k)=>nodes.reduce((a,n)=>a+((n.clients||{})[k]||0),0);
  $('summary').innerHTML=[[nodes.length,'节点'],[up,'可用节点'],[view.devices_now??0,'当前上报设备'],[sum('sessions_now'),'当前中继会话']]
    .map(([n,l])=>`<div class="tile"><b>${n}</b><span>${l}</span></div>`).join('');
  drawMap(nodes);
  if(!nodes.length){$('nodes').innerHTML='<div class="empty">还没有 ArdFerry 节点。</div>';return;}
  $('nodes').innerHTML=nodes.map(n=>{
    const fams=n.families||{},c=n.clients||{},any=Object.values(fams).some(f=>f.available);
    const rtt=Object.values(fams).map(f=>f.probe_rtt_ms_median).filter(x=>x!=null);
    const rate=Object.values(fams).map(f=>f.success_rate_24h).filter(x=>x!=null);
    const mRate=rate.length?Math.max(...rate):null;
    const pills=['ipv4','ipv6'].map(f=>`<span class="pill ${fams[f]&&fams[f].available?'on':'off'}">${f==='ipv4'?'IPv4':'IPv6'}</span>`).join('');
    const role=n.official?'官方节点':'社区节点';
    const where=n.location?[n.location.country,n.location.region,n.location.city].filter(Boolean).join(' · '):'';
    return `<article class="node ${any?'':'down'}">
      <div class="top"><div class="name">${esc(n.node)}</div><span class="badge">${role}</span></div>${where?`<div class="where">${esc(where)}</div>`:''}
      <div class="fams">${pills}</div>
      <div class="cols">
        <div class="col"><h3>M 测得</h3>
          <div class="row"><span>握手延迟</span><b>${v(rtt.length?Math.min(...rtt):null,' ms')}</b></div>
          <div class="row"><span>可用率 24h</span><b>${pct(mRate)}</b></div>${bar(mRate)}
          <div class="row"><span>心跳</span><b>${n.heartbeat_age_s==null?'—':n.heartbeat_age_s<15?'正常':v(n.heartbeat_age_s,' s 前')}</b></div></div>
        <div class="col"><h3>客户端测得</h3>
          <div class="row"><span>握手延迟</span><b>${v(c.client_rtt_ms_median,' ms')}</b></div>
          <div class="row"><span>成功率 24h</span><b>${pct(c.client_success_rate_24h)}</b></div>${bar(c.client_success_rate_24h)}
          <div class="row"><span>设备 当前 / 24h</span><b>${v(c.devices_now)} / ${v(c.devices_24h)}</b></div>
          <div class="row"><span>中继会话</span><b>${v(c.sessions_now)}</b></div>
          <div class="row"><span>会话往返 · 带宽</span><b>${v(c.session_rtt_ms_median,' ms')} · ${v(c.session_mbps_median,' Mbps')}</b></div></div>
      </div></article>`}).join('');
}
render(JSON.parse($('data').textContent));
setInterval(async()=>{try{const r=await fetch('/api/v1/ferries'+location.search,{cache:'no-store'});if(r.ok)render(await r.json());}catch(e){}},30000);
</script></body></html>'''


def ferry_page(view):
    # the data rides in the page (no extra request on first paint) and is refreshed in place every 30 s
    data = json.dumps(view, ensure_ascii=False).replace('</', '<\\/')
    return FERRY_PAGE.replace('__DATA__', data)

def dispatch(path, endpoint, data, ip, envelope):
    now=int(time.time())
    if path == '/api/v1/ferry-report':
        return ferry_report(endpoint, data, now)
    if path == '/api/v1/register':
        with database() as db:
            row=db.execute('SELECT * FROM devices WHERE endpoint=?',(endpoint,)).fetchone()
            if row: return {'code':row['code'],'endpoint':endpoint}
        limit('register:'+ip,30,3600)
        with database() as db:
            db.execute('BEGIN IMMEDIATE')
            row=db.execute('SELECT * FROM devices WHERE endpoint=?',(endpoint,)).fetchone()
            if row: return {'code':row['code'],'endpoint':endpoint}
            for _ in range(100):
                code=''.join(secrets.choice(string.ascii_uppercase+string.digits) for _ in range(6))
                try:
                    db.execute('INSERT INTO devices(endpoint,code) VALUES(?,?)',(endpoint,code))
                    return {'code':code,'endpoint':endpoint}
                except sqlite3.IntegrityError: continue
        raise ApiError(503,'编号分配暂时不可用。')
    if path == '/api/v1/lookup':
        code=text(data,'code',CODE)
        limit('lookup:'+endpoint,60,60)
        with database() as db:
            registered(db,endpoint)
            row=db.execute('SELECT endpoint,code FROM devices WHERE code=?',(code,)).fetchone()
        if row is None: raise ApiError(404,'机器编号不存在。')
        return dict(row)
    if path == '/api/v1/probe/start':
        # A short-lived token for the UDP probe service (registered devices only, every level rate limited). The token names no address; the
        # probe service only ever sends to addresses that answered its cookie challenge. 'ip' is the address this request came from.
        if data: raise ApiError(400, '探测请求不接受参数。')
        limit('probe-device:'+endpoint, 12, 3600); limit('probe-ip:'+ip, 30, 3600); limit('probe-all', 1500, 3600)
        with database() as db: registered(db, endpoint)
        _, seen = probe_ip16(ip)
        return {'v': 2, 'token': probe_token().hex(), 'expires': int(time.time()) + 120, 'ip': seen, 'host': PROBE_HOST,
                'ports': [PROBE_PORT, PROBE_PORT + 1]}
    if path == '/api/v1/access':
        enabled=data.get('enabled')
        if type(enabled) is not bool or set(data)!={'enabled'}: raise ApiError(400,'无效访问开关；服务器不接收密码。')
        with database() as db:
            registered(db,endpoint)
            db.execute('UPDATE devices SET enabled=?,epoch=epoch+1,seen=? WHERE endpoint=?',(int(enabled),now,endpoint))
            dropped=[r[0] for r in db.execute('SELECT id FROM requests WHERE target=?',(endpoint,))]
            db.execute('DELETE FROM requests WHERE target=?',(endpoint,))
        notify('poll:'+endpoint,*('ticket:'+t for t in dropped))   # waiting controllers learn at once that their request is gone
        return {'enabled':enabled}
    if path == '/api/v1/offline':
        if data: raise ApiError(400,'离线请求不接受参数。')
        with database() as db:
            registered(db,endpoint)
            db.execute('UPDATE devices SET seen=0 WHERE endpoint=?',(endpoint,))
            db.execute('DELETE FROM requests WHERE target=?',(endpoint,))
        return {'offline':True}
    if path == '/api/v1/poll':
        enabled=data.get('enabled')
        if type(enabled) is not bool or 'enabled' not in data or not set(data)<={'enabled','name','known'}: raise ApiError(400,'无效访问开关；服务器不接收密码。')
        computer=data.get('name')
        if computer is not None and (not isinstance(computer,str) or not COMPUTER_NAME.fullmatch(computer)): raise ApiError(400,'计算机名无效。')
        known=data.get('known',[])   # the requests this host already handles: only others are news
        if not isinstance(known,list) or len(known)>16 or not all(isinstance(k,str) and TICKET.fullmatch(k) for k in known): raise ApiError(400,'已知请求列表无效。')
        key='poll:'+endpoint; version=news(key)
        def pending(db):
            # The access switch is changed through /access only: a poll just reads it, so a late poll cannot undo a change.
            row=registered(db,endpoint)
            active=bool(enabled and row['enabled'])
            tickets=db.execute('''SELECT r.id,r.caller AS controllerEndpoint,d.code AS controllerCode,
              r.client_session AS clientSessionId,r.expires,r.proof FROM requests r JOIN devices d ON d.endpoint=r.caller
              WHERE r.target=? AND r.status='pending' AND r.epoch=? AND r.expires>=? ORDER BY r.rowid LIMIT 16''',
              (endpoint,row['epoch'],int(time.time()))).fetchall() if active else []
            return {'enabled':active,'tickets':[{**dict(r),'proof':json.loads(r['proof'])} for r in tickets]}
        with database() as db:
            db.execute('BEGIN IMMEDIATE')
            row=registered(db,endpoint)
            db.execute('UPDATE devices SET seen=? WHERE endpoint=?',(now,endpoint))
            if computer is not None and computer!=row['name']:
                db.execute('UPDATE devices SET name=? WHERE endpoint=?',(computer,endpoint))
            db.execute('DELETE FROM requests WHERE expires < ?',(now,))
            reply=pending(db)
        if any(t['id'] not in known for t in reply['tickets']): return reply
        def again():
            with database() as db: return pending(db)
        raise Wait(key,version,again)
    if path == '/api/v1/presence':
        peers=data.get('peers')
        if set(data)!={'peers'} or not isinstance(peers,list) or len(peers)>64: raise ApiError(400,'在线状态请求无效。')
        limit('presence:'+endpoint,30,60)
        answer={}
        with database() as db:
            registered(db,endpoint)
            for item in peers:
                if not isinstance(item,dict) or set(item)!={'endpoint','grant'}: raise ApiError(400,'在线状态请求无效。')
                target=text(item,'endpoint',ENDPOINT)
                try: check_grant(item['grant'],endpoint,target)
                except ApiError: continue   # without the target's signed grant nothing is revealed about it
                row=db.execute('SELECT enabled,seen,name FROM devices WHERE endpoint=?',(target,)).fetchone()
                if row is None: continue
                answer[target]={'online':bool(row['enabled'] and row['seen']>=now-20),'name':row['name']}
        return {'peers':answer}
    if path == '/api/v1/connect':
        code=text(data,'code',CODE)
        session=text(data,'sessionId',ENDPOINT)
        target_id=text(data,'targetEndpoint',ENDPOINT)
        ticket=text(data,'requestId',TICKET)
        expires=data.get('expires')
        fields={'code','sessionId','targetEndpoint','requestId','expires','client','ferry'}
        ferry=data.get('ferry')   # the session's ArdFerry, chosen by the controller; the host checks the signed request itself
        if (not isinstance(ferry,dict) or set(ferry)!={'node','key','addresses'} or not isinstance(ferry['node'],str) or not FERRY_NAME.fullmatch(ferry['node'])
                or not isinstance(ferry['key'],str) or not re.fullmatch(r'spki:[0-9a-f]{2,512}',ferry['key']) or not isinstance(ferry['addresses'],list)
                or not 1<=len(ferry['addresses'])<=8 or not all(isinstance(a,str) and len(a)<=64 for a in ferry['addresses'])):
            raise ApiError(400,'连接请求没有有效的 ArdFerry。')
        if data.get('transport')=='easytier-v1': fields.add('transport')
        if type(expires) is not int or not now < expires <= now+360 or set(data)!=fields:
            raise ApiError(400,'连接请求无效；服务器不接收密码。')
        client=data.get('client'); match=CLIENT.fullmatch(client) if isinstance(client,str) else None
        if match is None or (int(match.group(1)),int(match.group(2)))<MIN_CLIENT: raise ApiError(426,f'客户端版本过旧，请升级到 ArdUi {MIN_TEXT} 或更高版本。')
        limit('connect-device:'+endpoint,20,60)
        with database() as db:
            db.execute('BEGIN IMMEDIATE')
            registered(db,endpoint)
            target=db.execute('SELECT * FROM devices WHERE code=? AND endpoint=?',(code,target_id)).fetchone()
            if target is None or not target['enabled'] or target['seen']<now-20:
                raise ApiError(403,'对端离线或未开放远程访问。')
            if target_id==endpoint: raise ApiError(400,'不能连接本机。')
            outstanding=db.execute('SELECT count(*) FROM requests WHERE target=? AND expires>?',(target_id,now)).fetchone()[0]
            if outstanding>=16: raise ApiError(429,'对端正在处理较多连接，请稍后再试。')
            try:
                db.execute('INSERT INTO requests VALUES(?,?,?,?,?,?,?,?,NULL)',(ticket,target_id,endpoint,session,target['epoch'],expires,'pending',json.dumps(envelope)))
            except sqlite3.IntegrityError: raise ApiError(409,'重复连接请求。')
        notify('poll:'+target_id)   # after the commit: the host's waiting poll finds it
        return {'id':ticket,'endpoint':target_id,'code':code}
    match=re.fullmatch(r'/api/v1/tickets/([0-9a-f]{32})(/ready|/reject)?',path)
    if match:
        ticket,action=match.groups()
        key='ticket:'+ticket; version=news(key)
        with database() as db:
            db.execute('BEGIN IMMEDIATE')
            row=db.execute('SELECT * FROM requests WHERE id=?',(ticket,)).fetchone()
            if row is None or row['expires']<now: raise ApiError(410,'连接授权已过期，请重试。')
            target=registered(db,row['target'])
            if not target['enabled'] or target['epoch']!=row['epoch']: raise ApiError(410,'远程访问已关闭或密码已更改。')
            if action:
                if endpoint!=row['target']: raise ApiError(403,'无权更新此授权。')
                if row['status']!='pending': raise ApiError(409,'此授权已处理。')
                if action=='/reject':
                    db.execute("UPDATE requests SET status='rejected' WHERE id=?",(ticket,))
                else:
                    text(data,'sessionId',ENDPOINT)
                    if data.get('requestId')!=ticket or data.get('controllerEndpoint')!=row['caller'] or data.get('targetEndpoint')!=endpoint or data.get('clientSessionId')!=row['client_session'] or data.get('expires')!=row['expires']:
                        raise ApiError(400,'会话签名内容不匹配。')
                    fields={'sessionId','requestId','controllerEndpoint','targetEndpoint','clientSessionId','expires'}
                    requested=json.loads(base64.b64decode(json.loads(row['proof'])['payload'],validate=True))
                    if requested.get('transport')=='easytier-v1':
                        fields.update(('transport','transportPort'))
                        if data.get('transport')!='easytier-v1' or type(data.get('transportPort')) is not int or not 1<=data['transportPort']<=65535:
                            raise ApiError(400,'无效传输协议或端口。')
                    if set(data)!=fields:
                        raise ApiError(400,'无效会话响应；禁止发送密码或流量密钥。')
                    db.execute("UPDATE requests SET status='ready',offer=? WHERE id=?",(json.dumps(envelope),ticket))
                answered=True
            else:
                if endpoint!=row['caller']: raise ApiError(403,'无权读取此授权。')
                answered=False
                result={'status':row['status']}
                if row['status']=='ready': result['offer']=json.loads(row['offer'])
        if answered:
            notify(key)   # after the commit: the controller's waiting look sees the answer
            return {'ok':True}
        if result['status']!='pending': return result
        def again():
            with database() as db:
                row=db.execute('SELECT status,offer FROM requests WHERE id=?',(ticket,)).fetchone()
            if row is None: raise ApiError(410,'连接授权已过期，请重试。')
            answer={'status':row['status']}
            if row['status']=='ready': answer['offer']=json.loads(row['offer'])
            return answer
        raise Wait(key,version,again)
    raise ApiError(404,'接口不存在。')

def application(environ,start_response):
    status=200
    try:
        path=environ.get('PATH_INFO','')
        if path=='/api/health' and environ['REQUEST_METHOD']=='GET': result={'service':'arduiserver','version':VERSION,'minClient':'v%d.pre%d' % MIN_CLIENT,'ok':True}
        elif path=='/api/v1/network' and environ['REQUEST_METHOD']=='GET':
            result=network()   # public facts about this network, for ArdUi clients and ArdFerry nodes alike
        elif path in ('/api/v1/ferries','/api/ferries') and environ['REQUEST_METHOD']=='GET':
            import hmac, urllib.parse
            token=urllib.parse.parse_qs(environ.get('QUERY_STRING','')).get('token',[''])[0]
            admin=bool(ADMIN_TOKEN) and hmac.compare_digest(token.encode(),ADMIN_TOKEN.encode())
            limit('ferries:'+(environ.get('HTTP_X_REAL_IP') or environ.get('REMOTE_ADDR','unknown')),60,60)
            view=ferry_view(admin)
            if path=='/api/ferries':
                body=ferry_page(view).encode()
                start_response('200 OK',[('Content-Type','text/html; charset=utf-8'),('Content-Length',str(len(body))),('Cache-Control','no-store'),('X-Content-Type-Options','nosniff')])
                return [body]
            result=view
        elif (path in FERRY_SCRIPTS or path.startswith('/ardferry/')) and environ['REQUEST_METHOD']=='GET':
            return ferry_download(path,environ,start_response)
        elif environ['REQUEST_METHOD']!='POST': raise ApiError(405,'此接口仅接受 POST。')
        else:
            try: length=int(environ.get('CONTENT_LENGTH') or 0)
            except ValueError: raise ApiError(400,'无效内容长度。')
            if not 1<=length<=32768: raise ApiError(413,'请求体大小无效。')
            check_client(environ)
            # Caddy overwrites this header; the backend listens on loopback only.
            ip=environ.get('HTTP_X_REAL_IP') or environ.get('REMOTE_ADDR','unknown')
            endpoint,data,envelope=authenticate(path,environ['wsgi.input'].read(length),ip)
            try: result=dispatch(path,endpoint,data,ip,envelope)
            except Wait as wait:
                if environ.get('ardui.async'): raise
                result=wait.again()
    except ApiError as ex: status,result=ex.status,{'error':ex.message}
    except Wait: raise   # the asynchronous front waits for news, then answers (see asgi)
    except Exception:
        # Never log request bodies, access passwords, signatures or session capabilities.
        import traceback
        traceback.print_exc()
        status,result=500,{'error':'服务暂时不可用。'}
    return json_reply(status,result,start_response)

def json_reply(status,result,start_response):
    body=json.dumps(result,ensure_ascii=False).encode()
    start_response(f'{status} {HTTPStatus(status).phrase}', [('Content-Type','application/json; charset=utf-8'),('Content-Length',str(len(body))),('Cache-Control','no-store'),('X-Content-Type-Options','nosniff')])
    return [body]

# ---- the asynchronous front (ASGI, served by uvicorn) ------------------------------------------------------------------
# The request handlers above stay synchronous (sqlite) and run in a thread pool; only waiting for news happens on the event loop,
# so a host that waits for connection requests costs no thread.
_pool = ThreadPoolExecutor(max_workers=32, thread_name_prefix='arduiserver')

async def _waited(wait, environ):
    """Wait until wait.key has news after wait.version (or WAIT_SECONDS), then answer with wait.again()."""
    loop = asyncio.get_running_loop()
    future = loop.create_future()
    _news_waiters.setdefault(wait.key, set()).add(future)
    try:
        if news(wait.key) == wait.version:   # registered first, then checked: news between the handler's look and now is not lost
            try: await asyncio.wait_for(asyncio.shield(future), WAIT_SECONDS)
            except asyncio.TimeoutError: pass
    finally:
        waiters = _news_waiters.get(wait.key)
        if waiters is not None:
            waiters.discard(future)
            if not waiters: _news_waiters.pop(wait.key, None)
    reply = {}
    def again():
        try: status, result = 200, wait.again()
        except ApiError as ex: status, result = ex.status, {'error': ex.message}
        except Exception:
            import traceback
            traceback.print_exc()
            status, result = 500, {'error': '服务暂时不可用。'}
        return b''.join(json_reply(status, result, lambda line, headers: reply.update(status=line, headers=headers)))
    return await loop.run_in_executor(_pool, again), reply

async def asgi(scope, receive, send):
    global _news_loop
    if scope['type'] == 'lifespan':
        while True:
            message = await receive()
            if message['type'] == 'lifespan.startup':
                _news_loop = asyncio.get_running_loop()
                await send({'type': 'lifespan.startup.complete'})
            elif message['type'] == 'lifespan.shutdown':
                await send({'type': 'lifespan.shutdown.complete'})
                return
    if scope['type'] != 'http':
        return
    _news_loop = asyncio.get_running_loop()
    body = bytearray()
    while True:
        message = await receive()
        if message['type'] == 'http.disconnect':
            return
        body += message.get('body', b'')
        if not message.get('more_body') or len(body) > 65536:
            break
    environ = {'REQUEST_METHOD': scope['method'], 'PATH_INFO': scope['path'], 'QUERY_STRING': scope['query_string'].decode('latin-1'),
               'REMOTE_ADDR': (scope.get('client') or ('', 0))[0], 'wsgi.input': io.BytesIO(bytes(body)), 'ardui.async': True}
    for name, value in scope['headers']:
        name = name.decode('latin-1').upper().replace('-', '_'); value = value.decode('latin-1')
        environ[name if name in ('CONTENT_TYPE', 'CONTENT_LENGTH') else 'HTTP_' + name] = value
    reply = {}
    def run():
        chunks = application(environ, lambda line, headers: reply.update(status=line, headers=headers))
        return b''.join(chunks)
    try:
        payload = await asyncio.get_running_loop().run_in_executor(_pool, run)
    except Wait as wait:
        payload, reply = await _waited(wait, environ)
    await send({'type': 'http.response.start', 'status': int(reply['status'].split()[0]),
                'headers': [(k.lower().encode('latin-1'), v.encode('latin-1')) for k, v in reply['headers']]})
    await send({'type': 'http.response.body', 'body': payload})

initialize()
if __name__=='__main__':
    # Local integration tests only (no waiting: WSGI asks again at once); production runs uvicorn with asgi behind Caddy.
    from wsgiref.simple_server import make_server
    make_server('127.0.0.1',int(os.environ.get('ARDUI_PORT','8091')),application).serve_forever()
