$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

$root=if($env:ARDUI_INSTALL_ROOT){[IO.Path]::GetFullPath($env:ARDUI_INSTALL_ROOT)}else{Join-Path $env:LOCALAPPDATA 'ArdUi'}
$payload=Join-Path $PSScriptRoot 'payload'
$version=[IO.File]::ReadAllText((Join-Path $payload 'release.txt'),[Text.Encoding]::UTF8).Trim()
if([string]::IsNullOrWhiteSpace($version)){throw '安装包缺少版本信息。'}
$data=Join-Path $root 'data'
$destination=Join-Path (Join-Path $root 'versions') $version

New-Item -ItemType Directory -Force -Path $root,(Join-Path $root 'versions'),$destination | Out-Null
Copy-Item -Path (Join-Path $payload '*') -Destination $destination -Recurse -Force

if(-not (Test-Path -LiteralPath $data -PathType Container)){
    New-Item -ItemType Directory -Path $data | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'config.json') -Destination (Join-Path $data 'config.json')
    & (Join-Path $destination 'ArdUi.exe') --identity-store $data
    $identityExit=Get-Variable -Name LASTEXITCODE -ValueOnly -ErrorAction SilentlyContinue
    if($null -ne $identityExit -and $identityExit -ne 0){throw '首次设备身份创建失败。'}
}

$launcher=Join-Path $root 'ArdUi.cmd'
[IO.File]::WriteAllLines($launcher,@('@echo off','set "ARDUI_DATA_ROOT=%~dp0data"',('start "" "%~dp0versions\{0}\ArdUi.exe"' -f $version)),[Text.Encoding]::ASCII)
if($env:ARDUI_INSTALL_NO_SHORTCUT -ne '1'){
    $startup=[Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startup 'ArdUi.lnk'))
    $shortcut.TargetPath=$env:ComSpec
    $shortcut.Arguments=('/d /c ""{0}""' -f $launcher)
    $shortcut.WorkingDirectory=$root
    $shortcut.WindowStyle=7
    $shortcut.Description='ArdUi startup'
    $shortcut.Save()
}
if($env:ARDUI_INSTALL_NO_START -ne '1'){Start-Process -FilePath $launcher}
Write-Host "ArdUi $version installed and started at $root"
