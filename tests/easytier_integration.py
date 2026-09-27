"""Official EasyTier binaries and isolated directory; never uses installed user data."""
import argparse, base64, json, os, shutil, socket, subprocess, sys, time
from pathlib import Path
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from cryptography.hazmat.primitives.serialization import Encoding, PrivateFormat, PublicFormat, NoEncryption
sys.stdout.reconfigure(encoding='utf-8',errors='replace')

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1',0))
        return s.getsockname()[1]

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--binaries',required=True)
    parser.add_argument('--dll',default='bin/Release/net8.0-windows/ArdUi.dll')
    parser.add_argument('--prototype',action='store_true')
    parser.add_argument('--overlay',action='store_true')
    parser.add_argument('--relay-config',help='Use an existing pinned relay JSON instead of the isolated local relay')
    args=parser.parse_args()
    repo=Path(__file__).resolve().parents[1]
    root=repo/'dist'/('easytier-'+str(time.time_ns()));root.mkdir(parents=True)
    binaries=root/'easytier';binaries.mkdir()
    for name in ('easytier-core.exe','easytier-cli.exe','Packet.dll','wintun.dll'):
        shutil.copy2(Path(args.binaries)/name,binaries/name)
    key=X25519PrivateKey.generate()
    secret=base64.b64encode(key.private_bytes(Encoding.Raw,PrivateFormat.Raw,NoEncryption())).decode()
    public=base64.b64encode(key.public_key().public_bytes(Encoding.Raw,PublicFormat.Raw)).decode()
    relay_port,api_port,rpc=port(),port(),port()
    config=root/'relay.toml'
    config.write_text(f'''hostname = "test-relay"
listeners = ["tcp://127.0.0.1:{relay_port}"]
rpc_portal = "127.0.0.1:{rpc}"
[network_identity]
network_name = "test-bootstrap"
network_secret = "{os.urandom(32).hex()}"
[secure_mode]
enabled = true
local_private_key = "{secret}"
local_public_key = "{public}"
[flags]
no_tun = true
relay_network_whitelist = "ardui-v4-*"
''',encoding='utf-8')
    (binaries/'relay.json').write_text(json.dumps({'url':f'tcp://127.0.0.1:{relay_port}','publicKey':public}))
    if args.relay_config: shutil.copy2(args.relay_config,binaries/'relay.json')
    env=dict(os.environ,ARDUI_EASYTIER_DIR=str(binaries),ARDUI_DATABASE=str(root/'directory.sqlite3'),
        ARDUI_TRANSIT_KEY=str(root/'directory-key'),ARDUI_PORT=str(api_port),ARDUI_TEST_SERVER=f'http://127.0.0.1:{api_port}')
    children=[];logs=[]
    def start(name,command):
        log=open(root/(name+'.log'),'wb');logs.append(log)
        p=subprocess.Popen(command,cwd=repo,env=env,stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        children.append(p);return p
    try:
        if not args.relay_config:
            start('relay',[str(binaries/'easytier-core.exe'),'-c',str(config),'--rpc-portal',f'127.0.0.1:{rpc}'])
        start('directory',[sys.executable,'server/arduiserver.py'])
        time.sleep(2)
        app=(repo/args.dll).resolve()
        command=([str(app)] if app.suffix=='.exe' else ['dotnet',str(app)])+['--prototype-test' if args.prototype else '--easytier-overlay-test' if args.overlay else '--easytier-test']
        print('Evidence:',root,flush=True)
        client=start('client',command)
        try:returncode=client.wait(timeout=360)
        except subprocess.TimeoutExpired:
            print('Integration timed out',flush=True);returncode=124
        print((root/'client.log').read_text(encoding='utf-8',errors='replace'),flush=True)
        return returncode
    finally:
        for child in reversed(children):
            if child.poll() is None:
                subprocess.run(['taskkill','/PID',str(child.pid),'/T','/F'],capture_output=True)
                child.wait(timeout=15)
        for log in logs:log.close()

if __name__=='__main__':sys.exit(main())
