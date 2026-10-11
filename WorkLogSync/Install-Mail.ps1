param([switch]$UpdateExisting)
$ErrorActionPreference = 'Stop'
if ((Get-TimeZone).Id -ne 'China Standard Time') { throw 'This installation expects the verified Beijing Windows time zone.' }
$taskName = 'WorkLogMail-Dispatch'
$program = Join-Path $PSScriptRoot 'WorkLogDispatch.exe'
$configPath = Join-Path $PSScriptRoot 'mail-config.json'
if (!(Test-Path -LiteralPath $program)) { throw 'Build the mail controller first.' }
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$config.enabled) { throw 'Mail configuration is disabled; enable only after end-to-end verification.' }
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and !$UpdateExisting) { throw 'Task already exists; inspect it before updating.' }
if ($existing -and ($existing.Actions.Count -ne 1 -or $existing.Actions[0].Execute -ne $program -or $existing.Actions[0].WorkingDirectory -ne $PSScriptRoot)) { throw 'This task does not belong to the project.' }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $program -WorkingDirectory $PSScriptRoot
$daily = New-ScheduledTaskTrigger -Daily -At '08:00'
$logon = New-ScheduledTaskTrigger -AtLogOn -User $user
$retry = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 12)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($daily,$logon,$retry) -Principal $principal -Settings $settings -Description 'WorkLog mail v3: 08:00 daily, logon catch-up, cheap local retry checks; no console. Uses existing ChatGPT and Gmail sign-in only for due reports.' -Force:$UpdateExisting | Select-Object TaskName,State
Start-ScheduledTask -TaskName $taskName
