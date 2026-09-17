param([string]$ArdPath,[string]$Python='python')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v2.pre1'
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Force tools,dist | Out-Null
    if($ArdPath){
        $source=(Resolve-Path -LiteralPath $ArdPath).Path
        $target=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'tools/ard.exe'))
        if($source -ne $target){Copy-Item -LiteralPath $source -Destination $target -Force}
    }
    if(-not (Test-Path tools/ard.exe -PathType Leaf)){throw 'Pass -ArdPath with the trusted ARD binary.'}
    $ardVersion=(& ./tools/ard.exe --version | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $ardVersion -ne 'ard 2.0.0-pre.6'){throw "ARD 2.0.0-pre.6 is required; found '$ardVersion'."}
    $output=Join-Path $PSScriptRoot "dist/final-$version"
    $obj=Join-Path $PSScriptRoot "dist/obj-$version/"
    $distRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))+[IO.Path]::DirectorySeparatorChar
    if(-not [IO.Path]::GetFullPath($output).StartsWith($distRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid publish output path.'}
    if(Test-Path -LiteralPath $output){
        if((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publish output must not be a directory link.'}
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    dotnet publish ArdUi.csproj -c Release -r win-x64 --self-contained false -o $output `
        -p:PublishSingleFile=true -p:PublishTrimmed=false `
        -p:BaseIntermediateOutputPath=$obj -p:MSBuildProjectExtensionsPath=$obj
    if($LASTEXITCODE -ne 0){throw 'dotnet publish failed.'}
    $exe=Join-Path $output 'ArdUi.exe'
    if(-not (Test-Path $exe -PathType Leaf)){throw 'Publish did not produce ArdUi.exe.'}
    $extra=@(Get-ChildItem -LiteralPath $output -File | Where-Object Name -ne 'ArdUi.exe')
    if($extra.Count){throw 'Framework-dependent single-file publish produced unexpected sidecar files: '+(($extra.Name) -join ', ')}
    foreach($mode in @('self-test','transit-test')){
        $stdout=Join-Path $output ($mode+'.stdout');$stderr=Join-Path $output ($mode+'.stderr')
        $test=Start-Process -FilePath $exe -ArgumentList ('--'+$mode) -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        Get-Content -LiteralPath $stdout;Get-Content -LiteralPath $stderr
        if($test.ExitCode -ne 0){throw "$mode failed."}
    }
    $previousArdPath=$env:ARDUI_ARD_PATH
    try{
        $env:ARDUI_ARD_PATH=(Resolve-Path -LiteralPath 'tools/ard.exe').Path
        & $Python tests/test_transit_server.py
        if($LASTEXITCODE -ne 0){throw 'Directory authorization regression failed.'}
        & $Python tests/integration_v2.py --dll $exe
        if($LASTEXITCODE -ne 0){throw 'Real ARD candidate and failover regression failed.'}
        & $Python tests/integration_v2.py --dll $exe --prototype
        if($LASTEXITCODE -ne 0){throw 'Two-device enrollment regression failed.'}
    }
    finally{$env:ARDUI_ARD_PATH=$previousArdPath}
    $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Published: $exe"
    Write-Host "SHA-256: $hash"
    Write-Host "Size: $((Get-Item $exe).Length) bytes"
}
finally{Pop-Location}
