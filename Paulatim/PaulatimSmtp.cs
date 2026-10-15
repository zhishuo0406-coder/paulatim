using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
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
        public string stage { get; set; }
        public string diagnostic { get; set; }
        public string attempted_at_utc { get; set; }
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
        return DeliverGuarded(root, requested, send, null);
    }
    public static string DeliverGuarded(string root, Mail requested, Action<Mail> send, Action<Mail> beforeAttempt) {
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
                // A diagnosed pre-submission failure cannot have delivered mail.
                // Refresh only that safely retryable payload so later iCloud updates
                // do not strand retries behind an obsolete attachment/snapshot.
                if (record.error_type == "TransportFailure") {
                    if (beforeAttempt != null) beforeAttempt(requested);
                    record.frozen = requested;
                    Save(path,ledger);
                }
            } else {
                if (beforeAttempt != null) beforeAttempt(requested);
                record = new Delivery { phase="prepared", message_id=requested.message_id, frozen=requested };
                ledger.deliveries.Add(requested.key, record);
                Save(path, ledger);
            }
            // Durable before entering external send. Crash/timeout cannot cause a blind retry.
            if (beforeAttempt != null) beforeAttempt(record.frozen);
            record.phase = "attempting";
            Save(path, ledger);
            record.attempted_at_utc = DateTimeOffset.UtcNow.ToString("o");
            Save(path, ledger);
            try { send(record.frozen); }
            catch (Exception error) {
                var smtpError = error as SmtpException;
                var transportError = error as TransportFailure;
                record.error_type = error.GetType().Name;
                record.smtp_status_code = transportError != null ? transportError.Code : smtpError == null ? (int?)null : (int)smtpError.StatusCode;
                record.stage = transportError == null ? "legacy_unknown" : transportError.Stage;
                record.diagnostic = Diagnostic(error);
                // 535 is an authentication refusal before message submission, not ambiguous acceptance.
                bool authenticationRefused = record.smtp_status_code == 535;
                bool safe = authenticationRefused || (transportError != null && transportError.SafeToRetry);
                record.phase = safe ? "prepared" : "uncertain";
                Save(path, ledger);
                if (authenticationRefused) throw new InvalidOperationException("QQ SMTP authentication was refused (535); update the client authorization code.");
                if (safe) throw new InvalidOperationException("SMTP did not submit the message; bounded retry is safe. Stage: " + record.stage);
                throw new InvalidOperationException("SMTP attempt did not complete reliably; recipient verification is required before resending.");
            }
            record.phase = "accepted";
            record.error_type = null; record.smtp_status_code = null;
            record.stage = "accepted"; record.diagnostic = null;
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
    public static Delivery Inspect(string root,string key) {
        string path=Path.Combine(root,"smtp-state.json");
        if(!File.Exists(path)) return null;
        var ledger=Json.Deserialize<Ledger>(File.ReadAllText(path,Utf8));
        if(ledger==null || ledger.schema_version!=1 || ledger.deliveries==null) throw new InvalidDataException("Unsupported SMTP ledger; refusing recovery.");
        Delivery value;return ledger.deliveries.TryGetValue(key,out value)?value:null;
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
        Transfer(frozen,authorizationCode,false);
    }
    public static void Probe(Config config,string authorizationCode) {
        ValidateConfig(config,config.recipient);
        Transfer(Freeze("probe:smtp",config,"[Paulatim连接检查]","<p>Not submitted</p>",Convert.ToBase64String(Utf8.GetBytes("probe"))),authorizationCode,true);
    }
    private static void Transfer(Mail frozen,string authorizationCode,bool probe) {
        ValidateMail(frozen);
        if (String.IsNullOrWhiteSpace(authorizationCode)) throw new InvalidOperationException("QQ authorization code missing.");
        string stage = "connect";
        try {
            using (var socket = new TcpClient()) {
                var pending = socket.BeginConnect("smtp.qq.com",587,null,null);
                using (pending.AsyncWaitHandle) {
                    if (!pending.AsyncWaitHandle.WaitOne(15000)) throw new TimeoutException("SMTP connect deadline");
                    socket.EndConnect(pending);
                }
                socket.ReceiveTimeout=15000; socket.SendTimeout=15000;
                using (var network=socket.GetStream()) {
                    stage="greeting"; Expect(network,220);
                    stage="starttls";
                    string features=Command(network,"EHLO paulatim.local",250);
                    if (!Regex.IsMatch(features,@"(?im)^250[ -]STARTTLS\s*$")) throw new IOException("SMTP server did not offer STARTTLS");
                    Command(network,"STARTTLS",220);
                    // Default SslStream validation verifies the certificate and hostname.
                    using (var tls=new SslStream(network,false)) {
                        stage="tls"; tls.ReadTimeout=15000; tls.WriteTimeout=15000;
                        tls.AuthenticateAsClient("smtp.qq.com",null,SslProtocols.Tls12,true);
                        SubmitGuarded(tls,frozen,authorizationCode,probe);
                        stage="accepted";
                    }
                }
            }
        } catch (TransportFailure) { throw;
        } catch (ReplyFailure error) {
            // An explicit 4xx/5xx reply rejects the transaction, including after DATA.
            throw new TransportFailure(stage,true,error.Code,error);
        } catch (Exception error) {
            if(stage=="accepted") return;
            throw new TransportFailure(stage,true,null,error);
        }
    }

    internal sealed class TransportFailure : Exception {
        public readonly string Stage; public readonly bool SafeToRetry; public readonly int? Code;
        public TransportFailure(string stage,bool safe,int? code,Exception inner) : base("SMTP transport failed at " + stage,inner) {Stage=stage;SafeToRetry=safe;Code=code;}
    }
    private sealed class ReplyFailure : Exception {
        public readonly int Code;
        public ReplyFailure(int code) : base("SMTP reply " + code) {Code=code;}
    }
    // Read only type/code diagnostics: never retain SMTP replies, AUTH text or secrets.
    private static string Diagnostic(Exception error) {
        var result=new StringBuilder();
        for (Exception e=error;e!=null;e=e.InnerException) {
            if(result.Length>0) result.Append(" > "); result.Append(e.GetType().Name);
            var socket=e as SocketException; if(socket!=null) result.Append(":"+socket.SocketErrorCode);
            var transport=e as TransportFailure; if(transport!=null) result.Append(":"+transport.Stage);
        }
        return result.ToString();
    }
    private static string Expect(Stream stream,int expected) {
        var all=new StringBuilder(); int code=0;
        for(int lines=0;lines<100;lines++) {
            var line=new StringBuilder();
            while(true) {int b=stream.ReadByte();if(b<0) throw new EndOfStreamException("SMTP response ended");if(b=='\n') break; if(b!='\r') line.Append((char)b);if(line.Length>2048) throw new IOException("SMTP reply too long");}
            string text=line.ToString();int value;
            if(text.Length<4 || !Int32.TryParse(text.Substring(0,3),out value) || text[3]!=' ' && text[3]!='-' || code!=0 && code!=value) throw new IOException("Invalid SMTP reply");
            code=value; all.AppendLine(text);
            if(text[3]==' ') {if(code!=expected) throw new ReplyFailure(code);return all.ToString();}
        }
        throw new IOException("Too many SMTP reply lines");
    }
    private static void Put(Stream stream,string text) {byte[] bytes=Encoding.ASCII.GetBytes(text);stream.Write(bytes,0,bytes.Length);stream.Flush();}
    private static string Command(Stream stream,string text,int expected) {Put(stream,text+"\r\n");return Expect(stream,expected);}
    internal static void SubmitGuarded(Stream stream,Mail mail,string secret,bool probe=false) {
        string stage="authenticate";bool committed=false;
        try { Submit(stream,mail,secret,next=>{stage=next;if(next=="commit") committed=true;},probe); }
        catch(ReplyFailure error) {throw new TransportFailure(stage,!committed || error.Code>=400 && error.Code<=599,error.Code,error);}
        catch(Exception error) {throw new TransportFailure(stage,!committed,null,error);}
    }
    internal static void Submit(Stream stream,Mail mail,string secret,Action<string> progress,bool probe=false) {
        progress("authenticate");
        string features=Command(stream,"EHLO paulatim.local",250);
        if(!Regex.IsMatch(features,@"(?im)^250[ -]AUTH[^\r\n]*\bLOGIN\b")) throw new IOException("SMTP AUTH LOGIN unavailable");
        Command(stream,"AUTH LOGIN",334);
        Command(stream,Convert.ToBase64String(Utf8.GetBytes(mail.sender)),334);
        Command(stream,Convert.ToBase64String(Utf8.GetBytes(secret)),235);
        progress("envelope");Command(stream,"MAIL FROM:<"+mail.sender+">",250);Command(stream,"RCPT TO:<"+mail.recipient+">",250);
        if(probe) {Command(stream,"RSET",250);return;}
        progress("data");Command(stream,"DATA",354);
        Put(stream,Mime(mail));
        // No end-of-DATA marker has been written before this point. All MIME bodies
        // are base64 and cannot accidentally contain a line consisting of a dot.
        progress("commit");Put(stream,".\r\n");Expect(stream,250);
        progress("accepted");
        // Close the connection after acceptance; QUIT failure cannot negate 250.
    }
    internal static string Mime(Mail mail) {
        ValidateMail(mail);
        string boundary="paulatim_"+Guid.NewGuid().ToString("N");
        var subject=new StringBuilder();
        for(int i=0;i<mail.subject.Length;) {
            int count=Math.Min(10,mail.subject.Length-i);
            if(i+count<mail.subject.Length && Char.IsHighSurrogate(mail.subject[i+count-1])) count--;
            if(subject.Length>0) subject.Append("\r\n ");
            subject.Append("=?UTF-8?B?"+Convert.ToBase64String(Utf8.GetBytes(mail.subject.Substring(i,count)))+"?=");i+=count;
        }
        return "From: Paulatim <"+mail.sender+">\r\nTo: "+mail.recipient+"\r\nDate: "+DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000",CultureInfo.InvariantCulture)+
            "\r\nSubject: "+subject+"\r\nMessage-ID: "+mail.message_id+"\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\""+boundary+"\"\r\n\r\n--"+boundary+
            "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"+Convert.ToBase64String(Utf8.GetBytes(mail.html),Base64FormattingOptions.InsertLineBreaks)+
            "\r\n--"+boundary+"\r\nContent-Type: text/csv; name=\"work_log.csv\"\r\nContent-Disposition: attachment; filename=\"work_log.csv\"\r\nContent-Transfer-Encoding: base64\r\n\r\n"+
            Convert.ToBase64String(Convert.FromBase64String(mail.csv_base64),Base64FormattingOptions.InsertLineBreaks)+"\r\n--"+boundary+"--\r\n";
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
