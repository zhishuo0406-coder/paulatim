$ErrorActionPreference = 'Stop'
$program = Join-Path $PSScriptRoot 'WorkLogSync.exe'
$testRoot = Join-Path $PSScriptRoot ('test-runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$today = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).Date
$yesterday = $today.AddDays(-1).ToString('yyyy-MM-dd')
$results = [Collections.Generic.List[object]]::new()
function Assert-Condition($value, $message) { if (!$value) { throw $message } }
function New-Case($name, $content) {
    $folder = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path $folder | Out-Null
    $source = Join-Path $folder 'source.csv'
    [IO.File]::WriteAllText($source, $content, $utf8)
    [IO.File]::WriteAllText((Join-Path $folder 'config.json'), (@{ source = $source; max_bytes = 16777216 } | ConvertTo-Json), $utf8)
    return $folder
}
function Run-Case($folder) {
    $start = New-Object Diagnostics.ProcessStartInfo($program, ('--config "' + (Join-Path $folder 'config.json') + '"'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Sync test timed out.' }
    return $process.ExitCode
}
function Load-Json($folder, $file) { Get-Content -LiteralPath (Join-Path $folder $file) -Raw -Encoding UTF8 | ConvertFrom-Json }
function Test($name, [scriptblock]$body) {
    try { & $body; $results.Add([pscustomobject]@{ test = $name; passed = $true }) }
    catch { $results.Add([pscustomobject]@{ test = $name; passed = $false; error = $_.Exception.Message }) }
}
$basic = "$yesterday 16:33,测试任务A,90,75`n$yesterday 16:33,测试任务B,55,55"
Test 'windows-subsystem-no-console' {
    $binary = [IO.File]::ReadAllBytes($program); $pe = [BitConverter]::ToInt32($binary, 60)
    Assert-Condition ([BitConverter]::ToUInt16($binary, $pe + 92) -eq 2) 'Executable must use Windows subsystem.'
}
Test 'first-row-and-daily-totals' {
    $folder = New-Case 'basic' $basic
    Assert-Condition ((Run-Case $folder) -eq 0) 'Initial sync failed.'
    $report = Load-Json $folder 'report-data.json'
    Assert-Condition ($report.rows.Count -eq 2 -and $report.daily.actual_minutes -eq 145 -and $report.daily.effective_minutes -eq 130 -and $report.daily.effective_percent -eq 89.7) 'First row or totals incorrect.'
    Assert-Condition ([IO.File]::ReadAllText((Join-Path $folder 'work_log.csv'), $utf8) -ceq $basic) 'Raw bytes changed.'
}
Test 'unchanged-no-rewrite' {
    $folder = Join-Path $testRoot 'basic'; $copy = Join-Path $folder 'work_log.csv'; $before = (Get-Item -LiteralPath $copy).LastWriteTimeUtc
    Assert-Condition ((Run-Case $folder) -eq 0) 'Unchanged sync failed.'
    Assert-Condition ((Get-Item -LiteralPath $copy).LastWriteTimeUtc -eq $before -and !(Load-Json $folder 'sync-status.json').changed) 'Unchanged CSV rewritten.'
}
Test 'update-and-previous-copy' {
    $folder = Join-Path $testRoot 'basic'
    [IO.File]::WriteAllText((Join-Path $folder 'source.csv'), ($basic + "`n$yesterday 17:00,测试,10,8"), $utf8)
    Assert-Condition ((Run-Case $folder) -eq 0) 'Update failed.'
    Assert-Condition ([IO.File]::ReadAllText((Join-Path $folder 'work_log.csv.previous'), $utf8) -ceq $basic) 'Previous good CSV not retained.'
}
$invalidCases = @{ empty = ''; bad_date = 'bad,task,10,8'; bad_number = "$yesterday 09:00,task,abc,8"; negative = "$yesterday 09:00,task,-10,8"; wrong_columns = "$yesterday 09:00,task,10"; unclosed_quote = "$yesterday 09:00,`"unfinished,10,8" }
foreach ($entry in $invalidCases.GetEnumerator()) {
    $invalidName = $entry.Key; $invalidText = $entry.Value
    Test ('reject-and-preserve-' + $invalidName) {
        $folder = New-Case $invalidName $basic; Assert-Condition ((Run-Case $folder) -eq 0) 'Baseline failed.'
        [IO.File]::WriteAllText((Join-Path $folder 'source.csv'), $invalidText, $utf8)
        Assert-Condition ((Run-Case $folder) -ne 0) 'Invalid CSV accepted.'
        $status = Load-Json $folder 'sync-status.json'
        Assert-Condition (!$status.success -and $status.last_success_at_utc -and [IO.File]::ReadAllText((Join-Path $folder 'work_log.csv'), $utf8) -ceq $basic) 'Good data or last success lost.'
    }
}
Test 'quotes-commas-newlines-decimals' {
    $folder = New-Case 'quotes' "$yesterday 09:00,`"中文,含逗号`n和换行`",10.5,8.25"
    Assert-Condition ((Run-Case $folder) -eq 0) 'Quoted CSV rejected.'
    $report = Load-Json $folder 'report-data.json'
    Assert-Condition ($report.rows.Count -eq 1 -and $report.daily.actual_minutes -eq 10.5 -and $report.rows[0].task.Contains("`n")) 'Quoted CSV incorrect.'
}
Test 'header-bom-and-zero-denominator' {
    $folder = New-Case 'header' ([char]0xfeff + "timestamp,task,actual_minutes,effective_minutes`n$yesterday 09:00,task,0,0")
    Assert-Condition ((Run-Case $folder) -eq 0) 'Header/BOM rejected.'
    $report = Load-Json $folder 'report-data.json'
    Assert-Condition ($report.has_header -and $report.rows.Count -eq 1 -and $null -eq $report.daily.effective_percent) 'Header/zero denominator incorrect.'
}
Test 'duplicate-and-overrun-warnings' {
    $row = "$yesterday 09:00,task,10,12"; $folder = New-Case 'warnings' ($row + "`n" + $row)
    Assert-Condition ((Run-Case $folder) -eq 0) 'Warning records rejected.'
    $report = Load-Json $folder 'report-data.json'
    Assert-Condition ($report.rows.Count -eq 2 -and $report.warnings.Count -eq 3) 'Warnings missing or data silently changed.'
}
Test 'missing-source-preserves-copy' {
    $folder = New-Case 'missing' $basic; Assert-Condition ((Run-Case $folder) -eq 0) 'Baseline failed.'
    Move-Item -LiteralPath (Join-Path $folder 'source.csv') -Destination (Join-Path $folder 'source.held')
    Assert-Condition ((Run-Case $folder) -ne 0 -and (Test-Path (Join-Path $folder 'work_log.csv'))) 'Missing source erased copy.'
}
Test 'writer-lock-and-recovery' {
    $folder = New-Case 'writer' $basic; Assert-Condition ((Run-Case $folder) -eq 0) 'Baseline failed.'
    $lease = [IO.File]::Open((Join-Path $folder 'source.csv'), 'Open', 'ReadWrite', 'None')
    try { Assert-Condition ((Run-Case $folder) -ne 0) 'Concurrent writer ignored.' } finally { $lease.Dispose() }
    Assert-Condition ((Run-Case $folder) -eq 0) 'Recovery failed.'
}
Test 'concurrent-instance-and-recovery' {
    $folder = New-Case 'concurrent' $basic; $lease = [IO.File]::Open((Join-Path $folder 'sync.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try { Assert-Condition ((Run-Case $folder) -eq 0 -and !(Test-Path (Join-Path $folder 'work_log.csv'))) 'Concurrent instance wrote data.' } finally { $lease.Dispose() }
    Assert-Condition ((Run-Case $folder) -eq 0) 'Recovery failed.'
}
Test 'invalid-utf8-rejected' {
    $folder = New-Case 'encoding' $basic; [IO.File]::WriteAllBytes((Join-Path $folder 'source.csv'), [byte[]]@(255,255,255))
    Assert-Condition ((Run-Case $folder) -ne 0) 'Invalid UTF8 accepted.'
}
Test 'size-limit-enforced' {
    $folder = New-Case 'limit' $basic
    [IO.File]::WriteAllText((Join-Path $folder 'config.json'), (@{ source = (Join-Path $folder 'source.csv'); max_bytes = 10 } | ConvertTo-Json), $utf8)
    Assert-Condition ((Run-Case $folder) -ne 0) 'Size limit ignored.'
}
Test 'source-destination-collision' {
    $folder = New-Case 'same-path' $basic
    [IO.File]::WriteAllText((Join-Path $folder 'config.json'), (@{ source = (Join-Path $folder 'work_log.csv') } | ConvertTo-Json), $utf8)
    Assert-Condition ((Run-Case $folder) -ne 0) 'Path collision accepted.'
}
Test 'weekly-boundary-and-empty-daily' {
    $in = $today.AddDays(-7).ToString('yyyy-MM-dd'); $outside = $today.AddDays(-8).ToString('yyyy-MM-dd'); $current = $today.ToString('yyyy-MM-dd')
    $folder = New-Case 'boundary' "$in 00:00,inside,10,8`n$outside 23:59,outside,20,10`n$current 00:00,today,30,20"
    Assert-Condition ((Run-Case $folder) -eq 0) 'Boundary fixture failed.'
    $report = Load-Json $folder 'report-data.json'
    Assert-Condition ($report.weekly.record_count -eq 1 -and $report.weekly.actual_minutes -eq 10 -and $report.weekly.days.Count -eq 7 -and $report.daily.record_count -eq 0) 'Period boundary incorrect.'
}
Test 'corrupt-status-recovers-on-failure' {
    $folder = New-Case 'corrupt-status' ''
    [IO.File]::WriteAllText((Join-Path $folder 'sync-status.json'), '{broken', $utf8)
    Assert-Condition ((Run-Case $folder) -ne 0) 'Empty source unexpectedly succeeded.'
    Assert-Condition (!(Load-Json $folder 'sync-status.json').success) 'Corrupt prior status prevented error recording.'
}
Test 'source-never-modified' {
    $folder = New-Case 'readonly-source' $basic; $source = Join-Path $folder 'source.csv'; $before = (Get-Item -LiteralPath $source).LastWriteTimeUtc
    Assert-Condition ((Run-Case $folder) -eq 0) 'Source test failed.'
    Assert-Condition ((Get-Item -LiteralPath $source).LastWriteTimeUtc -eq $before -and [IO.File]::ReadAllText($source, $utf8) -ceq $basic) 'Source modified.'
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
Write-Output ('Evidence: ' + $testRoot)
if (@($results | Where-Object { !$_.passed }).Count -ne 0) { exit 1 }
