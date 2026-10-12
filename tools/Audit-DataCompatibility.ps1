param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\data-compatibility-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'Paulatim'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $project 'Build.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $project 'Build-Mail.ps1') -OutputDirectory $OutputDirectory
$utf8 = New-Object Text.UTF8Encoding($false)
$results = [Collections.Generic.List[object]]::new()
function Assert($condition,[string]$detail) { if (!$condition) { throw $detail } }
function Probe([string]$name,[string]$finding,[scriptblock]$body) {
    try { & $body; $results.Add([pscustomobject]@{name=$name;reproduced=$true;finding=$finding}) }
    catch { $results.Add([pscustomobject]@{name=$name;reproduced=$false;finding=$finding;error=$_.Exception.Message}) }
}
function Fixture([string]$name,[string]$csv) {
    $dir=Join-Path $OutputDirectory $name
    New-Item -ItemType Directory -Path $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),$csv,$utf8)
    [IO.File]::WriteAllText((Join-Path $dir 'config.json'),(@{source=(Join-Path $dir 'source.csv');max_bytes=16777216}|ConvertTo-Json),$utf8)
    return $dir
}
function Run([string]$dir) {
    $start=New-Object Diagnostics.ProcessStartInfo((Join-Path $OutputDirectory 'Paulatim.exe'),('--config "'+(Join-Path $dir 'config.json')+'"'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $process=[Diagnostics.Process]::Start($start)
    try { if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Synthetic sync timed out.' }; return $process.ExitCode } finally { $process.Dispose() }
}
function Read-Report([string]$dir) { Get-Content -LiteralPath (Join-Path $dir 'report-data.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
$a='2030-01-01 09:00,Audit A,30,20'
$b='2030-01-01 10:00,Audit B,40,35'
$header='timestamp,task,actual_minutes,effective_minutes'
Probe 'legacy-roundtrip' 'Four-column headerless CSV and exact source bytes are preserved.' {
    $dir=Fixture 'roundtrip' ($a+"`n"+$b)
    $hash=(Get-FileHash (Join-Path $dir 'source.csv')).Hash
    Assert ((Run $dir) -eq 0) 'Legacy input failed.'
    Assert ((Get-FileHash (Join-Path $dir 'work_log.csv')).Hash -eq $hash -and (Get-FileHash (Join-Path $dir 'source.csv')).Hash -eq $hash) 'Byte identity changed.'
}
Probe 'no-permanent-record-identity' 'Output schema marker exists; permanent record ID and statistics version do not.' {
    $dir=Fixture 'identity' $a; Assert ((Run $dir) -eq 0) 'Sync failed.'; $report=Read-Report $dir
    Assert ($report.schema_version -eq 2 -and !$report.psobject.Properties['statistics_version'] -and !$report.rows[0].psobject.Properties['record_id']) 'Unexpected identity/version support.'
}
Probe 'header-shifts-record-number' 'Adding a header changes the same record number from 1 to 2.' {
    $dir=Fixture 'header' $a; Assert ((Run $dir) -eq 0) 'Sync failed.'; $before=(Read-Report $dir).rows[0].record_number
    [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),($header+"`n"+$a),$utf8); Assert ((Run $dir) -eq 0) 'Header input failed.'
    Assert ($before -eq 1 -and (Read-Report $dir).rows[0].record_number -eq 2) 'Position behavior differed.'
}
Probe 'reorder-shifts-record-number' 'Reordering valid rows renumbers the same task.' {
    $dir=Fixture 'reorder' ($a+"`n"+$b); Assert ((Run $dir) -eq 0) 'Sync failed.'
    [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),($b+"`n"+$a),$utf8); Assert ((Run $dir) -eq 0) 'Reorder failed.'
    Assert ((Read-Report $dir).rows[1].task -eq 'Audit A' -and (Read-Report $dir).rows[1].record_number -eq 2) 'Position behavior differed.'
}
Probe 'identical-records-retained' 'Exact duplicate field arrays produce a warning; both records remain.' {
    $dir=Fixture 'duplicate' ($a+"`n"+$a); Assert ((Run $dir) -eq 0) 'Duplicates rejected.'; $report=Read-Report $dir
    Assert ($report.rows.Count -eq 2 -and @($report.warnings | Where-Object {$_ -like 'Possible duplicate*'}).Count -eq 1) 'Duplicate behavior differed.'
}
Probe 'equivalent-minutes-not-flagged' 'Equivalent values 30/30.0 do not trigger the exact-fields duplicate warning.' {
    $dir=Fixture 'equivalent' ($a+"`n2030-01-01 09:00,Audit A,30.0,20.0"); Assert ((Run $dir) -eq 0) 'Sync failed.'; $report=Read-Report $dir
    Assert ($report.rows.Count -eq 2 -and @($report.warnings | Where-Object {$_ -like 'Possible duplicate*'}).Count -eq 0) 'Duplicate detection changed.'
}
Probe 'five-columns-rejected' 'Schema extension is rejected and the previous valid mirror survives.' {
    $dir=Fixture 'five-column' $a; Assert ((Run $dir) -eq 0) 'Baseline failed.'; $hash=(Get-FileHash (Join-Path $dir 'work_log.csv')).Hash
    [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),($a+',IELTS'),$utf8)
    Assert ((Run $dir) -eq 1 -and (Get-FileHash (Join-Path $dir 'work_log.csv')).Hash -eq $hash) 'Invalid source replaced valid mirror.'
}
Probe 'previous-backup-overwritten' 'The .previous file rotates; it is not an immutable original archive.' {
    $dir=Fixture 'backup' $a; Assert ((Run $dir) -eq 0) 'Baseline failed.'
    $second=$a+"`n"+$b; [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),$second,$utf8); Assert ((Run $dir) -eq 0) 'Second sync failed.'
    [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),($second+"`n2030-01-01 11:00,Audit C,10,5"),$utf8); Assert ((Run $dir) -eq 0) 'Third sync failed.'
    Assert ([IO.File]::ReadAllText((Join-Path $dir 'work_log.csv.previous')) -ceq $second) 'Rotating backup behavior differed.'
}
Probe 'valid-source-shortening-accepted' 'A nonempty valid CSV can remove prior records without a deletion warning.' {
    $dir=Fixture 'shortening' ($a+"`n"+$b); Assert ((Run $dir) -eq 0) 'Baseline failed.'
    [IO.File]::WriteAllText((Join-Path $dir 'source.csv'),$a,$utf8); Assert ((Run $dir) -eq 0) 'Shortened source rejected.'; $report=Read-Report $dir
    Assert ($report.rows.Count -eq 1 -and @($report.warnings | Where-Object {$_ -match 'delet|remov|truncat'}).Count -eq 0) 'Deletion detection changed.'
}
Probe 'legacy-time-precision-normalized' 'Minute timestamps become :00 seconds; original text exists only in the raw snapshot.' {
    $dir=Fixture 'precision' $a; Assert ((Run $dir) -eq 0) 'Sync failed.'; $report=Read-Report $dir
    Assert ($report.rows[0].timestamp -eq '2030-01-01 09:00:00' -and !$report.rows[0].psobject.Properties['timestamp_precision'] -and $report.raw_csv -ceq $a) 'Timestamp behavior changed.'
}
Add-Type -AssemblyName System.Web.Extensions
$json=New-Object Web.Script.Serialization.JavaScriptSerializer
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'PaulatimDispatch.exe'))
$dispatch=$assembly.GetType('PaulatimDispatch'); $flags=[Reflection.BindingFlags]'Public,NonPublic,Static'
$weekly=$json.Deserialize('{"key":"weekly:2030-01-05","kind":"weekly","start_date":"2029-12-30","end_date":"2030-01-05","subject":"audit weekly"}',$dispatch.GetNestedType('Request'))
Probe 'report-version-not-checked-by-builder' 'BuildMail accepts a changed schema marker when old field names remain; this is a builder probe, not a live dispatch bypass.' {
    $dir=Fixture 'report-schema' ($a+"`n"+$b); Assert ((Run $dir) -eq 0) 'Sync failed.'
    $snapshot=$json.DeserializeObject([IO.File]::ReadAllText((Join-Path $dir 'report-data.json'))); $snapshot['schema_version']=999
    $mail=$dispatch.GetMethod('BuildMail',$flags).Invoke($null,@($weekly,$snapshot))
    Assert ($mail.payload.parts[0].body.content.Contains('70') -and $mail.payload.parts[1].body.base64_url_content) 'Builder rejected marker.'
}
Probe 'weekly-summary-has-separate-boundary-policy' 'Sync weekly is a rolling seven-day window; formal weekly follows Sunday through Saturday.' {
    $dir=Fixture 'weekly-policy' $a; Assert ((Run $dir) -eq 0) 'Sync failed.'; $report=Read-Report $dir
    $today=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).Date
    Assert ($report.weekly.start_date -eq $today.AddDays(-7).ToString('yyyy-MM-dd') -and $report.weekly.end_date -eq $today.AddDays(-1).ToString('yyyy-MM-dd')) 'Sync window changed.'
    $config=$json.Deserialize('{"first_daily_report":"2029-01-01","first_weekly_end":"2029-01-06"}',$dispatch.GetNestedType('Config'))
    $state=[Activator]::CreateInstance($dispatch.GetNestedType('State'))
    $due=$dispatch.GetMethod('Due',$flags).Invoke($null,@($config,$state,[DateTimeOffset]::Parse('2030-01-07T08:30:00+08:00')))
    $week=@($due | Where-Object {$_.kind -eq 'weekly'})[0]
    Assert ($week.start_date -eq '2029-12-30' -and $week.end_date -eq '2030-01-05') 'Formal weekly window changed.'
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'audit-results.json') -Encoding UTF8
$results | Format-Table name,reproduced -AutoSize
'Synthetic audit evidence: '+$OutputDirectory
if (@($results | Where-Object {!$_.reproduced}).Count -ne 0) { exit 1 }
