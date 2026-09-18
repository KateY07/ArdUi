$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v2.pre2'
$expectedSha256='9daf89bf0b98189e0f93fa3ef0bf7e632d067925280929eb52d87cfc298ea6e7'
$expectedArdSha256='04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d'
# FRD release manifest begin
$frdVersion='v1.pre6'
$frdArchiveName='FRD-v1.pre6-win-x64.tar.xz'
$frdArchiveSha256='6aee2eb0912d1b0915eef79156c06bcf64c8c008181d2e6b26845137da09944d'
$frdFiles=@{
    'FRD.exe'='37c95ff75dceb75c222b5afdc769a7941d7c9a8c96ce5543e09a2927d0ea2286'
    'codec-config.json'='2625bdcb5ba824a36e0b8529b981ed036e57a5ecd004c4ac842cd33bc485b939'
    'THIRD-PARTY-NOTICES.md'='850a93669aec92a0a1e49831fc15582fe7cc66df6e37ebb557794dec2cb4ae68'
    'ffmpeg/LICENSE.txt'='8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903'
    'ffmpeg/avcodec-62.dll'='34f5b1baac01c4be3edf464309c79db05ffbd4a9c905c94b4a4651cd15370296'
    'ffmpeg/avdevice-62.dll'='d213d6cad9f3a526f7664ebb3f93d6882669540db7164daecd03f52e0f5288cc'
    'ffmpeg/avfilter-11.dll'='e318cac83d648869180d0b57c45f21aad1e4db34a467000c157da2938ff7f63f'
    'ffmpeg/avformat-62.dll'='c04e6ed2f9f36d42325d4f4df5babb5d6ce7c55dbffeb7ef1007e25e97bcb716'
    'ffmpeg/avutil-60.dll'='6f172b5d10224fcc3f729c8baa6fd36a97bb58042bb3b1417078d77d2da59b87'
    'ffmpeg/swresample-6.dll'='72e2721672c11fd37d983b05cc2370f612784e4e3218362a0cb4315d08c917fb'
    'ffmpeg/swscale-9.dll'='3d07972cada6ba38c492e92b0f6c025a6835607fe00cdf83afc604c2fbdfe550'
}
# FRD release manifest end
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
    catch{Write-Warning "无法校验文件 $path ($($_.Exception.Message))";return $false}
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
            Write-Warning "$label 下载或校验失败（第 $attempt 次）：$($_.Exception.Message)"
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

function Test-FrdDirectory([string]$path){
    if(-not (Test-Path -LiteralPath $path -PathType Container)){return $false}
    foreach($name in $frdFiles.Keys){if(-not (Test-VerifiedFile (Join-Path $path $name) $frdFiles[$name])){return $false}}
    return $true
}

