$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$packages=[ordered]@{
    'KateY07.ArdUi.WinX64'='3.0.0-pre.8'
    'KateY07.Ard.WinX64'='3.0.0-pre.2'
    'KateY07.FRD.WinX64'='1.0.0-pre.13'
}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi-Offline-'+[guid]::NewGuid().ToString('N'))
$packageDirectory=Join-Path $PSScriptRoot 'packages'
$manifestPath=Join-Path $PSScriptRoot 'SHA256SUMS'

function Read-BundleManifest {
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'Offline bundle checksum manifest is missing.'}
    $manifest=@{}
    foreach($line in Get-Content -LiteralPath $manifestPath){
        if($line -notmatch '^([0-9a-f]{64})  (.+)$'){throw "Invalid checksum manifest line: $line"}
        $manifest[$Matches[2]]=$Matches[1]
    }
    return $manifest
}
function Expand-Package([string]$package,[string]$destination){
    New-Item -ItemType Directory -Path $destination | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($package,$destination)
}
function Get-Sha256([string]$path){
    $stream=[IO.File]::OpenRead($path)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
    finally{$sha.Dispose();$stream.Dispose()}
}

$oldPackageRoot=$env:ARDUI_PACKAGE_ROOT
$oldFrdSource=$env:ARDUI_FRD_SOURCE
try{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $manifest=Read-BundleManifest
    $expanded=@{}
    New-Item -ItemType Directory -Path $temporary | Out-Null
    foreach($id in $packages.Keys){
        $name="$id.$($packages[$id]).nupkg"
        $relative="packages/$name"
        $package=Join-Path $packageDirectory $name
        if(-not $manifest.ContainsKey($relative)){throw "Checksum is missing for $name"}
        if(-not (Test-Path -LiteralPath $package -PathType Leaf)){throw "Offline package is missing: $name"}
        $actual=Get-Sha256 $package
        if($actual -cne $manifest[$relative]){throw "Offline package checksum failed: $name"}
        $destination=Join-Path $temporary $id
        Expand-Package $package $destination
        $expanded[$id]=$destination
    }
    $uiTools=Join-Path $expanded['KateY07.ArdUi.WinX64'] 'tools'
    $ardTools=Join-Path $expanded['KateY07.Ard.WinX64'] 'tools'
    $frdTools=Join-Path $expanded['KateY07.FRD.WinX64'] 'tools'
    $payload=Join-Path $temporary 'payload'
    $frd=Join-Path $temporary 'frd'
    New-Item -ItemType Directory -Path $payload,$frd | Out-Null
    foreach($name in @('ArdUi.exe','ArdUi.exe.sha256','config.json','THIRD-PARTY-NOTICES.md')){
        Copy-Item -LiteralPath (Join-Path $uiTools $name) -Destination (Join-Path $payload $name)
    }
    Copy-Item -LiteralPath (Join-Path $ardTools 'ard.exe') -Destination (Join-Path $payload 'ard.exe')
    Copy-Item -LiteralPath (Join-Path $ardTools 'ard.exe.sha256') -Destination (Join-Path $payload 'ard.exe.sha256')
    $ardExpected=((Get-Content -LiteralPath (Join-Path $ardTools 'ard.exe.sha256') -Raw) -split '\s+')[0]
    if((Get-Sha256 (Join-Path $payload 'ard.exe')) -cne $ardExpected){throw 'ARD package checksum failed.'}
    $frdArchive=Join-Path $frdTools 'FRD-v1.pre13-win-x64.zip'
    $frdExpected=((Get-Content -LiteralPath ($frdArchive+'.sha256') -Raw) -split '\s+')[0]
    if((Get-Sha256 $frdArchive) -cne $frdExpected){throw 'FRD package checksum failed.'}
    [IO.Compression.ZipFile]::ExtractToDirectory($frdArchive,$frd)
    $env:ARDUI_PACKAGE_ROOT=$payload
    $env:ARDUI_FRD_SOURCE=$frd
    & (Join-Path $uiTools 'install.ps1')
}
finally{
    if($null -eq $oldPackageRoot){Remove-Item Env:ARDUI_PACKAGE_ROOT -ErrorAction SilentlyContinue}else{$env:ARDUI_PACKAGE_ROOT=$oldPackageRoot}
    if($null -eq $oldFrdSource){Remove-Item Env:ARDUI_FRD_SOURCE -ErrorAction SilentlyContinue}else{$env:ARDUI_FRD_SOURCE=$oldFrdSource}
    $tempPrefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(-not [IO.Path]::GetFullPath($temporary).StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid temporary directory.'}
    if(Test-Path -LiteralPath $temporary){
        if((Get-Item -LiteralPath $temporary).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Temporary directory cannot be a link.'}
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
