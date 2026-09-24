"""Run isolated optimization variants sequentially, never overlapping performance loads."""
import json
import argparse
from pathlib import Path
import subprocess
import sys

repo=Path(__file__).resolve().parents[2]
root=repo/'dist/optimization-round1'
production=root/'harness-production/ArdUi.Performance.dll'
experiment=root/'harness-experiment/ArdUi.Performance.dll'
ard=root/'ard-variants/ard-perf.exe'
ard_hash=json.loads((root/'ard-variants/build.json').read_text())['sha256']
cases=[('production','full','baseline',None,True),
       ('rust-original','ard','baseline',0,False),
       ('rust-64k','ard','baseline',64,False),
       ('rust-128k','ard','baseline',128,False),
       ('ui-baseline','full','baseline',None,True),
       ('ui-aes','full','aes',None,True),
       ('ui-packed','full','packed',None,True),
       ('ui-resend','full','resend',None,True),
       ('ui-combined','full','combined',None,True)]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--confirm',action='store_true')
args=parser.parse_args()
if args.confirm:
    cases=[('combined-repeat','full','combined',None,True),
           ('baseline-repeat','full','baseline',None,True),
           ('udp','full','udp',None,True),
           ('rust64-repeat','ard','baseline',64,False),
           ('rust8-control','ard','baseline',8,False),
           ('rust0-repeat','ard','baseline',0,False),
           ('full-rust64','full','baseline',64,True)]
results=[]
for label,mode,variant,copy_kib,loaded in cases:
    harness=production if label=='production' else experiment
    command=[sys.executable,str(repo/'tests/performance_fixture.py'),'--harness',str(harness),
             '--seconds','8','--label','r1-'+label,'--variant',variant]
    if label!='production':command+=['--source-root',str(root/'ui-source')]
    if copy_kib is not None:command+=['--ard',str(ard),'--ard-sha256',ard_hash,'--copy-kib',str(copy_kib)]
    command+=['--','--mode',mode]
    if loaded:command+=['--loaded']
    print('CASE '+label,flush=True)
    child=subprocess.Popen(command,cwd=repo,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8',errors='replace',creationflags=subprocess.CREATE_NO_WINDOW)
    lines=[];folder=None
    for line in child.stdout:
        lines.append(line);print(line,end='',flush=True)
        if line.startswith('Performance fixture: '):folder=Path(line.partition(': ')[2].strip())
    code=child.wait()
    record=dict(label=label,mode=mode,variant=variant,copyKiB=copy_kib,loaded=loaded,exitCode=code,folder=str(folder),command=command)
    if folder and (folder/'measurements.json').exists():
        document=json.loads((folder/'measurements.json').read_text(encoding='utf-8'))
        record['results']=document['results'];record['error']=document['error']
        if document['results']:
            row=document['results'][0]['measured']
            print('SCORE '+json.dumps(dict(label=label,up=row['tcpUpload']['mbps'],down=row['tcpDownload']['mbps'],
                tcpP50=row['tcpEcho']['p50Ms'],udp1200P95=row['udp1200Echo']['p95Ms'],
                loadedUdpP95=(row.get('loadedDownload') or {}).get('udpEcho',{}).get('p95Ms'),cpu=row['processCost'])),flush=True)
    results.append(record)
    (root/('confirm.json' if args.confirm else 'runs.json')).write_text(json.dumps(results,indent=2),encoding='utf-8')
    if code:raise SystemExit(code)
print('ROUND1 RESULTS '+str(root/('confirm.json' if args.confirm else 'runs.json')),flush=True)
