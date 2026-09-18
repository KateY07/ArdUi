"""Rebuild the pinned FRD distribution from uploaded app files and upstream FFmpeg."""
import argparse
import concurrent.futures
import ctypes
import ctypes.util
import hashlib
import json
from pathlib import Path
import re
import tarfile
import time
import urllib.request

UPSTREAM = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-full_build-shared.7z'
UPSTREAM_SHA = 'cba748035c21ce1431d0823c7a3a711f38616f89f87a265dceddf9b7f6749d2d'
UPSTREAM_SIZE = 59_459_100

def sha(path):
    with path.open('rb') as stream:
        digest = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024*1024), b''): digest.update(chunk)
        return digest.hexdigest()

def fetch(stage):
    archive = stage/'ffmpeg-upstream.7z'
    if archive.exists() and sha(archive) == UPSTREAM_SHA: return archive
    parts = stage/'ffmpeg-download-parts';parts.mkdir(exist_ok=True)
    chunk_size = 2*1024*1024
    ranges = [(index, start, min(start+chunk_size, UPSTREAM_SIZE)-1)
              for index, start in enumerate(range(0, UPSTREAM_SIZE, chunk_size))]
    prefix_size = archive.stat().st_size if archive.exists() else 0
    if 0 < prefix_size < UPSTREAM_SIZE:
        with archive.open('rb') as source:
            for index, start, end in ranges:
                reusable = min(end-start+1, max(0, prefix_size-start))
                part = parts/f'{index:03d}.part'
                existing = part.stat().st_size if part.exists() else 0
                if reusable > existing:
                    source.seek(start+existing)
                    with part.open('ab') as output: output.write(source.read(reusable-existing))

    def download(item):
        index, start, end = item;part = parts/f'{index:03d}.part'
        for attempt in range(5):
            offset = part.stat().st_size if part.exists() else 0
            if offset == end-start+1: return
            if offset > end-start+1: raise ValueError('Oversized cached FFmpeg part')
            request = urllib.request.Request(UPSTREAM, headers={
                'Range':f'bytes={start+offset}-{end}', 'Accept-Encoding':'identity'})
            try:
                with urllib.request.urlopen(request, timeout=45) as response:
                    expected_range = f'bytes {start+offset}-{end}/{UPSTREAM_SIZE}'
                    if response.status != 206 or response.headers.get('Content-Range') != expected_range:
                        raise ValueError(f'Unexpected FFmpeg range response: {response.status} '
                                         f'{response.headers.get("Content-Range")}')
                    remaining = end-start+1-offset
                    length = response.headers.get('Content-Length')
                    if length is not None and int(length) != remaining:
                        raise ValueError('Unexpected FFmpeg range length')
                    with part.open('ab') as output:
                        while remaining:
                            chunk = response.read(min(64*1024, remaining))
                            if not chunk: raise EOFError('Incomplete FFmpeg range response')
                            output.write(chunk);output.flush();remaining -= len(chunk)
                        if response.read(1): raise ValueError('Oversized FFmpeg range response')
                return
            except Exception as error:
                print(f'FFmpeg part {index+1}/{len(ranges)} attempt {attempt+1}: {error}', flush=True)
                if attempt == 4: raise
                time.sleep(2)

    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        pending = {pool.submit(download, item) for item in ranges}
        while pending:
            completed, pending = concurrent.futures.wait(pending, timeout=20,
                return_when=concurrent.futures.FIRST_EXCEPTION)
            for future in completed: future.result()
            received = sum(part.stat().st_size for part in parts.glob('*.part'))
            print(f'FFmpeg download: {received:,}/{UPSTREAM_SIZE:,} bytes '
                  f'({received/UPSTREAM_SIZE:.0%}), {time.monotonic()-started:.0f}s', flush=True)
    complete = stage/'ffmpeg-upstream.complete'
    with complete.open('wb') as output:
        for index, start, end in ranges:
            with (parts/f'{index:03d}.part').open('rb') as source:
                while chunk := source.read(1024*1024): output.write(chunk)
    if complete.stat().st_size != UPSTREAM_SIZE or sha(complete) != UPSTREAM_SHA:
        raise ValueError('Official FFmpeg archive hash mismatch; cached parts retained for inspection')
    complete.replace(archive)
    print('Official FFmpeg archive verified.', flush=True)
    return archive

