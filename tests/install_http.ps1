param(
    [string]$Uri='https://f.visnova.cn/ardui/install.ps1',
    [string]$ExpectedVersion='v2.pre2',
    [string]$ExpectedSha256=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1){
    throw 'Run this read-only regression in Windows PowerShell 5.1.'
}
if($ExpectedSha256 -and $ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'ExpectedSha256 must be a SHA-256 hex digest.'}

# Preserve precisely the string returned by the real HTTP cmdlet: no file decoding, trimming or BOM removal.
$payload=Microsoft.PowerShell.Utility\Invoke-RestMethod -Uri $Uri -Method Get
if($payload -isnot [string]){throw ('Invoke-RestMethod did not return script text: '+$payload.GetType().FullName)}
$codepoints=@($payload.ToCharArray() | Select-Object -First 12 | ForEach-Object {[int]$_})
$nonAscii=0
foreach($character in $payload.ToCharArray()){if([int]$character -gt 127){$nonAscii++}}
$bom=($codepoints.Count -gt 0 -and $codepoints[0] -in @(65279,65534)) -or
    ($codepoints.Count -ge 3 -and $codepoints[0] -eq 239 -and $codepoints[1] -eq 187 -and $codepoints[2] -eq 191)
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($payload,[ref]$tokens,[ref]$parseErrors)
$first=$null
if($ast.EndBlock -and $ast.EndBlock.Statements.Count -gt 0){$first=$ast.EndBlock.Statements[0]}
$firstKind=if($first){$first.GetType().Name}else{'None'}
$assignment=$first -is [Management.Automation.Language.AssignmentStatementAst] -and
    $first.Left -is [Management.Automation.Language.VariableExpressionAst] -and
    $first.Left.VariablePath.UserPath -eq 'ErrorActionPreference' -and [string]$first.Operator -eq 'Equals'
$match=[regex]::Match($payload,'(?m)^\s*\$version\s*=\s*''([^'']+)''\s*$')
$version=if($match.Success){$match.Groups[1].Value}else{''}
$digest=''
if($nonAscii -eq 0){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$digest=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($payload)))).Replace('-','').ToLowerInvariant()}
    finally{$sha.Dispose()}
}
$failures=[Collections.Generic.List[string]]::new()
if($bom){$failures.Add('BOM or Latin-1-decoded UTF-8 BOM is present at the HTTP entry point')}
if($nonAscii -ne 0){$failures.Add('HTTP script contains non-ASCII characters')}
if($parseErrors.Count -ne 0){$failures.Add('HTTP script contains parser errors')}
if(-not $assignment){$failures.Add('First statement must assign ErrorActionPreference; a BOM-prefixed command can parse without errors')}
if($version -ne $ExpectedVersion){$failures.Add("Unexpected installer version: $version")}
if($ExpectedSha256 -and $digest -ne $ExpectedSha256.ToLowerInvariant()){$failures.Add('HTTP script does not match the expected SHA-256')}
[pscustomobject]@{
    PowerShellVersion=$PSVersionTable.PSVersion.ToString()
    Uri=$Uri
    Version=$version
    Characters=$payload.Length
    FirstCodepoints=$codepoints
    FirstStatement=$firstKind
    IsAscii=($nonAscii -eq 0)
    HasBom=$bom
    ParserErrors=@($parseErrors | ForEach-Object {$_.Message})
    Sha256=$digest
    ExecutedInstaller=$false
    Passed=($failures.Count -eq 0)
} | ConvertTo-Json -Depth 5
if($failures.Count){throw ('HTTP installer validation failed: '+($failures -join '; '))}
Write-Host 'PASS: raw PowerShell 5.1 Invoke-RestMethod response is an ASCII, BOM-free installer with the expected entry statement and version. No installation was executed.'
