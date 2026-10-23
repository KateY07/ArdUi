<#
ArdFerry: the traffic forwarding node of an ArdUi network, in one step on Windows. Get this script from the network's M and run it
in PowerShell (elevated, to start at boot and add the firewall rule; otherwise it starts at logon):

  & ([scriptblock]::Create((irm https://M-DOMAIN/ardferry.ps1)))

The script fetched from an M joins that M's network: the M's domain is its only setting (ROLES.md). The node makes its own key, finds
M through the domain and registers itself; anyone may run one, and M publishes it only after probing it. A copy of this script from
GitHub has no M filled in: add -M M-DOMAIN.

Options (after the closing parenthesis, e.g. ... ))) -NoUpnp):
  -M DOMAIN          the network's M (default: the M this script came from)
  -NoUpnp            no UPnP / NAT-PMP port mapping on the router
  -Python PATH       the Python 3 to use (default: python.exe on PATH)
  -PipIndex URL      a PyPI mirror for the one Python package (cryptography), e.g. https://mirrors.aliyun.com/pypi/simple/
  -RelayArchive F    an already downloaded official iroh-relay release zip
  -RelayMirror URL   download the official iroh-relay zip from URL/<zip name> only
  -Prefix DIR        install directory (default %LOCALAPPDATA%\ArdFerry)
  -Uninstall         remove the task and the firewall rule (the node key in the install directory is kept)
The official iroh-relay 1.1.0 is downloaded from its GitHub release, or from the M when GitHub cannot be reached, and checked against
the SHA-256 its release publishes either way.
#>
param(
    [string]$M = '@M@',
    [string]$Python,
    [string]$PipIndex,
    [string]$RelayArchive,
    [string]$RelayMirror,
    [string]$Prefix = (Join-Path $env:LOCALAPPDATA 'ArdFerry'),
    [switch]$NoUpnp,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskName = 'ArdFerry'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$admin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Get-NetFirewallRule -DisplayName 'ArdFerry' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$Prefix*" -and $_.Name -match 'python|iroh-relay' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Write-Host "Task and firewall rule removed. Files in $Prefix are kept; delete them by hand (they contain the node key)."
    return
}

if ($M -like '@*') { throw 'Give -M M-DOMAIN (the HTTPS domain of the network''s M).' }
$M = ($M -replace '^https://', '') -replace '/.*$', ''
if ($M -notmatch '^[A-Za-z0-9.:-]+$') { throw "Not a domain: $M" }
$system = if ($Python) { $Python } else { (Get-Command python.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source) }
if (-not $system -or -not (Test-Path -LiteralPath $system) -or $system -like '*\WindowsApps\*') {
    throw 'Python 3 is required: install it from python.org (tick "Add python.exe to PATH"), or give -Python PATH.'
}
Write-Host "ArdFerry joins the network of $M"

