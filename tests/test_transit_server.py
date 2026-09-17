import base64
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import io
import json
import os
import secrets
import sys
import tempfile
import time
import unittest
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey

root = tempfile.TemporaryDirectory(prefix='ArdUi-server-v2-')
os.environ['ARDUI_DATABASE'] = str(Path(root.name) / 'devices.sqlite3')
os.environ['ARDUI_TRANSIT_KEY'] = str(Path(root.name) / 'transit-key')
spec = importlib.util.spec_from_file_location('server', Path(__file__).resolve().parents[1] / 'server/arduiserver.py')
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)

def endpoint(key):
    return key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()

def sign(key, route, data):
    value=dict(endpoint=endpoint(key), issuedAt=int(time.time()), nonce=secrets.token_hex(16),
               payload=base64.b64encode(json.dumps(data).encode()).decode())
    raw=f"ArdUiServer/1\n{route}\n{value['endpoint']}\n{value['issuedAt']}\n{value['nonce']}\n{value['payload']}".encode()
    value['signature']=base64.b64encode(key.sign(raw)).decode()
    return value

def api(key, route, data, envelope=None):
    body=json.dumps(envelope or sign(key,route,data)).encode()
    status=[]
    result=b''.join(server.application({'PATH_INFO':route,'REQUEST_METHOD':'POST','CONTENT_LENGTH':str(len(body)),
             'wsgi.input':io.BytesIO(body),'REMOTE_ADDR':'127.0.0.1'}, lambda code,headers:status.append(int(code.split()[0]))))
    return status[0],json.loads(result)

