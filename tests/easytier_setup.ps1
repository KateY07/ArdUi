param([Parameter(Mandatory)][string]$Setup,[Parameter(Mandatory)][string]$Version)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$case=Join-Path (Join-Path $PSScriptRoot '..\dist') ('setup-check-'+[guid]::NewGuid().ToString('N'))
$saved=@{}
foreach($name in @('ARDUI_INSTALL_ROOT','ARDUI_INSTALL_NO_START','ARDUI_INSTALL_NO_SHORTCUT')){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
try{
    $env:ARDUI_INSTALL_ROOT=Join-Path $case 'install with spaces'
    $env:ARDUI_INSTALL_NO_START='1';$env:ARDUI_INSTALL_NO_SHORTCUT='1'
    function Install {
        $process=Start-Process -FilePath $Setup -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(120000)){throw "Installer timed out, PID $($process.Id); evidence retained at $case"}
        if($process.ExitCode -ne 0){throw "Installer exit code: $($process.ExitCode)"}
    }
    Install
    $payload=Join-Path $env:ARDUI_INSTALL_ROOT ('versions\'+$Version)
    foreach($file in @('ArdUi.exe','frd\FRD.exe','easytier\easytier-core.exe','easytier\easytier-cli.exe','easytier\Packet.dll','easytier\wintun.dll','easytier\relay.json')){
        if(-not(Test-Path -LiteralPath (Join-Path $payload $file))){throw "Missing installed file: $file"}
    }
    $data=Join-Path $env:ARDUI_INSTALL_ROOT 'data'
    if(-not(Test-Path -LiteralPath (Join-Path $data 'device\identity'))){throw 'Identity not created'}
    [IO.File]::WriteAllText((Join-Path $data 'peers.json'),'{"preserve":true}')
    $hashes=@{}
    Get-ChildItem -LiteralPath $data -File -Recurse | ForEach-Object {$hashes[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash}
    Install
    foreach($file in $hashes.Keys){if((Get-FileHash -LiteralPath $file).Hash -ne $hashes[$file]){throw "Data changed: $file"}}
    if(-not [IO.File]::ReadAllText((Join-Path $env:ARDUI_INSTALL_ROOT 'ArdUi.cmd')).Contains("versions\$Version\ArdUi.exe")){throw 'Launcher version mismatch'}
    $result="PASS: actual EXE first install and reinstall; EasyTier/FRD payload present; $($hashes.Count) data files unchanged; launcher selects $Version."
    [IO.File]::WriteAllText((Join-Path $case 'result.txt'),$result)
    Write-Output $result
    Write-Output "Evidence: $case"
}finally{foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}}
