param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is unavailable.' }
$out = Join-Path $OutputDirectory 'WorkLogSync.exe'
& $compiler /nologo /target:winexe /optimize+ /warn:4 ('/out:' + $out) ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')) ('/reference:' + (Join-Path $framework 'Microsoft.VisualBasic.dll')) (Join-Path $PSScriptRoot 'WorkLogSync.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed; scheduled task must remain disabled.' }
$out
