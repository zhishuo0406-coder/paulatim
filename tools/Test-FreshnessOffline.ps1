param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\freshness-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$test = Join-Path $OutputDirectory 'FreshnessTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /warn:4 ('/out:'+$test) ('/reference:'+(Join-Path $framework 'System.Web.Extensions.dll')) (Join-Path $PSScriptRoot 'PaulatimFreshnessTests.cs') (Join-Path $repo 'Paulatim\PaulatimFreshness.cs') (Join-Path $repo 'Paulatim\PaulatimSmtp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Freshness test build failed.' }
& $test (Join-Path $OutputDirectory 'fixtures')
if ($LASTEXITCODE -ne 0) { throw 'Freshness acceptance failed; inspect fixtures/results.json.' }
& (Join-Path $repo 'Paulatim\Build.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $repo 'Paulatim\Build-Mail.ps1') -OutputDirectory $OutputDirectory
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object Web.Script.Serialization.JavaScriptSerializer
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'PaulatimDispatch.exe'))
$dispatch = $assembly.GetType('PaulatimDispatch')
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$utf8 = New-Object Text.UTF8Encoding($false)
$results = New-Object Collections.Generic.List[object]
function Check([string]$name,[scriptblock]$body) { try { & $body; $results.Add([pscustomobject]@{name=$name;passed=$true;detail=''}) } catch { $results.Add([pscustomobject]@{name=$name;passed=$false;detail=$_.Exception.Message}) } }
function Assert($value) { if (!$value) { throw 'Assertion failed' } }
function Run([string]$folder) {
    $start = New-Object Diagnostics.ProcessStartInfo((Join-Path $folder 'PaulatimDispatch.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $process=[Diagnostics.Process]::Start($start)
    try { if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'Controller timeout' }; return $process.ExitCode } finally { $process.Dispose() }
}
function Write-Json([string]$path,$value) { [IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 8),$utf8) }
$today = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).Date
$midnight = New-Object DateTimeOffset($today,([TimeSpan]::FromHours(8)))
$through = $today.AddDays(-1).ToString('yyyy-MM-dd')
foreach ($transport in @('qq_smtp','gmail_cli')) {
    $folder=Join-Path $OutputDirectory $transport; New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'Paulatim.exe'),(Join-Path $OutputDirectory 'PaulatimDispatch.exe') -Destination $folder
    $source=Join-Path $folder 'source.csv'; [IO.File]::WriteAllText($source,"$through 20:00,Fictional task,30,20",$utf8)
    Write-Json (Join-Path $folder 'config.json') @{source=$source;max_bytes=16777216}
    Write-Json (Join-Path $folder 'mail-config.json') @{enabled=$true;recipient='fictional@example.com';first_daily_report='2026-01-01';first_weekly_end='2099-01-03';max_attempts_per_period=3;timeout_minutes=10;transport=$transport}
    $statePath=Join-Path $folder 'mail-state.json'; Write-Json $statePath @{receipts=@{'daily:2026-01-01'='0123456789abcdef'};attempts=0}
    $before=[IO.File]::ReadAllText($statePath)
    function Assert-Held {
        Assert ((Run $folder) -eq 1)
        $status=Get-Content (Join-Path $folder 'mail-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert ($status.status -eq 'waiting_source' -and [IO.File]::ReadAllText($statePath) -eq $before -and !(Test-Path (Join-Path $folder 'outbox')) -and !(Test-Path (Join-Path $folder 'smtp-state.json')))
    }
    $dispatch.GetField('Root',$flags).SetValue($null,($folder+[IO.Path]::DirectorySeparatorChar))
    Check "$transport-fresh-acquisition-of-yesterday-source-passes" {
        [IO.File]::SetLastWriteTimeUtc($source,$midnight.UtcDateTime.AddSeconds(-1))
        $snapshot=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
        $dispatch.GetMethod('CheckFresh',$flags).Invoke($null,@($snapshot))
        Assert ([DateTimeOffset]::Parse($snapshot['snapshot_at_utc']) -gt $midnight -and [DateTimeOffset]::Parse($snapshot['source_modified_at_utc']) -lt $midnight -and [IO.File]::ReadAllText($statePath) -eq $before)
    }
    Check "$transport-exact-midnight-snapshot-held" {
        $snapshot=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
        $snapshot['snapshot_at_utc']=$midnight.ToString('o')
        $rejected=$false
        try { $dispatch.GetMethod('CheckFresh',$flags).Invoke($null,@($snapshot)) } catch { $rejected=$true }
        Assert ($rejected -and [IO.File]::ReadAllText($statePath) -eq $before -and !(Test-Path (Join-Path $folder 'outbox')))
    }
    Check "$transport-old-snapshot-reacquired-before-send" {
        $snapshot=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
        $snapshot['snapshot_at_utc']=$midnight.AddSeconds(-1).ToString('o')
        [IO.File]::WriteAllText((Join-Path $folder 'report-data.json'),$json.Serialize($snapshot),$utf8)
        $refreshed=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
        $dispatch.GetMethod('CheckFresh',$flags).Invoke($null,@($refreshed))
        Assert ([DateTimeOffset]::Parse($refreshed['snapshot_at_utc']) -gt $midnight)
        Assert ([IO.File]::GetLastWriteTimeUtc($source) -eq $midnight.UtcDateTime.AddSeconds(-1))
    }
    Check "$transport-missing-source-preserves-last-mirror" {
        $mirror=Join-Path $folder 'work_log.csv'; $mirrorHash=(Get-FileHash -LiteralPath $mirror).Hash
        $heldSource=Join-Path $folder 'source-held.csv'; Move-Item -LiteralPath $source -Destination $heldSource
        try { Assert-Held; Assert ((Get-FileHash -LiteralPath $mirror).Hash -eq $mirrorHash) } finally { Move-Item -LiteralPath $heldSource -Destination $source }
    }
    Check "$transport-late-row-refresh-is-eligible-without-manual-confirmation" {
        [IO.File]::AppendAllText($source,"`n$through 23:00,Fictional late task,15,10",$utf8)
        [IO.File]::SetLastWriteTimeUtc($source,$midnight.UtcDateTime.AddSeconds(-1))
        $dispatch.GetField('Root',$flags).SetValue($null,($folder+[IO.Path]::DirectorySeparatorChar))
        $snapshot=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
        $dispatch.GetMethod('CheckFresh',$flags).Invoke($null,@($snapshot))
        Assert ($snapshot['raw_csv'].Contains('Fictional late task') -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($snapshot['raw_csv_base64'])) -eq [IO.File]::ReadAllText($source))
        Assert (!(Test-Path (Join-Path $folder 'source-ready.json')) -and [IO.File]::ReadAllText($statePath) -eq $before)
    }
    if ($transport -eq 'qq_smtp') {
        Check 'fresh-qq-source-reaches-transport-preflight-without-send' {
            # No smtp-config exists: main can reach transport setup but cannot send.
            Run $folder | Out-Null
            $status=Get-Content (Join-Path $folder 'mail-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            Assert ($status.status -eq 'waiting_network' -or $status.detail.Contains('smtp-config.json'))
            Assert (!(Test-Path (Join-Path $folder 'smtp-state.json')) -and [IO.File]::ReadAllText($statePath) -eq $before)
        }
    }
}
$snapshot=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
$request=[Activator]::CreateInstance($dispatch.GetNestedType('Request'))
$request.key='daily:'+$through; $request.kind='daily'; $request.start_date=$through; $request.end_date=$through; $request.subject='[Paulatim test]'
$built=$dispatch.GetMethod('BuildMail',$flags).Invoke($null,@($request,$snapshot))
$frozen=[Activator]::CreateInstance($assembly.GetType('PaulatimSmtp').GetNestedType('Mail'))
$frozen.html=$built.payload.parts[0].body.content
Check 'actual-frozen-payload-with-today-acquisition-passes' { $dispatch.GetMethod('CheckFrozenFresh',$flags).Invoke($null,@($frozen)) }
Check 'same-csv-frozen-payload-with-yesterday-acquisition-blocks' {
    $frozen.html=$built.payload.parts[0].body.content.Replace($snapshot['snapshot_at_utc'],$midnight.AddSeconds(-1).ToString('o'))
    $blocked=$false; try{$dispatch.GetMethod('CheckFrozenFresh',$flags).Invoke($null,@($frozen))}catch{$blocked=$true}
    Assert $blocked
}
Check 'frozen-payload-with-missing-acquisition-metadata-blocks' {
    $frozen.html='<p>No acquisition metadata</p>'
    $blocked=$false; try{$dispatch.GetMethod('CheckFrozenFresh',$flags).Invoke($null,@($frozen))}catch{$blocked=$true}
    Assert $blocked
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'integration-results.json') -Encoding UTF8
$results | Format-Table -AutoSize
if (@($results | Where-Object {!$_.passed}).Count) { throw 'Freshness process acceptance failed.' }
