param([string]$ArdPath = (Join-Path $PSScriptRoot 'tools\ard.exe'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1pre.1'
$pythonVersion='3.12.10'
$pythonUrl="https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embeddable-amd64.zip"
$cache=Join-Path $PSScriptRoot 'dist\console-cache'
$stage=Join-Path $PSScriptRoot "dist\console-$version"
$archive=Join-Path $PSScriptRoot "dist\ArdUi-console-$version.zip"
New-Item -ItemType Directory -Force $cache | Out-Null
$pythonZip=Join-Path $cache "python-$pythonVersion-embeddable-amd64.zip"
if(-not (Test-Path $pythonZip)){Invoke-WebRequest $pythonUrl -OutFile $pythonZip}
if((Get-Item $pythonZip).Length -ne 11128720){throw 'Unexpected Python archive size.'}
if(Test-Path $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
New-Item -ItemType Directory -Force $stage | Out-Null
Expand-Archive $pythonZip $stage
New-Item -ItemType Directory -Force (Join-Path $stage 'Lib\site-packages') | Out-Null
$builder='C:\Users\arosa\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if(-not (Test-Path $builder)){throw 'Build Python runtime is unavailable.'}
& $builder -m pip install --disable-pip-version-check --no-compile --only-binary=:all: --target (Join-Path $stage 'Lib\site-packages') 'cryptography==46.0.3'
if($LASTEXITCODE){throw 'Installing the pinned cryptography wheel failed.'}
$pth=Get-ChildItem $stage -Filter 'python*._pth' -File -ErrorAction Stop | Select-Object -First 1
[IO.File]::AppendAllText($pth.FullName,"`r`nLib\site-packages`r`nimport site`r`n",[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'prototype.py') -Destination $stage
Copy-Item -LiteralPath (Resolve-Path $ArdPath).Path -Destination (Join-Path $stage 'ard.exe')
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
