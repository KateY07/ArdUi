param([string]$InstallRoot=$PSScriptRoot)
$ErrorActionPreference='Stop'
$relayRoot=[IO.Path]::GetFullPath($InstallRoot)
$statusPath=Join-Path $relayRoot 'data/status.json'
if(-not (Test-Path -LiteralPath $statusPath)){Write-Host 'ArdTransit has not started yet.';return}
$state=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
$age=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()-[double]$state.lastHeartbeat
$fresh=$state.lastHeartbeat -and $age -ge 0 -and $age -lt 20
$process=Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue
$prefix=[IO.Path]::GetFullPath((Join-Path $relayRoot 'versions'))+[IO.Path]::DirectorySeparatorChar
$owned=$process -and $process.Path -and $process.Path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)
$online=$state.state -eq 'online' -and $fresh -and $owned
Write-Host "ArdTransit $($state.version) | online=$online | state=$($state.state) | active=$($state.active)/$($state.capacity) | limit=$($state.mbps) Mbps"
Write-Host "EndpointId: $($state.endpoint)"
Write-Host "Log: $relayRoot\data\relay.log"
Write-Host "Diagnostics: $relayRoot\data\diagnostics.zip"