# ---- the official relay: the Windows build, verified against the SHA-256 its GitHub release publishes ----
$name = 'iroh-relay-v1.1.0-x86_64-pc-windows-msvc.zip'
$sum = '052bd32704afcb4492a0cd8e619e1bb47c69cbe8ecaa89907c22033634b3da1c'
$work = Join-Path ([IO.Path]::GetTempPath()) ('ardferry-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $zip = Join-Path $work $name
    function Fetch([string]$base) {
        try { Invoke-WebRequest -UseBasicParsing -Uri ($base.TrimEnd('/') + '/' + $name) -OutFile $zip -TimeoutSec 600; $true } catch { $false }
    }
    function Verified { (Test-Path -LiteralPath $zip) -and (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower() -eq $sum }
    $kept = Join-Path $Prefix $name
    if ($RelayArchive) { Copy-Item -LiteralPath $RelayArchive $zip }
    elseif ($RelayMirror) { Write-Host "Downloading $name from $RelayMirror"; [void](Fetch $RelayMirror) }
    elseif (Test-Path -LiteralPath $kept) { Copy-Item -LiteralPath $kept $zip }   # kept by an earlier run; checked again below
    if (-not $RelayArchive -and -not $RelayMirror -and -not (Verified)) {
        Write-Host "Downloading $name"
        if (-not (Fetch 'https://github.com/n0-computer/iroh/releases/download/v1.1.0')) {
            Write-Host "GitHub did not answer; trying the copy on $M"; [void](Fetch "https://$M/ardferry")
        }
    }
    if (-not (Verified)) { throw "Could not get $name matching the official SHA-256 (use -RelayArchive FILE or -RelayMirror URL)." }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'relay') -Force
    $relay = Get-ChildItem (Join-Path $work 'relay') -Recurse -Filter 'iroh-relay.exe' | Select-Object -First 1
    if (-not $relay) { throw "No iroh-relay.exe inside $name." }
    Write-Host "Official iroh-relay verified ($name)"

    # ---- the node: its Python code is part of this script ----
    $payload = @'
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
/Oav8ytX6kK6lpJknjChqgIyUCZta2Z20bE6/vrr8dTqbqyi9BfKbeJyk6vlriCb21DSVwkJ1osfxwmE86G89zf88dMpCQbovk1Y
JUBPL4jx64vLT0A/9BtEALV9OYlJmzFoDalLZ5rSm+tWaFrLTbvZiAffvvhn+ByWqa2bZfz7KNJ7LQaPIJ0f3lUm1dhUjGM0gHm/
He9uhGRVNTPmYHVVFf5qn9BcTFDPmTdY5/TmCkuf3Q51lmpkRc9j71vU3UO21+TH0ya6ENxGhCGbThVxArYyLpntfx9yb+JR+iEj
e2Tm2Js3Du0/kSMzVh/KErtCenDmHPd38JGWst9RisUH4oMaxMmt4M3TO2Eo8IuOfDqlRWFBkwDKolGUohLqaseZ4ccSYa2KLozy
BmnYswouT+GeFgLCAq+YqE3iJveJHkziZgn6Om4l3O6DVRWoDS1cwqjga/Z+PAyOMi3fbpQIbN8uvfr2/CQBl077ycHNUdSkD34x
AruZZzWRPZtowZpMSAK5Jy2jthPAYByfNqYHzhrL1aFLjhPd0Nv5pUa6PvmqH9+otPY5fj4oJtTPXJy/uTmLDhJ/nBbox79g6jvU
YtPtm7pfHs3MS6O4EQWOU51T0hgufVwUqIOv+dmIAVdaI7eV31EhDY0juQSwMgiaCYMGrodt34XWSg9W3ysDo61+252GXdftbuXI
7fx45yyWwHKwE734f8kmfu4Ls51LF4PjZTDqu9dXoLEcbpFBXYdBYbxfhSeTojBCGksLs2Cf290qAWN1P1WjnBzE8G0GXx1/czIV
ZMJtP5Uu4xiUBts1I1YSbRXTLpJIC2n386zTLvyD5lKn13n/+Gy67ak4ZlbDEmE2Ir4YCzvA4bdhEL3HzjGYwI9d43lNJnzHwAwg
PY3yedlmjhMtL1m1UbrGYtwT762dYr9hcsjCgM/Wpm8rZ3r9DsaN1rOUH6U3WXKp1lIguV0Iqyp1j4VfcoHZ4f3ymeqFDw1yWnSP
E4grOkGGgXOfs+CzbAB5Oed+RUNzTqHQ+1TNLC/DyrVnkm7QusX1K0NDa9It2rG/zTdK57xixjzVS8d+a+JA/f7EwTknn3bZMfx7
GmhMdtMgQ48lgacouehyW6AZ37FPRberYeHj5Pssg9tB66ELWL1cmdPlmFuKO8XiA3MpMfz+MGrVdbiu+XMZYNoMujlq2oD6wep0
nnumecdd9FWhqkRjBT9v9R0Oo+WsfXxOGloxejHWCNhj/IITSnfWC7GXsNc03JCfsBd7yXwe6v1jLqjDnHJGCZGzfiQli3EWJl93
RDp87PDTZN9bc/aplpC6H4XcTxxt01QCi3GNNsmQ+1sCX3kjYiXUFGdYuzbTrOl7lUlRnwLkVOFzVm2VFras4+epkR6m9Oj9WYp7
QE/QnDtqoLon4Kf0K9PS4LziIrjLlbJvVUs/1L20HpzNNpzBgMyZaV4MeqLzn2T+ALlJQ+v9cO8XtjG/el0dctK3hiEB0dp7lop8
40Xz1qS+Lv4LUEsBAhQAFAAAAAgAAAAhAIMM5xcLFgAA2UMAAAcAAAAAAAAAAAAAAIABAAAAAG5vZGUucHlQSwECFAAUAAAACAAA
ACEAvj1+0AMSAABWOgAACgAAAAAAAAAAAAAAgAEwFgAAbWFwcGluZy5weVBLAQIUABQAAAAIAAAAIQB1+0R9mg0AAIwpAAAJAAAA
AAAAAAAAAACAAVsoAABoZWFsdGgucHlQSwECFAAUAAAACAAAACEAsxurGOcJAABeHQAAEQAAAAAAAAAAAAAAgAEcNgAAZmVycnlf
aWRlbnRpdHkucHlQSwUGAAAAAAQABADjAAAAMkAAAAAA
'@
    $nodeZip = Join-Path $work 'node.zip'
    [IO.File]::WriteAllBytes($nodeZip, [Convert]::FromBase64String(($payload -replace '\s', '')))
    Expand-Archive -LiteralPath $nodeZip -DestinationPath (Join-Path $work 'node') -Force

    # Before v5.5 the node was installed as "ArdRelayNode": stop it so the two do not fight over the relay port and the mapping.
    if (Get-ScheduledTask -TaskName 'ArdRelayNode' -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName 'ArdRelayNode' -Confirm:$false
        Get-NetFirewallRule -DisplayName 'ARD relay node' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        $old = Join-Path $env:LOCALAPPDATA 'ArdRelayNode'
        Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$old*" -and $_.Name -match 'python|iroh-relay' } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        Write-Host "Stopped the old ArdRelayNode task (its files in $old are kept)."
    }
    # A re-run replaces the program files: stop the running node first.
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$Prefix*" -and $_.Name -match 'python|iroh-relay' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    New-Item -ItemType Directory -Force -Path (Join-Path $Prefix 'data') | Out-Null
    Copy-Item (Join-Path $work 'node\*.py') $Prefix -Force
    Copy-Item $relay.FullName (Join-Path $Prefix 'iroh-relay.exe') -Force
    Copy-Item -LiteralPath $zip $kept -Force   # re-runs need no download
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

# its own Python with the one package it needs (cryptography: its key, certificate and signed registrations)
$venv = Join-Path $Prefix 'venv'
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) { & $system -m venv $venv; if ($LASTEXITCODE) { throw 'python -m venv failed.' } }
$index = if ($PipIndex) { @('--index-url', $PipIndex) } else { @() }
& (Join-Path $venv 'Scripts\python.exe') -m pip install --quiet --no-cache-dir --disable-pip-version-check @index cryptography
if ($LASTEXITCODE) { throw 'Could not install the Python package cryptography (try -PipIndex URL).' }
$config = [ordered]@{ m = $M; data = (Join-Path $Prefix 'data'); relay_port = 18443; upnp = (-not $NoUpnp)
    official_relay_sha256 = (Get-FileHash (Join-Path $Prefix 'iroh-relay.exe') -Algorithm SHA256).Hash.ToLower() }
