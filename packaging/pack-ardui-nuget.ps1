param([string]$ArdPath,[string]$FrdSourcePath,[string]$UiPath,[string]$OutputDirectory)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $repo 'dist/nuget'}
$installer=Get-Content -LiteralPath (Join-Path $repo 'install.ps1') -Raw

function Read-Literal([string]$name){
    $match=[regex]::Match($installer,('(?m)^\$'+[regex]::Escape($name)+"='([^']+)'"))
    if(-not $match.Success){throw "Missing installer literal: $name"}
    return $match.Groups[1].Value
}
function Read-PackageVersion([string]$project){
    [xml]$xml=Get-Content -LiteralPath $project -Raw
    return [string]$xml.Project.PropertyGroup.PackageVersion
}
function Convert-ReleaseVersion([string]$version){
    if($version -match '^v(\d+\.\d+\.\d+)$'){return $Matches[1]}
    if($version -notmatch '^v(\d+)\.pre(\d+)$'){throw "Unsupported release version: $version"}
    return "$($Matches[1]).0.0-pre.$($Matches[2])"
}
function Reset-Payload([string]$packageRoot){
    $root=[IO.Path]::GetFullPath($packageRoot)
    $payload=[IO.Path]::GetFullPath((Join-Path $root 'payload'))
    $prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(-not $payload.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid package payload path.'}
    if(Test-Path -LiteralPath $payload){
        if((Get-Item -LiteralPath $payload).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Package payload cannot be a link.'}
        Remove-Item -LiteralPath $payload -Recurse -Force
    }
    New-Item -ItemType Directory -Path $payload | Out-Null
    return $payload
}
function Assert-Hash([string]$path,[string]$expected){
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing release file: $path"}
    $actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -cne $expected){throw "SHA-256 mismatch for $path; expected $expected, got $actual"}
}
function Get-Source([string]$localPath,[string]$name,[string]$sha256){
    if($localPath){$source=[IO.Path]::GetFullPath($localPath)}
    else{
        $source=Join-Path $repo "dist/nuget-input/$name"
        New-Item -ItemType Directory -Force -Path (Split-Path $source) | Out-Null
        if(-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sha256){
            Invoke-WebRequest -Uri "https://f.visnova.cn/ardui/$name" -OutFile $source
        }
    }
    Assert-Hash $source $sha256
    return $source
}
function Pack-Project([string]$project,[string[]]$sources){
    $arguments=@('restore',$project,'--ignore-failed-sources')
    foreach($source in $sources){$arguments+=@('--source',$source)}
    & dotnet @arguments
    if($LASTEXITCODE -ne 0){throw "NuGet restore failed: $project"}
    dotnet pack $project --no-restore -c Release -o $output
    if($LASTEXITCODE -ne 0){throw "NuGet pack failed: $project"}
}

$uiRoot=Join-Path $PSScriptRoot 'ArdUi.WinX64'
$ardRoot=Join-Path $PSScriptRoot 'Ard.WinX64'
$frdRoot=Join-Path $PSScriptRoot 'FRD.WinX64'
$uiProject=Join-Path $uiRoot 'ArdUi.WinX64.csproj'
$ardProject=Join-Path $ardRoot 'Ard.WinX64.csproj'
$frdProject=Join-Path $frdRoot 'FRD.WinX64.csproj'
$frdSourceInput=if($FrdSourcePath){[IO.Path]::GetFullPath($FrdSourcePath)}else{''}
$frdPayloadRoot=[IO.Path]::GetFullPath((Join-Path $frdRoot 'payload'))
$frdPayloadPrefix=$frdPayloadRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if($frdSourceInput -and ($frdSourceInput -eq $frdPayloadRoot -or $frdSourceInput.StartsWith($frdPayloadPrefix,[StringComparison]::OrdinalIgnoreCase))){throw 'FRD source cannot be inside the package payload staging directory.'}
$uiPayload=Reset-Payload $uiRoot
$ardPayload=Reset-Payload $ardRoot
$frdPayload=Reset-Payload $frdRoot

[xml]$appProject=Get-Content -LiteralPath (Join-Path $repo 'ArdUi.csproj') -Raw
$uiVersion=[string]$appProject.Project.PropertyGroup.Version
$releaseVersion=Read-Literal 'version'
$ardSource=if($ArdPath){[IO.Path]::GetFullPath($ArdPath)}else{[IO.Path]::GetFullPath((Join-Path $repo '..\target\release\ard.exe'))}
$ardOutput=(& $ardSource --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $ardOutput -notmatch '^ard ([0-9]+\.[0-9]+\.[0-9]+-pre\.[0-9]+)$'){throw "Unsupported ARD binary: $ardOutput"}
$ardVersion=$Matches[1]
$ardHash=(Get-FileHash -LiteralPath $ardSource -Algorithm SHA256).Hash.ToLowerInvariant()
$frdVersion=Convert-ReleaseVersion (Read-Literal 'frdVersion')
$frdRuntimeSource=Get-Content -LiteralPath (Join-Path $repo 'Frd/FrdRuntime.cs') -Raw
if($frdRuntimeSource -match 'v\d+\.pre\d+'){throw 'ArdUi runtime resolver must not hardcode an FRD version.'}
if($uiVersion -cne (Read-PackageVersion $uiProject)){throw 'ArdUi application and package versions differ.'}
if($uiVersion -cne (Convert-ReleaseVersion $releaseVersion)){throw 'ArdUi application and installer versions differ.'}
if($ardVersion -cne (Read-PackageVersion $ardProject)){throw 'ARD package version differs from the installer contract.'}
if($frdVersion -cne (Read-PackageVersion $frdProject)){throw 'FRD package version differs from the installer contract.'}
if($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -cne $releaseVersion){throw "Tag $env:GITHUB_REF_NAME differs from $releaseVersion"}

Copy-Item -LiteralPath $ardSource -Destination (Join-Path $ardPayload 'ard.exe')
if((& (Join-Path $ardPayload 'ard.exe') --version | Out-String).Trim() -cne "ard $ardVersion"){throw 'ARD binary version mismatch.'}
[IO.File]::WriteAllText((Join-Path $ardPayload 'ard.exe.sha256'),"$ardHash  ard.exe`n",[Text.UTF8Encoding]::new($false))

$frdSource=$frdSourceInput
if(-not $frdSource -or -not (Test-Path -LiteralPath $frdSource -PathType Container)){throw 'Pass -FrdSourcePath with the verified binary release delivered by the FRD development team.'}
$manifestBlock=[regex]::Match($installer,'(?s)# FRD release manifest begin\s*(.*?)\s*# FRD release manifest end')
if(-not $manifestBlock.Success){throw 'FRD file manifest is missing.'}
$entries=[regex]::Matches($manifestBlock.Groups[1].Value,"'([^']+)'='([0-9a-f]{64})'")
if($entries.Count -lt 10){throw 'FRD file manifest is incomplete.'}
$frdFiles=@{}
foreach($entry in $entries){$frdFiles[$entry.Groups[1].Value]=$entry.Groups[2].Value}
foreach($name in $frdFiles.Keys){
    $source=Join-Path $frdSource $name
    Assert-Hash $source $frdFiles[$name]
    $destination=Join-Path (Join-Path $frdPayload 'runtime') $name
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
$frdLines=@($frdFiles.Keys | Sort-Object | ForEach-Object {'{0}  {1}' -f $frdFiles[$_],($_ -replace '\\','/')})
[IO.File]::WriteAllLines((Join-Path $frdPayload 'FRD-SHA256SUMS'),$frdLines,[Text.UTF8Encoding]::new($false))
$frdArchiveName=Read-Literal 'frdArchiveName'
$frdArchive=Join-Path $frdPayload $frdArchiveName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $frdPayload 'runtime'),$frdArchive,[IO.Compression.CompressionLevel]::Optimal,$false)
$frdArchiveHash=(Get-FileHash -LiteralPath $frdArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($frdArchive+'.sha256'),"$frdArchiveHash  $frdArchiveName`n",[Text.UTF8Encoding]::new($false))

