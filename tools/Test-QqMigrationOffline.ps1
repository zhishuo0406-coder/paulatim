$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'Paulatim'
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('_audit\qq-migration-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
# Stub only credential access and task scheduling; all file mutations use real isolated files.
$stub = @'
public static class PaulatimSmtp {
    public class Config { public string sender {get;set;} public string recipient {get;set;} }
    public static void ValidateConfig(Config c,string recipient) { if(c.recipient != recipient) throw new System.Exception(); }
    public static string ReadCredential(Config c) { return "fictional-offline-value"; }
}
'@
Add-Type -TypeDefinition $stub -OutputAssembly (Join-Path $root 'adapter.dll')
function Get-ScheduledTask { param($TaskName) if ($TaskName) { return $global:qqTesttask }; @($global:qqTesttask) + @($global:qqTestother) }
function Export-ScheduledTask { param($TaskName) '<Task>fictional</Task>' }
function Disable-ScheduledTask { param($TaskName) $global:qqTesttask.State='Disabled'; $global:qqTestchanges++ }
function Enable-ScheduledTask { param($TaskName) $global:qqTesttask.State='Ready' }
function Start-ScheduledTask { param($TaskName) $global:qqTeststarts++ }
function New-ScheduledTaskAction { param($Execute,$WorkingDirectory) [pscustomobject]@{Execute=$Execute;WorkingDirectory=$WorkingDirectory;Arguments=$null} }
function Set-ScheduledTask {
    param($TaskName,$Action)
    if ($global:qqTestfailAction) { $global:qqTestfailAction=$false; throw 'Injected task update failure' }
    $global:qqTesttask.Actions=@($Action)
}
function Write-Json($path,$value) { $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8 }
$results=@()
foreach ($scenario in @('preflight','cutover','pending','recipient','second-sender','uncertain-ledger','task-update-failure','locked-sender')) {
    $runtime=Join-Path $root ($scenario+'-runtime'); $stage=Join-Path $root ($scenario+'-stage')
    New-Item -ItemType Directory -Path $runtime,$stage | Out-Null
    $mail=@{enabled=$true;recipient='audit@gmail.com';first_daily_report='2030-01-01';first_weekly_end='2030-01-05';max_attempts_per_period=3;timeout_minutes=10}
    Write-Json (Join-Path $runtime 'mail-config.json') $mail
    $mail.enabled=$false; $mail.transport='qq_smtp'; Write-Json (Join-Path $stage 'mail-config.json') $mail
    foreach($dir in @($runtime,$stage)) { Write-Json (Join-Path $dir 'config.json') @{source='fictional-source.csv'} }
    Write-Json (Join-Path $runtime 'mail-state.json') @{receipts=@{'daily:2030-01-01'='fictional-receipt'};attempts=0;pending_job=$(if($scenario -eq 'pending') {'fictional-job'} else {$null})}
    Write-Json (Join-Path $stage 'smtp-config.json') @{enabled=$true;sender='audit@qq.com';recipient='audit@gmail.com';credential_target='Paulatim/SMTP/QQ'}
    Write-Json (Join-Path $stage 'smtp-verified.json') @{success=$true;gmail_message_id='fictional';sender='audit@qq.com';recipient=$(if($scenario -eq 'recipient') {'different@gmail.com'} else {'audit@gmail.com'})}
    Write-Json (Join-Path $stage 'smtp-state.json') @{schema_version=1;deliveries=@{'verification:qq-smtp-v1'=@{phase=$(if($scenario -eq 'uncertain-ledger') {'uncertain'} else {'accepted'})}}}
    Copy-Item -LiteralPath (Join-Path $root 'adapter.dll') -Destination (Join-Path $stage 'PaulatimDispatch.exe')
    Set-Content -LiteralPath (Join-Path $stage 'Paulatim.exe') -Value 'fictional binary'
    $global:qqTesttask=[pscustomobject]@{TaskName='WorkLogMail-Dispatch';State='Ready';Actions=@(New-ScheduledTaskAction -Execute (Join-Path $runtime 'WorkLogDispatch.exe') -WorkingDirectory $runtime);Principal=@{UserId=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}}
    $global:qqTestother=if($scenario -eq 'second-sender'){[pscustomobject]@{TaskName='PaulatimMail-Dispatch';State='Ready'}}else{$null}
    $global:qqTestchanges=0; $global:qqTeststarts=0; $global:qqTestfailAction=$scenario -eq 'task-update-failure'
    $configHash=(Get-FileHash (Join-Path $runtime 'mail-config.json')).Hash
    $stateHash=(Get-FileHash (Join-Path $runtime 'mail-state.json')).Hash
    $lock=$null
    if($scenario -eq 'locked-sender') { $lock=[IO.File]::Open((Join-Path $runtime 'mail.lock'),'OpenOrCreate','ReadWrite','None') }
    $failed=$false
    $failureDetail=''
    try { & (Join-Path $project 'Migrate-MailToQq.ps1') -RuntimeRoot $runtime -VerifiedRoot $stage -ValidateOnly:($scenario -eq 'preflight') | Out-Null } catch { $failed=$true; $failureDetail=$_.Exception.Message } finally { if($lock){$lock.Dispose()} }
    $expectedFailure=$scenario -notin @('preflight','cutover')
    if($failed -ne $expectedFailure -or (Get-FileHash (Join-Path $runtime 'mail-state.json')).Hash -ne $stateHash -or $global:qqTesttask.State -ne 'Ready') { throw "Failed migration scenario: $scenario ($failureDetail)" }
    if($scenario -eq 'cutover') {
        $config=Get-Content (Join-Path $runtime 'mail-config.json') -Raw | ConvertFrom-Json
        if($config.transport -ne 'qq_smtp' -or !$config.enabled -or $global:qqTeststarts -ne 1 -or $global:qqTesttask.Actions[0].Execute -ne (Join-Path $runtime 'PaulatimDispatch.exe')) { throw 'Cutover failed' }
    } else {
        if((Get-FileHash (Join-Path $runtime 'mail-config.json')).Hash -ne $configHash -or $global:qqTeststarts -ne 0 -or (Test-Path (Join-Path $runtime 'smtp-state.json'))) { throw "Unintended mutation: $scenario" }
    }
    $results += [pscustomobject]@{name=$scenario;passed=$true}
}
$results | ConvertTo-Json
