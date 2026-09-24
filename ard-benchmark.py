import argparse
import os
from pathlib import Path
import queue
import socket
import statistics
import struct
import subprocess
import tempfile
import threading
import time


def exact(sock,size):
    chunks=[]
    while size:
        data=sock.recv(min(size,1<<20))
        if not data: raise EOFError
        chunks.append(data); size-=len(data)
    return b''.join(chunks)


def serve(listener,stop):
    block=bytes(1<<20)
    def client(sock):
        with sock:
            sock.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
            command=exact(sock,1)
            if command==b'L':
                while True:
                    try: sock.sendall(exact(sock,8))
                    except EOFError: return
            if command==b'd':
                try:
                    while True: sock.sendall(block)
                except OSError: return
            if command==b'u':
                while sock.recv(len(block)): pass
                sock.sendall(b'K'); return
            size=struct.unpack('!Q',exact(sock,8))[0]
            if command==b'D':
                while size:
                    piece=block[:min(size,len(block))]; sock.sendall(piece); size-=len(piece)
            elif command==b'U':
                while size:
                    size-=len(sock.recv(min(size,len(block))))
                sock.sendall(b'K')
    listener.settimeout(.5)
    while not stop.is_set():
        try: sock,_=listener.accept()
        except TimeoutError: continue
        threading.Thread(target=client,args=(sock,),daemon=True).start()


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0)); return sock.getsockname()[1]


def endpoint(ard,folder):
    return subprocess.check_output([ard,'id'],cwd=folder,text=True).strip().splitlines()[-1]


def start(command,cwd,ready):
    process=subprocess.Popen(command,cwd=cwd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1)
    deadline=time.monotonic()+90
    lines=[]
    while time.monotonic()<deadline:
        line=process.stdout.readline()
        if line:
            lines.append(line.strip())
            if 'network path' in line or 'selected path' in line or 'CONNECTED' in line:
                print('ARD_PATH '+line.strip(),flush=True)
            if ready in line:
                def drain():
                    for later in process.stdout:
                        if 'network path' in later or 'selected path' in later or 'CONNECTED' in later:
                            print('ARD_PATH '+later.strip(),flush=True)
                threading.Thread(target=drain,daemon=True).start()
                return process
        elif process.poll() is not None: break
    raise RuntimeError('ARD did not become ready: '+' | '.join(lines[-8:]))


def connect(local):
    sock=socket.create_connection(('127.0.0.1',local),10)
    sock.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
    return sock


def percentile(values,p):
    values=sorted(values); return values[round((len(values)-1)*p)]


def transfer(local,command,size):
    with connect(local) as sock:
        sock.sendall(command+struct.pack('!Q',size)); start_at=time.perf_counter()
        if command==b'D': exact(sock,size)
        else:
            block=bytes(1<<20); left=size
            while left:
                piece=block[:min(left,len(block))]; sock.sendall(piece); left-=len(piece)
            exact(sock,1)
        return size*8/(time.perf_counter()-start_at)/1e9


def timed_transfer(local,command,duration):
    with connect(local) as sock:
        sock.settimeout(max(5,duration+2)); sock.sendall(command)
        start_at=time.perf_counter(); deadline=start_at+duration; total=0; block=bytes(1<<20)
        if command==b'd':
            while time.perf_counter()<deadline:
                total+=len(sock.recv(len(block)))
        else:
            while time.perf_counter()<deadline:
                sock.sendall(block); total+=len(block)
            sock.shutdown(socket.SHUT_WR); exact(sock,1)
        return total*8/(time.perf_counter()-start_at)/1e9,total


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--ard',type=Path,default=Path(__file__).parent/'tools'/'ard.exe')
    parser.add_argument('--relay',default='http://175.27.160.144:8080')
    parser.add_argument('--seconds',type=float,default=2)
    args=parser.parse_args(); ard=str(args.ard.resolve())
    listener=socket.socket(); listener.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); listener.bind(('127.0.0.1',0)); listener.listen()
    target=listener.getsockname()[1]; local=port(); stop=threading.Event()
    thread=threading.Thread(target=serve,args=(listener,stop),daemon=True); thread.start()
    with tempfile.TemporaryDirectory(prefix='ard-benchmark-') as root:
        server=Path(root)/'server'; client=Path(root)/'client'; server.mkdir(); client.mkdir()
        server_id=endpoint(ard,server); client_id=endpoint(ard,client)
        opened=forwarded=None
        try:
            began=time.perf_counter()
            opened=start([ard,'-v','open',str(target),'to',client_id,'--relay',args.relay],server,'relay online')
            forwarded=start([ard,'-v','forward',str(local),'to',server_id,'--relay',args.relay],client,'READY:')
            ready=time.perf_counter()-began
            with connect(local) as sock:
                sock.sendall(b'L')
                for _ in range(20): sock.sendall(b'12345678'); exact(sock,8)
                latency=[]
                deadline=time.perf_counter()+args.seconds
                while time.perf_counter()<deadline:
                    began=time.perf_counter_ns(); sock.sendall(b'12345678'); exact(sock,8)
                    latency.append((time.perf_counter_ns()-began)/1e6)
            print('LATENCY_SAMPLES_DONE',flush=True)
            stream=[]
            deadline=time.perf_counter()+args.seconds
            while time.perf_counter()<deadline:
                with connect(local) as sock:
                    began=time.perf_counter_ns(); sock.sendall(b'L12345678'); exact(sock,8)
                    stream.append((time.perf_counter_ns()-began)/1e6)
            print('STREAM_SAMPLES_DONE',flush=True)
            download,download_bytes=timed_transfer(local,b'd',args.seconds)
            print('DOWNLOAD_DONE',flush=True)
            upload,upload_bytes=timed_transfer(local,b'u',args.seconds)
            print(f'ARD_SHA256={subprocess.check_output(["powershell","-NoProfile","-Command",f"(Get-FileHash -Algorithm SHA256 \'{ard}\').Hash.ToLowerInvariant()"],text=True).strip()}')
            print(f'TUNNEL_READY_MS={ready*1000:.2f}')
            print(f'STREAM_OPEN_RTT_MS min={min(stream):.3f} mean={statistics.fmean(stream):.3f} p50={percentile(stream,.5):.3f} p95={percentile(stream,.95):.3f} p99={percentile(stream,.99):.3f}')
            print(f'STEADY_RTT_MS min={min(latency):.3f} mean={statistics.fmean(latency):.3f} p50={percentile(latency,.5):.3f} p95={percentile(latency,.95):.3f} p99={percentile(latency,.99):.3f}')
            print(f'SAMPLES steady={len(latency)} streams={len(stream)} download_bytes={download_bytes} upload_bytes={upload_bytes}')
            print(f'DOWNLOAD_GBPS={download:.3f}')
            print(f'UPLOAD_GBPS={upload:.3f}')
        finally:
            for process in (forwarded,opened):
                if process and process.poll() is None: process.kill(); process.wait()
    stop.set(); listener.close(); thread.join(2)


if __name__=='__main__': main()