# node.py parses with json.loads, which rejects a UTF-8 BOM, so write without one.
[IO.File]::WriteAllText((Join-Path $Prefix 'node.json'), ([pscustomobject]$config | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
# Restrict the node's key to this user, Administrators and SYSTEM (the boot-time task runs as SYSTEM).
icacls (Join-Path $Prefix 'data') /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)(F)" 'Administrators:(OI)(CI)(F)' 'SYSTEM:(OI)(CI)(F)' | Out-Null

$python = Join-Path $venv 'Scripts\pythonw.exe'
$arguments = "-u `"$Prefix\node.py`" --config `"$Prefix\node.json`" --official-relay `"$Prefix\iroh-relay.exe`""
$action = New-ScheduledTaskAction -Execute $python -Argument $arguments -WorkingDirectory $Prefix
$trigger = if ($admin) { New-ScheduledTaskTrigger -AtStartup } else { New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME }
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
$runAs = if ($admin) { New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest } else { New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive }
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $runAs -Force | Out-Null
if ($admin) {
    Get-NetFirewallRule -DisplayName 'ArdFerry' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'ArdFerry' -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort 18443 -Program (Join-Path $Prefix 'iroh-relay.exe') | Out-Null
} else {
    Write-Warning 'Not elevated: it starts at logon, and no firewall rule was added. Allow inbound TCP 18443 for iroh-relay.exe if Windows asks.'
}
$status = Join-Path $Prefix 'data\status.json'
Remove-Item -LiteralPath $status -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName $taskName
Write-Host 'Waiting for the start-up self-check (up to 100 s)...'
for ($i = 0; $i -lt 50; $i++) {
    Start-Sleep -Seconds 2
    if (Test-Path $status) {
        $report = Get-Content $status -Raw | ConvertFrom-Json
        if ($report.uptime_s -gt 15 -and $report.families.ipv4.verdict -ne 'probing' -and $report.families.ipv6.verdict -ne 'probing') { break }
    }
}
if (Test-Path $status) {
    $report = Get-Content $status -Raw | ConvertFrom-Json
    Write-Host ("SELF-CHECK RESULT overall={0} ipv4={1} ipv6={2}" -f $report.overall, $report.families.ipv4.verdict, $report.families.ipv6.verdict)
    foreach ($family in 'ipv4', 'ipv6') { if ($report.families.$family.reason) { Write-Host "  ${family}: $($report.families.$family.reason)" } }
} else { Write-Warning 'No status yet; check that Python can start node.py and that M is reachable.' }
Write-Host ''
Write-Host "Status any time:  & `"$venv\Scripts\python.exe`" `"$Prefix\node.py`" --config `"$Prefix\node.json`" --status"
Write-Host "Remove:           & ([scriptblock]::Create((irm https://$M/ardferry.ps1))) -Uninstall"
