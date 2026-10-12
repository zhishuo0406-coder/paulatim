param(
    [Parameter(Mandatory=$true)][string]$Sender,
    [Parameter(Mandatory=$true)][string]$Recipient,
    [string]$CredentialTarget = 'Paulatim/SMTP/QQ',
    [string]$Root = $PSScriptRoot,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
if (![IO.Directory]::Exists($Root)) { throw 'Configuration directory does not exist.' }
foreach ($value in @($Sender,$Recipient)) {
    if ($value -match '[\s\x00-\x1F\x7F]' -or (New-Object Net.Mail.MailAddress($value)).Address -cne $value) { throw 'Use plain mailbox addresses without control characters.' }
}
if (!$Sender.EndsWith('@qq.com',[StringComparison]::OrdinalIgnoreCase) -or $Sender -eq $Recipient) { throw 'Use the approved independent QQ sender.' }
if ($CredentialTarget -notmatch '\APaulatim/SMTP/[A-Za-z0-9._-]{1,64}\z') { throw 'Invalid credential reference.' }
$mailPath = Join-Path $Root 'mail-config.json'
$mail = [IO.File]::ReadAllText($mailPath,[Text.Encoding]::UTF8) | ConvertFrom-Json
if ($mail.recipient -cne $Recipient -or $mail.enabled) { throw 'Prepare a disabled mail configuration for the approved recipient first.' }
if ($ValidateOnly) { 'Local SMTP setup preflight passed; no credential read or write.'; return }
Import-Module (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
$credential = Get-Credential -UserName $Sender -Message 'Paulatim QQ SMTP: use the CLIENT AUTHORIZATION CODE generated in QQ Mail settings. NOT your QQ login password, NOT an SMS code. The code stays in the Windows credential vault.'
if (!$credential -or $credential.UserName -cne $Sender -or $credential.Password.Length -lt 16 -or $credential.Password.Length -gt 256) { throw 'Input cancelled or too short for a QQ client authorization code. Generate the code in QQ Mail settings; do not use a login password or SMS code.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PaulatimCredentialWriter {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct Credential {
        public uint Flags, Type; public string TargetName, Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount; public IntPtr Attributes; public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool Write(ref Credential credential,uint flags);
}
'@
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToCoTaskMemUnicode($credential.Password)
try {
    $native = New-Object PaulatimCredentialWriter+Credential
    $native.Type=1; $native.TargetName=$CredentialTarget; $native.UserName=$Sender; $native.Persist=2
    $native.CredentialBlob=$pointer; $native.CredentialBlobSize=$credential.Password.Length*2
    if (![PaulatimCredentialWriter]::Write([ref]$native,0)) { throw ('Windows credential storage failed: ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeCoTaskMemUnicode($pointer)
    $credential.Password.Dispose()
}
$smtp = @{enabled=$true;sender=$Sender;recipient=$Recipient;credential_target=$CredentialTarget}
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $Root 'smtp-config.json'),($smtp | ConvertTo-Json),$utf8)
$mail | Add-Member -MemberType NoteProperty -Name transport -Value 'qq_smtp' -Force
[IO.File]::WriteAllText($mailPath,($mail | ConvertTo-Json),$utf8)
'QQ authorization code stored locally. SMTP selected; regular mail remains disabled.'
