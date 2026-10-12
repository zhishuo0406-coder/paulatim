param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\smtp-tests-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$repo = Split-Path $PSScriptRoot -Parent
& (Join-Path $repo 'Paulatim\Build-Mail.ps1') -OutputDirectory $OutputDirectory
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object Web.Script.Serialization.JavaScriptSerializer
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'PaulatimDispatch.exe'))
$smtp = $assembly.GetType('PaulatimSmtp')
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$configType = $smtp.GetNestedType('Config')
$mailType = $smtp.GetNestedType('Mail')
$ledgerType = $smtp.GetNestedType('Ledger')
$config = $json.Deserialize('{"enabled":true,"sender":"sender@qq.com","recipient":"recipient@example.com","credential_target":"Paulatim/SMTP/TEST"}',$configType)
$base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('2026-10-11 09:00,Example task,30,20'))
function New-Mail([string]$key='daily:2026-10-11') {
    return $smtp.GetMethod('Freeze',$flags).Invoke($null,@($key,$config,'[Paulatim test]','<p>fictional snapshot</p>',$base64))
}
function New-Folder([string]$name) { $path=Join-Path $OutputDirectory $name; New-Item -ItemType Directory -Path $path | Out-Null; return $path }
function Deliver([string]$root,$mail,[Action[object]]$action) { return $smtp.GetMethod('Deliver',$flags).Invoke($null,@($root,$mail,$action)) }
function Assert($condition,$message) { if (!$condition) { throw $message } }
function Must-Reject([scriptblock]$operation) { $rejected=$false; try { & $operation | Out-Null } catch { $rejected=$true }; Assert $rejected 'Expected rejection' }
$results = New-Object Collections.Generic.List[object]
function Check([string]$name,[scriptblock]$operation) { try { & $operation; $results.Add([pscustomobject]@{name=$name;passed=$true}) } catch { $results.Add([pscustomobject]@{name=$name;passed=$false;detail=$_.Exception.Message}) } }
$global:PaulatimSmtpTestCalls = 0
$send = [Action[object]]{param($mail); $global:PaulatimSmtpTestCalls++}
$fail = [Action[object]]{param($mail); $global:PaulatimSmtpTestCalls++; throw 'Synthetic interrupted send'}
Add-Type -TypeDefinition 'using System; using System.Net.Mail; public static class PaulatimSmtpTestFaults { public static void Refuse(object mail) { throw new SmtpException((SmtpStatusCode)535,"Synthetic authentication refusal"); } }'
$refuse = [Action[object]][Delegate]::CreateDelegate([Action[object]],[PaulatimSmtpTestFaults].GetMethod('Refuse'))
Check 'valid-independent-qq-config' { $smtp.GetMethod('ValidateConfig',$flags).Invoke($null,@($config,'recipient@example.com')) }
Check 'control-characters-in-address-rejected' { Assert (!$smtp.GetMethod('AddressValid',$flags).Invoke($null,@("recipient@example.com`n"))) 'Accepted newline' }
Check 'recipient-mismatch-rejected-before-send' { Must-Reject { $smtp.GetMethod('ValidateConfig',$flags).Invoke($null,@($config,'another@example.com')) } }
Check 'non-qq-sender-rejected' { $bad=$json.Deserialize('{"enabled":true,"sender":"sender@outlook.com","recipient":"recipient@example.com","credential_target":"Paulatim/SMTP/TEST"}',$configType); Must-Reject { $smtp.GetMethod('ValidateConfig',$flags).Invoke($null,@($bad,'recipient@example.com')) } }
Check 'same-sender-and-recipient-rejected' { $bad=$json.Deserialize('{"enabled":true,"sender":"sender@qq.com","recipient":"sender@qq.com","credential_target":"Paulatim/SMTP/TEST"}',$configType); Must-Reject { $smtp.GetMethod('ValidateConfig',$flags).Invoke($null,@($bad,'sender@qq.com')) } }
Check 'message-id-stable-for-same-period' { Assert ((New-Mail).message_id -eq (New-Mail).message_id) 'Unstable ID' }
Check 'message-id-distinct-for-different-period' { Assert ((New-Mail).message_id -ne (New-Mail 'weekly:2026-10-17').message_id) 'ID collision' }
Check 'bad-subject-rejected-before-intent' { Must-Reject { $smtp.GetMethod('Freeze',$flags).Invoke($null,@('daily:2026-10-11',$config,"[Paulatim]`nBcc:other@example.com",'<p>test</p>',$base64)) } }
Check 'invalid-attachment-rejected-before-intent' { Must-Reject { $smtp.GetMethod('Freeze',$flags).Invoke($null,@('daily:2026-10-11',$config,'[Paulatim]','<p>test</p>','not-base64')) } }
Check 'accepted-delivery-not-repeated' {
    $root=New-Folder 'accepted'; $mail=New-Mail; $global:PaulatimSmtpTestCalls=0
    $id=Deliver $root $mail $send; $mail.html='<p>changed snapshot</p>'
    Assert ((Deliver $root $mail $send) -eq $id -and $global:PaulatimSmtpTestCalls -eq 1) 'Repeated accepted send'
    $ledger=Get-Content (Join-Path $root 'smtp-state.json') -Raw | ConvertFrom-Json
    Assert ($ledger.deliveries.'daily:2026-10-11'.phase -eq 'accepted' -and $null -eq $ledger.deliveries.'daily:2026-10-11'.frozen) 'Accepted state retained full snapshot or wrong phase'
}
Check 'uncertain-send-blocks-blind-retry' {
    $root=New-Folder 'uncertain'; $mail=New-Mail; $global:PaulatimSmtpTestCalls=0
    Must-Reject { Deliver $root $mail $fail }; Must-Reject { Deliver $root $mail $send }
    Assert ($global:PaulatimSmtpTestCalls -eq 1) 'Retried uncertain send'
}
Check 'authentication-refusal-recorded-before-safe-retry' {
    $root=New-Folder 'authentication'; $mail=New-Mail
    Must-Reject { Deliver $root $mail $refuse }
    $ledger=Get-Content (Join-Path $root 'smtp-state.json') -Raw | ConvertFrom-Json
    Assert ($ledger.deliveries.'daily:2026-10-11'.phase -eq 'prepared' -and $ledger.deliveries.'daily:2026-10-11'.smtp_status_code -eq 535) 'Authentication refusal not classified'
    $global:PaulatimSmtpTestCalls=0; Deliver $root $mail $send | Out-Null
    Assert ($global:PaulatimSmtpTestCalls -eq 1) 'Safe prepared retry did not use one send'
}
Check 'crash-after-intent-blocks-send' {
    $root=New-Folder 'attempting'; $mail=New-Mail
    $data=@{schema_version=1;deliveries=@{'daily:2026-10-11'=@{phase='attempting';message_id=$mail.message_id;frozen=$mail}}}
    [IO.File]::WriteAllText((Join-Path $root 'smtp-state.json'),($data|ConvertTo-Json -Depth 8))
    $global:PaulatimSmtpTestCalls=0; Must-Reject { Deliver $root $mail $send }
    Assert ($global:PaulatimSmtpTestCalls -eq 0) 'Sent after ambiguous crash'
}
Check 'prepared-retry-uses-frozen-snapshot' {
    $root=New-Folder 'prepared'; $old=New-Mail; $data=@{schema_version=1;deliveries=@{'daily:2026-10-11'=@{phase='prepared';message_id=$old.message_id;frozen=$old}}}
    [IO.File]::WriteAllText((Join-Path $root 'smtp-state.json'),($data|ConvertTo-Json -Depth 8))
    $new=New-Mail; $new.html='<p>changed snapshot</p>'; $global:PaulatimSmtpFrozenHtml=''
    $capture=[Action[object]]{param($mail);$global:PaulatimSmtpFrozenHtml=$mail.html}
    Deliver $root $new $capture | Out-Null; Assert ($global:PaulatimSmtpFrozenHtml -eq $old.html) 'Rebuilt prepared request'
}
Check 'corrupt-ledger-preserved-no-send' {
    $root=New-Folder 'corrupt'; [IO.File]::WriteAllText((Join-Path $root 'smtp-state.json'),'{broken'); $global:PaulatimSmtpTestCalls=0
    Must-Reject { Deliver $root (New-Mail) $send }
    Assert ($global:PaulatimSmtpTestCalls -eq 0 -and [IO.File]::ReadAllText((Join-Path $root 'smtp-state.json')) -eq '{broken') 'Corrupt history reset'
}
Check 'unknown-phase-rejected-no-send' {
    $root=New-Folder 'unknown'; $mail=New-Mail; $data=@{schema_version=1;deliveries=@{'daily:2026-10-11'=@{phase='unknown';message_id=$mail.message_id;frozen=$mail}}}
    [IO.File]::WriteAllText((Join-Path $root 'smtp-state.json'),($data|ConvertTo-Json -Depth 8)); $global:PaulatimSmtpTestCalls=0
    Must-Reject { Deliver $root $mail $send }; Assert ($global:PaulatimSmtpTestCalls -eq 0) 'Sent unknown phase'
}
Check 'concurrent-ledger-owner-blocks-send' {
    $root=New-Folder 'locked'; $lock=New-Object IO.FileStream((Join-Path $root 'smtp.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { $global:PaulatimSmtpTestCalls=0; Must-Reject { Deliver $root (New-Mail) $send }; Assert ($global:PaulatimSmtpTestCalls -eq 0) 'Concurrent send' } finally { $lock.Dispose() }
}
Check 'partial-success-does-not-repeat-first-period' {
    $root=New-Folder 'partial'; $daily=New-Mail; $weekly=New-Mail 'weekly:2026-10-17'; $global:PaulatimSmtpTestCalls=0
    Deliver $root $daily $send | Out-Null; Must-Reject { Deliver $root $weekly $fail }; Deliver $root $daily $send | Out-Null
    Assert ($global:PaulatimSmtpTestCalls -eq 2) 'Repeated first period'
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
'SMTP offline checks=' + $results.Count + '; passed=' + @($results|Where-Object passed).Count
if (@($results|Where-Object {!$_.passed}).Count) { exit 1 }
