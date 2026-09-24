namespace ArdUi;

// Query the launched process's sockets without connecting to or interpreting FRD.
static class FrdListener
{
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable([Out] byte[] table,ref int size,bool ordered,int family,int kind,uint reserved);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable([Out] byte[] table,ref int size,bool ordered,int family,int kind,uint reserved);

    public static bool Ready(int processId,int port)=>Bound(true,processId,port)&&Bound(false,processId,port);

    static bool Bound(bool tcp,int processId,int port)
    {
        var buffer=new byte[4096];
        for(var attempt=0;attempt<4;attempt++)
        {
            var size=buffer.Length;
            // IPv4 TCP_TABLE_OWNER_PID_LISTENER / UDP_TABLE_OWNER_PID.
            var error=tcp?GetExtendedTcpTable(buffer,ref size,false,2,3,0):GetExtendedUdpTable(buffer,ref size,false,2,1,0);
            if(error==122){buffer=new byte[checked(Math.Max(size,buffer.Length*2))];continue;}
            if(error!=0)throw new System.ComponentModel.Win32Exception((int)error,"无法查询 FRD 的本地监听端口。");
            var stride=tcp?24:12;var count=BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if(count>(buffer.Length-4)/stride)throw new IOException("系统返回了无效的监听端口表。");
            for(var index=0;index<count;index++)
            {
                var row=buffer.AsSpan(4+index*stride,stride);var address=tcp?4:0;
                if(BinaryPrimitives.ReadUInt32LittleEndian(row[(tcp?20:8)..])!=(uint)processId)continue;
                if(BinaryPrimitives.ReadUInt16BigEndian(row[(address+4)..])!=port)continue;
                if(row[address]==127&&row[address+1]==0&&row[address+2]==0&&row[address+3]==1)return true;
            }
            return false;
        }
        throw new IOException("监听端口表持续变化，无法确认 FRD 启动状态。");
    }

    public static async Task Wait(Process process,int port,CancellationToken ct,TimeSpan? budget=null)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget??TimeSpan.FromSeconds(20));
        try
        {
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(process.HasExited)throw new IOException($"FRD 被控端启动失败（退出码 {process.ExitCode}），请查看诊断日志。");
                if(Ready(process.Id,port)&&!process.HasExited)return;
                await Task.Delay(100,timeout.Token);
            }
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested)
        {throw new IOException($"等待 FRD 监听 127.0.0.1:{port} 的 TCP 和 UDP 超时，请查看诊断日志。");}
    }
}
