param(
    [Parameter(Mandatory=$true)][string]$SourceCsv,
    [Parameter(Mandatory=$true)][string]$Recipient,
    [string]$FirstReportDate = ([DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd'))
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceCsv).Path
if (![IO.File]::Exists($source)) { throw 'SourceCsv must be an existing CSV file.' }
if ($source -eq (Join-Path $PSScriptRoot 'work_log.csv')) { throw 'The source and mirror must be different files.' }
if ($Recipient -eq 'your-account@gmail.com' -or $Recipient -match '[\r\n]') { throw 'Provide your own Gmail address.' }
$address = New-Object Net.Mail.MailAddress($Recipient)
if ($address.Address -cne $Recipient) { throw 'Use an email address without a display name.' }
$first = [DateTime]::ParseExact($FirstReportDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
$firstSaturday = $first.AddDays(6 - [int]$first.DayOfWeek)
foreach ($name in @('config.json','mail-config.json')) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name)) { throw 'Existing configuration found. Edit it deliberately; configuration will not be overwritten.' }
}
$utf8 = New-Object Text.UTF8Encoding($false)
$sync = @{ source=$source; max_bytes=16777216 }
$mail = @{ enabled=$false; recipient=$Recipient; first_daily_report=$first.ToString('yyyy-MM-dd'); first_weekly_end=$firstSaturday.ToString('yyyy-MM-dd'); max_attempts_per_period=3; timeout_minutes=10 }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'config.json'), ($sync | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'mail-config.json'), ($mail | ConvertTo-Json), $utf8)
Write-Output 'Configuration created. Mail remains disabled. Build and test before installing tasks.'
