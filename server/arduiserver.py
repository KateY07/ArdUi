"""ArdUi trusted directory and short-lived connection authorization (WSGI).
Production: gunicorn --bind 127.0.0.1:8091 --workers 1 --threads 8 arduiserver:application
"""
import base64
import json
import os
import re
import secrets
import sqlite3
import string
import time
from http import HTTPStatus
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

DB = os.environ.get('ARDUI_DATABASE', '/var/lib/arduiserver/devices.sqlite3')
CODE = re.compile(r'[A-Z0-9]{6}\Z')
ENDPOINT = re.compile(r'[0-9a-f]{64}\Z')
TICKET = re.compile(r'[0-9a-f]{32}\Z')

class ApiError(Exception):
    def __init__(self, status, message): self.status, self.message = status, message

def database():
    db = sqlite3.connect(DB, timeout=15)
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
        ''')
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

def registered(db, endpoint):
    row = db.execute('SELECT * FROM devices WHERE endpoint=?',(endpoint,)).fetchone()
    if row is None: raise ApiError(403, '请先注册本机。')
    return row

def dispatch(path, endpoint, data, ip, envelope):
    now=int(time.time())
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
    if path == '/api/v1/access':
        enabled=data.get('enabled')
        if type(enabled) is not bool or set(data)!={'enabled'}: raise ApiError(400,'无效访问开关；服务器不接收密码。')
        with database() as db:
            registered(db,endpoint)
            db.execute('UPDATE devices SET enabled=?,epoch=epoch+1,seen=? WHERE endpoint=?',(int(enabled),now,endpoint))
            db.execute('DELETE FROM requests WHERE target=?',(endpoint,))
        return {'enabled':enabled}
    if path == '/api/v1/poll':
        enabled=data.get('enabled')
        if type(enabled) is not bool: raise ApiError(400,'缺少访问开关。')
        with database() as db:
            db.execute('BEGIN IMMEDIATE')
            row=registered(db,endpoint)
            # Poll cannot re-enable server permission; /access is required.
            active=bool(enabled and row['enabled'])
            db.execute('UPDATE devices SET seen=? WHERE endpoint=?',(now,endpoint))
            if not enabled:
                db.execute('UPDATE devices SET enabled=0 WHERE endpoint=?',(endpoint,))
                db.execute('DELETE FROM requests WHERE target=?',(endpoint,))
            db.execute('DELETE FROM requests WHERE expires < ?',(now,))
            tickets=db.execute('''SELECT r.id,r.caller AS controllerEndpoint,d.code AS controllerCode,
              r.client_session AS clientSessionId,r.expires,r.proof FROM requests r JOIN devices d ON d.endpoint=r.caller
              WHERE r.target=? AND r.status='pending' AND r.epoch=? ORDER BY r.rowid LIMIT 16''', (endpoint,row['epoch'])).fetchall() if active else []
        return {'enabled':active,'tickets':[{**dict(r),'proof':json.loads(r['proof'])} for r in tickets]}
    if path == '/api/v1/connect':
        code=text(data,'code',CODE)
        session=text(data,'sessionId',ENDPOINT)
        target_id=text(data,'targetEndpoint',ENDPOINT)
        ticket=text(data,'requestId',TICKET)
        expires=data.get('expires')
        if type(expires) is not int or not now < expires <= now+360 or set(data)!={'code','sessionId','targetEndpoint','requestId','expires'}:
            raise ApiError(400,'连接请求无效；服务器不接收密码。')
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
        return {'id':ticket,'endpoint':target_id,'code':code}
    match=re.fullmatch(r'/api/v1/tickets/([0-9a-f]{32})(/ready|/reject)?',path)
    if match:
        ticket,action=match.groups()
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
                    if set(data)!={'sessionId','requestId','controllerEndpoint','targetEndpoint','clientSessionId','expires'}:
                        raise ApiError(400,'无效会话响应；禁止发送密码或流量密钥。')
                    db.execute("UPDATE requests SET status='ready',offer=? WHERE id=?",(json.dumps(envelope),ticket))
                return {'ok':True}
            if endpoint!=row['caller']: raise ApiError(403,'无权读取此授权。')
            result={'status':row['status']}
            if row['status']=='ready': result['offer']=json.loads(row['offer'])
            return result
    raise ApiError(404,'接口不存在。')

def application(environ,start_response):
    status=200
    try:
        path=environ.get('PATH_INFO','')
        if path=='/api/health' and environ['REQUEST_METHOD']=='GET': result={'service':'arduiserver','version':'v1pre.1-console','ok':True}
        elif environ['REQUEST_METHOD']!='POST': raise ApiError(405,'此接口仅接受 POST。')
        else:
            try: length=int(environ.get('CONTENT_LENGTH') or 0)
            except ValueError: raise ApiError(400,'无效内容长度。')
            if not 1<=length<=32768: raise ApiError(413,'请求体大小无效。')
            # Caddy overwrites this header; the backend listens on loopback only.
            ip=environ.get('HTTP_X_REAL_IP') or environ.get('REMOTE_ADDR','unknown')
            endpoint,data,envelope=authenticate(path,environ['wsgi.input'].read(length),ip)
            result=dispatch(path,endpoint,data,ip,envelope)
    except ApiError as ex: status,result=ex.status,{'error':ex.message}
    except Exception:
        # Never log request bodies, access passwords, signatures or session capabilities.
        import traceback
        traceback.print_exc()
        status,result=500,{'error':'服务暂时不可用。'}
    body=json.dumps(result,ensure_ascii=False).encode()
    start_response(f'{status} {HTTPStatus(status).phrase}', [('Content-Type','application/json; charset=utf-8'),('Content-Length',str(len(body))),('Cache-Control','no-store'),('X-Content-Type-Options','nosniff')])
    return [body]

initialize()
if __name__=='__main__':
    # Local integration tests only; production runs behind Caddy using Gunicorn.
    from wsgiref.simple_server import make_server
    make_server('127.0.0.1',int(os.environ.get('ARDUI_PORT','8091')),application).serve_forever()
