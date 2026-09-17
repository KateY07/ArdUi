param([string]$Payload=(Join-Path $PSScriptRoot '../dist/final-v2.pre1/ArdUi.exe'))
$ErrorActionPreference='Stop'
$repoPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$caseRoot=Join-Path $repoPath ('dist/install-v2-'+[guid]::NewGuid().ToString('N'))
$cachePath=Join-Path $caseRoot 'versions/cache'
New-Item -ItemType Directory -Force $cachePath | Out-Null
Copy-Item -LiteralPath $Payload -Destination (Join-Path $cachePath 'ArdUi.exe')
Copy-Item -LiteralPath (Join-Path $repoPath 'tools/ard.exe') -Destination (Join-Path $cachePath 'ard.exe')
$savedRoot=$env:ARDUI_INSTALL_ROOT;$savedStart=$env:ARDUI_INSTALL_NO_START;$savedShortcut=$env:ARDUI_INSTALL_NO_SHORTCUT;$savedData=$env:ARDUI_DATA_ROOT
$downloadedUris=[Collections.Generic.List[string]]::new()
function Invoke-WebRequest([string]$Uri,[string]$OutFile){
    $downloadedUris.Add($Uri)
    if($Uri -ne 'https://f.visnova.cn/ardui/ArdUi-v2.pre1.exe'){throw "Unexpected payload request: $Uri"}
    Copy-Item -LiteralPath $Payload -Destination $OutFile
}
try{
    $env:ARDUI_INSTALL_ROOT=$caseRoot;$env:ARDUI_INSTALL_NO_START='1';$env:ARDUI_INSTALL_NO_SHORTCUT='1'
    & (Join-Path $repoPath 'install.ps1')
    if($downloadedUris.Count -ne 0){throw 'Verified local cache triggered a download.'}
    $identityPath=Join-Path $caseRoot 'data/device/identity'
    $identityHash=(Get-FileHash -LiteralPath $identityPath).Hash
    $accessPath=Join-Path $caseRoot 'data/access.json'
    $peersPath=Join-Path $caseRoot 'data/peers.json'
    [IO.File]::WriteAllText($accessPath,'{"enabled":false,"code":"ABC123","controllers":[]}')
    [IO.File]::WriteAllText($peersPath,'[{"id":"fixture-preserved","code":"DEF456"}]')
    $accessHash=(Get-FileHash -LiteralPath $accessPath).Hash;$peersHash=(Get-FileHash -LiteralPath $peersPath).Hash
    & (Join-Path $repoPath 'install.ps1')
    if($downloadedUris.Count -ne 0){throw 'Reinstall downloaded verified payloads.'}
    if((Get-FileHash -LiteralPath $identityPath).Hash -ne $identityHash -or (Get-FileHash -LiteralPath $accessPath).Hash -ne $accessHash -or (Get-FileHash -LiteralPath $peersPath).Hash -ne $peersHash){throw 'Reinstall changed user data.'}
    Write-Host 'PASS: hash-verified cache and reinstall avoid downloads; identity, code, disabled state and peer file preserved.'
    [IO.File]::WriteAllBytes((Join-Path $cachePath 'ArdUi.exe'),[byte[]](1,2,3))
    [IO.File]::WriteAllBytes((Join-Path $caseRoot 'versions/v2.pre1/ArdUi.exe'),[byte[]](1,2,3))
    & (Join-Path $repoPath 'install.ps1')
    if($downloadedUris.Count -ne 1){throw 'Repair did not fetch exactly the changed UI payload.'}
    if((Get-FileHash -LiteralPath $identityPath).Hash -ne $identityHash){throw 'Repair changed identity.'}
    Write-Host 'PASS: corrupt UI cache triggers one verified UI fetch; unchanged ARD is reused.'
    Write-Host "Installer evidence: $caseRoot"
}
finally{
    $env:ARDUI_INSTALL_ROOT=$savedRoot;$env:ARDUI_INSTALL_NO_START=$savedStart;$env:ARDUI_INSTALL_NO_SHORTCUT=$savedShortcut;$env:ARDUI_DATA_ROOT=$savedData
}
