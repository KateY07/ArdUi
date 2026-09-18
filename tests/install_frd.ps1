param([string]$Repository=(Split-Path -Parent $PSScriptRoot),[string]$Payload='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath($Repository)
$installerText=Get-Content -LiteralPath (Join-Path $repo 'install.ps1') -Raw -Encoding UTF8
function Release-Value([string]$name){
    $value=[regex]::Match($installerText,('\$'+[regex]::Escape($name)+"='([^']+)'"))
    if(-not $value.Success){throw "Missing installer value: $name"}
    return $value.Groups[1].Value
}
function Hash([string]$path){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Check([bool]$passed,[string]$message){if(-not $passed){throw "FAIL: $message"}}
function Install-Test {& ([scriptblock]::Create($installerText))}
$uiHash=Release-Value 'expectedSha256'
$uiVersion=Release-Value 'version'
$frdVersion=Release-Value 'frdVersion'
$archiveName=Release-Value 'frdArchiveName'
$archiveHash=Release-Value 'frdArchiveSha256'
$archiveSource=Join-Path $repo ('dist\'+$archiveName)
Check ((Hash $archiveSource) -eq $archiveHash) 'FRD artifact does not match installer manifest'
if(-not $Payload){
    foreach($candidate in @((Join-Path $repo ('dist\final-'+$uiVersion+'\ArdUi.exe')),(Join-Path $repo 'dist\final-v2.pre2\ArdUi.exe'),(Join-Path $repo 'dist\final-v2.pre1\ArdUi.exe'))){
        if((Test-Path -LiteralPath $candidate -PathType Leaf) -and (Hash $candidate) -eq $uiHash){$Payload=$candidate;break}
    }
}
Check ([bool]$Payload -and (Hash $Payload) -eq $uiHash) 'A built UI payload matching the installer hash is required'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi FRD installer '+[guid]::NewGuid().ToString('N'))
$installRoot=Join-Path $testRoot 'main install with spaces'
$saved=@{}
foreach($name in @('ARDUI_INSTALL_ROOT','ARDUI_INSTALL_NO_START','ARDUI_INSTALL_NO_SHORTCUT','ARDUI_DATA_ROOT','ARDUI_FRD_SOURCE')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$global:FrdInstallerTestDownloads=0
$global:FrdInstallerTestMode='Deny'
$global:FrdInstallerTestArchive=$archiveSource
function global:Invoke-WebRequest {
    param([string]$Uri,[string]$OutFile)
    $global:FrdInstallerTestDownloads++
    if($global:FrdInstallerTestMode -eq 'Deny'){throw "Unexpected network request: $Uri"}
    if($global:FrdInstallerTestMode -eq 'BadHash'){[IO.File]::WriteAllText($OutFile,'bad download');return}
    if([IO.Path]::GetFileName(([uri]$Uri).AbsolutePath) -ne [IO.Path]::GetFileName($global:FrdInstallerTestArchive)){throw "Unexpected fixture download: $Uri"}
    Copy-Item -LiteralPath $global:FrdInstallerTestArchive -Destination $OutFile -Force
}
function Seed-Ui([string]$path){
    $cache=Join-Path $path 'versions\fixture'
    New-Item -ItemType Directory -Force $cache | Out-Null
    Copy-Item -LiteralPath $Payload -Destination (Join-Path $cache 'ArdUi.exe')
    Copy-Item -LiteralPath (Join-Path $repo 'tools\ard.exe') -Destination (Join-Path $cache 'ard.exe')
}
function Check-Frd([string]$path){
    $manifestText=[regex]::Match($installerText,'(?s)\$frdFiles=@\{(.*?)\n\}').Groups[1].Value
    $entries=[regex]::Matches($manifestText,"'([^']+)'='([0-9a-f]{64})'")
    Check ($entries.Count -ge 4) 'FRD manifest was empty'
    foreach($entry in $entries){Check ((Hash (Join-Path $path $entry.Groups[1].Value)) -eq $entry.Groups[2].Value) ('FRD dependency mismatch: '+$entry.Groups[1].Value)}
}
try{
    Seed-Ui $installRoot
    $cacheRoot=Join-Path $installRoot 'cache';New-Item -ItemType Directory -Path $cacheRoot | Out-Null
    $cachedArchive=Join-Path $cacheRoot $archiveName
    Copy-Item -LiteralPath $archiveSource -Destination $cachedArchive
    $env:ARDUI_INSTALL_ROOT=$installRoot;$env:ARDUI_INSTALL_NO_START='1';$env:ARDUI_INSTALL_NO_SHORTCUT='1'
    $env:ARDUI_FRD_SOURCE=Join-Path $testRoot 'absent external runtime'
    Install-Test
    Check ($global:FrdInstallerTestDownloads -eq 0) 'Valid cached payloads triggered network'
    $frdRoot=Join-Path $installRoot ('frd\'+$frdVersion)
    Check-Frd $frdRoot
    $identity=Join-Path $installRoot 'data\device\identity'
    Check ((Get-Item -LiteralPath $identity).Length -eq 32) 'Isolated identity initialization failed'
    Write-Host 'PASS: complete FRD runtime installed from verified cache under an ArdUi path with spaces; no downloads.'

    $access=Join-Path $installRoot 'data\access.json';$peers=Join-Path $installRoot 'data\peers.json'
    [IO.File]::WriteAllText($access,'{"enabled":false,"code":"ABC123","controllers":[]}')
    [IO.File]::WriteAllText($peers,'[{"id":"frd-fixture-preserved","code":"DEF456"}]')
    $config=Join-Path $installRoot 'data\config.json'
    $snapshots=@{};foreach($path in @($identity,$access,$peers,$config)){$snapshots[$path]=Hash $path}
    $frdTime=(Get-Item -LiteralPath (Join-Path $frdRoot 'FRD.exe')).LastWriteTimeUtc
    Install-Test
    Check ($global:FrdInstallerTestDownloads -eq 0) 'Verified reinstallation triggered network'
    Check ((Get-Item -LiteralPath (Join-Path $frdRoot 'FRD.exe')).LastWriteTimeUtc -eq $frdTime) 'Verified FRD executable was needlessly replaced'
    foreach($path in $snapshots.Keys){Check ((Hash $path) -eq $snapshots[$path]) 'Reinstallation changed ArdUi identity or settings'}
    Write-Host 'PASS: repeat install retains ArdUi identity, authorized peers, controlled-state and settings without redownload.'

    Remove-Item -LiteralPath (Join-Path $frdRoot 'ffmpeg\swresample-6.dll') -Force
    [IO.File]::WriteAllText((Join-Path $frdRoot 'ffmpeg\avutil-60.dll'),'corrupt dependency')
    Install-Test
    Check-Frd $frdRoot
    Check ($global:FrdInstallerTestDownloads -eq 0) 'Dependency repair ignored the valid cache'
    foreach($path in $snapshots.Keys){Check ((Hash $path) -eq $snapshots[$path]) 'Dependency repair changed user data'}
    Write-Host 'PASS: both missing and corrupt FFmpeg dependencies repaired from cache; identity unchanged.'

    $legacyRoot=Join-Path $testRoot 'existing runtime reuse'
    Seed-Ui $legacyRoot
    $env:ARDUI_INSTALL_ROOT=$legacyRoot;$env:ARDUI_FRD_SOURCE=$frdRoot
    Install-Test
    Check-Frd (Join-Path $legacyRoot ('frd\'+$frdVersion))
    Check ($global:FrdInstallerTestDownloads -eq 0) 'A complete verified existing FRD directory triggered download'
    Write-Host 'PASS: an existing complete FRD directory is reused only after every published file hash matches.'
    $env:ARDUI_INSTALL_ROOT=$installRoot;$env:ARDUI_FRD_SOURCE=Join-Path $testRoot 'absent external runtime'

    [IO.File]::WriteAllText($cachedArchive,'corrupt archive cache')
    Remove-Item -LiteralPath (Join-Path $frdRoot 'ffmpeg\swresample-6.dll') -Force
    $global:FrdInstallerTestMode='Valid'
    Install-Test
    Check ($global:FrdInstallerTestDownloads -eq 1) 'Bad archive was not replaced with one verified download'
    Check-Frd $frdRoot
    Write-Host 'PASS: a damaged archive cache is downloaded once, validated, and used to repair runtime files.'

    [IO.File]::WriteAllText($cachedArchive,'corrupt archive cache again')
    Remove-Item -LiteralPath (Join-Path $frdRoot 'ffmpeg\swresample-6.dll') -Force
    $global:FrdInstallerTestMode='BadHash';$global:FrdInstallerTestDownloads=0
    $rejected=$false
    try{Install-Test}catch{if($_.Exception.Message -notmatch 'SHA-256'){throw};$rejected=$true}
    Check ($rejected -and $global:FrdInstallerTestDownloads -eq 3) 'Invalid downloaded hash was not rejected after bounded retries'
    foreach($path in $snapshots.Keys){Check ((Hash $path) -eq $snapshots[$path]) 'Failed repair changed identity or settings'}
    Write-Host 'PASS: wrong downloaded hash rejected; original ArdUi identity and configuration preserved.'
    Write-Host 'PASS: FRD installer regression completed without launching FRD or changing real user data/startup.'
}
finally{
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
    Remove-Item -LiteralPath Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    foreach($name in @('FrdInstallerTestDownloads','FrdInstallerTestMode','FrdInstallerTestArchive')){Remove-Variable -Name $name -Scope Global -ErrorAction SilentlyContinue}
    if(Test-Path -LiteralPath $testRoot){
        $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
        $resolved=[IO.Path]::GetFullPath($testRoot)
        if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'ArdUi FRD installer *' -or
            (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe test cleanup target.'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
