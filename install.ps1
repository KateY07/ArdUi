$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1.pre10'
$expectedSha256='74d20e92e1c534ba17ca4225c43c0d0a63f6da88b7bd1f93bdf2924012c2ec66'
$expectedArdSha256='04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d'
$base='https://f.visnova.cn/ardui'
$root=Join-Path $env:LOCALAPPDATA 'ArdUi'
$data=Join-Path $root 'data'
$destination=Join-Path (Join-Path $root 'versions') $version
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi-'+[guid]::NewGuid().ToString('N'))

function Protect-ArdUiDirectory([string]$path){
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @($user,$system)){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))
    }
    if($PSVersionTable.PSEdition -eq 'Core'){
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($path),$acl)
    }else{[IO.Directory]::SetAccessControl($path,$acl)}
}

try{
    if(-not [Environment]::Is64BitOperatingSystem){throw 'ArdUi 需要 64 位 Windows。'}
    $build=[Environment]::OSVersion.Version.Build
    if($build -lt 26100){throw "ArdUi 需要 Windows 11 24H2 / Server 2025 或更新版本（build 26100+）；当前为 $build。"}
    if(-not (Get-Command dotnet -ErrorAction SilentlyContinue)){throw '缺少 .NET 8 Runtime。请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 .NET Runtime x64，再重新执行此命令。'}
    $runtime=@(& dotnet --list-runtimes 2>$null | Where-Object {$_ -match '^Microsoft\.NETCore\.App 8\.'})
    if(-not $runtime){throw '缺少 .NET 8 Runtime。请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 .NET Runtime x64，再重新执行此命令。'}
    New-Item -ItemType Directory -Force $root,(Join-Path $root 'versions'),$data,$temporary | Out-Null
    Protect-ArdUiDirectory $root
    $download=Join-Path $temporary 'ArdUi.exe'
    $ardDownload=Join-Path $temporary 'ard.exe'
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    for($attempt=1;$attempt -le 3;$attempt++){
        try{
            Invoke-WebRequest "$base/ArdUi-$version.exe" -OutFile $download
            Invoke-WebRequest "$base/ard-v2.0.0-pre.6.exe" -OutFile $ardDownload
            break
        }
        catch{if($attempt -eq 3){throw};Start-Sleep -Seconds $attempt}
    }
    $actual=(Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -ne $expectedSha256){throw 'ArdUi 发布文件 SHA-256 不匹配，安装已停止。'}
    if((Get-FileHash $ardDownload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedArdSha256){throw 'ARD 发布文件 SHA-256 不匹配，安装已停止。'}
    New-Item -ItemType Directory -Force $destination | Out-Null
    $installed=Join-Path $destination 'ArdUi.exe'
    if(Test-Path $installed){
        if((Get-FileHash $installed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256){
            try{Move-Item $installed ($installed+'.replaced-'+(Get-Date -Format 'yyyyMMddHHmmss'))}
            catch{throw 'ArdUi 正在运行且需要修复。请关闭程序后再次执行安装命令。'}
        }
    }
    if(-not (Test-Path $installed)){Move-Item $download $installed}
    $installedArd=Join-Path $destination 'ard.exe'
    if(Test-Path $installedArd){
        if((Get-FileHash $installedArd -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedArdSha256){
            try{Move-Item $installedArd ($installedArd+'.replaced-'+(Get-Date -Format 'yyyyMMddHHmmss'))}
            catch{throw 'ard.exe 正在运行且需要修复。请关闭 ArdUi 后再次执行安装命令。'}
        }
    }
    if(-not (Test-Path $installedArd)){Move-Item $ardDownload $installedArd}
    $legacy=Join-Path $data 'identity';$current=Join-Path (Join-Path $data 'device') 'identity'
    if((Test-Path $legacy -PathType Leaf) -and -not (Test-Path $current)){
        if((Get-Item $legacy).Length -ne 32){throw '已有身份文件无效，安装已停止，未覆盖原文件。'}
        New-Item -ItemType Directory -Force (Split-Path $current) | Out-Null
        Copy-Item $legacy $current
    }
    $config=Join-Path $data 'config.json'
    if(-not (Test-Path $config)){
        [IO.File]::WriteAllText($config,@'
{
  "server": "https://f.visnova.cn/",
  "relay": "http://175.27.160.144:8080",
  "relayKey": "spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f",
  "tcpPorts": [3389, 445],
  "udpPorts": [3389]
}
'@,[Text.UTF8Encoding]::new($false))
    }
    $env:ARDUI_DATA_ROOT=$data
    & $installed --identity-store $data
    if($LASTEXITCODE){throw '身份初始化失败；已有身份未被覆盖。'}
    $launcher=Join-Path $root 'ArdUi.cmd'
    [IO.File]::WriteAllLines($launcher,@('@echo off','set "ARDUI_DATA_ROOT=%~dp0data"','start "" "%~dp0versions\v1.pre10\ArdUi.exe"'),[Text.Encoding]::ASCII)
    Protect-ArdUiDirectory $root
    Write-Host "ArdUi $version 已安装到 $root"
    if($env:ARDUI_INSTALL_NO_START -ne '1'){
        Start-Process -FilePath $launcher -WorkingDirectory $root -WindowStyle Hidden
    }
}
finally{if(Test-Path $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}}
