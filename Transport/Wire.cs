namespace ArdUi;

static class Wire
{
    public const int MaxUdp = 1482; // 16-byte session capability + u16 target port, within ARD's 1500 bytes.
    public const int BandwidthProbeSize=65536;
    public static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Endpoint(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.StartsWith("ardui:")) value = value[6..].TrimStart('/');
        if (!Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z"))
            throw new InvalidDataException("EndpointId 应为 64 位十六进制公钥。请粘贴完整 ID 或选择二维码图片。");
        return value;
    }
    public static string Machine(string value)
    {
        value = value.Trim().ToUpperInvariant();
        if (value.StartsWith("ARDUI:")) value = value[6..].TrimStart('/');
        if (!Regex.IsMatch(value, @"\A[A-Z0-9]{6}\z")) throw new InvalidDataException("机器编号应为 6 位大写字母或数字。");
        return value;
    }
    public static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static void ConfigureUdp(UdpClient udp)
    {
        udp.Client.ReceiveBufferSize=1<<20;
        if(OperatingSystem.IsWindows())
            try{udp.Client.IOControl(unchecked((int)0x9800000C),new byte[4],null);}
            catch(SocketException ex){Diagnostics.Log("udp-socket-option",ex.Message);}
    }
    public static async Task<byte[]> Read(Stream s, int size, CancellationToken ct)
    { var b = new byte[size]; await s.ReadExactlyAsync(b, ct); return b; }
    public static async Task WriteJson<T>(Stream s, T value, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (data.Length > 8192) throw new InvalidDataException("消息过大。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await s.WriteAsync(header, ct); await s.WriteAsync(data, ct);
    }
    public static async Task<T> ReadJson<T>(Stream s, CancellationToken ct)
    {
        var size = BinaryPrimitives.ReadInt32BigEndian(await Read(s, 4, ct));
        if (size is < 1 or > 8192) throw new InvalidDataException("无效的握手长度。");
        return JsonSerializer.Deserialize<T>(await Read(s, size, ct), Json) ?? throw new InvalidDataException("握手为空。");
    }
    public static byte[] Packet(byte[] token, int port, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxUdp) throw new InvalidDataException("UDP 报文超出虚拟网络 MTU。");
        var data = new byte[18 + payload.Length]; token.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), checked((ushort)port));
        payload.CopyTo(data.AsSpan(18)); return data;
    }
    public static bool ValidPacket(byte[] data, byte[] token) => data.Length is >= 18 and <= 1500 &&
        CryptographicOperations.FixedTimeEquals(data.AsSpan(0, 16), token);
    // FIN in one direction must not cancel the reverse direction (RDP/SMB rely on this).
    public static async Task Bridge(TcpClient a, TcpClient b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var aStream = a.GetStream(); var bStream = b.GetStream();
        async Task Copy(NetworkStream from, NetworkStream to, Socket socket)
        {
            try { await from.CopyToAsync(to, linked.Token); socket.Shutdown(SocketShutdown.Send); }
            catch { linked.Cancel(); throw; }
        }
        await Task.WhenAll(Copy(aStream, bStream, b.Client), Copy(bStream, aStream, a.Client));
    }
}
