"""Upload a compressed NJ artifact with bounded parallelism and verified reuse."""
import argparse
from concurrent.futures import ThreadPoolExecutor,wait
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time


def run(arguments):
    return subprocess.run(arguments,check=True,capture_output=True,text=True,
        creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0)).stdout


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source',type=Path)
    parser.add_argument('--stage',default='/tmp/ardui-v2.pre2-frd')
    parser.add_argument('--key',type=Path,default=Path.home()/'.ssh/nj_key')
    parser.add_argument('--host',default='root@175.27.160.144')
    args=parser.parse_args()
    if not re.fullmatch(r'/tmp/ardui-v2\.pre2-[a-zA-Z0-9-]+',args.stage): raise ValueError('Unexpected stage')
    source=args.source.resolve(strict=True)
    if not re.fullmatch(r'[a-zA-Z0-9_.-]+',source.name): raise ValueError('Unsafe artifact filename')
    content=source.read_bytes();digest=hashlib.sha256(content).hexdigest()
    prefix='upload-'+digest[:16]
    folder=source.parent/prefix;folder.mkdir(exist_ok=True)
    pieces=[]
    for index,start in enumerate(range(0,len(content),3*1024*1024)):
        path=folder/f'{prefix}.part{index:03d}';data=content[start:start+3*1024*1024]
        path.write_bytes(data);pieces.append((path,hashlib.sha256(data).hexdigest()))
    options=['-i',str(args.key),'-o','BatchMode=yes','-o','ConnectTimeout=20']
    output=run(['ssh',*options,args.host,f"mkdir -p {args.stage} && find {args.stage} -maxdepth 1 -name '{prefix}.part*' -type f -exec sha256sum {{}} +"])
    existing={line.split()[-1].rsplit('/',1)[-1]:line.split()[0] for line in output.splitlines() if line.strip()}
    def upload(piece):
        path,expected=piece
        if existing.get(path.name)==expected: print('Reused '+path.name,flush=True);return
        for attempt in range(3):
            try:
                run(['scp',*options,str(path),f'{args.host}:{args.stage}/{path.name}'])
                print('Uploaded '+path.name,flush=True);return
            except subprocess.CalledProcessError as error:
                print(f'{path.name} attempt {attempt+1}: {error.stderr}',flush=True)
                if attempt==2: raise
                time.sleep(1)
    last_time=time.monotonic();last_bytes=sum(path.stat().st_size for path,expected in pieces if existing.get(path.name)==expected)
    with ThreadPoolExecutor(max_workers=8) as pool:
        pending={pool.submit(upload,piece) for piece in pieces}
        while pending:
            done,pending=wait(pending,timeout=20)
            for future in done: future.result()
            raw=run(['ssh',*options,args.host,f"find {args.stage} -maxdepth 1 -name '{prefix}.part*' -type f -printf '%s\\n'"])
            transferred=sum(int(line) for line in raw.splitlines())
            now=time.monotonic();speed=max(0,transferred-last_bytes)/(now-last_time)
            remaining=max(0,len(content)-transferred)
            eta=f'{remaining/speed:.0f}s' if speed>0 else 'pending'
            print(f'Progress {transferred}/{len(content)} bytes ({100*transferred/len(content):.1f}%), {speed/1024:.1f} KiB/s, ETA {eta}',flush=True)
            last_time=now;last_bytes=transferred
    paths=' '.join(f'{args.stage}/{path.name}' for path,_ in pieces)
    target=f'{args.stage}/{source.name}'
    command=f'cat {paths} > {target}.next && echo "{digest}  {target}.next" | sha256sum -c - && mv {target}.next {target}'
    print(run(['ssh',*options,args.host,command]),flush=True)
    print(json.dumps(dict(file=source.name,bytes=len(content),sha256=digest)),flush=True)


if __name__=='__main__': main()
