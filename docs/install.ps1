$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$packages=[ordered]@{
    'KateY07.ArdUi.WinX64'='2.0.0-pre.6'
    'KateY07.Ard.WinX64'='2.0.0-pre.6'
    'KateY07.FRD.WinX64'='1.0.0-pre.9'
}
$root=if($env:ARDUI_INSTALL_ROOT){[IO.Path]::GetFullPath($env:ARDUI_INSTALL_ROOT)}else{Join-Path $env:LOCALAPPDATA 'ArdUi'}
$cache=Join-Path $root 'cache\nuget'
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi-NuGet-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $cache,$temporary | Out-Null

function Get-NuGetPackage([string]$id,[string]$version){
    $lower=$id.ToLowerInvariant()
    $name="$lower.$version.nupkg"
    $cached=Join-Path $cache $name
    if(Test-Path -LiteralPath $cached -PathType Leaf){
        try{
            $archive=[IO.Compression.ZipFile]::OpenRead($cached)
            $archive.Dispose()
            Write-Host "Reusing $id $version"
            return $cached
        }
        catch{
            Write-Warning "Cached $id $version is invalid and will be downloaded again: $($_.Exception.Message)"
            Remove-Item -LiteralPath $cached -Force
        }
    }
    $download=Join-Path $temporary $name
    $url="https://api.nuget.org/v3-flatcontainer/$lower/$version/$name"
    Invoke-WebRequest -Uri $url -OutFile $download
    $archive=[IO.Compression.ZipFile]::OpenRead($download)
    $archive.Dispose()
    Move-Item -LiteralPath $download -Destination $cached -Force
    Write-Host "Downloaded $id $version"
    return $cached
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

try{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expanded=@{}
    foreach($id in $packages.Keys){
        $destination=Join-Path $temporary $id
        Expand-Package (Get-NuGetPackage $id $packages[$id]) $destination
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
    $frdArchive=Join-Path $frdTools 'FRD-v1.pre9-win-x64.tar.xz'
    $frdExpected=((Get-Content -LiteralPath ($frdArchive+'.sha256') -Raw) -split '\s+')[0]
    if((Get-Sha256 $frdArchive) -cne $frdExpected){throw 'FRD package checksum failed.'}
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $frdArchive -C $frd
    if($LASTEXITCODE -ne 0){throw 'FRD package extraction failed.'}
    $env:ARDUI_PACKAGE_ROOT=$payload
    $env:ARDUI_FRD_SOURCE=$frd
    & (Join-Path $uiTools 'install.ps1')
}
finally{
    Remove-Item Env:ARDUI_PACKAGE_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:ARDUI_FRD_SOURCE -ErrorAction SilentlyContinue
    $tempPrefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(-not [IO.Path]::GetFullPath($temporary).StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid temporary directory.'}
    if(Test-Path -LiteralPath $temporary){
        if((Get-Item -LiteralPath $temporary).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Temporary directory cannot be a link.'}
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