if($UiPath){$uiSource=[IO.Path]::GetFullPath($UiPath)}
else{
    $uiOutput=Join-Path $repo 'dist/nuget-ui'
    dotnet publish (Join-Path $repo 'ArdUi.csproj') -c Release -r win-x64 --self-contained false -o $uiOutput -p:PublishSingleFile=true -p:PublishTrimmed=false
    if($LASTEXITCODE -ne 0){throw 'ArdUi publish failed.'}
    $uiSource=Join-Path $uiOutput 'ArdUi.exe'
}
if(-not (Test-Path -LiteralPath $uiSource -PathType Leaf)){throw "Missing ArdUi.exe: $uiSource"}
Copy-Item -LiteralPath $uiSource -Destination (Join-Path $uiPayload 'ArdUi.exe')
$uiHash=(Get-FileHash -LiteralPath (Join-Path $uiPayload 'ArdUi.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $uiPayload 'ArdUi.exe.sha256'),"$uiHash  ArdUi.exe`n",[Text.UTF8Encoding]::new($false))
$oldHash=Read-Literal 'expectedSha256'
$oldLine='$expectedSha256='+"'$oldHash'"
if(-not $installer.Contains($oldLine)){throw 'Could not locate the ArdUi checksum in the installer.'}
$packageInstaller=$installer.Replace($oldLine,('$expectedSha256='+"'$uiHash'"))
$archiveLine = '$frdArchiveSha256=' + [char]39 + $frdArchiveHash + [char]39
$packageInstaller=[regex]::Replace($packageInstaller,'(?m)^\$frdArchiveSha256=''[0-9a-f]*''',$archiveLine)
[IO.File]::WriteAllText((Join-Path $uiPayload 'install.ps1'),$packageInstaller,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repo 'config.json') -Destination (Join-Path $uiPayload 'config.json')
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $uiPayload 'THIRD-PARTY-NOTICES.md')

New-Item -ItemType Directory -Force -Path $output | Out-Null
Get-ChildItem -LiteralPath $output -Filter '*.nupkg' -File -ErrorAction SilentlyContinue | Remove-Item -Force
Pack-Project $ardProject @()
Pack-Project $frdProject @()
Pack-Project $uiProject @($output)
$expected=@(
    "KateY07.Ard.WinX64.$ardVersion.nupkg",
    "KateY07.FRD.WinX64.$frdVersion.nupkg",
    "KateY07.ArdUi.WinX64.$uiVersion.nupkg"
)
foreach($name in $expected){if(-not (Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf)){throw "Expected package is missing: $name"}}
Write-Output "ArdUi package: $uiVersion ($uiHash)"
Write-Output "ARD package: $ardVersion ($ardHash)"
Write-Output "FRD package: $frdVersion (delivered binary $((Read-Literal 'frdVersion')); $($frdFiles.Count) verified runtime files; $frdArchiveHash)"
