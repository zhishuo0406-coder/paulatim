param(
    [Parameter(Mandatory=$true)][string]$ThroughDate,
    [Parameter(Mandatory=$true)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedSha256,
    [switch]$ConfirmCompleteExport,
    [string]$InstallationDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
if (!$ConfirmCompleteExport) { throw 'After checking the phone export and Windows CSV are complete through ThroughDate, explicitly pass -ConfirmCompleteExport. Local stability is insufficient.' }
$through = [DateTime]::ParseExact($ThroughDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
$now = [DateTimeOffset]::UtcNow
if ($through.Date -ge $now.ToOffset([TimeSpan]::FromHours(8)).Date) { throw 'Only closed Beijing dates can be confirmed.' }
$root = (Resolve-Path -LiteralPath $InstallationDirectory).Path
$lease = New-Object IO.FileStream((Join-Path $root 'mail.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $config = Get-Content -LiteralPath (Join-Path $root 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $source = New-Object IO.FileStream($config.source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($source)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
        if ($hash -cne $ExpectedSha256) { throw 'Source changed from the reviewed export; readiness not written.' }
        $ready = @{schema_version=1;source_sha256=$hash;confirmed_through_date=$ThroughDate;confirmed_at_utc=$now.ToString('o')}
        $path = Join-Path $root 'source-ready.json'
        $temp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($ready | ConvertTo-Json))
            $file = New-Object IO.FileStream($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
            if ([IO.File]::Exists($path)) { [IO.File]::Replace($temp,$path,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp,$path) }
        } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
    } finally { $source.Dispose() }
} finally { $lease.Dispose() }
'Source readiness recorded for the reviewed hash through ' + $ThroughDate + '. No mail sent; the next controller ticks still require stable observation.'
