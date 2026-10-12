param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$migration = Join-Path $PSScriptRoot 'Migrations\AddCategoryV2.cs'
# Hash normalized source text, so checkout newline conversion does not change the migration identity.
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try {
    $sourceBytes = [Text.Encoding]::UTF8.GetBytes(([IO.File]::ReadAllText($migration)).Replace("`r`n","`n"))
    $migrationHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash($sourceBytes)).Replace('-','').ToLowerInvariant()
} finally { $hashAlgorithm.Dispose() }
$metadata = Join-Path $OutputDirectory 'PaulatimDataBuildInfo.generated.cs'
[IO.File]::WriteAllText($metadata,('internal static class PaulatimDataBuildInfo { internal const string Migration12Hash = "'+$migrationHash+'"; }'))
$library = Join-Path $OutputDirectory 'PaulatimDataCore.dll'
& $compiler /nologo /target:library /optimize+ /warn:4 ('/out:'+$library) ('/reference:'+(Join-Path $framework 'System.Web.Extensions.dll')) ('/reference:'+(Join-Path $framework 'Microsoft.VisualBasic.dll')) (Join-Path $PSScriptRoot 'PaulatimData.cs') $migration $metadata
if ($LASTEXITCODE -ne 0) { throw 'Data library build failed.' }
$program = Join-Path $OutputDirectory 'PaulatimData.exe'
& $compiler /nologo /target:exe /optimize+ /warn:4 ('/out:'+$program) ('/reference:'+$library) (Join-Path $PSScriptRoot 'PaulatimDataCli.cs')
if ($LASTEXITCODE -ne 0) { throw 'Data CLI build failed.' }
$program
