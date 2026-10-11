param([switch]$UpdateExisting)
$ErrorActionPreference = 'Stop'
foreach ($legacyName in @('WorkLogSync-iCloud-to-D', 'WorkLogMail-Dispatch')) {
    if (Get-ScheduledTask -TaskName $legacyName -ErrorAction SilentlyContinue) {
        throw 'Legacy scheduled tasks exist. Use a reviewed migration preserving configuration and receipts; do not create a second installation.'
    }
}
$taskName = 'Paulatim-iCloud-to-D'
$program = Join-Path $PSScriptRoot 'Paulatim.exe'
if (!(Test-Path -LiteralPath $program)) { throw 'Build Paulatim.exe first.' }
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and !$UpdateExisting) { throw 'Task already exists; use -UpdateExisting after reviewing it.' }
if ($existing) {
    $expected = @($program, (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'), (Join-Path $env:SystemRoot 'System32\wscript.exe'))
    if ($existing.Actions.Count -ne 1 -or $existing.Actions[0].Execute -notin $expected -or $existing.Actions[0].WorkingDirectory -ne $PSScriptRoot) {
        throw 'Existing task does not match this project; refusing to overwrite it.'
    }
    Disable-ScheduledTask -TaskName $taskName | Out-Null
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}
$start = New-Object Diagnostics.ProcessStartInfo($program)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$result = [Diagnostics.Process]::Start($start)
if (!$result.WaitForExit(60000)) { $result.Kill(); throw 'Initial synchronization timed out.' }
if ($result.ExitCode -ne 0) { throw 'Initial sync failed; task remains disabled. Inspect sync-status.json.' }
$status = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sync-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$status.success) { throw 'Initial sync status is not successful.' }
$action = New-ScheduledTaskAction -Execute $program -WorkingDirectory $PSScriptRoot
$repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$logon = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($repeat, $logon) -Principal $principal -Settings $settings -Description 'Paulatim v2: native Windows subsystem application; no console or child process. Validated iCloud CSV mirror every minute while signed in.' -Force:$UpdateExisting | Select-Object TaskName,State
