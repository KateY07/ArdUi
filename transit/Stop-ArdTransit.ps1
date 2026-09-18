param([string]$InstallRoot=$PSScriptRoot,[switch]$KeepAutoStart)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$relayRoot=[IO.Path]::GetFullPath($InstallRoot)
$relayData=Join-Path $relayRoot 'data'
if(-not (Test-Path -LiteralPath $relayData -PathType Container)){throw 'ArdTransit data directory not found.'}
[IO.File]::WriteAllText((Join-Path $relayData 'stop.request'),'stop')
$prefix=[IO.Path]::GetFullPath((Join-Path $relayRoot 'versions'))+[IO.Path]::DirectorySeparatorChar
function Get-OwnedRelayProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='ArdTransit.exe'" | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)
    })
}
$watch=[Diagnostics.Stopwatch]::StartNew()
do{
    $owned=@(Get-OwnedRelayProcesses)
    if(-not $owned.Count){break}
    Start-Sleep -Milliseconds 400
}while($watch.Elapsed.TotalSeconds -lt 20)
foreach($process in (Get-OwnedRelayProcesses)){
    $stillOwned=@(Get-OwnedRelayProcesses | Where-Object ProcessId -eq $process.ProcessId)
    if(-not $stillOwned.Count){continue}
    Write-Warning "Stopping remaining ArdTransit process tree $($process.ProcessId)."
    & "$env:SystemRoot\System32\taskkill.exe" /PID $process.ProcessId /T /F | Out-Host
    if($LASTEXITCODE -ne 0 -and @(Get-OwnedRelayProcesses).Count){throw 'Unable to stop this ArdTransit instance.'}
}
if(-not $KeepAutoStart){
    $runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $entry=Get-ItemProperty -LiteralPath $runKey -Name ArdTransit -ErrorAction SilentlyContinue
    if($entry -and $entry.ArdTransit.IndexOf((Join-Path $relayRoot 'Start-ArdTransit.ps1'),[StringComparison]::OrdinalIgnoreCase) -ge 0){
        Remove-ItemProperty -LiteralPath $runKey -Name ArdTransit
    }
}
Write-Host 'ArdTransit stopped. Identity and configuration retained.'
