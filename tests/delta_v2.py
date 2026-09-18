"""Standard-library deployment delta; validates the old and reconstructed binary hashes."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

def digest(data): return hashlib.sha256(data).hexdigest()

def reconstruct(old, meta, literals):
    if digest(old)!=meta['oldSha256']: raise ValueError('Old binary hash mismatch')
    result=bytearray()
    for source,offset,size in meta['operations']:
        if source not in (0,1): raise ValueError('Invalid delta source')
        value=old if source==0 else literals
        if offset<0 or size<0 or offset+size>len(value): raise ValueError('Delta range outside source')
        result.extend(value[offset:offset+size])
        if len(result)>meta['size']: raise ValueError('Delta exceeds target size')
    if len(result)!=meta['size'] or digest(result)!=meta['sha256']: raise ValueError('Reconstructed binary hash mismatch')
    return result

def make(repo, previous='v1.pre11', version='v2.pre1'):
    old=(repo/f'dist/final-{previous}/ArdUi.exe').read_bytes()
    new=(repo/f'dist/final-{version}/ArdUi.exe').read_bytes()
    matches=[]
    block_size=65536
    for offset in range(0,len(old)-block_size+1,block_size):
        block=old[offset:offset+block_size]
        anchor=max((0,16384,32768,49152),key=lambda n:len(set(block[n:n+64])))
        start=0
        for _ in range(64):
            match=new.find(block[anchor:anchor+64],start)
            if match<0: break
            pos=match-anchor
            if pos>=0 and new[pos:pos+block_size]==block:
                matches.append((pos,offset,block_size))
                break
            start=match+1
    operations=[]
    literals=bytearray()
    cursor=0
    for pos,offset,size in sorted(matches):
        if pos+size<=cursor: continue
        if pos>cursor:
            operations.append([1,len(literals),pos-cursor]);literals.extend(new[cursor:pos]);cursor=pos
        skip=cursor-pos
        operations.append([0,offset+skip,size-skip]);cursor=pos+size
    if cursor<len(new): operations.append([1,len(literals),len(new)-cursor]);literals.extend(new[cursor:])
    meta=dict(oldSha256=digest(old),sha256=digest(new),size=len(new),operations=operations)
    if reconstruct(old,meta,literals)!=new: raise ValueError('Local reconstruction failed')
    target=repo/f'dist/upload-{version}-delta.zip'
    with zipfile.ZipFile(repo/f'dist/upload-{version}.zip') as full,zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as delta:
        for name in full.namelist():
            if name!=f'ArdUi-{version}.exe': delta.writestr(name,full.read(name))
        delta.writestr('binary-delta.json',json.dumps(meta,separators=(',',':')))
        delta.writestr('binary-literals.bin',literals)
        delta.writestr('apply_delta.py',Path(__file__).read_bytes().replace(b'\r\n',b'\n'))
    print(json.dumps(dict(bundle=str(target),bytes=target.stat().st_size,literalBytes=len(literals),sha256=digest(target.read_bytes()),binarySha256=meta['sha256']),indent=2))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply',nargs=2,metavar=('OLD','OUTPUT'))
    parser.add_argument('--previous',default='v1.pre11')
    parser.add_argument('--version',default='v2.pre1')
    args=parser.parse_args()
    if args.apply:
        stage=Path(__file__).resolve().parent
        meta=json.loads((stage/'binary-delta.json').read_text())
        result=reconstruct(Path(args.apply[0]).read_bytes(),meta,(stage/'binary-literals.bin').read_bytes())
        with Path(args.apply[1]).open('xb') as out: out.write(result)
        print('Reconstructed and verified: '+meta['sha256'])
    else: make(Path(__file__).resolve().parents[1],args.previous,args.version)
