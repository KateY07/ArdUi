$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1.pre5'
$expectedSha256='558c739a489ae795fc1b0a50553f4cb3b9e98ae2d2d43a7c3a0fe8a2622f967e'
$base='https://f.visnova.cn/ardui'
$root=Join-Path $env:LOCALAPPDATA 'ArdUi'
$versions=Join-Path $root 'versions'
$destination=Join-Path $versions $version
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('ArdUi-'+[guid]::NewGuid().ToString('N'))

function Protect-ArdUiTree([string]$path){
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $admins=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $allow=[Security.AccessControl.AccessControlType]::Allow
    $directory=[Security.AccessControl.DirectorySecurity]::new()
    $directory.SetAccessRuleProtection($true,$false)
    foreach($sid in @($user,$system,$admins)){
        $directory.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None',$allow))
    }
    [IO.Directory]::SetAccessControl($path,$directory)
    Get-ChildItem -LiteralPath $path -Recurse -Force | ForEach-Object {
        if($_.PSIsContainer){
            [IO.Directory]::SetAccessControl($_.FullName,$directory)
        } else {
            $file=[Security.AccessControl.FileSecurity]::new(); $file.SetAccessRuleProtection($true,$false)
            foreach($sid in @($user,$system,$admins)){
                $file.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow'))
            }
            [IO.File]::SetAccessControl($_.FullName,$file)
        }
    }
}

function Test-SameTree([string]$left,[string]$right){
    $a=@(Get-ChildItem -LiteralPath $left -File -Recurse | ForEach-Object { [pscustomobject]@{Path=$_.FullName.Substring($left.Length); Hash=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} })
    $b=@(Get-ChildItem -LiteralPath $right -File -Recurse | ForEach-Object { [pscustomobject]@{Path=$_.FullName.Substring($right.Length); Hash=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} })
    if($a.Count -ne $b.Count){return $false}
    $lookup=@{}; foreach($item in $a){$lookup[$item.Path]=$item.Hash}
    foreach($item in $b){if($lookup[$item.Path] -ne $item.Hash){return $false}}
    return $true
}

try {
    if(-not [Environment]::Is64BitOperatingSystem){throw 'ArdUi requires 64-bit Windows.'}
    $build=[Environment]::OSVersion.Version.Build
    if($build -lt 26100){throw "ArdUi requires Windows 11 24H2 / Server 2025 or later (build 26100+); current build is $build."}
    New-Item -ItemType Directory -Force $root,$versions,$temporary | Out-Null
    Protect-ArdUiTree $root
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
    if(Test-Path $destination){
        if(Test-SameTree $destination $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
        else {
            $quarantine=$destination+'.replaced-'+(Get-Date -Format 'yyyyMMddHHmmss')
            try { Move-Item -LiteralPath $destination -Destination $quarantine }
            catch { throw 'Existing v1.pre5 files need repair. Close ArdUi and run the install command again.' }
            Move-Item -LiteralPath $stage -Destination $destination
        }
    } else { Move-Item -LiteralPath $stage -Destination $destination }
    Protect-ArdUiTree $root
    $launcher=Join-Path $root 'ArdUi.cmd'
    $lines=@('@echo off','"%~dp0versions\v1.pre5\python.exe" "%~dp0versions\v1.pre5\prototype.py" run --data "%~dp0data" --ard "%~dp0versions\v1.pre5\ard.exe"')
    [IO.File]::WriteAllLines($launcher,$lines,[Text.Encoding]::ASCII)
    Protect-ArdUiTree $root
    & (Join-Path $destination 'python.exe') (Join-Path $destination 'prototype.py') init --data (Join-Path $root 'data') --ard (Join-Path $destination 'ard.exe')
    if($LASTEXITCODE){throw 'Identity initialization failed; existing identity was not overwritten.'}
    Protect-ArdUiTree $root
    Write-Host "ArdUi console $version installed at $root"
    if($env:ARDUI_INSTALL_NO_START -ne '1'){
        Write-Host 'Starting ArdUi in a new console...'
        Start-Process -FilePath $launcher -WorkingDirectory $root
    }
}
finally {
    if(Test-Path $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}
}
