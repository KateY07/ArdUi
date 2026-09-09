param([string]$ArdPath = (Join-Path $PSScriptRoot 'tools\ard.exe'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1.pre7'
$pythonVersion='3.13.15'
$pythonUrl="https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip"
$pythonSha256='d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf'
$cache=Join-Path $PSScriptRoot 'dist\console-cache'
$stage=Join-Path $PSScriptRoot "dist\console-$version"
$archive=Join-Path $PSScriptRoot "dist\ArdUi-console-$version.zip"
New-Item -ItemType Directory -Force $cache | Out-Null
$pythonZip=Join-Path $cache "python-$pythonVersion-embed-amd64.zip"
if(-not (Test-Path $pythonZip)){Invoke-WebRequest $pythonUrl -OutFile $pythonZip}
if((Get-FileHash $pythonZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pythonSha256){throw 'Python archive SHA-256 mismatch.'}
if(Test-Path $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
New-Item -ItemType Directory -Force $stage | Out-Null
Expand-Archive $pythonZip $stage
$builder='C:\Users\arosa\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if(-not (Test-Path $builder)){throw 'Build Python runtime is unavailable.'}
$wheels=Join-Path $cache 'wheels-cp313'
New-Item -ItemType Directory -Force $wheels | Out-Null
$expected=@{
    'cryptography-50.0.1-cp311-abi3-win_amd64.whl'='aed8db4f6d71c51efb89530e12d9464e7bf2923d46c3205dc794a2a93f8c0648'
    'cffi-2.1.1-cp313-cp313-win_amd64.whl'='1aa5645c30469b09530c4ebca77ebf8f17618293c58f8549cb1a543a50236e7d'
    'pycparser-3.0-py3-none-any.whl'='b727414169a36b7d524c1c3e31839a521725078d7b2ff038656844266160a992'
}
if($expected.Keys | Where-Object {-not (Test-Path (Join-Path $wheels $_))}){
    & $builder -m pip download --disable-pip-version-check --dest $wheels --only-binary=:all: --platform win_amd64 --python-version 313 --implementation cp --abi cp313 'cryptography==50.0.1' 'cffi==2.1.1' 'pycparser==3.0'
    if($LASTEXITCODE){throw 'Downloading pinned dependency wheels failed.'}
}
$site=Join-Path $stage 'Lib\site-packages'; New-Item -ItemType Directory -Force $site | Out-Null
[Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem') | Out-Null
foreach($name in $expected.Keys){
    $wheel=Join-Path $wheels $name
    if(-not (Test-Path $wheel) -or (Get-FileHash $wheel -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$name]){throw "Dependency wheel SHA-256 mismatch: $name"}
    [IO.Compression.ZipFile]::ExtractToDirectory($wheel,$site)
}
$pth=Get-ChildItem $stage -Filter 'python*._pth' -File -ErrorAction Stop | Select-Object -First 1
[IO.File]::AppendAllText($pth.FullName,"`r`nLib\site-packages`r`nimport site`r`n",[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'prototype.py') -Destination $stage
Copy-Item -LiteralPath (Resolve-Path $ArdPath).Path -Destination (Join-Path $stage 'ard.exe')
$ardVersion=(& (Join-Path $stage 'ard.exe') --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $ardVersion -ne 'ard 2.0.0-pre.6'){throw "ARD 2.0.0-pre.6 is required; got '$ardVersion'."}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PROTOTYPE.md') -Destination $stage
[IO.File]::WriteAllText((Join-Path $stage 'VERSION'),$version+"`n",[Text.UTF8Encoding]::new($false))
& (Join-Path $stage 'python.exe') -c "import cryptography; print(cryptography.__version__)"
if($LASTEXITCODE){throw 'Packaged Python runtime test failed.'}
if(Test-Path $archive){Remove-Item -LiteralPath $archive -Force}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
$hash=(Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$size=(Get-Item $archive).Length
Write-Host "ARCHIVE=$archive"
Write-Host "SHA256=$hash"
Write-Host "SIZE=$size"
