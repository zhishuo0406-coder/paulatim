param(
    [string]$ProjectRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Paulatim'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\mail-branding-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& (Join-Path $ProjectRoot 'Build-Mail.ps1') -OutputDirectory $OutputDirectory
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object Web.Script.Serialization.JavaScriptSerializer
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'PaulatimDispatch.exe'))
$dispatch = $assembly.GetType('PaulatimDispatch')
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$config = $json.Deserialize('{"recipient":"audit@example.com","first_daily_report":"2026-10-11","first_weekly_end":"2026-10-17"}', $dispatch.GetNestedType('Config'))
$state = [Activator]::CreateInstance($dispatch.GetNestedType('State'))
$due = $dispatch.GetMethod('Due',$flags).Invoke($null,@($config,$state,[DateTimeOffset]::Parse('2026-10-18T08:30:00+08:00')))
$results = @()
function Check([string]$name,[bool]$condition) {
    if (!$condition) { throw ('Branding check failed: ' + $name) }
    $script:results += [pscustomobject]@{name=$name;passed=$true}
}
$dailySuffix = [string][char]0x65e5 + [char]0x62a5 + '] 2026-10-17'
Check 'daily-new-subject-with-legacy-alias-and-stable-key' ($due[0].subject -eq ('[Paulatim' + $dailySuffix) -and $due[0].legacy_subject -eq ('[WorkLog' + $dailySuffix) -and $due[0].key -eq 'daily:2026-10-17')
Check 'weekly-new-subject-with-legacy-alias-and-stable-period' ($due[1].subject.StartsWith('[Paulatim') -and $due[1].legacy_subject.StartsWith('[WorkLog') -and $due[1].key -eq 'weekly:2026-10-17' -and $due[1].start_date -eq '2026-10-11' -and $due[1].end_date -eq '2026-10-17')
$state.receipts.Add('daily:2026-10-17','0123456789abcdef')
$filtered = $dispatch.GetMethod('Due',$flags).Invoke($null,@($config,$state,[DateTimeOffset]::Parse('2026-10-18T08:30:00+08:00')))
Check 'existing-receipt-suppresses-daily-after-rename' ($filtered.Count -eq 1 -and $filtered[0].key -eq 'weekly:2026-10-17')
$bytes = [Text.Encoding]::UTF8.GetBytes('2026-10-11 09:00,Example task,30,20')
$snapshotInput = @{
    rows=@(@{timestamp='2026-10-11 09:00:00';task='Example task';actual_minutes=30;effective_minutes=20})
    raw_csv=[Text.Encoding]::UTF8.GetString($bytes)
    raw_csv_base64=[Convert]::ToBase64String($bytes)
    warnings=@()
    snapshot_at_utc='2026-10-18T00:30:00Z'
    source_modified_at_utc='2026-10-18T00:29:00Z'
}
$snapshot = $json.DeserializeObject(($snapshotInput | ConvertTo-Json -Depth 8))
$mail = $dispatch.GetMethod('BuildMail',$flags).Invoke($null,@($due[1],$snapshot))
Check 'outgoing-mail-uses-only-new-subject' ($mail.subject -eq $due[1].subject -and !$mail.subject.StartsWith('[WorkLog'))
Check 'csv-attachment-remains-byte-identical' ($mail.payload.parts[1].body.base64_url_content -eq [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'branding-results.json') -Encoding UTF8
$results | ConvertTo-Json
