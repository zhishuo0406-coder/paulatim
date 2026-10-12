using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// SMTP acknowledgement is distinct from proof that the recipient received mail.
internal static class PaulatimSmtp
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 67108864 };
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public sealed class Config {
        public bool enabled { get; set; }
        public string sender { get; set; }
        public string recipient { get; set; }
        public string credential_target { get; set; }
    }
    public sealed class Mail {
        public string key { get; set; }
        public string sender { get; set; }
        public string recipient { get; set; }
        public string subject { get; set; }
        public string html { get; set; }
        public string csv_base64 { get; set; }
        public string message_id { get; set; }
    }
    public sealed class Delivery {
        public string phase { get; set; }
        public string message_id { get; set; }
        public string acknowledged_at_utc { get; set; }
        public string error_type { get; set; }
        public int? smtp_status_code { get; set; }
        public Mail frozen { get; set; }
    }
    public sealed class Ledger {
        public int schema_version { get; set; }
        public Dictionary<string, Delivery> deliveries { get; set; }
        public Ledger() { schema_version = 1; deliveries = new Dictionary<string, Delivery>(); }
    }

    public static bool AddressValid(string value) {
        if (String.IsNullOrWhiteSpace(value) || value.Length > 254) return false;
        foreach (char c in value) if (Char.IsControl(c) || Char.IsWhiteSpace(c)) return false;
        try { return new MailAddress(value).Address == value && Regex.IsMatch(value, "\\A[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}\\z"); }
        catch (FormatException) { return false; }
    }
    public static void ValidateConfig(Config config, string recipient) {
        if (config == null || !config.enabled || !AddressValid(config.sender) || !config.sender.EndsWith("@qq.com", StringComparison.OrdinalIgnoreCase) ||
            !AddressValid(config.recipient) || config.recipient != recipient || config.sender.Equals(recipient, StringComparison.OrdinalIgnoreCase) ||
            String.IsNullOrEmpty(config.credential_target) || !Regex.IsMatch(config.credential_target, "\\APaulatim/SMTP/[A-Za-z0-9._-]{1,64}\\z"))
            throw new InvalidDataException("Invalid enabled QQ sender, approved recipient or credential reference.");
    }
    public static string MessageId(string key, string sender, string recipient) {
        using (var hash = SHA256.Create()) {
            string digest = BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(key + "\n" + sender.ToLowerInvariant() + "\n" + recipient.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
            return "<paulatim." + digest.Substring(0, 32) + "@qq.com>";
        }
    }
    private static void ValidateMail(Mail mail) {
        if (mail == null || String.IsNullOrWhiteSpace(mail.key) || mail.key.Length > 100 || !AddressValid(mail.sender) || !AddressValid(mail.recipient) ||
            String.IsNullOrWhiteSpace(mail.subject) || !mail.subject.StartsWith("[Paulatim", StringComparison.Ordinal) || mail.subject.IndexOfAny(new[] {'\r','\n'}) >= 0 ||
            mail.html == null || mail.csv_base64 == null || mail.message_id != MessageId(mail.key, mail.sender, mail.recipient))
            throw new InvalidDataException("Invalid frozen SMTP message.");
        byte[] csv = Convert.FromBase64String(mail.csv_base64);
        if (csv.Length == 0 || csv.Length > 16777216 || Utf8.GetByteCount(mail.html) + mail.csv_base64.Length > 20000000)
            throw new InvalidDataException("SMTP message is empty or exceeds the report size limit.");
    }
    public static Mail Freeze(string key, Config config, string subject, string html, string csvBase64) {
        var mail = new Mail { key=key, sender=config.sender, recipient=config.recipient, subject=subject, html=html, csv_base64=csvBase64, message_id=MessageId(key,config.sender,config.recipient) };
        ValidateMail(mail);
        return mail;
    }
    public static string Deliver(string root, Mail requested, Action<Mail> send) {
        ValidateMail(requested);
        string path = Path.Combine(root, "smtp-state.json");
        using (var lease = new FileStream(Path.Combine(root, "smtp.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            Ledger ledger;
            if (!File.Exists(path)) ledger = new Ledger();
            else {
                try { ledger = Json.Deserialize<Ledger>(File.ReadAllText(path, Utf8)); }
                catch { throw new InvalidDataException("SMTP ledger is damaged; refusing to reset delivery history."); }
                if (ledger == null || ledger.schema_version != 1 || ledger.deliveries == null) throw new InvalidDataException("Unsupported SMTP ledger.");
            }
            Delivery record;
            if (ledger.deliveries.TryGetValue(requested.key, out record)) {
                if (record == null || record.message_id != requested.message_id) throw new InvalidDataException("SMTP identity mismatch; migration requires review.");
                if (record.phase == "accepted") return record.message_id;
                if (record.phase == "attempting" || record.phase == "uncertain") throw new InvalidOperationException("SMTP delivery is uncertain; verify the recipient mailbox before any resend.");
                if (record.phase != "prepared" || record.frozen == null) throw new InvalidDataException("Invalid SMTP delivery phase.");
                ValidateMail(record.frozen);
                if (record.frozen.key != requested.key || record.frozen.message_id != record.message_id) throw new InvalidDataException("Frozen SMTP identity mismatch.");
            } else {
                record = new Delivery { phase="prepared", message_id=requested.message_id, frozen=requested };
                ledger.deliveries.Add(requested.key, record);
                Save(path, ledger);
            }
            // Durable before entering external send. Crash/timeout cannot cause a blind retry.
            record.phase = "attempting";
            Save(path, ledger);
            try { send(record.frozen); }
            catch (Exception error) {
                var smtpError = error as SmtpException;
                record.error_type = error.GetType().Name;
                record.smtp_status_code = smtpError == null ? (int?)null : (int)smtpError.StatusCode;
                // 535 is an authentication refusal before message submission, not ambiguous acceptance.
                bool authenticationRefused = record.smtp_status_code == 535;
                record.phase = authenticationRefused ? "prepared" : "uncertain";
                Save(path, ledger);
                if (authenticationRefused) throw new InvalidOperationException("QQ SMTP authentication was refused (535); update the client authorization code.");
                throw new InvalidOperationException("SMTP attempt did not complete reliably; recipient verification is required before resending.");
            }
            record.phase = "accepted";
            record.error_type = null; record.smtp_status_code = null;
            record.acknowledged_at_utc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            record.frozen = null; // Accepted ledger retains metadata, not another full-history copy.
            Save(path, ledger);
            return record.message_id;
        }
    }
    private static void Save(string path, Ledger ledger) {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            byte[] bytes = Utf8.GetBytes(Json.Serialize(ledger));
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes,0,bytes.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string ReadCredential(Config config) {
        IntPtr pointer;
        if (!CredRead(config.credential_target, 1, 0, out pointer)) throw new InvalidOperationException("QQ credential is not stored for this Windows user.");
        try {
            var credential = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
            if (!String.Equals(credential.UserName,config.sender,StringComparison.OrdinalIgnoreCase) || credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 512 || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("Stored credential does not match the approved sender.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        } finally { CredFree(pointer); }
    }
    public static void Send(Mail frozen, string authorizationCode) {
        ValidateMail(frozen);
        if (String.IsNullOrWhiteSpace(authorizationCode)) throw new InvalidOperationException("QQ authorization code missing.");
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using (var message = new MailMessage())
        using (var smtp = new SmtpClient("smtp.qq.com",587)) {
            message.From = new MailAddress(frozen.sender,"Paulatim",Encoding.UTF8);
            message.To.Add(new MailAddress(frozen.recipient));
            message.Subject = frozen.subject; message.SubjectEncoding=Encoding.UTF8;
            message.Body=frozen.html; message.BodyEncoding=Encoding.UTF8; message.IsBodyHtml=true;
            message.Headers.Add("Message-ID",frozen.message_id);
            using (var csv = new MemoryStream(Convert.FromBase64String(frozen.csv_base64))) {
                message.Attachments.Add(new Attachment(csv,"work_log.csv","text/csv"));
                smtp.EnableSsl=true; smtp.UseDefaultCredentials=false;
                smtp.Credentials=new NetworkCredential(frozen.sender,authorizationCode); smtp.Timeout=60000;
                // No certificate validation bypass, proxy rewrite, or model API.
                smtp.Send(message);
            }
        }
    }
    public static void RemoveCredential(Config config) {
        ValidateConfig(config,config.recipient);
        IntPtr pointer;
        if (!CredRead(config.credential_target,1,0,out pointer)) return;
        try {
            var credential=(Credential)Marshal.PtrToStructure(pointer,typeof(Credential));
            if (!String.Equals(credential.UserName,config.sender,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to remove another sender's credential.");
        } finally { CredFree(pointer); }
        if (!CredDelete(config.credential_target,1,0)) throw new InvalidOperationException("Cannot remove this application's credential.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct Credential {
        public uint Flags, Type; public string TargetName, Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount; public IntPtr Attributes; public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CredRead(string target,uint type,uint flags,out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint="CredDeleteW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CredDelete(string target,uint type,uint flags);
}
