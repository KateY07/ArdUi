param([string]$ArdPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version='v1.pre10'
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
    if(Test-Path $output){Remove-Item -LiteralPath $output -Recurse -Force}
    dotnet publish ArdUi.csproj -c Release -r win-x64 --self-contained false -o $output `
        -p:PublishSingleFile=true -p:PublishTrimmed=false `
        -p:BaseIntermediateOutputPath=$obj -p:MSBuildProjectExtensionsPath=$obj
    if($LASTEXITCODE -ne 0){throw 'dotnet publish failed.'}
    $exe=Join-Path $output 'ArdUi.exe'
    if(-not (Test-Path $exe -PathType Leaf)){throw 'Publish did not produce ArdUi.exe.'}
    $extra=@(Get-ChildItem -LiteralPath $output -File | Where-Object Name -ne 'ArdUi.exe')
    if($extra.Count){throw 'Framework-dependent single-file publish produced unexpected sidecar files: '+(($extra.Name) -join ', ')}
    $test=Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru -NoNewWindow
    if($test.ExitCode -ne 0){throw 'Headless self-test failed.'}
    $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Published: $exe"
    Write-Host "SHA-256: $hash"
    Write-Host "Size: $((Get-Item $exe).Length) bytes"
}
finally{Pop-Location}
