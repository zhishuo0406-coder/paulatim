param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
function Run-Hidden([string]$program, [string]$arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo($program, $arguments)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Test process timed out.' }
    return $process.ExitCode
}
$exe = Join-Path $PSScriptRoot 'WorkLogDispatch.exe'
$testArgument = if ($Offline) { '--self-test-offline' } else { '--self-test' }
if ((Run-Hidden $exe $testArgument) -ne 0) { throw 'Dispatch self-tests failed; see mail-tests.json.' }
$fixture = Join-Path $PSScriptRoot ('mail-test-runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$fixtureExe = Join-Path $fixture 'WorkLogDispatch.exe'
Copy-Item -LiteralPath $exe -Destination $fixtureExe
$fixtureConfig = @{ enabled=$true; recipient='your-account@gmail.com'; first_daily_report='2099-01-01'; first_weekly_end='2099-01-03'; max_attempts_per_period=3; timeout_minutes=10 }
[IO.File]::WriteAllText((Join-Path $fixture 'mail-config.json'), ($fixtureConfig | ConvertTo-Json), $utf8)
$exit = Run-Hidden $fixtureExe ''
$status = Get-Content -LiteralPath (Join-Path $fixture 'mail-status.json') -Raw | ConvertFrom-Json
$noDue = $exit -eq 0 -and $status.status -eq 'idle' -and !(Test-Path -LiteralPath (Join-Path $fixture 'outbox'))
$lock = New-Object IO.FileStream((Join-Path $fixture 'mail.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $concurrent = (Run-Hidden $fixtureExe '') -eq 0 } finally { $lock.Dispose() }
$statePath = Join-Path $fixture 'mail-state.json'
[IO.File]::WriteAllText($statePath, '{broken', $utf8)
$exit = Run-Hidden $fixtureExe ''
$status = Get-Content -LiteralPath (Join-Path $fixture 'mail-status.json') -Raw | ConvertFrom-Json
$preserved = $exit -eq 1 -and $status.status -eq 'failed' -and [IO.File]::ReadAllText($statePath) -eq '{broken'
$bytes = [IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($bytes, 60)
$subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)
$result = [ordered]@{ Fixture=$fixture; NoDueNoModel=$noDue; ConcurrentSkipped=$concurrent; CorruptReceiptsPreserved=$preserved; WindowsSubsystem=$subsystem }
$json = $result | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $fixture 'integration-results.json'), $json, $utf8)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'mail-integration-results.json'), $json, $utf8)
if (!$noDue -or !$concurrent -or !$preserved -or $subsystem -ne 2) { throw 'Dispatch integration check failed.' }
$json
