param([switch]$Public,[switch]$DownloadPayloads)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $repo ('dist/relay smoke '+[guid]::NewGuid().ToString('N'))
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$oldEntry=Get-ItemProperty -LiteralPath $runKey -Name ArdTransit -ErrorAction SilentlyContinue
$oldAutoStart=if($oldEntry){$oldEntry.ArdTransit}else{$null}
$names=@('ARDTRANSIT_INSTALL_ROOT','ARDTRANSIT_INSTALL_NO_AUTOSTART','ARDTRANSIT_INSTALL_NO_START')
$saved=@{};foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
function Invoke-RelayHelper([string]$name){
    & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $testRoot $name)))) -InstallRoot $testRoot
}
function Assert-Smoke([bool]$condition,[string]$message){if(-not $condition){throw $message}}
try{
    New-Item -ItemType Directory -Force (Join-Path $testRoot 'cache') | Out-Null
    if(-not $DownloadPayloads){
        Copy-Item -LiteralPath (Join-Path $repo 'dist/ArdTransit-v2.pre1-relay1-win-x64.zip') -Destination (Join-Path $testRoot 'cache')
        Copy-Item -LiteralPath (Join-Path $repo 'tools/ard.exe') -Destination (Join-Path $testRoot 'cache/ard-v2.0.0-pre.6.exe')
    }
    $env:ARDTRANSIT_INSTALL_ROOT=$testRoot
    $env:ARDTRANSIT_INSTALL_NO_AUTOSTART=$null;$env:ARDTRANSIT_INSTALL_NO_START=$null
    if($Public){
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        $installer=Invoke-RestMethod 'https://f.visnova.cn/ardui/install-relay.ps1'
        Assert-Smoke ($installer -eq [IO.File]::ReadAllText((Join-Path $repo 'install-relay.ps1'))) 'Public installer differs from verified source'
    }else{$installer=[IO.File]::ReadAllText((Join-Path $repo 'install-relay.ps1'))}
    & ([scriptblock]::Create($installer))
    $statusPath=Join-Path $testRoot 'data/status.json'
    $first=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Smoke ($first.state -eq 'online' -and $first.lastHeartbeat -gt 0) 'Relay did not register and heartbeat to NJ'
    $registered=(Get-ItemProperty -LiteralPath $runKey -Name ArdTransit).ArdTransit
    Assert-Smoke ($registered.Contains((Join-Path $testRoot 'Start-ArdTransit.ps1'))) 'Login startup does not use installed launcher'
    Assert-Smoke ($registered.Contains('-WindowStyle Hidden') -and $registered.Contains('-ExecutionPolicy Bypass')) 'Startup flags missing'
    $identity=Get-FileHash -LiteralPath (Join-Path $testRoot 'data/identity') -Algorithm SHA256
    Write-Host 'PASS: standalone EXE registered and sent a heartbeat to real NJ; hidden login startup installed.'
    & ([scriptblock]::Create($installer))
    $same=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Smoke ($same.pid -eq $first.pid) 'Repeated installation restarted a healthy same-version relay'
    Invoke-RelayHelper 'Status-ArdTransit.ps1'
    Write-Host 'PASS: reinstall reused cache and kept the live process.'
    Invoke-RelayHelper 'Stop-ArdTransit.ps1'
    Assert-Smoke (-not (Get-Process -Id ([int]$first.pid) -ErrorAction SilentlyContinue)) 'Relay process survived stop'
    Assert-Smoke (-not (Get-ItemProperty -LiteralPath $runKey -Name ArdTransit -ErrorAction SilentlyContinue)) 'Stop left login startup enabled'
    Invoke-RelayHelper 'Start-ArdTransit.ps1'
    $restarted=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Smoke ($restarted.state -eq 'online' -and $restarted.endpoint -eq $first.endpoint) 'Restart did not preserve identity and recover online'
    Assert-Smoke ((Get-FileHash -LiteralPath $identity.Path -Algorithm SHA256).Hash -eq $identity.Hash) 'Identity bytes changed'
    Assert-Smoke (Test-Path -LiteralPath (Join-Path $testRoot 'data/diagnostics.zip')) 'Diagnostic package missing'
    Write-Host 'PASS: stop/restart preserves identity, returns online, writes diagnostics, and disables login startup.'
    Write-Host "Evidence: $testRoot"
}
finally{
    if(Test-Path -LiteralPath (Join-Path $testRoot 'Stop-ArdTransit.ps1')){Invoke-RelayHelper 'Stop-ArdTransit.ps1'}
    if($null -ne $oldAutoStart){New-ItemProperty -LiteralPath $runKey -Name ArdTransit -Value $oldAutoStart -PropertyType String -Force | Out-Null}
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
}
