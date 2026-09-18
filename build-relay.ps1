param([string]$Python='python')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try{
    $build=Join-Path $PSScriptRoot 'dist/relay-build'
    $envPython=Join-Path $build 'env/Scripts/python.exe'
    New-Item -ItemType Directory -Force $build | Out-Null
    if(-not (Test-Path -LiteralPath $envPython)){
        & $Python -m venv (Join-Path $build 'env')
        if($LASTEXITCODE -ne 0){throw 'Relay build environment creation failed.'}
    }
    & $envPython -m pip install -r transit/requirements-build.txt
    if($LASTEXITCODE -ne 0){throw 'Relay build dependencies failed.'}
    & $envPython -m PyInstaller --noconfirm --clean --onefile --console --name ArdTransit --distpath "$build/output" --workpath "$build/work" --specpath $build transit/ardtransit.py
    if($LASTEXITCODE -ne 0){throw 'ArdTransit executable build failed.'}
    & $envPython tests/package_relay.py
    if($LASTEXITCODE -ne 0){throw 'ArdTransit packaging failed.'}
}
finally{Pop-Location}
