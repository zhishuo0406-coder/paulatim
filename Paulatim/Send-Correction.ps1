param(
    [Parameter(Mandatory=$true)][string]$DailyDate,
    [Parameter(Mandatory=$true)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedSourceSha256,
    [Parameter(Mandatory=$true)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedControllerSha256,
    [string]$RuntimeRoot='D:\WorkLogSync',
    [switch]$Send
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1.' }
$day=[DateTime]::ParseExact($DailyDate,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)
if ($day -ge [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).Date) { throw 'Correction requires a closed daily period.' }
$root=(Resolve-Path -LiteralPath $RuntimeRoot).Path
$exe=Join-Path $root 'PaulatimDispatch.exe'
if ((Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant() -cne $ExpectedControllerSha256) { throw 'Installed controller differs from accepted build.' }
$lease=New-Object IO.FileStream((Join-Path $root 'mail.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$sourceLease=$null
try {
    Add-Type -AssemblyName System.Web.Extensions
    $json=New-Object Web.Script.Serialization.JavaScriptSerializer
    $json.MaxJsonLength=67108864
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
    $dispatch=$assembly.GetType('PaulatimDispatch'); $smtp=$assembly.GetType('PaulatimSmtp')
    $flags=[Reflection.BindingFlags]'Public,NonPublic,Static'
    $dispatch.GetField('Root',$flags).SetValue($null,($root+[IO.Path]::DirectorySeparatorChar))
    $config=$json.Deserialize([IO.File]::ReadAllText((Join-Path $root 'mail-config.json')),$dispatch.GetNestedType('Config'))
    if (!$config.enabled -or $config.transport -ne 'qq_smtp') { throw 'Expected enabled formal QQ transport.' }
    $dispatch.GetField('Recipient',$flags).SetValue($null,$config.recipient)
    $state=$dispatch.GetMethod('LoadState',$flags).Invoke($null,@())
    $originalKey='daily:'+$DailyDate
    if (!$state.receipts.ContainsKey($originalKey) -or $state.pending_job) { throw 'Original daily receipt must exist, with no pending legacy send.' }
    $oldReceipts=@{}; foreach($key in $state.receipts.Keys){$oldReceipts[$key]=$state.receipts[$key]}
    $sourceConfig=Get-Content (Join-Path $root 'config.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $sourceLease=New-Object IO.FileStream($sourceConfig.source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $snapshot=$dispatch.GetMethod('Snapshot',$flags).Invoke($null,@())
    $dispatch.GetMethod('CheckFresh',$flags).Invoke($null,@($snapshot))
    if ($snapshot['source_sha256'] -cne $ExpectedSourceSha256) { throw 'Source differs from reviewed local download; no send.' }
    $sender=$json.Deserialize([IO.File]::ReadAllText((Join-Path $root 'smtp-config.json')),$smtp.GetNestedType('Config'))
    $smtp.GetMethod('ValidateConfig',$flags).Invoke($null,@($sender,$config.recipient))
    $versionKey=$originalKey+':correction:'+$ExpectedSourceSha256
    $request=[Activator]::CreateInstance($dispatch.GetNestedType('Request'))
    $request.key=$versionKey; $request.kind='daily'; $request.start_date=$DailyDate; $request.end_date=$DailyDate
    $request.subject='[Paulatim日报更正] '+$DailyDate
    $built=$dispatch.GetMethod('BuildMail',$flags).Invoke($null,@($request,$snapshot))
    $html='<p>这是 '+$DailyDate+' 日报的更正版，使用今天重新抓取的本地 iCloud 数据。原日报和原回执保留，本版独立登记。</p>'+$built.payload.parts[0].body.content
    $frozen=$smtp.GetMethod('Freeze',$flags).Invoke($null,@($versionKey,$sender,$request.subject,$html,$snapshot['raw_csv_base64']))
    $summary=[ordered]@{period=$DailyDate;snapshot_at_beijing=([DateTimeOffset]::Parse($snapshot['snapshot_at_utc'])).ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss');snapshot_after_midnight=$true;original_receipt_preserved=$true;record_count=@($snapshot['rows']).Count;attachment_bytes=[Convert]::FromBase64String($snapshot['raw_csv_base64']).Length}
    if (!$Send) { $summary.phase='preflight'; $summary|ConvertTo-Json; return }
    $job=Join-Path $root ('outbox\correction-'+$DailyDate+'-'+$ExpectedSourceSha256)
    New-Item -ItemType Directory -Path $job -Force|Out-Null
    if (!(Test-Path -LiteralPath (Join-Path $job 'request.json'))) { [IO.File]::WriteAllText((Join-Path $job 'request.json'),$json.Serialize($frozen),(New-Object Text.UTF8Encoding($false))) }
    $authorization=$smtp.GetMethod('ReadCredential',$flags).Invoke($null,@($sender))
    try {
        $submit=[Action[object]]{param($mail); $smtp.GetMethod('Send',$flags).Invoke($null,@($mail,$authorization))|Out-Null}
        $guard=[Action[object]]{param($mail)
            $hashArgs=New-Object object[] 1
            $hashArgs[0]=[Convert]::FromBase64String($mail.csv_base64)
            $digest=$assembly.GetType('PaulatimFreshness').GetMethod('Hash').Invoke($null,$hashArgs)
            if($digest -cne $ExpectedSourceSha256){throw 'Frozen correction does not match reviewed source.'}
            $dispatch.GetMethod('CheckFresh',$flags).Invoke($null,@($snapshot))
            $dispatch.GetMethod('CheckFrozenFresh',$flags).Invoke($null,@($mail))
        }
        $id=$smtp.GetMethod('DeliverGuarded',$flags).Invoke($null,@($root,$frozen,$submit,$guard))
    } finally { $authorization=$null }
    $state.receipts[$versionKey]='smtp:'+$id
    foreach($key in $oldReceipts.Keys){if($state.receipts[$key] -cne $oldReceipts[$key]){throw 'Original receipt unexpectedly changed.'}}
    $dispatch.GetMethod('SaveState',$flags).Invoke($null,@($state))
    $summary.phase='smtp_accepted'
    [IO.File]::WriteAllText((Join-Path $job 'result.json'),($summary|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
    $summary|ConvertTo-Json
} finally { if($sourceLease){$sourceLease.Dispose()};$lease.Dispose() }
