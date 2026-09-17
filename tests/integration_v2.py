"""Isolated real ARD + directory + Python ArdTransit + C# client regression."""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import urllib.request

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0))
        return sock.getsockname()[1]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', default='bin/Release/net8.0-windows/ArdUi.dll')
    parser.add_argument('--prototype', action='store_true', help='Run consent, revoke and repeated enrollment regression')
    args=parser.parse_args()
    repo=Path(__file__).resolve().parents[1]
    root=repo/'dist'/('v2-integration-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
    root.mkdir(parents=True)
    ard=repo/'tools/ard.exe'
    relay=repo.parent/'target/release/ard-relay.exe'
    server_port,relay_port=port(),port()
    relay_url=f'http://127.0.0.2:{relay_port}'
    base=f'http://127.0.0.1:{server_port}'
    env=dict(os.environ,ARDUI_DATABASE=str(root/'directory.sqlite3'),ARDUI_TRANSIT_KEY=str(root/'directory-key'),ARDUI_PORT=str(server_port))
    children=[]
    logs=[]
    creation={'creationflags':subprocess.CREATE_NO_WINDOW} if os.name=='nt' else {}
    def start(name,command):
        output=open(root/(name+'.log'),'wb')
        logs.append(output)
        process=subprocess.Popen(command,cwd=repo,env=env,stdout=output,stderr=subprocess.STDOUT,**creation)
        children.append(process)
        return process
    try:
        start('relay',[str(relay),'--listen',f'127.0.0.2:{relay_port}','--identity',str(root/'relay-key')])
        relay_key=None
        for _ in range(100):
            match=re.search(r'ARD_RELAY_KEY=(spki:[0-9a-f]+)',(root/'relay.log').read_text(errors='replace'))
            if match: relay_key=match[1];break
            time.sleep(.1)
        if relay_key is None: raise RuntimeError('local ArdRelay failed: '+(root/'relay.log').read_text(errors='replace'))
        start('directory',[sys.executable,'server/arduiserver.py'])
        for _ in range(50):
            try:
                with urllib.request.urlopen(base+'/api/health',timeout=1) as response:
                    if json.load(response)['version']=='v2.pre1': break
            except OSError as ex:
                if children[-1].poll() is not None: raise RuntimeError('test directory exited') from ex
                time.sleep(.1)
        c=start('transit',[sys.executable,'transit/ardtransit.py','--ard',str(ard),'--server',base,
             '--relay',relay_url,'--relay-key',relay_key,'--data',str(root/'c'),'--diagnostics',str(root/'transit-diagnostics.zip'),'--test-local-directory'])
        env.update(ARDUI_ARD_PATH=str(ard),ARDUI_TEST_ROOT=str(root),ARDUI_TEST_SERVER=base,ARDUI_TEST_RELAY=relay_url,
                   ARDUI_TEST_RELAY_KEY=relay_key,ARDUI_TEST_TRANSIT_PID=str(c.pid))
        print('Fixture:',root,flush=True)
        app=(repo/args.dll).resolve()
        command=([str(app)] if app.suffix=='.exe' else ['dotnet',str(app)])
        command+=['--prototype-test'] if args.prototype else ['--transit-test','--integration-only']
        with open(root/'client.log','wb') as output:
            result=subprocess.run(command,cwd=repo,env=env,stdout=output,stderr=subprocess.STDOUT,timeout=230,**creation)
        print((root/'client.log').read_text(encoding='utf-8',errors='replace'),flush=True)
        return result.returncode
    finally:
        for process in reversed(children):
            if process.poll() is None:
                if os.name=='nt': subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True,**creation)
                else: process.terminate()
                try: process.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        for output in logs: output.close()

if __name__=='__main__': sys.exit(main())
