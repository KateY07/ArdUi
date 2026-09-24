param(
    [Parameter(Mandatory=$true)][string]$PreviousBundle,
    [Parameter(Mandatory=$true)][string]$Bundle,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$FrdVersion,
    [Parameter(Mandatory=$true)][string]$FrdExeSha256
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$caseRoot=Join-Path $repository ('dist\offline-upgrade-'+$Version+'-'+[guid]::NewGuid().ToString('N'))
$installRoot=Join-Path $caseRoot 'install with spaces'
$saved=@{}
foreach($name in @('ARDUI_INSTALL_ROOT','ARDUI_INSTALL_NO_START','ARDUI_INSTALL_NO_SHORTCUT','ARDUI_DATA_ROOT')){
    $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
}
$global:ArdUiOfflineNetworkAttempts=0
function global:Invoke-WebRequest {
    param([string]$Uri,[string]$OutFile)
    $global:ArdUiOfflineNetworkAttempts++
    throw "Offline installation attempted a network request: $Uri"
}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Check([bool]$condition,[string]$message){if(-not $condition){throw $message};Write-Output ('PASS: '+$message)}
try {
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $previous=Join-Path $caseRoot 'previous';$current=Join-Path $caseRoot 'current'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($PreviousBundle),$previous)
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Bundle),$current)
    $env:ARDUI_INSTALL_ROOT=$installRoot;$env:ARDUI_INSTALL_NO_START='1';$env:ARDUI_INSTALL_NO_SHORTCUT='1'
    & (Join-Path $previous 'install.ps1')
    $data=Join-Path $installRoot 'data'
    [IO.File]::WriteAllText((Join-Path $data 'access.json'),'{"enabled":false,"code":"ABC123","controllers":{}}')
    [IO.File]::WriteAllText((Join-Path $data 'peers.json'),'[{"id":"fixture-preserved","code":"DEF456","name":"saved device","autoConnect":false}]')
    $configPath=Join-Path $data 'config.json'
    $config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $oldFrdPath=[IO.File]::ReadAllText((Join-Path $installRoot 'frd-current.path')).Trim()
    $config | Add-Member -NotePropertyName frdPath -NotePropertyValue $oldFrdPath -Force
    [IO.File]::WriteAllText($configPath,($config|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    $snapshots=@{}
    foreach($file in Get-ChildItem -LiteralPath $data -File -Recurse){$snapshots[$file.FullName]=Hash $file.FullName}
    Check ($snapshots.Count -ge 4) 'existing identity, configuration, device code and authorizations seeded'
    & (Join-Path $current 'install.ps1')
    foreach($path in $snapshots.Keys){Check ((Hash $path) -ceq $snapshots[$path]) ('upgrade preserves '+[IO.Path]::GetFileName($path))}
    $frd=Join-Path $installRoot ('frd\'+$FrdVersion+'\FRD.exe')
    Check ((Hash $frd) -ceq $FrdExeSha256) 'installed FRD executable matches the delivered binary'
    Check ([IO.File]::ReadAllText((Join-Path $installRoot 'frd-current.path')).Trim() -ceq $frd) 'managed FRD pointer selects the new runtime'
    Check ([IO.File]::ReadAllText((Join-Path $installRoot 'ArdUi.cmd')).Contains(('versions\'+$Version+'\ArdUi.exe'))) 'launcher selects the new ArdUi version'
    $time=(Get-Item -LiteralPath $frd).LastWriteTimeUtc
    & (Join-Path $current 'install.ps1')
    foreach($path in $snapshots.Keys){Check ((Hash $path) -ceq $snapshots[$path]) ('repeat installation preserves '+[IO.Path]::GetFileName($path))}
    Check ((Get-Item -LiteralPath $frd).LastWriteTimeUtc -eq $time) 'repeat installation reuses the verified FRD runtime'
    Check ($global:ArdUiOfflineNetworkAttempts -eq 0) 'all installations completed with zero network requests'
    [IO.File]::WriteAllText((Join-Path $caseRoot 'result.json'),(@{
        passed=$true;version=$Version;frdVersion=$FrdVersion;frdExeSha256=$FrdExeSha256
        preservedFiles=$snapshots.Count;networkRequests=$global:ArdUiOfflineNetworkAttempts
    }|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Write-Output ('Evidence: '+$caseRoot)
}
finally {
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
    Remove-Item -LiteralPath Function:\Invoke-WebRequest
    Remove-Variable -Name ArdUiOfflineNetworkAttempts -Scope Global
}
