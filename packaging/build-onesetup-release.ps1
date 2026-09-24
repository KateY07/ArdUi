$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

$root=Split-Path $PSScriptRoot -Parent
$project=Join-Path $root 'ArdUi.csproj'
$projectXml=[xml](Get-Content -LiteralPath $project -Raw)
$projectVersion=[string]$projectXml.Project.PropertyGroup.Version
$match=[regex]::Match($projectVersion,'^(\d+)\.\d+\.\d+-pre\.(\d+)$')
if(-not $match.Success){throw 'ArdUi.csproj must contain a prerelease semantic version.'}
$version='v'+$match.Groups[1].Value+'.pre'+$match.Groups[2].Value
$packer=Join-Path (Split-Path $root -Parent) '.tools\onesetup\onesetup.exe'
$frdCandidates=@(Get-ChildItem -LiteralPath 'D:\pub\FRD' -File -Filter 'v*.zip' -ErrorAction SilentlyContinue | ForEach-Object {
    if($_.BaseName -match '^v(\d+)\.pre(\d+)$'){
        [pscustomobject]@{Path=$_.FullName;Major=[int]$Matches[1];Pre=[int]$Matches[2]}
    }
})
$frdArchive=($frdCandidates | Sort-Object Major,Pre -Descending | Select-Object -First 1).Path
$outputRoot='D:\pub\ArdUi'
$output=Join-Path $outputRoot ($version+'_setup.exe')
$sidecar=$output+'.sha256'
$stage=Join-Path $root ('dist\'+$version+'-onesetup')
$publish=Join-Path $root ('dist\final-'+$version)

foreach($path in @($output,$sidecar,$stage,$publish)){
    if(Test-Path -LiteralPath $path){throw "Refusing to overwrite existing release or staging path: $path"}
}
foreach($path in @($packer,$frdArchive)){
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Required release input is missing: $path"}
}

dotnet publish $project -c Release -r win-x64 --self-contained false -o $publish -p:PublishSingleFile=true -p:PublishTrimmed=false
if($LASTEXITCODE -ne 0){throw 'ArdUi publish failed.'}
$ui=Join-Path $publish 'ArdUi.exe'
$ard=Join-Path (Split-Path $root -Parent) 'target\release\ard.exe'
foreach($path in @($ui,$ard)){
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Required payload file is missing: $path"}
}
& $ui --self-test
if($LASTEXITCODE -ne 0){throw 'ArdUi self-test failed.'}

$payload=Join-Path $stage 'payload'
New-Item -ItemType Directory -Force -Path (Join-Path $payload 'frd') | Out-Null
Copy-Item -LiteralPath $ui,(Join-Path $root 'config.json'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $payload
Copy-Item -LiteralPath $ard -Destination (Join-Path $payload 'ard.exe')
[IO.File]::WriteAllText((Join-Path $payload 'release.txt'),$version+[Environment]::NewLine,[Text.Encoding]::ASCII)
Expand-Archive -LiteralPath $frdArchive -DestinationPath (Join-Path $payload 'frd')
if(-not (Test-Path -LiteralPath (Join-Path $payload 'frd\FRD.exe') -PathType Leaf)){throw 'FRD.exe is missing from the FRD release.'}

$installer=Join-Path $root 'install.ps1'
$bytes=[IO.File]::ReadAllBytes($installer)
if($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF){throw 'install.ps1 must be UTF-8 with BOM.'}
Copy-Item -LiteralPath $installer,(Join-Path $root 'packaging\offline\install.bat') -Destination $stage
[IO.File]::WriteAllText((Join-Path $stage 'RELEASE.txt'),@"
ArdUi $version
This self-extracting installer invokes install.bat after extracting to a temporary directory.
It installs the complete payload into versions\$version, retains an existing data directory, and refreshes the startup shortcut.
Requires preinstalled .NET 8 and .NET 10 x64 runtimes.
"@,[Text.UTF8Encoding]::new($false))

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
& $packer $stage -o $output
if($LASTEXITCODE -ne 0){throw 'OneSetup packaging failed.'}
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($sidecar,"$hash *$(Split-Path $output -Leaf)`n",[Text.Encoding]::ASCII)
Write-Host "$output`nSHA256 $hash`n$([math]::Round((Get-Item -LiteralPath $output).Length/1MB,2)) MiB"
