namespace ArdUi;

static class WindowsShares
{
    public static async Task<string> Map(Session session,string share,string? user,string? password,CancellationToken ct)
    {
        if(!Regex.IsMatch(share,@"\A[^\\/:*?\""<>|\x00-\x1f]{1,80}\z")) throw new InvalidDataException("请输入单个共享名，例如 Documents。");
        var bridge=session.Forward(445);
        var remote=@"\\localhost\"+share;
        var script="""
        $ErrorActionPreference='Stop'
        $d=[Console]::In.ReadToEnd() | ConvertFrom-Json
        $used=@(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name)
        $letter=90..68 | ForEach-Object { [string][char]$_ } | Where-Object { $_ -notin $used } | Select-Object -First 1
        if(-not $letter){throw '没有可用的盘符。'}
        $argsMap=@{LocalPath=($letter+':');RemotePath=$d.Remote;TcpPort=[uint16]$d.Port;Persistent=$false;ErrorAction='Stop'}
        if($d.User){$argsMap.Credential=[pscredential]::new($d.User,(ConvertTo-SecureString $d.Password -AsPlainText -Force))}
        New-SmbMapping @argsMap | Out-Null
        Write-Output ($letter+':')
        """;
        var drive=(await Run(script,new { Remote=remote,Port=bridge.Port,User=user,Password=password },ct)).Trim();
        if(!Regex.IsMatch(drive,@"\A[D-Z]:\z")) throw new IOException("Windows 返回无效共享映射。");
        var mappings=session.Hub?.Mappings??throw new IOException("共享转发入口不可用。");
        lock(mappings)mappings[drive]=remote;
        return drive+"\\";
    }
    public static async Task Remove(string drive,string remote)
    {
        var script="""
        $ErrorActionPreference='Stop'
        $d=[Console]::In.ReadToEnd() | ConvertFrom-Json
        $m=Get-SmbMapping -LocalPath $d.Drive -ErrorAction SilentlyContinue
        if($m -and $m.RemotePath -eq $d.Remote){Remove-SmbMapping -LocalPath $d.Drive -Force -Confirm:$false -ErrorAction Stop}
        """;
        try { await Run(script,new{Drive=drive,Remote=remote},CancellationToken.None); } catch { }
    }
    static async Task<string> Run(string script,object input,CancellationToken ct)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        using var process=Process.Start(start) ?? throw new IOException("无法启动 Windows SMB 客户端。");
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));process.StandardInput.Close();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try{await process.WaitForExitAsync(deadline.Token);}catch{try{process.Kill(true);}catch{}throw;}
        var message=await error;var text=await output;
        if(process.ExitCode!=0)throw new IOException("Windows SMB 连接失败，请检查共享名、账户及系统策略。"+Environment.NewLine+message);
        return text;
    }
}
