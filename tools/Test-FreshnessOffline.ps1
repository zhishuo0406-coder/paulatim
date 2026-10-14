param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\freshness-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$test = Join-Path $OutputDirectory 'FreshnessTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /warn:4 ('/out:'+$test) ('/reference:'+(Join-Path $framework 'System.Web.Extensions.dll')) (Join-Path $PSScriptRoot 'PaulatimFreshnessTests.cs') (Join-Path $repo 'Paulatim\PaulatimFreshness.cs') (Join-Path $repo 'Paulatim\PaulatimSmtp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Freshness test build failed.' }
& $test (Join-Path $OutputDirectory 'fixtures')
if ($LASTEXITCODE -ne 0) { throw 'Freshness acceptance failed; inspect fixtures/results.json.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $repo 'Paulatim\Build.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $repo 'Paulatim\Build-Mail.ps1') -OutputDirectory $OutputDirectory
$utf8 = New-Object Text.UTF8Encoding($false)
$results = New-Object Collections.Generic.List[object]
function Check([string]$name,[scriptblock]$body) {
    try { & $body; $results.Add([pscustomobject]@{name=$name;passed=$true;detail=''}) }
    catch { $results.Add([pscustomobject]@{name=$name;passed=$false;detail=$_.Exception.Message}) }
}
function Assert($value) { if (!$value) { throw 'Assertion failed' } }
function Reject([scriptblock]$body) { $rejected=$false; try { & $body | Out-Null } catch { $rejected=$true }; Assert $rejected }
function Run([string]$folder) {
    $start = New-Object Diagnostics.ProcessStartInfo((Join-Path $folder 'PaulatimDispatch.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $process=[Diagnostics.Process]::Start($start)
    try { if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'Controller timeout' }; return $process.ExitCode } finally { $process.Dispose() }
}
function Write-Json([string]$path,$value) { [IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 8),$utf8) }
$through = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).Date.AddDays(-1).ToString('yyyy-MM-dd')
foreach ($transport in @('qq_smtp','gmail_cli')) {
    $folder=Join-Path $OutputDirectory $transport; New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'Paulatim.exe'),(Join-Path $OutputDirectory 'PaulatimDispatch.exe') -Destination $folder
    $source=Join-Path $folder 'source.csv'; [IO.File]::WriteAllText($source,"$through 20:00,Fictional task,30,20",$utf8)
    Write-Json (Join-Path $folder 'config.json') @{source=$source;max_bytes=16777216}
    Write-Json (Join-Path $folder 'mail-config.json') @{enabled=$true;recipient='fictional@example.com';first_daily_report='2026-01-01';first_weekly_end='2099-01-03';max_attempts_per_period=3;timeout_minutes=10;transport=$transport}
    $statePath=Join-Path $folder 'mail-state.json'
    Write-Json $statePath @{receipts=@{'daily:2026-01-01'='0123456789abcdef'};attempts=0}
    $before=[IO.File]::ReadAllText($statePath)
    Check "$transport-first-tick-holds-no-send-or-attempt" {
        Assert ((Run $folder) -eq 0)
        $status=Get-Content (Join-Path $folder 'mail-status.json') -Raw | ConvertFrom-Json
        Assert ($status.status -eq 'waiting_source' -and !(Test-Path (Join-Path $folder 'outbox')) -and !(Test-Path (Join-Path $folder 'smtp-state.json')) -and [IO.File]::ReadAllText($statePath) -eq $before)
    }
    $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    function Seed-Stable { Write-Json (Join-Path $folder 'source-observation.json') @{source_sha256=$hash;since_utc=[DateTimeOffset]::UtcNow.AddMinutes(-3).ToString('o');last_seen_utc=[DateTimeOffset]::UtcNow.AddSeconds(-30).ToString('o')} }
    Check "$transport-stable-old-source-missing-confirmation-still-blocks" {
        Seed-Stable; Assert ((Run $folder) -eq 1)
        $status=Get-Content (Join-Path $folder 'mail-status.json') -Raw | ConvertFrom-Json
        Assert ($status.status -eq 'waiting_source' -and $status.detail.Contains('source-ready.json') -and [IO.File]::ReadAllText($statePath) -eq $before)
        Assert (!(Test-Path (Join-Path $folder 'outbox')) -and !(Test-Path (Join-Path $folder 'smtp-state.json')))
    }
    Check "$transport-late-new-row-invalidates-prior-confirmation" {
        Write-Json (Join-Path $folder 'source-ready.json') @{schema_version=1;source_sha256=$hash;confirmed_through_date=$through;confirmed_at_utc=[DateTimeOffset]::UtcNow.ToString('o')}
        [IO.File]::AppendAllText($source,"`n$through 23:00,Fictional late task,15,10",$utf8)
        Assert ((Run $folder) -eq 0)
        $snapshot=Get-Content (Join-Path $folder 'report-data.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert ($snapshot.raw_csv.Contains('Fictional late task') -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($snapshot.raw_csv_base64)) -eq [IO.File]::ReadAllText($source))
        $newHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Json (Join-Path $folder 'source-observation.json') @{source_sha256=$newHash;since_utc=[DateTimeOffset]::UtcNow.AddMinutes(-3).ToString('o');last_seen_utc=[DateTimeOffset]::UtcNow.AddSeconds(-30).ToString('o')}
        Assert ((Run $folder) -eq 1)
        $status=Get-Content (Join-Path $folder 'mail-status.json') -Raw | ConvertFrom-Json
        Assert ($status.status -eq 'waiting_source' -and [IO.File]::ReadAllText($statePath) -eq $before -and !(Test-Path (Join-Path $folder 'outbox')) -and !(Test-Path (Join-Path $folder 'smtp-state.json')))
    }
}
$confirm = Join-Path $repo 'Paulatim\Confirm-SourceReady.ps1'
$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
Check 'confirmation-requires-explicit-owner-assertion' { Reject { & $confirm -ThroughDate $through -ExpectedSha256 $hash -InstallationDirectory $folder } }
Check 'confirmation-rejects-reviewed-hash-mismatch' { Reject { & $confirm -ThroughDate $through -ExpectedSha256 ('0'*64) -ConfirmCompleteExport -InstallationDirectory $folder } }
Check 'confirmation-rejects-unclosed-day' { Reject { & $confirm -ThroughDate ([DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd')) -ExpectedSha256 $hash -ConfirmCompleteExport -InstallationDirectory $folder } }
Check 'confirmation-writes-only-reviewed-source-readiness' {
    & $confirm -ThroughDate $through -ExpectedSha256 $hash -ConfirmCompleteExport -InstallationDirectory $folder | Out-Null
    $ready=Get-Content (Join-Path $folder 'source-ready.json') -Raw | ConvertFrom-Json
    Assert ($ready.source_sha256 -eq $hash -and $ready.confirmed_through_date -eq $through -and [IO.File]::ReadAllText($statePath) -eq $before -and !(Test-Path (Join-Path $folder 'smtp-state.json')))
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'integration-results.json') -Encoding UTF8
$results | Format-Table -AutoSize
if (@($results | Where-Object {!$_.passed}).Count) { throw 'Freshness process/confirmation acceptance failed.' }
