$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1pre.1'
$expectedSha256='bc51288a641303ec47e1f182336e9896858539ff498ec050e91721e54c1dcc60'
$base='https://f.visnova.cn/ardui'
$root=Join-Path $env:LOCALAPPDATA 'ArdUi'
$versions=Join-Path $root 'versions'
$destination=Join-Path $versions $version
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi-'+[guid]::NewGuid().ToString('N'))
try {
    if(-not [Environment]::Is64BitOperatingSystem){throw 'ArdUi requires 64-bit Windows.'}
    $build=[Environment]::OSVersion.Version.Build
    if($build -lt 26100){throw "ArdUi requires Windows 11 24H2 / Server 2025 or later (build 26100+); current build is $build."}
    New-Item -ItemType Directory -Force $versions | Out-Null
    if(-not (Test-Path $destination)){
        New-Item -ItemType Directory -Force $temporary | Out-Null
        $zip=Join-Path $temporary 'release.zip'
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "$base/ArdUi-console-$version.zip" -OutFile $zip
        $actual=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if($actual -ne $expectedSha256){throw 'ArdUi release SHA-256 mismatch; installation stopped.'}
        $stage=Join-Path $temporary $version
        Expand-Archive $zip $stage
        foreach($name in 'python.exe','prototype.py','ard.exe','VERSION'){
            if(-not (Test-Path (Join-Path $stage $name) -PathType Leaf)){throw "Release is missing $name."}
        }
        Move-Item -LiteralPath $stage -Destination $destination
    }
    $launcher=Join-Path $root 'ArdUi.cmd'
    $lines=@('@echo off',('"'+(Join-Path $destination 'python.exe')+'" "'+(Join-Path $destination 'prototype.py')+'" run --data "'+(Join-Path $root 'data')+'" --ard "'+(Join-Path $destination 'ard.exe')+'"'))
    [IO.File]::WriteAllLines($launcher,$lines,[Text.Encoding]::ASCII)
    & (Join-Path $destination 'python.exe') (Join-Path $destination 'prototype.py') init --data (Join-Path $root 'data') --ard (Join-Path $destination 'ard.exe')
    if($LASTEXITCODE){throw 'Identity initialization failed; existing identity was not overwritten.'}
    Write-Host "ArdUi console $version installed at $root"
    if($env:ARDUI_INSTALL_NO_START -ne '1'){
        Write-Host 'Starting ArdUi in a new console...'
        Start-Process -FilePath $launcher -WorkingDirectory $root
    }
}
finally {
    if(Test-Path $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}
}
