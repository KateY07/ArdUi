$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1.pre11'
$expectedSha256='36e05397db04283a84c5939c11a4e6466de2cbafeec60c77c745eb1e7805f7ae'
$expectedArdSha256='04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d'
$base='https://f.visnova.cn/ardui'
if($env:ARDUI_INSTALL_ROOT){$root=[IO.Path]::GetFullPath($env:ARDUI_INSTALL_ROOT)}else{$root=Join-Path $env:LOCALAPPDATA 'ArdUi'}
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

function Test-VerifiedFile([string]$path,[string]$expected){
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){return $false}
    try{return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected}
    catch{return $false}
}

function Find-VerifiedPayload([string]$name,[string]$expected){
    $current=Join-Path $destination $name
    if(Test-VerifiedFile $current $expected){return $current}
    $versions=Join-Path $root 'versions'
    foreach($candidate in @(Get-ChildItem -LiteralPath $versions -Filter $name -File -Recurse -ErrorAction SilentlyContinue)){
        if($candidate.FullName -ne $current -and (Test-VerifiedFile $candidate.FullName $expected)){return $candidate.FullName}
    }
    return $null
}

function Download-VerifiedPayload([string]$url,[string]$path,[string]$expected,[string]$label){
    for($attempt=1;$attempt -le 3;$attempt++){
        try{
            Invoke-WebRequest $url -OutFile $path
            if(-not (Test-VerifiedFile $path $expected)){throw "$label SHA-256 不匹配。"}
            return $path
        }
        catch{
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            if($attempt -eq 3){throw}
            Start-Sleep -Seconds $attempt
        }
    }
}

function Install-VerifiedPayload([string]$source,[string]$target,[string]$expected,[string]$label){
    if(Test-VerifiedFile $target $expected){Write-Host "复用已校验的 $label";return}
    if(Test-Path -LiteralPath $target){
        try{Move-Item -LiteralPath $target -Destination ($target+'.replaced-'+(Get-Date -Format 'yyyyMMddHHmmss'))}
        catch{throw "$label 正在运行且需要更新或修复。请关闭 ArdUi 后再次执行安装命令。"}
    }
    Copy-Item -LiteralPath $source -Destination $target
    if(-not (Test-VerifiedFile $target $expected)){throw "$label 安装后 SHA-256 校验失败。"}
    Write-Host "已安装 $label"
}

try{
    if(-not [Environment]::Is64BitOperatingSystem){throw 'ArdUi 需要 64 位 Windows。'}
    $build=[Environment]::OSVersion.Version.Build
    if($build -lt 26100){throw "ArdUi 需要 Windows 11 24H2 / Server 2025 或更新版本（build 26100+）；当前为 $build。"}
    if(-not (Get-Command dotnet -ErrorAction SilentlyContinue)){throw '缺少 .NET 8 Runtime。请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 .NET Runtime x64，再重新执行此命令。'}
    $runtime=@(& dotnet --list-runtimes 2>$null | Where-Object {$_ -match '^Microsoft\.NETCore\.App 8\.'})
    if(-not $runtime){throw '缺少 .NET 8 Runtime。请先从 https://dotnet.microsoft.com/download/dotnet/8.0 安装 .NET Runtime x64，再重新执行此命令。'}
    New-Item -ItemType Directory -Force $root,(Join-Path $root 'versions'),$data,$temporary,$destination | Out-Null
    Protect-ArdUiDirectory $root
    $download=Join-Path $temporary 'ArdUi.exe'
    $ardDownload=Join-Path $temporary 'ard.exe'
    $installed=Join-Path $destination 'ArdUi.exe'
    $installedArd=Join-Path $destination 'ard.exe'
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $uiSource=Find-VerifiedPayload 'ArdUi.exe' $expectedSha256
    if($uiSource){Write-Host '本地已找到校验通过的 ArdUi.exe'}else{$uiSource=Download-VerifiedPayload "$base/ArdUi-$version.exe" $download $expectedSha256 'ArdUi.exe'}
    $ardSource=Find-VerifiedPayload 'ard.exe' $expectedArdSha256
    if($ardSource){Write-Host '本地已找到校验通过的 ard.exe'}else{$ardSource=Download-VerifiedPayload "$base/ard-v2.0.0-pre.6.exe" $ardDownload $expectedArdSha256 'ard.exe'}
    Install-VerifiedPayload $uiSource $installed $expectedSha256 'ArdUi.exe'
    Install-VerifiedPayload $ardSource $installedArd $expectedArdSha256 'ard.exe'
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
    [IO.File]::WriteAllLines($launcher,@('@echo off','set "ARDUI_DATA_ROOT=%~dp0data"',('start "" "%~dp0versions\{0}\ArdUi.exe"' -f $version)),[Text.Encoding]::ASCII)
    if($env:ARDUI_INSTALL_NO_SHORTCUT -ne '1'){
        $startup=[Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
        $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startup 'ArdUi.lnk'))
        $shortcut.TargetPath=$env:ComSpec;$shortcut.Arguments=('/d /c ""{0}""' -f $launcher)
        $shortcut.WorkingDirectory=$root;$shortcut.WindowStyle=7;$shortcut.Description='ArdUi 开机自启动';$shortcut.Save()
    }
    Protect-ArdUiDirectory $root
    Write-Host "ArdUi $version 已安装到 $root"
    if($env:ARDUI_INSTALL_NO_START -ne '1'){
        Start-Process -FilePath $launcher -WorkingDirectory $root -WindowStyle Hidden
    }
}
finally{if(Test-Path $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}}
