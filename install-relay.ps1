$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$relayVersion='v2.pre1-relay1'
$bundleSha256='149c975f92e0ded715b619613fb052821968321e0cca5a48b8fa661b14162f8b'
$relaySha256='4e9db896c80d37e07a11a675ed93fd84338c5ab373a53ecaeb36a30566ef25f3'
$ardSha256='04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d'
$base='https://f.visnova.cn/ardui'
$relayRoot=if($env:ARDTRANSIT_INSTALL_ROOT){[IO.Path]::GetFullPath($env:ARDTRANSIT_INSTALL_ROOT)}else{Join-Path $env:LOCALAPPDATA 'ArdTransit'}
$relayData=Join-Path $relayRoot 'data'
$cache=Join-Path $relayRoot 'cache'
$destination=Join-Path (Join-Path $relayRoot 'versions') $relayVersion
$stage=Join-Path $relayRoot ('staging-'+[guid]::NewGuid().ToString('N'))
$installLock=$null
function Test-RelayFile([string]$path,[string]$sha){
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){return $false}
    try{return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sha}
    catch{Write-Warning "Cannot verify $path : $($_.Exception.Message)";return $false}
}
function Protect-RelayDirectory([string]$path){
    if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Installation directories cannot be links.'}
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @($user,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    if($PSVersionTable.PSEdition -eq 'Core'){[IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($path),$acl)}
    else{[IO.Directory]::SetAccessControl($path,$acl)}
}
function Get-RelayPayload([string]$url,[string]$path,[string]$sha){
    if(Test-RelayFile $path $sha){Write-Host "Reusing verified cache: $([IO.Path]::GetFileName($path))";return}
    $ProgressPreference='SilentlyContinue'
    Write-Host "Downloading: $([IO.Path]::GetFileName($path))"
    $partial=Join-Path $stage ([guid]::NewGuid().ToString('N')+'.download')
    for($attempt=1;$attempt -le 3;$attempt++){
        try{
            Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -TimeoutSec 600
            if(-not (Test-RelayFile $partial $sha)){throw 'Downloaded payload SHA-256 mismatch.'}
            Move-Item -LiteralPath $partial -Destination $path -Force
            return
        }
        catch{Write-Warning "Download attempt $attempt failed: $($_.Exception.Message)";if($attempt -eq 3){throw};Start-Sleep -Seconds $attempt}
    }
}
try{
    if($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64'){throw 'This installer requires Windows x64.'}
    if([Environment]::OSVersion.Version.Build -lt 17763){throw 'Windows 10 1809 / Server 2019 or newer is required.'}
    if($bundleSha256 -notmatch '^[0-9a-f]{64}$' -or $relaySha256 -notmatch '^[0-9a-f]{64}$'){throw 'Installer release hashes are missing.'}
    New-Item -ItemType Directory -Force $relayRoot | Out-Null
    Protect-RelayDirectory $relayRoot
    $installLock=[IO.File]::Open((Join-Path $relayRoot 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $versions=Join-Path $relayRoot 'versions'
    foreach($path in @($relayData,$cache,$stage,$versions,$destination)){
        New-Item -ItemType Directory -Force $path | Out-Null
        if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Installation directories cannot be links.'}
    }
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $bundle=Join-Path $cache "ArdTransit-$relayVersion-win-x64.zip"
    Get-RelayPayload "$base/ArdTransit-$relayVersion-win-x64.zip" $bundle $bundleSha256
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($bundle)
    try{
        foreach($entry in $zip.Entries){if($entry.FullName -notmatch '^[A-Za-z0-9_.-]+$'){throw 'Unexpected archive path.'}}
    }finally{$zip.Dispose()}
    [IO.Compression.ZipFile]::ExtractToDirectory($bundle,$stage)
    $relayBinary=Join-Path $stage 'ArdTransit.exe'
    if(-not (Test-RelayFile $relayBinary $relaySha256)){throw 'Unpacked ArdTransit hash mismatch.'}
    $ardCache=Join-Path $cache 'ard-v2.0.0-pre.6.exe'
    if(-not (Test-RelayFile $ardCache $ardSha256)){
        $uiRoot=if($env:ARDUI_INSTALL_ROOT){$env:ARDUI_INSTALL_ROOT}else{Join-Path $env:LOCALAPPDATA 'ArdUi'}
        foreach($searchRoot in @((Join-Path $relayRoot 'versions'),(Join-Path $uiRoot 'versions'))){
            if(-not (Test-Path -LiteralPath $searchRoot -PathType Container)){continue}
            foreach($candidate in @(Get-ChildItem -LiteralPath $searchRoot -Filter ard.exe -File -Recurse)){
                if(Test-RelayFile $candidate.FullName $ardSha256){Copy-Item -LiteralPath $candidate.FullName -Destination $ardCache -Force;break}
            }
            if(Test-RelayFile $ardCache $ardSha256){break}
        }
    }
    Get-RelayPayload "$base/ard-v2.0.0-pre.6.exe" $ardCache $ardSha256
    $needsUpdate=-not (Test-RelayFile (Join-Path $destination 'ArdTransit.exe') $relaySha256) -or -not (Test-RelayFile (Join-Path $destination 'ard.exe') $ardSha256)
    $currentPath=Join-Path $relayRoot 'current.json'
    if(Test-Path -LiteralPath $currentPath){$old=Get-Content -LiteralPath $currentPath -Raw -Encoding UTF8 | ConvertFrom-Json;$needsUpdate=$needsUpdate -or $old.version -ne $relayVersion}
    if($needsUpdate){& ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $stage 'Stop-ArdTransit.ps1')))) -InstallRoot $relayRoot -KeepAutoStart}
    foreach($file in @(Get-ChildItem -LiteralPath $stage -File)){
        $target=Join-Path $destination $file.Name
        $sha=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if(-not (Test-RelayFile $target $sha)){Copy-Item -LiteralPath $file.FullName -Destination $target -Force}
    }
    if(-not (Test-RelayFile (Join-Path $destination 'ard.exe') $ardSha256)){Copy-Item -LiteralPath $ardCache -Destination (Join-Path $destination 'ard.exe') -Force}
    foreach($name in @('Start-ArdTransit.ps1','Stop-ArdTransit.ps1','Status-ArdTransit.ps1')){Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $relayRoot $name) -Force}
    $configPath=Join-Path $relayRoot 'config.json'
    if(-not (Test-Path -LiteralPath $configPath)){
        [IO.File]::WriteAllText($configPath,(@{server='https://f.visnova.cn';relay='http://175.27.160.144:8080';relayKey='spki:3059301306072a8648ce3d020106082a8648ce3d0301070342000462f8877cf66d813f17028e3d1cf44443c481586a04219326d752623dd72ce3b005a7c3a8ea3db565b75f4e7a72209d17f29d30cbfaea2be0c48384672bb2f01f';capacity=2;mbps=10}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText(($currentPath+'.tmp'),(@{version=$relayVersion;sha256=$relaySha256;ardSha256=$ardSha256}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($currentPath+'.tmp') -Destination $currentPath -Force
    if($env:ARDTRANSIT_INSTALL_NO_AUTOSTART -ne '1'){
        $runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        New-Item -Path $runKey -Force | Out-Null
        $powerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $launch='"'+$powerShell+'" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+(Join-Path $relayRoot 'Start-ArdTransit.ps1')+'"'
        New-ItemProperty -LiteralPath $runKey -Name ArdTransit -Value $launch -PropertyType String -Force | Out-Null
    }
    Write-Host "ArdTransit $relayVersion installed: $relayRoot"
    if($env:ARDTRANSIT_INSTALL_NO_START -ne '1'){& ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $relayRoot 'Start-ArdTransit.ps1')))) -InstallRoot $relayRoot}
    Write-Host "Status: powershell -NoProfile -ExecutionPolicy Bypass -File `"$relayRoot\Status-ArdTransit.ps1`""
    Write-Host "Stop and disable login startup: powershell -NoProfile -ExecutionPolicy Bypass -File `"$relayRoot\Stop-ArdTransit.ps1`""
}
finally{
    if($installLock){$installLock.Dispose()}
    if(Test-Path -LiteralPath $stage){
        $prefix=[IO.Path]::GetFullPath($relayRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
        if(-not [IO.Path]::GetFullPath($stage).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe staging cleanup path.'}
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