def extract_ffmpeg(archive, destination, expected):
    api = ctypes.CDLL(ctypes.util.find_library('archive') or 'libarchive.so.13')
    pointer = ctypes.c_void_p
    signatures = {
        'archive_read_new': ([], pointer), 'archive_read_support_filter_all': ([pointer], ctypes.c_int),
        'archive_read_support_format_7zip': ([pointer], ctypes.c_int),
        'archive_read_open_filename': ([pointer,ctypes.c_char_p,ctypes.c_size_t],ctypes.c_int),
        'archive_read_next_header': ([pointer,ctypes.POINTER(pointer)],ctypes.c_int),
        'archive_entry_pathname': ([pointer],ctypes.c_char_p),
        'archive_read_data': ([pointer,pointer,ctypes.c_size_t],ctypes.c_ssize_t),
        'archive_read_data_skip': ([pointer],ctypes.c_int), 'archive_read_free': ([pointer],ctypes.c_int),
        'archive_error_string': ([pointer],ctypes.c_char_p)}
    for name, (arguments, result) in signatures.items():
        function = getattr(api,name); function.argtypes = arguments; function.restype = result
    reader = api.archive_read_new()
    if not reader: raise RuntimeError('Could not allocate archive reader')
    wanted = {Path(name).name:name for name in expected if name.startswith('ffmpeg/')}
    wanted['LICENSE'] = wanted.pop('LICENSE.txt')
    buffer = ctypes.create_string_buffer(1024*1024)
    try:
        api.archive_read_support_filter_all(reader);api.archive_read_support_format_7zip(reader)
        if api.archive_read_open_filename(reader,str(archive).encode(),1024*1024) != 0:
            raise RuntimeError(api.archive_error_string(reader))
        entry = pointer()
        while True:
            status = api.archive_read_next_header(reader,ctypes.byref(entry))
            if status == 1: break
            if status != 0: raise RuntimeError(api.archive_error_string(reader))
            name = Path(api.archive_entry_pathname(entry).decode()).name
            if name not in wanted: api.archive_read_data_skip(reader);continue
            target = destination/wanted[name];target.parent.mkdir(parents=True,exist_ok=True)
            with target.open('wb') as output:
                while True:
                    count = api.archive_read_data(reader,buffer,len(buffer))
                    if count == 0: break
                    if count < 0: raise RuntimeError(api.archive_error_string(reader))
                    output.write(buffer.raw[:count])
            if sha(target) != expected[wanted[name]]: raise ValueError('FFmpeg file hash mismatch: '+name)
    finally: api.archive_read_free(reader)
    for name in wanted.values():
        if not (destination/name).is_file() or sha(destination/name) != expected[name]:
            raise ValueError('Missing verified FFmpeg file: '+name)
    print('All FFmpeg runtime files verified.',flush=True)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stage',type=Path)
    parser.add_argument('--prepare',action='store_true')
    args=parser.parse_args();stage=args.stage.resolve()
    if stage.parent != Path('/tmp') or not stage.name.startswith('ardui-v2.pre2-'):
        raise ValueError('Unexpected deployment stage')
    manifest=json.loads((stage/'frd-files.json').read_text())
    expected=manifest['files'];runtime=stage/'frd-runtime';runtime.mkdir(exist_ok=True)
    extract_ffmpeg(fetch(stage),runtime,expected)
    if args.prepare: return
    metadata=json.loads((stage/'frd-nj-meta.json').read_text())
    seed=stage/'frd-nj-seed.tar.xz'
    if sha(seed)!=metadata['seedSha256']: raise ValueError('Uploaded seed hash mismatch')
    with tarfile.open(seed,'r:xz') as archive:
        for member in archive.getmembers():
            if not member.isfile() or member.name not in ('FRD.exe','codec-config.json','THIRD-PARTY-NOTICES.md'):
                raise ValueError('Unexpected seed member')
            with archive.extractfile(member) as source,(runtime/member.name).open('wb') as output:
                while chunk:=source.read(1024*1024): output.write(chunk)
    for name,digest in expected.items():
        if sha(runtime/name)!=digest: raise ValueError('Runtime hash mismatch: '+name)
    output=stage/metadata['archiveName']
    if not re.fullmatch(r'FRD-v1\.pre[0-9]+-win-x64\.tar\.xz', metadata['archiveName']):
        raise ValueError('Unexpected archive filename')
    with tarfile.open(output,'w:xz',preset=9) as archive:
        for values in metadata['members']:
            if values['name'] not in expected: raise ValueError('Unknown final member')
            info=tarfile.TarInfo(values['name'])
            for field in ('mode','uid','gid','size','mtime','uname','gname','linkname','devmajor','devminor','pax_headers'):
                setattr(info,field,values[field])
            info.type=tarfile.REGTYPE
            with (runtime/info.name).open('rb') as source: archive.addfile(info,source)
    digest=sha(output)
    print(json.dumps({'archive':output.name,'sha256':digest,'bytes':output.stat().st_size,
        'matchesLocalArchive':digest==metadata['finalSha256']}),flush=True)
    with tarfile.open(output,'r:xz') as archive:
        members=archive.getmembers()
        if len(members)!=len(expected) or {m.name for m in members}!=set(expected): raise ValueError('Final members mismatch')
        for member in members:
            with archive.extractfile(member) as source:
                if hashlib.sha256(source.read()).hexdigest()!=expected[member.name]: raise ValueError('Final member hash mismatch')
    print('Final compressed archive contents verified.',flush=True)

if __name__=='__main__': main()
