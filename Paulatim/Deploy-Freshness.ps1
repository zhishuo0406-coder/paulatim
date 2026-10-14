param(
    [Parameter(Mandatory=$true)][string]$CandidateExe,
    [Parameter(Mandatory=$true)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedSha256,
    [string]$RuntimeRoot = 'D:\WorkLogSync',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this .NET Framework deployment with Windows PowerShell 5.1 (powershell.exe).' }
$root = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$candidate = (Resolve-Path -LiteralPath $CandidateExe).Path
$target = Join-Path $root 'PaulatimDispatch.exe'
if ($candidate -ieq $target) { throw 'Candidate must be staged outside the active installation.' }
if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSha256) { throw 'Candidate does not match the accepted build.' }
$binary = [IO.File]::ReadAllBytes($candidate)
$pe = [BitConverter]::ToInt32($binary,60)
if ([BitConverter]::ToUInt16($binary,$pe+92) -ne 2) { throw 'Expected no-console Windows executable.' }
$taskName = 'WorkLogMail-Dispatch'
$task = Get-ScheduledTask -TaskName $taskName
if ($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine $target -or $task.Actions[0].WorkingDirectory -ine $root -or $task.Actions[0].Arguments -or [string]$task.State -ne 'Ready') { throw 'Expected enabled, idle mail task owned by this installation.' }
$xmlBefore = Export-ScheduledTask -TaskName $taskName
$lease = New-Object IO.FileStream((Join-Path $root 'mail.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$smtpLease = $null
try {
    $smtpLease = New-Object IO.FileStream((Join-Path $root 'smtp.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $protected = @('config.json','mail-config.json','smtp-config.json','mail-state.json','smtp-state.json')
    $before = @{}
    foreach ($name in $protected) { $before[$name]=(Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash }
    Add-Type -AssemblyName System.Web.Extensions
    $json = New-Object Web.Script.Serialization.JavaScriptSerializer
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($candidate))
    $dispatch = $assembly.GetType('PaulatimDispatch')
    $freshness = $assembly.GetType('PaulatimFreshness')
    if (!$dispatch -or !$freshness) { throw 'Candidate lacks strict source readiness implementation.' }
    $config = $json.Deserialize([IO.File]::ReadAllText((Join-Path $root 'mail-config.json')),$dispatch.GetNestedType('Config'))
    $state = $json.Deserialize([IO.File]::ReadAllText((Join-Path $root 'mail-state.json')),$dispatch.GetNestedType('State'))
    $flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
    if (!$config.enabled -or $config.transport -ne 'qq_smtp' -or $state.pending_job) { throw 'Expected enabled QQ configuration without a pending legacy job.' }
    $due = $dispatch.GetMethod('Due',$flags).Invoke($null,@($config,$state,[DateTimeOffset]::UtcNow))
    if ($due.Count) { throw 'Newly due reports exist; use a separate reviewed held-report cutover.' }
    # Verify snapshot acquisition time controls sending; yesterday's source is valid.
    $blocked = $false
    $gateNow = [DateTimeOffset]::UtcNow
    try { $freshness.GetMethod('ValidateTimes',$flags).Invoke($null,@('2000-01-01T00:00:00Z','2000-01-01T00:00:01Z',$gateNow)) }
    catch { if ($_.Exception.InnerException -is [IO.InvalidDataException]) { $blocked=$true } else { throw } }
    if (!$blocked) { throw 'Old acquired snapshot did not block the candidate.' }
    $freshness.GetMethod('ValidateTimes',$flags).Invoke($null,@('2000-01-01T00:00:00Z',$gateNow.ToString('o'),$gateNow))
    if ($ValidateOnly) { 'Strict readiness deployment preflight passed; no runtime files or tasks changed.'; return }
    $backup = Join-Path $root ('backup-freshness-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-Item -LiteralPath $target -Destination $backup
    foreach ($name in $protected) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $backup }
    [IO.File]::WriteAllText((Join-Path $backup 'mail-task.xml'),$xmlBefore)
    $obsoleteScript = Join-Path $root 'Confirm-SourceReady.ps1'
    $hadObsoleteScript = Test-Path -LiteralPath $obsoleteScript
    if ($hadObsoleteScript) { Copy-Item -LiteralPath $obsoleteScript -Destination $backup }
    try {
        Copy-Item -LiteralPath $candidate -Destination $target -Force
        if ($hadObsoleteScript) { Remove-Item -LiteralPath $obsoleteScript }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSha256) { throw 'Installed build verification failed.' }
        foreach ($name in $protected) { if ((Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash -cne $before[$name]) { throw ('Protected file changed: '+$name) } }
        if ((Export-ScheduledTask -TaskName $taskName) -cne $xmlBefore) { throw 'Task definition changed during deployment.' }
    } catch {
        Copy-Item -LiteralPath (Join-Path $backup 'PaulatimDispatch.exe') -Destination $target -Force
        if ($hadObsoleteScript) { Copy-Item -LiteralPath (Join-Path $backup 'Confirm-SourceReady.ps1') -Destination $obsoleteScript -Force }
        # Never restore stale receipt ledgers as part of binary rollback.
        throw
    }
    'Strict readiness controller deployed. Task definition and protected files unchanged. Backup: '+$backup
} finally { if ($smtpLease) { $smtpLease.Dispose() }; $lease.Dispose() }