class TransitServerTest(unittest.TestCase):
    def setUp(self):
        with server.database() as db:
            for table in ('transit_routes','transit_nodes','requests','devices','nonces','limits'):
                db.execute('DELETE FROM '+table)
        self.a,self.b,self.c=(Ed25519PrivateKey.generate() for _ in range(3))
        for key in (self.a,self.b,self.c): self.assertEqual(api(key,'/api/v1/register',{})[0],200)
        self.ids=list(map(endpoint,(self.a,self.b,self.c)))
        self.heartbeat=dict(capacity=2,active=0,mbps=50,metrics={'version':'v2.pre1'})
        self.assertEqual(api(self.c,'/api/v2/transit/heartbeat',self.heartbeat)[0],200)
        grant=sign(self.b,'/grant/v1',dict(controllerEndpoint=self.ids[0],targetEndpoint=self.ids[1],grantId=secrets.token_hex(16)))
        self.request=dict(route=secrets.token_hex(16),target=self.ids[1],candidate=self.ids[2],aSession=secrets.token_hex(32),bSession=secrets.token_hex(32),grant=grant)
        self.approve()

    def approve(self, expires=None):
        self.request['approval']=sign(self.b,'/transit-approval/v2',dict(route=self.request['route'],a=self.ids[0],b=self.ids[1],c=self.ids[2],
            aSession=self.request['aSession'],bSession=self.request['bSession'],expires=expires or int(time.time())+90))

    def create(self):
        status,reply=api(self.a,'/api/v2/transit/request',self.request)
        self.assertEqual(status,200,reply)
        return reply

    def test_candidate_lease(self):
        status,result=api(self.a,'/api/v2/transit/candidates',{'target':self.ids[1]})
        self.assertEqual(status,200)
        self.assertEqual([x['endpoint'] for x in result['candidates']],[self.ids[2]])
        with server.database() as db: db.execute('UPDATE transit_nodes SET seen=?',(int(time.time())-21,))
        self.assertEqual(api(self.a,'/api/v2/transit/candidates',{'target':self.ids[1]})[1]['candidates'],[])

    def test_ticket_signature_and_binding(self):
        value=self.create()['ticket']
        server_key=api(self.a,'/api/v2/transit/key',{})[1]['endpoint']
        raw=f"ArdUiServer/1\n/transit-ticket/v2\n{value['endpoint']}\n{value['issuedAt']}\n{value['nonce']}\n{value['payload']}".encode()
        Ed25519PublicKey.from_public_bytes(bytes.fromhex(server_key)).verify(base64.b64decode(value['signature']),raw)
        ticket=json.loads(base64.b64decode(value['payload']))
        self.assertEqual((ticket['a'],ticket['b'],ticket['c']),tuple(self.ids))
        self.assertEqual(ticket['aSession'],self.request['aSession'])
        self.assertEqual(len(api(self.c,'/api/v2/transit/heartbeat',self.heartbeat)[1]['jobs']),1)

    def test_forged_grant_denied(self):
        self.request['grant']['signature']=base64.b64encode(bytes(64)).decode()
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],403)

    def test_wrong_target_grant_denied(self):
        self.request['grant']=sign(self.b,'/grant/v1',dict(controllerEndpoint=self.ids[2],targetEndpoint=self.ids[1],grantId='x'))
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],403)

    def test_replay_and_wrong_signer_denied(self):
        route='/api/v2/transit/candidates'
        envelope=sign(self.a,route,{'target':self.ids[1]})
        self.assertEqual(api(self.a,route,{},envelope)[0],200)
        self.assertEqual(api(self.a,route,{},envelope)[0],401)
        envelope=sign(self.a,route,{'target':self.ids[1]})
        envelope['endpoint']=self.ids[2]
        self.assertEqual(api(self.a,route,{},envelope)[0],401)

    def test_roles_ready_and_close(self):
        self.create()
        route='/api/v2/transit/routes/'+self.request['route']
        offer=dict(route=self.request['route'],aRelaySession=secrets.token_hex(32),bRelaySession=secrets.token_hex(32),aToken=secrets.token_hex(16),bToken=secrets.token_hex(16))
        self.assertEqual(api(self.a,route+'/ready',offer)[0],403)
        self.assertEqual(api(self.c,route+'/ready',offer)[0],200)
        self.assertEqual(api(self.a,route,{})[1]['status'],'ready')
        self.assertEqual(api(self.c,route+'/renew',{})[0],403)
        self.assertEqual(api(self.b,route+'/close',{})[0],200)
        self.assertEqual(api(self.a,route+'/renew',{})[0],410)
        self.assertEqual(api(self.c,'/api/v2/transit/heartbeat',self.heartbeat)[1]['leases'],{})

    def test_both_lease_renewals_required(self):
        self.create()
        route='/api/v2/transit/routes/'+self.request['route']
        with server.database() as db: db.execute('UPDATE transit_routes SET lease_b=?',(int(time.time())-1,))
        self.assertEqual(api(self.a,route+'/renew',{})[0],410)

    def test_capacity_and_duplicate_route(self):
        self.create()
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],409)
        self.request['route']=secrets.token_hex(16)
        self.approve()
        self.create()
        self.request['route']=secrets.token_hex(16)
        self.approve()
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],429)

    def test_live_target_approval_required(self):
        del self.request['approval']
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],403)

    def test_caller_cannot_substitute_target_session(self):
        self.request['bSession']=secrets.token_hex(32)
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],403)

    def test_expired_target_approval_denied(self):
        self.approve(expires=int(time.time())-1)
        self.assertEqual(api(self.a,'/api/v2/transit/request',self.request)[0],403)

    def test_foreign_route_hidden(self):
        self.create()
        stranger=Ed25519PrivateKey.generate()
        api(stranger,'/api/v1/register',{})
        self.assertEqual(api(stranger,'/api/v2/transit/routes/'+self.request['route'],{})[0],404)

    def test_invalid_capacity_and_key_persistence(self):
        self.assertEqual(api(self.c,'/api/v2/transit/heartbeat',dict(self.heartbeat,capacity=True))[0],400)
        first=api(self.a,'/api/v2/transit/key',{})[1]['endpoint']
        second=server.transit_signer().public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw).hex()
        self.assertEqual(first,second)

    def test_concurrent_first_key_creation(self):
        previous=server.SIGNING_KEY
        server.SIGNING_KEY=str(Path(root.name)/('concurrent-key-'+secrets.token_hex(8)))
        try:
            with ThreadPoolExecutor(max_workers=16) as workers:
                identities=list(workers.map(lambda _: endpoint(server.transit_signer()),range(32)))
            self.assertEqual(len(set(identities)),1)
            self.assertEqual(Path(server.SIGNING_KEY).stat().st_size,32)
        finally: server.SIGNING_KEY=previous

if __name__=='__main__':
    try: unittest.main(verbosity=2)
    finally: root.cleanup()
