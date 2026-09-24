"""Run isolated performance measurements with local ArdRelay and directory services."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import socket
import subprocess
import sys
import time
import urllib.request


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1',0))
        return listener.getsockname()[1]


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--harness',required=True,type=Path)
    parser.add_argument('--seconds',type=int,default=8)
    parser.add_argument('--label',default='')
    parser.add_argument('--ard',type=Path)
    parser.add_argument('--ard-sha256')
    parser.add_argument('--source-root',type=Path)
    parser.add_argument('--variant',choices=['baseline','aes','packed','resend','combined','udp'],default='baseline')
    parser.add_argument('--copy-kib',type=int,choices=[0,8,32,64,128],default=0)
    parser.add_argument('extra',nargs=argparse.REMAINDER)
    args=parser.parse_args()
    repo=Path(__file__).resolve().parents[1]
    if args.label and not re.fullmatch(r'[A-Za-z0-9_-]{1,48}',args.label): raise ValueError('Invalid fixture label')
    root=repo/'dist'/('performance-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S')+('-'+args.label if args.label else ''))
    root.mkdir(parents=True)
    ard=args.ard.resolve(strict=True) if args.ard else repo/'tools/ard.exe'
    if args.ard and not args.ard_sha256: raise ValueError('Experimental ARD requires explicit --ard-sha256')
    expected=args.ard_sha256 or '04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d'
    source_root=args.source_root.resolve(strict=True) if args.source_root else repo
    if hashlib.sha256(ard.read_bytes()).hexdigest()!=expected: raise ValueError('ARD release hash mismatch')
    server_port,relay_port=free_port(),free_port()
    relay_url=f'http://127.0.0.2:{relay_port}'
    server_url=f'http://127.0.0.1:{server_port}'
    env=dict(os.environ,ARDUI_DATABASE=str(root/'directory.sqlite3'),ARDUI_TRANSIT_KEY=str(root/'directory-key'),
        ARDUI_PORT=str(server_port),ARDUI_ARD_PATH=str(ard),ARDUI_DATA_ROOT=str(root/'app'),
        ARDUI_TEST_ROOT=str(root),ARDUI_TEST_SERVER=server_url,ARDUI_TEST_RELAY=relay_url,
        ARDUI_BENCH_VARIANT=args.variant,ARDUI_BENCH_ARD_SHA256=expected,ARD_BENCH_COPY_KIB=str(args.copy_kib))
    children=[];logs=[]
    hidden={'creationflags':subprocess.CREATE_NO_WINDOW} if os.name=='nt' else {}
    def start(name,command):
        output=(root/(name+'.log')).open('wb');logs.append(output)
        child=subprocess.Popen(command,cwd=repo,env=env,stdout=output,stderr=subprocess.STDOUT,**hidden)
        children.append(child);return child
    try:
        relay=start('relay',[str(repo.parent/'target/release/ard-relay.exe'),
            '--listen',f'127.0.0.2:{relay_port}','--qad-listen','127.0.0.2:7842','--identity',str(root/'relay-key')])
        for _ in range(100):
            match=re.search(r'ARD_RELAY_KEY=(spki:[0-9a-f]+)',(root/'relay.log').read_text(errors='replace'))
            if match: env['ARDUI_TEST_RELAY_KEY']=match[1];break
            if relay.poll() is not None: raise RuntimeError('Local ArdRelay failed; inspect relay.log')
            time.sleep(.1)
        else: raise TimeoutError('Local ArdRelay did not become ready')
        directory=start('directory',[sys.executable,'server/arduiserver.py'])
        for _ in range(50):
            try:
                with urllib.request.urlopen(server_url+'/api/health',timeout=1) as response:
                    if json.load(response)['ok']: break
            except OSError as error:
                if directory.poll() is not None: raise RuntimeError('Local directory failed') from error
                time.sleep(.1)
        else: raise TimeoutError('Local directory did not become ready')
        harness=args.harness.resolve(strict=True)
        extra=args.extra[1:] if args.extra[:1]==['--'] else args.extra
        command=([str(harness)] if harness.suffix.lower()=='.exe' else ['dotnet',str(harness)])
        command+=['--seconds',str(args.seconds),'--output',str(root/'measurements.json'),*extra]
        source_files=[file for folder in ('Core','Transport','Overlay','TransitClient','Frd')
            for file in (source_root/folder).rglob('*.cs')]
        source_hashes={str(file.relative_to(source_root)):hashlib.sha256(file.read_bytes()).hexdigest()
            for file in sorted(source_files)}
        (root/'fixture.json').write_text(json.dumps(dict(ardSha256=expected,harness=str(harness),
            harnessSha256=hashlib.sha256(harness.read_bytes()).hexdigest(),
            seconds=args.seconds,localOnly=True,relay=relay_url,server=server_url,
            variant=args.variant,ardCopyKiB=args.copy_kib,sourceRoot=str(source_root),
            platform=platform.platform(),processor=os.environ.get('PROCESSOR_IDENTIFIER'),
            productionSourceSha256=source_hashes),indent=2))
        print('Performance fixture: '+str(root),flush=True)
        child=start('client',command)
        deadline=time.monotonic()+420;offset=0
        while child.poll() is None:
            if time.monotonic()>deadline: raise TimeoutError('Performance harness exceeded 7 minutes')
            with (root/'client.log').open('r',encoding='utf-8',errors='replace') as output:
                output.seek(offset);new=output.read();offset=output.tell()
            if new: print(new,end='',flush=True)
            time.sleep(.5)
        with (root/'client.log').open('r',encoding='utf-8',errors='replace') as output:
            output.seek(offset);print(output.read(),end='',flush=True)
        return child.returncode
    finally:
        for child in reversed(children):
            if child.poll() is None:
                if os.name=='nt': subprocess.run(['taskkill','/PID',str(child.pid),'/T','/F'],capture_output=True,**hidden)
                else: child.terminate()
                try: child.wait(timeout=8)
                except subprocess.TimeoutExpired: child.kill();child.wait()
        for output in logs: output.close()


if __name__=='__main__': sys.exit(main())
