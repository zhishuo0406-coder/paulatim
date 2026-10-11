param(
    [string]$ProjectRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Paulatim'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) '_audit\independent')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $ProjectRoot 'Build.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $ProjectRoot 'Build-Mail.ps1') -OutputDirectory $OutputDirectory
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object Web.Script.Serialization.JavaScriptSerializer
$json.MaxJsonLength = 33554432
$utf8 = New-Object Text.UTF8Encoding($false)
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'PaulatimDispatch.exe'))
$dispatch = $assembly.GetType('PaulatimDispatch')
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$dispatch.GetField('Root',$flags).SetValue($null,($OutputDirectory+[IO.Path]::DirectorySeparatorChar))
$configType=$dispatch.GetNestedType('Config')
$stateType=$dispatch.GetNestedType('State')
$requestType=$dispatch.GetNestedType('Request')
$resultType=$dispatch.GetNestedType('Result')
$config=$json.Deserialize('{"enabled":false,"recipient":"audit@example.com","first_daily_report":"2026-10-11","first_weekly_end":"2026-10-17","max_attempts_per_period":3,"timeout_minutes":10}',$configType)
$results=New-Object Collections.Generic.List[object]
function Assert($condition,$detail) { if(!$condition) { throw $detail } }
function Check([string]$name,[scriptblock]$body) {
    try { & $body; $results.Add([pscustomobject]@{name=$name;passed=$true;detail=''}) }
    catch { $results.Add([pscustomobject]@{name=$name;passed=$false;detail=$_.Exception.Message}) }
}
function New-State { return [Activator]::CreateInstance($stateType) }
function Get-Due([string]$when,$state) {
    return ,($dispatch.GetMethod('Due',$flags).Invoke($null,@($config,$state,[DateTimeOffset]::Parse($when))))
}
Check 'late-login-selects-previous-calendar-day' {
    $due=Get-Due '2026-10-12T08:30:00+08:00' (New-State)
    Assert ($due.Count -eq 1 -and $due[0].key -eq 'daily:2026-10-11') 'Incorrect catch-up period'
}
Check 'multiple-days-offline-latest-daily-and-weekly-only' {
    $due=Get-Due '2026-10-20T12:00:00+08:00' (New-State)
    Assert ($due.Count -eq 2 -and $due[0].key -eq 'daily:2026-10-19' -and $due[1].key -eq 'weekly:2026-10-17') 'Unexpected backlog policy'
}
Check 'daily-confirmed-weekly-remains-due' {
    $state=New-State
    $state.receipts.Add('daily:2026-10-17','0123456789abcdef')
    $due=Get-Due '2026-10-18T08:30:00+08:00' $state
    Assert ($due.Count -eq 1 -and $due[0].kind -eq 'weekly') 'Confirmed daily should not repeat'
}
$csv="2026-10-11 09:00,Audit A,10,8`n2026-10-12 09:00,Audit B,20,15`n2026-10-17 09:00,`"<b>&Audit</b>`",30,12`n2026-10-18 09:00,Audit outside,40,30"
$source=Join-Path $OutputDirectory 'synthetic-source.csv'
[IO.File]::WriteAllText($source,$csv,$utf8)
$syncConfig = @{source=$source;max_bytes=16777216} | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'config.json'),$syncConfig,$utf8)
$start=New-Object Diagnostics.ProcessStartInfo((Join-Path $OutputDirectory 'Paulatim.exe'))
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$process=[Diagnostics.Process]::Start($start)
if(!$process.WaitForExit(30000)) { $process.Kill(); throw 'Synthetic sync timed out' }
if($process.ExitCode -ne 0) { throw 'Synthetic sync failed' }
$snapshot=$json.DeserializeObject([IO.File]::ReadAllText((Join-Path $OutputDirectory 'report-data.json')))
$weekly=$json.Deserialize('{"key":"weekly:2026-10-17","kind":"weekly","start_date":"2026-10-11","end_date":"2026-10-17","subject":"audit weekly"}',$requestType)
$mail=$dispatch.GetMethod('BuildMail',$flags).Invoke($null,@($weekly,$snapshot))
$html=$mail.payload.parts[0].body.content
Check 'weekly-report-boundaries-and-independent-totals' {
    Assert ($html.Contains('实际60分钟') -and $html.Contains('有效35分钟') -and $html.Contains('58.3%')) 'Wrong weekly totals'
}
Check 'report-task-text-html-escaped' {
    Assert ($html.Contains('&lt;b&gt;&amp;Audit&lt;/b&gt;') -and !$html.Contains('<b>&Audit</b>')) 'Task text is not escaped'
}
Check 'mail-attachment-keeps-exact-source-bytes' {
    $base64=$mail.payload.parts[1].body.base64_url_content.Replace('-','+').Replace('_','/')
    $base64=$base64.PadRight($base64.Length+((4-$base64.Length%4)%4),'=')
    Assert ([Convert]::ToBase64String([Convert]::FromBase64String($base64)) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($source))) 'Attachment bytes differ'
}
Check 'full-raw-snapshot-in-body-includes-outside-period' {
    Assert ($html.Contains('2026-10-11 09:00,Audit A,10,8') -and $html.Contains('2026-10-18 09:00,Audit outside,40,30')) 'Full raw archive is incomplete'
}
Check 'source-failure-blocks-snapshot-without-sending' {
    [IO.File]::WriteAllText($source,'broken',$utf8)
    $blocked=$false
    try { $dispatch.GetMethod('Snapshot',$flags).Invoke($null,@()) | Out-Null } catch { $blocked=$true }
    Assert ($blocked -and !(Test-Path -LiteralPath (Join-Path $OutputDirectory 'outbox'))) 'Bad source did not block snapshot'
}
Check 'trailing-newline-in-recipient-must-be-rejected' {
    $accepted=$dispatch.GetMethod('ValidRecipient',$flags).Invoke($null,@("audit@example.com`n"))
    Assert (!$accepted) 'Defect: trailing LF is accepted by the recipient regex'
}
Check 'trailing-newline-in-message-id-must-be-rejected' {
    $state=New-State
    $due=Get-Due '2026-10-12T08:30:00+08:00' $state
    $result=$json.Deserialize('{"results":[{"key":"daily:2026-10-11","status":"sent","message_id":"0123456789abcdef\n","detail":"synthetic"}]}',$resultType)
    $rejected=$false
    try { $dispatch.GetMethod('ApplyReceipts',$flags).Invoke($null,@($state,$due,$result)) | Out-Null } catch { $rejected=$true }
    Assert ($rejected -and $state.receipts.Count -eq 0) 'Defect: trailing LF receipt is accepted'
}
Check 'unverified-synthetic-id-is-not-a-server-verification' {
    $state=New-State
    $due=Get-Due '2026-10-12T08:30:00+08:00' $state
    $result=$json.Deserialize('{"results":[{"key":"daily:2026-10-11","status":"sent","message_id":"0123456789abcdef","detail":"synthetic"}]}',$resultType)
    $dispatch.GetMethod('ApplyReceipts',$flags).Invoke($null,@($state,$due,$result)) | Out-Null
    Assert ($state.receipts.Count -eq 0) 'Risk: a syntactically valid synthetic ID is accepted without independent Gmail verification'
}
$text=$results | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'results.json'),$text,$utf8)
$results | Format-Table -AutoSize
Write-Output ('Checks='+$results.Count+'; Passed='+@($results | Where-Object passed).Count+'; Findings='+@($results | Where-Object {!$_.passed}).Count)
if (@($results | Where-Object {!$_.passed}).Count -gt 0) { exit 1 }
