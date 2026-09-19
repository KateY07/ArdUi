"""Package the approved FRD v1.pre8 runtime without developer/test artifacts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tarfile
import zipfile

VERSION = 'v1.pre8'
FILES = ['FRD.exe','codec-config.json','THIRD-PARTY-NOTICES.md','ffmpeg/LICENSE.txt',
    'ffmpeg/avcodec-62.dll','ffmpeg/avdevice-62.dll','ffmpeg/avfilter-11.dll','ffmpeg/avformat-62.dll',
    'ffmpeg/avutil-60.dll','ffmpeg/swresample-6.dll','ffmpeg/swscale-9.dll']


def digest(path):
    with path.open('rb') as source: return hashlib.file_digest(source,'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,default=Path(os.environ['LOCALAPPDATA'])/'Programs/FRD'/VERSION)
    parser.add_argument('--update-installer',action='store_true')
    parser.add_argument('--format',choices=('zip','tar.xz'),default='zip')
    parser.add_argument('--reuse-existing',action='store_true',help='Verify the existing archive and update metadata without recompressing it')
    parser.add_argument('--nj-seed',action='store_true',help='Package only local FRD components and export final tar metadata for NJ reconstruction')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    source = args.source.resolve(strict=True)
    for name in FILES:
        path = source/name
        if not path.is_file() or path.is_symlink(): raise ValueError('Missing or linked runtime dependency: '+name)
    config = json.loads((source/'codec-config.json').read_text(encoding='utf-8-sig'))
    def inspect(value):
        if isinstance(value,dict):
            for key,item in value.items():
                if key.lower() in ('token','password','secret','privatekey'): raise ValueError('Private configuration field is not distributable: '+key)
                inspect(item)
        elif isinstance(value,list):
            for item in value: inspect(item)
    inspect(config)
    if config.get('libraryDirectory')!='ffmpeg': raise ValueError('Unexpected FFmpeg library directory')
    hashes = {name:digest(source/name) for name in FILES}
    output = repo/'dist'/f'FRD-{VERSION}-win-x64.{args.format}'
    output.parent.mkdir(parents=True,exist_ok=True)
    temporary = output.with_name(output.name+'.tmp')
    if args.reuse_existing:
        if not output.is_file(): raise ValueError('Archive to reuse does not exist')
    elif args.format=='zip':
        with zipfile.ZipFile(temporary,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
            for name in FILES: archive.write(source/name,name)
    else:
        with tarfile.open(temporary,'w:xz',preset=9) as archive:
            for name in FILES: archive.add(source/name,name,recursive=False)
    if not args.reuse_existing: os.replace(temporary,output)
    archive_hash = digest(output)
    if args.format=='zip':
        with zipfile.ZipFile(output) as archive:
            if set(archive.namelist())!=set(FILES): raise ValueError('Unexpected package member')
            for name,expected in hashes.items():
                with archive.open(name) as entry:
                    if hashlib.file_digest(entry,'sha256').hexdigest()!=expected: raise ValueError('Package integrity failure: '+name)
    else:
        with tarfile.open(output,'r:xz') as archive:
            if set(archive.getnames())!=set(FILES) or not all(member.isfile() for member in archive.getmembers()): raise ValueError('Unexpected package member')
            for name,expected in hashes.items():
                with archive.extractfile(name) as entry:
                    if hashlib.file_digest(entry,'sha256').hexdigest()!=expected: raise ValueError('Package integrity failure: '+name)
    manifest = dict(version=VERSION,archive=output.name,format=args.format,sha256=archive_hash,bytes=output.stat().st_size,
        unpackedBytes=sum((source/name).stat().st_size for name in FILES),files=hashes)
    (repo/f'dist/FRD-{VERSION}-{args.format}-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    if args.update_installer:
        installer = repo/'install.ps1'
        text = installer.read_text(encoding='utf-8-sig')
        block = "# FRD release manifest begin\n$frdVersion='"+VERSION+"'\n$frdArchiveName='"+output.name+"'\n$frdArchiveSha256='"+archive_hash+"'\n$frdFiles=@{\n"
        block += ''.join("    '"+name+"'='"+value+"'\n" for name,value in hashes.items())
        block += '}\n# FRD release manifest end'
        text,count = re.subn(r'# FRD release manifest begin\n.*?# FRD release manifest end',lambda _:block,text,flags=re.S)
        if count!=1: raise ValueError('Installer FRD manifest placeholder missing')
        installer.write_text(text,encoding='ascii',newline='\n')
    print(json.dumps({key:value for key,value in manifest.items() if key!='files'},indent=2))
    if args.nj_seed:
        if args.format!='tar.xz': raise ValueError('NJ reconstruction metadata requires the final tar.xz')
        with tarfile.open(output,'r:xz') as archive:
            members = archive.getmembers()
            metadata = []
            for member in members:
                info = {key:getattr(member,key) for key in ('name','mode','uid','gid','size','mtime','uname','gname','linkname','devmajor','devminor','pax_headers')}
                info['type'] = member.type.decode('ascii')
                metadata.append(info)
        meta = dict(finalSha256=archive_hash,archiveName=output.name,compression=dict(format='xz',preset=9),
            files=[dict(name=member.name,sha256=hashes[member.name]) for member in members],members=metadata)
        (repo/'dist/frd-nj-meta.json').write_text(json.dumps(meta,indent=2)+'\n',encoding='utf-8')
        seed = repo/'dist/frd-nj-seed.tar.xz'
        pending_seed = seed.with_name(seed.name+'.tmp')
        with tarfile.open(pending_seed,'w:xz',preset=9) as archive:
            for member in members:
                if member.name.startswith('ffmpeg/'): continue
                with (source/member.name).open('rb') as entry: archive.addfile(member,entry)
        os.replace(pending_seed,seed)
        meta['seedSha256'] = digest(seed)
        metadata_file = repo/'dist/frd-nj-meta.json'
        pending_meta = metadata_file.with_suffix('.json.tmp')
        pending_meta.write_text(json.dumps(meta,indent=2)+'\n',encoding='utf-8')
        os.replace(pending_meta,metadata_file)
        print(json.dumps(dict(seed=str(seed),bytes=seed.stat().st_size,sha256=meta['seedSha256']),indent=2),flush=True)


if __name__=='__main__': main()