function Install-FrdRuntime {
    if($frdArchiveSha256 -notmatch '^[0-9a-f]{64}$' -or $frdFiles.Count -lt 4){throw 'FRD release hashes are missing.'}
    $frdRoot=Join-Path $root 'frd'
    $frdDestination=Join-Path $frdRoot $frdVersion
    $frdCacheRoot=Join-Path $root 'cache'
    foreach($path in @($frdRoot,$frdDestination,$frdCacheRoot,(Join-Path $frdDestination 'ffmpeg'))){
        if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'FRD installation directories cannot be links.'}
    }
    if(Test-FrdDirectory $frdDestination){Write-Host "复用已完整校验的 FRD $frdVersion";return}
    $frdSource=if($env:ARDUI_FRD_SOURCE){[IO.Path]::GetFullPath($env:ARDUI_FRD_SOURCE)}else{Join-Path (Join-Path $env:LOCALAPPDATA 'Programs\FRD') $frdVersion}
    if(Test-FrdDirectory $frdSource){Write-Host '本机已有完整且校验通过的 FRD，直接复用运行文件。'}
    else{
        New-Item -ItemType Directory -Force $frdCacheRoot | Out-Null
        $frdArchive=Join-Path $frdCacheRoot $frdArchiveName
        if(Test-VerifiedFile $frdArchive $frdArchiveSha256){Write-Host '复用已校验的 FRD 安装包缓存。'}
        else{
            $frdDownload=Join-Path $temporary $frdArchiveName
            Download-VerifiedPayload "$base/$frdArchiveName" $frdDownload $frdArchiveSha256 'FRD 安装包' | Out-Null
            Move-Item -LiteralPath $frdDownload -Destination $frdArchive -Force
        }
        $frdSource=Join-Path $temporary 'frd-runtime'
        New-Item -ItemType Directory -Path $frdSource | Out-Null
        if($frdArchiveName.EndsWith('.zip',[StringComparison]::OrdinalIgnoreCase)){
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive=[IO.Compression.ZipFile]::OpenRead($frdArchive)
            try{
                if($archive.Entries.Count -ne $frdFiles.Count){throw 'Unexpected FRD archive file count.'}
                foreach($entry in $archive.Entries){if(-not $frdFiles.ContainsKey($entry.FullName)){throw 'Unexpected FRD archive path.'}}
            }finally{$archive.Dispose()}
            [IO.Compression.ZipFile]::ExtractToDirectory($frdArchive,$frdSource)
        }
        elseif($frdArchiveName.EndsWith('.tar.xz',[StringComparison]::OrdinalIgnoreCase)){
            $tar=Join-Path $env:SystemRoot 'System32\tar.exe'
            if(-not (Test-Path -LiteralPath $tar -PathType Leaf)){throw 'Windows tar.exe is required to extract the FRD runtime.'}
            $names=@(& $tar -tf $frdArchive)
            if($LASTEXITCODE -ne 0 -or $names.Count -ne $frdFiles.Count){throw 'Cannot inspect FRD archive.'}
            foreach($name in $names){if(-not $frdFiles.ContainsKey($name)){throw 'Unexpected FRD archive path.'}}
            & $tar -xf $frdArchive -C $frdSource
            if($LASTEXITCODE -ne 0){throw 'Cannot extract FRD runtime.'}
        }
        else{throw 'Unsupported FRD archive format.'}
        if(-not (Test-FrdDirectory $frdSource)){throw 'FRD extracted files failed SHA-256 verification.'}
    }
    New-Item -ItemType Directory -Force $frdDestination,(Join-Path $frdDestination 'ffmpeg') | Out-Null
    foreach($name in $frdFiles.Keys){
        Install-VerifiedPayload (Join-Path $frdSource $name) (Join-Path $frdDestination $name) $frdFiles[$name] ('FRD '+$name)
    }
    if(-not (Test-FrdDirectory $frdDestination)){throw 'FRD installed files failed SHA-256 verification.'}
    Write-Host "FRD $frdVersion 已安装到 $frdDestination（内含运行时，无需另装 .NET 10）。"
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
    Install-FrdRuntime
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
    $identityOutput=Join-Path $temporary 'identity.stdout';$identityError=Join-Path $temporary 'identity.stderr'
    $identityProcess=Start-Process -FilePath $installed -ArgumentList @('--identity-store',('"{0}"' -f $data)) -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $identityOutput -RedirectStandardError $identityError
    if($identityProcess.ExitCode -ne 0){throw ('身份初始化失败；已有身份未被覆盖。'+(Get-Content -LiteralPath $identityError -Raw))}
    if(-not (Test-Path -LiteralPath $current -PathType Leaf)){throw '身份初始化没有生成预期文件，安装已停止。'}
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
finally{
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(-not [IO.Path]::GetFullPath($temporary).StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录路径无效，拒绝清理。'}
    if(Test-Path -LiteralPath $temporary){
        if((Get-Item -LiteralPath $temporary).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '临时目录是链接，拒绝递归清理。'}
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
