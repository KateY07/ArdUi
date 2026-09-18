param([string]$InstallRoot=$PSScriptRoot,[int]$WaitSeconds=30)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$relayRoot=[IO.Path]::GetFullPath($InstallRoot)
$relayData=Join-Path $relayRoot 'data'
$current=Get-Content -LiteralPath (Join-Path $relayRoot 'current.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($current.version -notmatch '^v[0-9A-Za-z.\-]+$'){throw 'Invalid ArdTransit version path.'}
$versionRoot=Join-Path (Join-Path $relayRoot 'versions') $current.version
$relayExe=Join-Path $versionRoot 'ArdTransit.exe';$ardExe=Join-Path $versionRoot 'ard.exe'
foreach($item in @(@($relayExe,$current.sha256),@($ardExe,$current.ardSha256))){
    if((Get-FileHash -LiteralPath $item[0] -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item[1]){throw 'ArdTransit/ARD hash mismatch. Run install-relay.ps1 to repair.'}
}
$startLock=[IO.File]::Open((Join-Path $relayRoot 'start.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $running=@(Get-CimInstance Win32_Process -Filter "Name='ArdTransit.exe'" | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.Equals($relayExe,[StringComparison]::OrdinalIgnoreCase)})
    if($running.Count){Write-Host 'ArdTransit is already running.';return}
    $stopRequest=Join-Path $relayData 'stop.request'
    if(Test-Path -LiteralPath $stopRequest){Remove-Item -LiteralPath $stopRequest -Force}
    $config=Get-Content -LiteralPath (Join-Path $relayRoot 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    function Quote-RelayArgument([string]$value){
        if($value -match '["\r\n]'){throw 'Invalid character in relay configuration.'}
        return '"'+$value+'"'
    }
    $arguments=@('--ard',(Quote-RelayArgument $ardExe),'--data',(Quote-RelayArgument $relayData),
        '--server',(Quote-RelayArgument $config.server),'--relay',(Quote-RelayArgument $config.relay),
        '--relay-key',(Quote-RelayArgument $config.relayKey),'--capacity',([int]$config.capacity).ToString(),
        '--mbps',([int]$config.mbps).ToString(),'--diagnostics',(Quote-RelayArgument (Join-Path $relayData 'diagnostics.zip')))
    $launched=Start-Process -FilePath $relayExe -ArgumentList $arguments -WorkingDirectory $relayRoot -WindowStyle Hidden -PassThru
    $started=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();$watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.Elapsed.TotalSeconds -lt $WaitSeconds){
        if($launched.HasExited){throw "ArdTransit exited ($($launched.ExitCode)). See $relayData\relay.log"}
        $statusPath=Join-Path $relayData 'status.json'
        if(Test-Path -LiteralPath $statusPath){
            $status=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $statusProcess=Get-Process -Id ([int]$status.pid) -ErrorAction SilentlyContinue
            if($statusProcess -and $statusProcess.Path -and $statusProcess.Path.Equals($relayExe,[StringComparison]::OrdinalIgnoreCase) -and $status.state -eq 'online' -and $status.lastHeartbeat -ge $started){
                Write-Host "ArdTransit online: capacity=$($status.capacity), limit=$($status.mbps) Mbps"
                Write-Host "EndpointId: $($status.endpoint)"
                return
            }
        }
        Start-Sleep -Milliseconds 400
    }
    Write-Host "ArdTransit started; waiting for network/registration and retrying automatically. Status: $relayData\status.json"
}
finally{$startLock.Dispose()}
