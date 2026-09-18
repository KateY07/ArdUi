namespace ArdUi;

sealed record OverlayHello(string Session, string Key);
sealed record OverlayControl(string Id, string Method, JsonElement Data, string? Error = null);

sealed class OverlayCipher : IDisposable
{
    readonly byte[] sendKey,receiveKey,context;
    readonly long[] serial=new long[2];
    readonly HashSet<ulong> datagrams=new();
    readonly object gate=new();
    ulong high;
    public long Replays,Rejected;
    public OverlayCipher(byte[] secret,byte[] context,bool caller)
    {
        this.context=context;
        sendKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,context,Encoding.ASCII.GetBytes(caller?"ArdUi/2 A-B":"ArdUi/2 B-A"));
        receiveKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,context,Encoding.ASCII.GetBytes(caller?"ArdUi/2 B-A":"ArdUi/2 A-B"));
    }
    public byte[] Encrypt(byte[] plain,bool udp)
    {
        var packet=new byte[plain.Length+25];packet[0]=udp?(byte)1:(byte)0;
        var number=Interlocked.Increment(ref serial[udp?1:0]);if(number<=0)throw new CryptographicException("覆盖层 nonce 已耗尽。");
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1),number);
        Span<byte> nonce=stackalloc byte[12];nonce.Clear();nonce[3]=packet[0];packet.AsSpan(1,8).CopyTo(nonce[4..]);
        using var aes=new AesGcm(sendKey,16);aes.Encrypt(nonce,plain,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length,16),context);
        return packet;
    }
    public byte[]? Decrypt(byte[] packet,bool udp)
    {
        if(packet.Length is <38 or >65536||packet[0]!=(udp?1:0)){Interlocked.Increment(ref Rejected);return null;}
        var seq=BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(1));
        lock(gate)
        {
            if(udp&&(seq==0||datagrams.Contains(seq)||(high>8192&&seq<=high-8192))){Replays++;return null;}
            Span<byte> nonce=stackalloc byte[12];nonce.Clear();nonce[3]=packet[0];packet.AsSpan(1,8).CopyTo(nonce[4..]);
            var plain=new byte[packet.Length-25];
            try{using var aes=new AesGcm(receiveKey,16);aes.Decrypt(nonce,packet.AsSpan(9,plain.Length),packet.AsSpan(9+plain.Length),plain,context);}
            catch(CryptographicException ex){Rejected++;Diagnostics.Log("authentication",ex.Message);return null;}
            if(udp){high=Math.Max(high,seq);datagrams.Add(seq);if(datagrams.Count>16384)datagrams.RemoveWhere(n=>high>8192&&n<=high-8192);}
            return plain;
        }
    }
    public void Dispose(){CryptographicOperations.ZeroMemory(sendKey);CryptographicOperations.ZeroMemory(receiveKey);}
}
