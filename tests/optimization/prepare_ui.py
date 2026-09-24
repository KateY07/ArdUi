"""Create isolated, opt-in C# benchmark variants without changing shipping sources."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil

REPO = Path(__file__).resolve().parents[2]
DEST = REPO / "dist" / "optimization-round1" / "ui-source"
MANIFEST: dict[str, dict[str, str]] = {}


def patch(relative: str, replacements: list[tuple[str, str]]) -> None:
    target = DEST / relative
    source = target.read_text(encoding="utf-8-sig")
    for before, after in replacements:
        count = source.count(before)
        if count != 1:
            raise RuntimeError(f"{relative}: expected exactly one patch anchor, got {count}: {before[:90]!r}")
        source = source.replace(before, after, 1)
    target.write_text(source, encoding="utf-8", newline="\n")


def main() -> None:
    DEST.mkdir(parents=True, exist_ok=True)
    for folder in ("Core", "Transport", "Overlay", "TransitClient", "Frd"):
        shutil.copytree(REPO / folder, DEST / folder, dirs_exist_ok=True)
        for source in sorted((REPO / folder).rglob("*.cs")):
            MANIFEST[str(source.relative_to(REPO)).replace("\\", "/")] = {
                "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest()
            }

    (DEST / "Overlay" / "BenchmarkVariant.cs").write_text('''namespace ArdUi;

// This file exists only in the isolated benchmark source tree.
static class BenchmarkVariant
{
    public static readonly string Name=Read();
    public static bool Aes=>Name is "aes" or "combined";
    public static bool Packed=>Name is "packed" or "combined";
    public static bool Resend=>Name is "resend" or "combined";
    public static bool Udp=>Name=="udp";
    static string Read()
    {
        var name=Environment.GetEnvironmentVariable("ARDUI_BENCH_VARIANT")??"baseline";
        if(name is not ("baseline" or "aes" or "packed" or "resend" or "combined" or "udp"))throw new InvalidOperationException("Unknown benchmark variant: "+name);
        Console.WriteLine("[benchmark-variant] "+name);
        return name;
    }
}
''', encoding="utf-8", newline="\n")

    patch("Transport/ArdProcess.cs", [(
        'const string expected="04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d";',
        '''var expected=Environment.GetEnvironmentVariable("ARDUI_BENCH_ARD_SHA256")??"04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d";
        if(!Regex.IsMatch(expected,@"\\A[0-9a-f]{64}\\z"))throw new IOException("Invalid benchmark ARD SHA-256 pin.");''')])

    patch("Overlay/OverlayCrypto.cs", [(
        'readonly object gate=new();',
        '''readonly object gate=new(),sendGate=new();
    readonly AesGcm? sendAes,receiveAes;'''), (
        'receiveKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,context,Encoding.ASCII.GetBytes(caller?"ArdUi/2 B-A":"ArdUi/2 A-B"));',
        '''receiveKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,context,Encoding.ASCII.GetBytes(caller?"ArdUi/2 B-A":"ArdUi/2 A-B"));
        if(BenchmarkVariant.Aes){sendAes=new(sendKey,16);receiveAes=new(receiveKey,16);}'''), (
        'using var aes=new AesGcm(sendKey,16);aes.Encrypt(nonce,plain,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length,16),context);',
        '''if(sendAes is{} reused)
        {lock(sendGate)reused.Encrypt(nonce,plain,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length,16),context);}
        else
        {using var aes=new AesGcm(sendKey,16);aes.Encrypt(nonce,plain,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length,16),context);}'''), (
        'try{using var aes=new AesGcm(receiveKey,16);aes.Decrypt(nonce,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length),plain,context);}',
        '''try
            {
                if(receiveAes is{} reused)reused.Decrypt(nonce,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length),plain,context);
                else{using var aes=new AesGcm(receiveKey,16);aes.Decrypt(nonce,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length),plain,context);}
            }'''), (
        'public void Dispose(){CryptographicOperations.ZeroMemory(sendKey);CryptographicOperations.ZeroMemory(receiveKey);}',
        '''public void Dispose()
    {
        lock(sendGate){sendAes?.Dispose();CryptographicOperations.ZeroMemory(sendKey);}
        lock(gate){receiveAes?.Dispose();CryptographicOperations.ZeroMemory(receiveKey);}
    }''')])

    patch("Overlay/OverlayLink.cs", [(
        '''var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,data.Length);
            await tcp.GetStream().WriteAsync(header,linked.Token);await tcp.GetStream().WriteAsync(data,linked.Token);''',
        '''if(BenchmarkVariant.Packed)
            {
                var packet=System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length+4);
                try
                {
                    BinaryPrimitives.WriteInt32BigEndian(packet,data.Length);data.CopyTo(packet,4);
                    await tcp.GetStream().WriteAsync(packet.AsMemory(0,data.Length+4),linked.Token);
                }
                finally{System.Buffers.ArrayPool<byte>.Shared.Return(packet);}
            }
            else
            {
                var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,data.Length);
                await tcp.GetStream().WriteAsync(header,linked.Token);await tcp.GetStream().WriteAsync(data,linked.Token);
            }''')])

    patch("Overlay/OverlaySession.cs", [(
        'const int Chunk=16384,Window=512,Fragment=1100;',
        '''const int Chunk=16384,Window=512;
    // The udp experiment requires both peers to use this variant and a verified sufficient path MTU.
    static int Fragment=>BenchmarkVariant.Udp?1280:1100;'''), (
        'readonly SortedDictionary<long,byte[]> pending=new(),reorder=new();',
        '''readonly SortedDictionary<long,byte[]> pending=new(),reorder=new();
    readonly Dictionary<long,(long Sent,long Epoch)> pendingSent=new();
    long pathEpoch=1,lastAckProgress=Environment.TickCount64,acknowledgedSequence;'''), (
        'links[link.Name]=link;link.Reader=link.Read(Receive);Diagnostics.Log("path-added",link.Name);Changed?.Invoke();',
        '''if(BenchmarkVariant.Resend)
        {lock(sequenceGate){links[link.Name]=link;if(link.Name==selected)pathEpoch++;}}
        else links[link.Name]=link;
        link.Reader=link.Read(Receive);Diagnostics.Log("path-added",link.Name);Changed?.Invoke();'''), (
        'selected=name;Interlocked.Increment(ref Switches);lastResend=0;Diagnostics.Log("path-selected",name);Changed?.Invoke();',
        '''if(BenchmarkVariant.Resend){lock(sequenceGate){selected=name;pathEpoch++;}}
        else selected=name;
        Interlocked.Increment(ref Switches);lastResend=0;
        Diagnostics.Log("path-selected",name);Changed?.Invoke();'''), (
        '''async Task SendReliable(byte kind,uint flow,byte[] body)
    {
        await window.WaitAsync(stop.Token);byte[] frame;
        lock(sequenceGate){frame=Frame(kind,++sendSequence,flow,body);pending.Add(sendSequence,frame);}
        if(Active() is{} link)
            try{await SendOn(link,frame,false);}catch(Exception ex)when(!stop.IsCancellationRequested){Diagnostics.Log("send-deferred",ex.Message);}
    }''',
        '''async Task SendReliable(byte kind,uint flow,byte[] body)
    {
        await window.WaitAsync(stop.Token);byte[] frame;long epoch;
        lock(sequenceGate)
        {
            frame=Frame(kind,++sendSequence,flow,body);pending.Add(sendSequence,frame);epoch=pathEpoch;
            if(BenchmarkVariant.Resend)pendingSent[sendSequence]=(Environment.TickCount64,epoch);
        }
        if(Active() is{} link)
            try{await SendOn(link,frame,false);if(BenchmarkVariant.Resend)MarkSent(frame,epoch);}
            catch(Exception ex)when(!stop.IsCancellationRequested){Diagnostics.Log("send-deferred",ex.Message);}
    }
    void MarkSent(byte[] frame,long epoch)
    {
        var seq=BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(1));
        lock(sequenceGate)if(pending.ContainsKey(seq))pendingSent[seq]=(Environment.TickCount64,epoch);
    }'''), (
        'foreach(var id in pending.Keys.TakeWhile(n=>n<=seq).ToArray()){pending.Remove(id);window.Release();}',
        '''if(BenchmarkVariant.Resend&&seq>acknowledgedSequence){acknowledgedSequence=seq;lastAckProgress=Environment.TickCount64;}
                foreach(var id in pending.Keys.TakeWhile(n=>n<=seq).ToArray()){pending.Remove(id);if(BenchmarkVariant.Resend)pendingSent.Remove(id);window.Release();}'''), (
        '''if(Environment.TickCount64-lastResend>=1000&&active!=null)
                {
                    lastResend=Environment.TickCount64;byte[][] frames;lock(sequenceGate)frames=pending.Values.Take(64).ToArray();
                    foreach(var frame in frames){await SendOn(active,frame,false);Retransmits++;}
                }''',
        '''if(BenchmarkVariant.Resend&&active!=null)
                {
                    var now=Environment.TickCount64;byte[][] frames;long epoch;
                    lock(sequenceGate)
                    {
                        active=Active();epoch=pathEpoch;
                        frames=active==null?[]:pending.Where(p=>pendingSent.TryGetValue(p.Key,out var stamp)&&
                            (stamp.Epoch!=epoch||now-stamp.Sent>=1000&&now-lastAckProgress>=1000))
                            .Take(64).Select(p=>p.Value).ToArray();
                    }
                    foreach(var frame in frames){await SendOn(active!,frame,false);MarkSent(frame,epoch);Retransmits++;}
                }
                else if(!BenchmarkVariant.Resend&&Environment.TickCount64-lastResend>=1000&&active!=null)
                {
                    lastResend=Environment.TickCount64;byte[][] frames;lock(sequenceGate)frames=pending.Values.Take(64).ToArray();
                    foreach(var frame in frames){await SendOn(active,frame,false);Retransmits++;}
                }''')])

    for name, hashes in MANIFEST.items():
        hashes["variantSha256"] = hashlib.sha256((DEST / name).read_bytes()).hexdigest()
    added = DEST / "Overlay" / "BenchmarkVariant.cs"
    MANIFEST["Overlay/BenchmarkVariant.cs"] = {"variantSha256": hashlib.sha256(added.read_bytes()).hexdigest()}
    (DEST / "source-manifest.json").write_text(json.dumps({
        "variants": ["baseline", "aes", "packed", "resend", "combined", "udp"],
        "sources": MANIFEST,
        "notes": {
            "baseline": "Original algorithms with benchmark option checks; compare with unmodified production baseline too.",
            "aes": "Reuse one AES-GCM object per session direction; send lock and original receive gate; original unique nonces.",
            "packed": "One TCP WriteAsync for length and encrypted payload, rented temporary buffer, finally returned.",
            "resend": "Resend only aged frames after ACK progress stalls; path epoch changes force replay independent of age.",
            "combined": "aes + packed + resend; no protocol or architecture changes.",
            "udp": "Experimental 1280-byte fragment limit on both peers, otherwise baseline behavior. Only for paths already verified to support the complete encapsulated packet (this localhost experiment: max_datagram=1414); not production path-MTU adaptation or mixed-peer compatibility.",
            "recovery": "Pending data stays owned until acknowledged; same-name active link reattachment increments epoch; maintenance checks path replay each iteration."
        }
    }, indent=2), encoding="utf-8", newline="\n")
    print(DEST)


if __name__ == "__main__":
    main()
