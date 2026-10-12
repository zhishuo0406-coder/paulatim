param(
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [Parameter(Mandatory=$true)][string]$VerifiedRoot,
    [string]$TaskName = 'WorkLogMail-Dispatch',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$RuntimeRoot = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$VerifiedRoot = (Resolve-Path -LiteralPath $VerifiedRoot).Path
if ($RuntimeRoot -eq $VerifiedRoot) { throw 'Use a separate verified staging directory.' }
function Read-Json([string]$root,[string]$name) {
    Get-Content -LiteralPath (Join-Path $root $name) -Raw -Encoding UTF8 | ConvertFrom-Json
}
$mail = Read-Json $RuntimeRoot 'mail-config.json'
$smtp = Read-Json $VerifiedRoot 'smtp-config.json'
$proof = Read-Json $VerifiedRoot 'smtp-verified.json'
$stageMail = Read-Json $VerifiedRoot 'mail-config.json'
if (!$mail.enabled -or $stageMail.enabled -or $stageMail.transport -ne 'qq_smtp' -or
    !$proof.success -or !$proof.gmail_message_id -or $proof.sender -cne $smtp.sender -or
    $proof.recipient -cne $smtp.recipient -or $mail.recipient -cne $smtp.recipient -or
    $stageMail.recipient -cne $mail.recipient) { throw 'Enabled runtime and verified QQ recipient must agree.' }
$source = Read-Json $RuntimeRoot 'config.json'
$stageSource = Read-Json $VerifiedRoot 'config.json'
if ($source.source -cne $stageSource.source) { throw 'Staged verification used a different source.' }
foreach ($name in @('smtp-config.json','smtp-state.json','Paulatim.exe','PaulatimDispatch.exe')) {
    if (Test-Path -LiteralPath (Join-Path $RuntimeRoot $name)) { throw 'Target already has QQ migration files; review before updating.' }
}
$ledger = Read-Json $VerifiedRoot 'smtp-state.json'
$entries = @($ledger.deliveries.psobject.Properties)
if ($ledger.schema_version -ne 1 -or $entries.Count -ne 1 -or
    $entries[0].Name -ne 'verification:qq-smtp-v1' -or $entries[0].Value.phase -ne 'accepted') {
    throw 'Only an accepted verification ledger may be imported; never replace formal delivery history.'
}
Add-Type -AssemblyName System.Web.Extensions
$serializer = New-Object Web.Script.Serialization.JavaScriptSerializer
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $VerifiedRoot 'PaulatimDispatch.exe'))
$adapter = $assembly.GetType('PaulatimSmtp')
$smtpConfig = $serializer.Deserialize(($smtp | ConvertTo-Json),$adapter.GetNestedType('Config'))
$adapter.GetMethod('ValidateConfig').Invoke($null,@($smtpConfig,[string]$mail.recipient)) | Out-Null
# Check the existing vault entry without printing or persisting its contents.
$adapter.GetMethod('ReadCredential').Invoke($null,@($smtpConfig)) | Out-Null
$task = Get-ScheduledTask -TaskName $TaskName
$oldExe = Join-Path $RuntimeRoot 'WorkLogDispatch.exe'
if ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine $oldExe -or
    $task.Actions[0].WorkingDirectory -ine $RuntimeRoot -or $task.Actions[0].Arguments -or
    $task.State -eq 'Disabled' -or $task.State -eq 'Running') { throw 'Expected enabled, idle legacy task owned by this runtime.' }
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$taskUser = $task.Principal.UserId
$taskSid = if ($taskUser -match '^S-1-') { $taskUser } else {
    (New-Object Security.Principal.NTAccount($taskUser)).Translate([Security.Principal.SecurityIdentifier]).Value
}
if ($taskSid -ne $currentUser.User.Value) { throw 'Task and credential vault must use the same Windows user.' }
foreach ($other in @(Get-ScheduledTask | Where-Object { $_.TaskName -ne $TaskName -and $_.TaskName -match '^(WorkLogMail|PaulatimMail)-Dispatch$' })) {
    if ($other.State -ne 'Disabled') { throw 'Another mail task is enabled; refuse a second sender.' }
}
function Check-State {
    $state = Read-Json $RuntimeRoot 'mail-state.json'
    if ($null -eq $state.receipts -or $state.pending_job -or $state.attempts -ne 0 -or $state.next_attempt_at_utc) {
        throw 'Legacy delivery requires reconciliation before cutover.'
    }
}
Check-State
if ($ValidateOnly) { 'QQ migration preflight passed; runtime and tasks unchanged.'; return }
$backup = Join-Path $RuntimeRoot ('backup-qq-cutover-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $backup | Out-Null
$xml = Export-ScheduledTask -TaskName $TaskName
[IO.File]::WriteAllText((Join-Path $backup 'mail-task.xml'),$xml)
$lease = $null
$committed = $false
$safeToEnable = $false
$copied = @()
try {
    Disable-ScheduledTask -TaskName $TaskName | Out-Null
    if ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') { throw 'Sender started during preflight; wait for it to finish.' }
    $lease = [IO.File]::Open((Join-Path $RuntimeRoot 'mail.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Check-State
    foreach ($name in @('config.json','mail-config.json','mail-state.json','WorkLogDispatch.exe','WorkLogSync.exe','work_log.csv','work_log.csv.previous','report-data.json','sync-status.json')) {
        $path = Join-Path $RuntimeRoot $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $backup $name) }
    }
    $receiptHash = (Get-FileHash -LiteralPath (Join-Path $RuntimeRoot 'mail-state.json')).Hash
    foreach ($name in @('Paulatim.exe','PaulatimDispatch.exe','smtp-config.json','smtp-state.json','smtp-verified.json')) {
        $copied += $name
        Copy-Item -LiteralPath (Join-Path $VerifiedRoot $name) -Destination (Join-Path $RuntimeRoot $name)
        if ((Get-FileHash -LiteralPath (Join-Path $VerifiedRoot $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $RuntimeRoot $name)).Hash) { throw 'Copy verification failed.' }
    }
    $mail | Add-Member -MemberType NoteProperty -Name transport -Value 'qq_smtp' -Force
    $configPath = Join-Path $RuntimeRoot 'mail-config.json'
    $temp = $configPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $replaced = $temp + '.previous.tmp'
    try {
        [IO.File]::WriteAllText($temp,($mail | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
        [IO.File]::Replace($temp,$configPath,$replaced)
    } finally {
        foreach ($temporaryPath in @($temp,$replaced)) {
            if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath }
        }
    }
    $action = New-ScheduledTaskAction -Execute (Join-Path $RuntimeRoot 'PaulatimDispatch.exe') -WorkingDirectory $RuntimeRoot
    Set-ScheduledTask -TaskName $TaskName -Action $action | Out-Null
    $updated = Get-ScheduledTask -TaskName $TaskName
    if ($updated.Actions[0].Execute -ine $action.Execute -or (Get-FileHash -LiteralPath (Join-Path $RuntimeRoot 'mail-state.json')).Hash -ne $receiptHash) {
        throw 'Cutover verification failed.'
    }
    $committed = $true
    $safeToEnable = $true
} catch {
    # No sender can run while the task is disabled and the lease is held.
    # Restore configuration/action only; never restore an older receipt ledger.
    if (Test-Path -LiteralPath (Join-Path $backup 'mail-config.json')) {
        Copy-Item -LiteralPath (Join-Path $backup 'mail-config.json') -Destination (Join-Path $RuntimeRoot 'mail-config.json') -Force
    }
    Set-ScheduledTask -TaskName $TaskName -Action $task.Actions | Out-Null
    foreach ($name in $copied) {
        $copiedPath = Join-Path $RuntimeRoot $name
        if (Test-Path -LiteralPath $copiedPath) { Remove-Item -LiteralPath $copiedPath }
    }
    $safeToEnable = $true
    throw
} finally {
    if ($lease) { $lease.Dispose() }
    if ($safeToEnable) { Enable-ScheduledTask -TaskName $TaskName | Out-Null }
}
if ($committed) {
    Start-ScheduledTask -TaskName $TaskName
    'Formal mail switched to QQ SMTP. Original triggers and receipts preserved; backup: ' + $backup
}
