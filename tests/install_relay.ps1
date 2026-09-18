param([string]$Repository=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop'){throw 'Run this regression with Windows PowerShell 5.1.'}
$repo=[IO.Path]::GetFullPath($Repository)
$installer=Join-Path $repo 'install-relay.ps1'
$bundleSource=Join-Path $repo 'dist\ArdTransit-v2.pre1-relay1-win-x64.zip'
$ardSource=Join-Path $repo 'tools\ard.exe'
foreach($path in @($installer,$bundleSource,$ardSource)){if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Build artifact missing: $path"}}
$installerText=Get-Content -LiteralPath $installer -Raw -Encoding UTF8
function Invoke-TestInstaller {& ([scriptblock]::Create($installerText))}
function Read-ReleaseValue([string]$name){
    $match=[regex]::Match($installerText,('\$'+[regex]::Escape($name)+"='([^']+)'"))
    if(-not $match.Success){throw "Installer value missing: $name"}
    return $match.Groups[1].Value
}
$version=Read-ReleaseValue 'relayVersion'
$bundleHash=Read-ReleaseValue 'bundleSha256'
$relayHash=Read-ReleaseValue 'relaySha256'
$ardHash=Read-ReleaseValue 'ardSha256'
foreach($hash in @($bundleHash,$relayHash,$ardHash)){if($hash -notmatch '^[0-9a-f]{64}$'){throw 'Build must populate release hashes before this regression.'}}
function Assert-RelayTest([bool]$condition,[string]$message){if(-not $condition){throw "FAIL: $message"}}
function File-Hash([string]$path){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
Assert-RelayTest ((File-Hash $bundleSource) -eq $bundleHash) 'Bundle does not match installer hash'
Assert-RelayTest ((File-Hash $ardSource) -eq $ardHash) 'ARD fixture does not match installer hash'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('ArdTransit installer regression '+[guid]::NewGuid().ToString('N'))
$installRoot=Join-Path $testRoot 'relay install with spaces'
$cacheRoot=Join-Path $installRoot 'cache'
$environmentNames=@('ARDTRANSIT_INSTALL_ROOT','ARDTRANSIT_INSTALL_NO_AUTOSTART','ARDTRANSIT_INSTALL_NO_START','ARDUI_INSTALL_ROOT')
$savedEnvironment=@{}
foreach($name in $environmentNames){$savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$global:ArdRelayInstallerTestDownloads=0
$global:ArdRelayInstallerTestMode='Deny'
$global:ArdRelayInstallerTestBundle=$bundleSource
$global:ArdRelayInstallerTestArd=$ardSource
function global:Invoke-WebRequest {
    param([string]$Uri,[string]$OutFile,[switch]$UseBasicParsing,[int]$TimeoutSec)
    Assert-RelayTest ($TimeoutSec -eq 600 -and $ProgressPreference -eq 'SilentlyContinue') 'Bounded download timeout or quiet progress missing'
    $global:ArdRelayInstallerTestDownloads++
    if($global:ArdRelayInstallerTestMode -eq 'Deny'){throw 'Unexpected network request; all installer network access is blocked by the regression.'}
    if($global:ArdRelayInstallerTestMode -eq 'BadHash'){
        [IO.File]::WriteAllText($OutFile,'intentionally invalid downloaded payload')
        return
    }
    $name=[IO.Path]::GetFileName(([uri]$Uri).AbsolutePath)
    if($name -eq [IO.Path]::GetFileName($global:ArdRelayInstallerTestBundle)){
        Copy-Item -LiteralPath $global:ArdRelayInstallerTestBundle -Destination $OutFile -Force
    }
    elseif($name -eq 'ard-v2.0.0-pre.6.exe'){
        Copy-Item -LiteralPath $global:ArdRelayInstallerTestArd -Destination $OutFile -Force
    }
    else{throw "Unexpected fixture download: $Uri"}
}
try{
    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
    $env:ARDTRANSIT_INSTALL_ROOT=$installRoot
    $env:ARDTRANSIT_INSTALL_NO_AUTOSTART='1'
    $env:ARDTRANSIT_INSTALL_NO_START='1'
    $env:ARDUI_INSTALL_ROOT=Join-Path $testRoot 'isolated nonexistent ArdUi'
    $cachedBundle=Join-Path $cacheRoot ([IO.Path]::GetFileName($bundleSource))
    $cachedArd=Join-Path $cacheRoot 'ard-v2.0.0-pre.6.exe'
    Copy-Item -LiteralPath $bundleSource -Destination $cachedBundle
    Copy-Item -LiteralPath $ardSource -Destination $cachedArd
    Invoke-TestInstaller
    Assert-RelayTest ($global:ArdRelayInstallerTestDownloads -eq 0) 'Verified cache caused a download'
    $versionRoot=Join-Path (Join-Path $installRoot 'versions') $version
    $installedRelay=Join-Path $versionRoot 'ArdTransit.exe'
    $installedArd=Join-Path $versionRoot 'ard.exe'
    Assert-RelayTest ((File-Hash $installedRelay) -eq $relayHash) 'Installed relay hash mismatch'
    Assert-RelayTest ((File-Hash $installedArd) -eq $ardHash) 'Installed ARD hash mismatch'
    $configPath=Join-Path $installRoot 'config.json'
    $config=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-RelayTest ($config.capacity -eq 2 -and $config.mbps -eq 10) 'Unexpected default capacity or bandwidth'
    foreach($name in @('Start-ArdTransit.ps1','Stop-ArdTransit.ps1','Status-ArdTransit.ps1')){
        Assert-RelayTest (Test-Path -LiteralPath (Join-Path $installRoot $name) -PathType Leaf) "Missing management script $name"
    }
    Write-Host 'PASS: first install into a path containing spaces, verified cache reused, default limits and payload hashes correct.'

    $identityPath=Join-Path $installRoot 'data\identity'
    $identity=New-Object byte[] 32
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$rng.GetBytes($identity)}finally{$rng.Dispose()}
    [IO.File]::WriteAllBytes($identityPath,$identity)
    $config.capacity=3;$config.mbps=12
    $config | Add-Member -NotePropertyName regressionNote -NotePropertyValue 'custom settings must survive reinstall'
    [IO.File]::WriteAllText($configPath,($config|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $identityHash=File-Hash $identityPath;$configHash=File-Hash $configPath
    $relayTime=(Get-Item -LiteralPath $installedRelay).LastWriteTimeUtc
    Invoke-TestInstaller
    Assert-RelayTest ($global:ArdRelayInstallerTestDownloads -eq 0) 'Repeated install caused a download'
    Assert-RelayTest ((File-Hash $identityPath) -eq $identityHash) 'Repeated install replaced permanent identity'
    Assert-RelayTest ((File-Hash $configPath) -eq $configHash) 'Repeated install changed custom configuration'
    Assert-RelayTest ((Get-Item -LiteralPath $installedRelay).LastWriteTimeUtc -eq $relayTime) 'Unchanged executable was unnecessarily overwritten'
    Write-Host 'PASS: repeated install preserves identity/configuration and does not download or overwrite the verified executable.'

    [IO.File]::WriteAllText($cachedBundle,'broken ZIP cache')
    $global:ArdRelayInstallerTestMode='Valid';$global:ArdRelayInstallerTestDownloads=0
    Invoke-TestInstaller
    Assert-RelayTest ($global:ArdRelayInstallerTestDownloads -eq 1) 'Damaged bundle cache did not cause exactly one replacement download'
    Assert-RelayTest ((File-Hash $cachedBundle) -eq $bundleHash) 'Downloaded replacement cache not verified'
    Assert-RelayTest ((File-Hash $identityPath) -eq $identityHash -and (File-Hash $configPath) -eq $configHash) 'Cache repair changed user state'
    Write-Host 'PASS: corrupt bundle cache is replaced with one verified fixture download.'

    [IO.File]::WriteAllText($cachedArd,'broken ARD cache')
    $global:ArdRelayInstallerTestMode='Deny';$global:ArdRelayInstallerTestDownloads=0
    Invoke-TestInstaller
    Assert-RelayTest ($global:ArdRelayInstallerTestDownloads -eq 0) 'Existing verified ARD was not reused to repair its cache'
    Assert-RelayTest ((File-Hash $cachedArd) -eq $ardHash) 'ARD cache repair failed'
    Write-Host 'PASS: corrupt ARD cache is repaired from the verified installed dependency without network access.'

    [IO.File]::WriteAllText($cachedBundle,'broken ZIP cache again')
    $global:ArdRelayInstallerTestMode='BadHash';$global:ArdRelayInstallerTestDownloads=0
    $rejected=$false
    try{Invoke-TestInstaller}
    catch{
        if($_.Exception.Message -notmatch 'SHA-256 mismatch'){throw}
        $rejected=$true
    }
    Assert-RelayTest $rejected 'Installer accepted an incorrect downloaded hash'
    Assert-RelayTest ($global:ArdRelayInstallerTestDownloads -eq 3) 'Wrong hash retry count changed unexpectedly'
    Assert-RelayTest ((File-Hash $installedRelay) -eq $relayHash -and (File-Hash $installedArd) -eq $ardHash) 'Rejected update damaged installed binaries'
    Assert-RelayTest ((File-Hash $identityPath) -eq $identityHash -and (File-Hash $configPath) -eq $configHash) 'Rejected update damaged user state'
    Assert-RelayTest (@(Get-ChildItem -LiteralPath $installRoot -Directory -Filter 'staging-*').Count -eq 0) 'Failed update left staging files behind'
    Write-Host 'PASS: wrong downloaded hash is rejected after bounded retries; existing binaries and user state survive.'

    & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $installRoot 'Stop-ArdTransit.ps1')))) -InstallRoot $installRoot -KeepAutoStart
    Assert-RelayTest (Test-Path -LiteralPath (Join-Path $installRoot 'data\stop.request') -PathType Leaf) 'Stop request not written'
    Assert-RelayTest ((File-Hash $identityPath) -eq $identityHash -and (File-Hash $configPath) -eq $configHash) 'Stopping changed user state'
    & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $installRoot 'Status-ArdTransit.ps1')))) -InstallRoot $installRoot
    Write-Host 'PASS: stopping an inactive isolated installation succeeds without changing identity, config or login startup.'
    Write-Host 'PASS: Windows PowerShell 5.1 installer regression completed; no public network, real installation or HKCU changes.'
}
finally{
    foreach($name in $environmentNames){[Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process')}
    Remove-Item -LiteralPath Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    foreach($name in @('ArdRelayInstallerTestDownloads','ArdRelayInstallerTestMode','ArdRelayInstallerTestBundle','ArdRelayInstallerTestArd')){
        Remove-Variable -Name $name -Scope Global -ErrorAction SilentlyContinue
    }
    if(Test-Path -LiteralPath $testRoot){
        $tempPrefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
        $resolved=[IO.Path]::GetFullPath($testRoot)
        if(-not $resolved.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notlike 'ArdTransit installer regression *' -or
            (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe test cleanup target.'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
