param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\dtests-' + [Guid]::NewGuid().ToString('N').Substring(0,8))))
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'Paulatim'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $project 'Build-Data.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $project 'Build.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $project 'Build-Mail.ps1') -OutputDirectory $OutputDirectory
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /warn:4 ('/out:'+(Join-Path $OutputDirectory 'DataTests.exe')) ('/reference:'+(Join-Path $OutputDirectory 'PaulatimDataCore.dll')) (Join-Path $PSScriptRoot 'PaulatimDataTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Data test harness build failed.' }
& (Join-Path $OutputDirectory 'DataTests.exe') (Join-Path $OutputDirectory 'fixtures')
if ($LASTEXITCODE -ne 0) { throw 'Data foundation tests failed; inspect data-test-results.json.' }
