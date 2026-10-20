$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# The installer window (onesetup --ui) reads these lines: '.title', '.info' (a new step), '.progress', '.error'. UTF-8 so Chinese text survives the pipe.
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
function Step([int]$percent,[string]$text){Write-Host ".progress $percent";Write-Host ".info $text"}
trap{Write-Host ".error $($_.Exception.Message)";exit 1}

$root=if($env:ARDUI_INSTALL_ROOT){[IO.Path]::GetFullPath($env:ARDUI_INSTALL_ROOT)}else{Join-Path $env:LOCALAPPDATA 'ArdUi'}
$payload=Join-Path $PSScriptRoot 'payload'
$version=[IO.File]::ReadAllText((Join-Path $payload 'release.txt'),[Text.Encoding]::UTF8).Trim()
if([string]::IsNullOrWhiteSpace($version)){throw '安装包缺少版本信息。'}
$data=Join-Path $root 'data'
Write-Host ".title 安装 ArdUi $version"
Step 8 '复制程序文件'
$destination=Join-Path (Join-Path $root 'versions') $version

New-Item -ItemType Directory -Force -Path $root,(Join-Path $root 'versions'),$destination | Out-Null
Copy-Item -Path (Join-Path $payload '*') -Destination $destination -Recurse -Force

Step 45 '检查设备身份与配置'
if(-not (Test-Path -LiteralPath $data -PathType Container)){
    New-Item -ItemType Directory -Path $data | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'config.json') -Destination (Join-Path $data 'config.json')
    & (Join-Path $destination 'ArdUi.exe') --identity-store $data
    $identityExit=Get-Variable -Name LASTEXITCODE -ValueOnly -ErrorAction SilentlyContinue
    if($null -ne $identityExit -and $identityExit -ne 0){throw '首次设备身份创建失败。'}
}

Step 65 '创建启动器和开始菜单项'
$launcher=Join-Path $root 'ArdUi.cmd'
[IO.File]::WriteAllLines($launcher,@('@echo off','set "ARDUI_DATA_ROOT=%~dp0data"',('start "" "%~dp0versions\{0}\ArdUi.exe"' -f $version)),[Text.Encoding]::ASCII)
# Autostart is a setting inside ArdUi (Settings > General); the installer only creates the shortcut when asked to.
# A Start menu entry that runs the program itself (with its own icon), so ArdUi can be started from the Start menu like any app.
# It points at the installed version and passes the data folder; every install refreshes it to the version just installed.
if($env:ARDUI_INSTALL_NO_SHORTCUT -ne '1'){
    $menuLink=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'ArdUi.lnk'
    $menuExe=Join-Path $destination 'ArdUi.exe'
    $menu=(New-Object -ComObject WScript.Shell).CreateShortcut($menuLink)
    $menu.TargetPath=$menuExe
    $menu.Arguments=('--data-root "{0}"' -f $data)
    $menu.WorkingDirectory=$destination
    $menu.IconLocation=('{0},0' -f $menuExe)
    $menu.Description='ArdUi'
    $menu.Save()
}
if($env:ARDUI_INSTALL_AUTOSTART -eq '1' -and $env:ARDUI_INSTALL_NO_SHORTCUT -ne '1'){
    $startup=[Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startup 'ArdUi.lnk'))
    $shortcut.TargetPath=$env:ComSpec
    $shortcut.Arguments=('/d /c ""{0}""' -f $launcher)
    $shortcut.WorkingDirectory=$root
    $shortcut.WindowStyle=7
    $shortcut.Description='ArdUi startup'
    $shortcut.Save()
}
if($env:ARDUI_INSTALL_NO_START -ne '1'){
    # ArdUi keeps one instance per data directory: a still-running older version would only be activated by the
    # new launch and keep running. Stop other versions (and their ard.exe) under this installation first.
    Step 82 '停止旧版本'
    $versions=Join-Path $root 'versions'
    Get-CimInstance Win32_Process -Filter "Name='ArdUi.exe' OR Name='ard.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($versions,[StringComparison]::OrdinalIgnoreCase) -and
        -not $_.ExecutablePath.StartsWith($destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Step 94 '启动 ArdUi'
    Start-Process -FilePath $launcher
}
Write-Host ".progress 100"
Write-Host ".info 完成：ArdUi $version 已安装"
Write-Host "ArdUi $version installed and started at $root"
