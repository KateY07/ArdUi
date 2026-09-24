param(
    [string]$PackageDirectory=(Join-Path $PSScriptRoot '..\dist\nuget'),
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\dist\offline')
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$packages=@(
    'KateY07.ArdUi.WinX64.3.0.0-pre.4.nupkg',
    'KateY07.Ard.WinX64.3.0.0-pre.2.nupkg',
    'KateY07.FRD.WinX64.1.0.0-pre.13.nupkg'
)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$stage=Join-Path $output 'ArdUi-v3.pre4-offline'
$archive=Join-Path $output 'ArdUi-v3.pre4-offline.zip'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$outputPrefix=$output.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if(-not [IO.Path]::GetFullPath($stage).StartsWith($outputPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid staging directory.'}
if(Test-Path -LiteralPath $stage){
    if((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Staging directory cannot be a link.'}
    Remove-Item -LiteralPath $stage -Recurse -Force
}
if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive -Force}
$stagePackages=Join-Path $stage 'packages'
New-Item -ItemType Directory -Path $stagePackages | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'offline\install.bat') -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'offline\install.ps1') -Destination $stage
$lines=@()
foreach($name in $packages){
    $source=Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) $name
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw "Package is missing: $name"}
    $destination=Join-Path $stagePackages $name
    Copy-Item -LiteralPath $source -Destination $destination
    $hash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines+="$hash  packages/$name"
}
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS'),$lines,[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$archive,[IO.Compression.CompressionLevel]::NoCompression,$false)
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
    foreach($required in @('install.bat','install.ps1','SHA256SUMS')){
        if(-not ($zip.Entries | Where-Object FullName -ceq $required)){throw "Offline ZIP entry is missing: $required"}
    }
}
finally{$zip.Dispose()}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive+'.sha256'),"$hash  $([IO.Path]::GetFileName($archive))`n",[Text.UTF8Encoding]::new($false))
Write-Output "Offline bundle: $archive"
Write-Output "SHA-256: $hash"
