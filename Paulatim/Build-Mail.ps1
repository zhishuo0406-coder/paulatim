param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$out = Join-Path $OutputDirectory 'PaulatimDispatch.exe'
& $compiler /nologo /target:winexe /optimize+ /warn:4 ('/out:' + $out) ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')) (Join-Path $PSScriptRoot 'PaulatimDispatch.cs') (Join-Path $PSScriptRoot 'PaulatimSmtp.cs') (Join-Path $PSScriptRoot 'PaulatimFreshness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mail-controller build failed.' }
$out
