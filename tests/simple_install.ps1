param([Parameter(Mandatory)][string]$Bundle)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

$bundle=[IO.Path]::GetFullPath($Bundle)
if(-not (Test-Path -LiteralPath $bundle -PathType Leaf)){throw "Bundle not found: $bundle"}
$root=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi-simple-install-'+[guid]::NewGuid().ToString('N'))
$installRoot=Join-Path $root 'install'
function Check([bool]$condition,[string]$message){if(-not $condition){throw "FAIL: $message"};Write-Host "PASS: $message"}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Invoke-Installer {
    $savedRoot=$env:ARDUI_INSTALL_ROOT;$savedStart=$env:ARDUI_INSTALL_NO_START;$savedShortcut=$env:ARDUI_INSTALL_NO_SHORTCUT
    $env:ARDUI_INSTALL_ROOT=$installRoot;$env:ARDUI_INSTALL_NO_START='1';$env:ARDUI_INSTALL_NO_SHORTCUT='1'
    try{
        $process=Start-Process -FilePath $bundle -Wait -PassThru
        if($process.ExitCode -ne 0){throw "Setup exited with code $($process.ExitCode)."}
    }
    finally{
        $env:ARDUI_INSTALL_ROOT=$savedRoot;$env:ARDUI_INSTALL_NO_START=$savedStart;$env:ARDUI_INSTALL_NO_SHORTCUT=$savedShortcut
    }
}
try{
    Invoke-Installer
    $versions=@(Get-ChildItem -LiteralPath (Join-Path $installRoot 'versions') -Directory)
    Check ($versions.Count -eq 1) 'installer creates exactly one version directory'
    $version=$versions[0].Name
    Check (Test-Path -LiteralPath (Join-Path $versions[0].FullName 'ArdUi.exe')) 'first install copies ArdUi into its version directory'
    Check (Test-Path -LiteralPath (Join-Path $versions[0].FullName 'ard.exe')) 'first install copies ARD into its version directory'
    Check (Test-Path -LiteralPath (Join-Path $versions[0].FullName 'frd\FRD.exe')) 'first install copies the full FRD runtime into its version directory'
    Check (Test-Path -LiteralPath (Join-Path $installRoot 'data\device\identity')) 'first install creates identity in data'
    $identity=Join-Path $installRoot 'data\device\identity';$config=Join-Path $installRoot 'data\config.json';$peers=Join-Path $installRoot 'data\peers.json'
    Set-Content -LiteralPath $peers -Value '{"preserve":true}' -NoNewline -Encoding utf8
    $before=@{identity=Hash $identity;config=Hash $config;peers=Hash $peers}
    Invoke-Installer
    Check ((Hash $identity) -ceq $before.identity -and (Hash $config) -ceq $before.config -and (Hash $peers) -ceq $before.peers) 'upgrade leaves every existing data file unchanged'
    Check ((Get-Content -LiteralPath (Join-Path $installRoot 'ArdUi.cmd') -Raw) -match ('versions\\'+[regex]::Escape($version)+'\\ArdUi\.exe')) 'launcher targets the installed version'
    Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'frd-current.path'))) 'installer creates no FRD pointer outside the version payload'
}
finally{
    if(Test-Path -LiteralPath $root){
        for($attempt=0;$attempt -lt 20 -and (Test-Path -LiteralPath $root);$attempt++){
            try{Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop}
            catch{if($attempt -eq 19){throw};Start-Sleep -Milliseconds 500}
        }
    }
}