param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\smtp-transport-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$repo = Split-Path $PSScriptRoot -Parent
$test = Join-Path $OutputDirectory 'SmtpTransportTests.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /main:PaulatimSmtpTransportTests /warn:4 ('/out:' + $test) /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'PaulatimSmtpTransportTests.cs') (Join-Path $repo 'Paulatim\PaulatimSmtp.cs') (Join-Path $repo 'Paulatim\PaulatimDispatch.cs') (Join-Path $repo 'Paulatim\PaulatimFreshness.cs')
if ($LASTEXITCODE -ne 0) { throw 'SMTP transport test build failed.' }
& $test (Join-Path $OutputDirectory 'fixtures')
if ($LASTEXITCODE -ne 0) { throw 'SMTP transport fault checks failed.' }
