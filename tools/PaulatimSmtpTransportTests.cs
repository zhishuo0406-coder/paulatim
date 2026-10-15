using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
class PaulatimSmtpTransportTests {
 static int failures=0;
 static string root;
 static PaulatimSmtp.Config config=new PaulatimSmtp.Config {enabled=true,sender="sender@qq.com",recipient="recipient@example.com",credential_target="Paulatim/SMTP/TEST"};
 static PaulatimSmtp.Mail Mail() {return PaulatimSmtp.Freeze("daily:2026-10-14",config,"[Paulatim日报] 2026-10-14","<p>测试 &amp; snapshot</p>",Convert.ToBase64String(Encoding.UTF8.GetBytes("2026-10-14 20:00,测试,30,20\n")));}
 static void Assert(bool ok,string message) {if(!ok) throw new Exception(message);}
 static void Test(string name,Action body) {try{body();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
 class Scripted : Stream {
  byte[] replies;int at=0;public StringBuilder Writes=new StringBuilder();public string FailOn;
  public Scripted(string text) {replies=Encoding.ASCII.GetBytes(text);}
  public override int Read(byte[] b,int o,int n) {if(at==replies.Length)return 0;int count=Math.Min(n,replies.Length-at);Array.Copy(replies,at,b,o,count);at+=count;return count;}
  public override void Write(byte[] b,int o,int n) {string s=Encoding.ASCII.GetString(b,o,n);if(FailOn!=null && s.StartsWith(FailOn,StringComparison.Ordinal))throw new IOException("Synthetic write failure");Writes.Append(s);}
  public override void Flush(){}public override bool CanRead{get{return true;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}public override long Seek(long a,SeekOrigin b){throw new NotSupportedException();}public override void SetLength(long a){throw new NotSupportedException();}
 }
 const string Ehlo="250-qq\r\n250 AUTH LOGIN PLAIN\r\n";
 const string Auth="334 username\r\n334 password\r\n235 authenticated\r\n";
 const string Envelope="250 sender\r\n250 recipient\r\n";
 static PaulatimSmtp.TransportFailure Failure(Scripted stream) {try{PaulatimSmtp.SubmitGuarded(stream,Mail(),"synthetic-secret");}catch(PaulatimSmtp.TransportFailure e){return e;}throw new Exception("Expected failure");}
 static void MainTest() {
  Test("successful-session-complete-mime",()=>{var s=new Scripted(Ehlo+Auth+Envelope+"354 send\r\n250 queued\r\n");PaulatimSmtp.SubmitGuarded(s,Mail(),"synthetic-secret");Assert(s.Writes.ToString().EndsWith("\r\n.\r\n"),"Missing terminator");File.WriteAllText(Path.Combine(root,"message.eml"),s.Writes.ToString().Substring(s.Writes.ToString().IndexOf("From: Paulatim")),Encoding.ASCII);});
  Test("authentication-refusal-safe",()=>{var e=Failure(new Scripted(Ehlo+"334 user\r\n334 pass\r\n535 denied\r\n"));Assert(e.SafeToRetry && e.Code==535 && e.Stage=="authenticate","Wrong classification");});
  Test("auth-disconnect-safe",()=>{var e=Failure(new Scripted(Ehlo));Assert(e.SafeToRetry && e.Stage=="authenticate","Wrong classification");});
  Test("recipient-rejection-safe",()=>{var e=Failure(new Scripted(Ehlo+Auth+"250 sender\r\n550 denied\r\n"));Assert(e.SafeToRetry && e.Code==550,"Wrong rejection");});
  Test("data-write-disconnect-safe",()=>{var s=new Scripted(Ehlo+Auth+Envelope+"354 send\r\n"){FailOn="From:"};var e=Failure(s);Assert(e.SafeToRetry && e.Stage=="data" && !s.Writes.ToString().EndsWith(".\r\n"),"Body failure unsafe classification");});
  Test("terminator-write-failure-uncertain",()=>{var s=new Scripted(Ehlo+Auth+Envelope+"354 send\r\n"){FailOn=".\r\n"};Assert(!Failure(s).SafeToRetry,"Ambiguous terminator retried");});
  Test("lost-final-ack-uncertain",()=>{var e=Failure(new Scripted(Ehlo+Auth+Envelope+"354 send\r\n"));Assert(!e.SafeToRetry && e.Stage=="commit","Lost ack retried");});
  Test("final-explicit-rejection-safe",()=>{var e=Failure(new Scripted(Ehlo+Auth+Envelope+"354 send\r\n451 rejected\r\n"));Assert(e.SafeToRetry && e.Code==451,"Rejected message held uncertain");});
  Test("malformed-final-ack-uncertain",()=>{Assert(!Failure(new Scripted(Ehlo+Auth+Envelope+"354 send\r\ngarbage\r\n")).SafeToRetry,"Malformed ack retried");});
  Test("inconsistent-multiline-reply-rejected",()=>{Assert(Failure(new Scripted("250-qq\r\n220 AUTH LOGIN\r\n")).SafeToRetry,"Invalid EHLO accepted");});
  Test("probe-never-submits-data",()=>{var s=new Scripted(Ehlo+Auth+Envelope+"250 reset\r\n");PaulatimSmtp.SubmitGuarded(s,Mail(),"synthetic-secret",true);Assert(!s.Writes.ToString().Contains("DATA\r\n") && s.Writes.ToString().EndsWith("RSET\r\n"),"Probe sent mail");});
  Test("safe-failure-ledger-refreshes-and-retries",()=>{
   string folder=Path.Combine(root,"safe");Directory.CreateDirectory(folder);var mail=Mail();int sends=0;
   try{PaulatimSmtp.Deliver(folder,mail,m=>{sends++;throw new PaulatimSmtp.TransportFailure("connect",true,null,new System.Net.Sockets.SocketException(10061));});}catch{}
   var old=PaulatimSmtp.Inspect(folder,mail.key);Assert(old.phase=="prepared" && old.stage=="connect" && old.diagnostic.Contains("ConnectionRefused"),"Missing safe diagnosis");
   mail.html="<p>Refreshed source</p>";PaulatimSmtp.Deliver(folder,mail,m=>{sends++;Assert(m.html==mail.html,"Stale payload retried");});PaulatimSmtp.Deliver(folder,mail,m=>{sends++;});Assert(sends==2,"Accepted delivery repeated");
  });
  Test("uncertain-failure-blocks-and-redacts",()=>{
   string folder=Path.Combine(root,"uncertain");Directory.CreateDirectory(folder);var mail=Mail();int sends=0;
   try{PaulatimSmtp.Deliver(folder,mail,m=>{sends++;throw new PaulatimSmtp.TransportFailure("commit",false,null,new IOException("synthetic-secret"));});}catch{}
   try{PaulatimSmtp.Deliver(folder,mail,m=>{sends++;});}catch{}
   var d=PaulatimSmtp.Inspect(folder,mail.key);Assert(sends==1 && d.phase=="uncertain" && !File.ReadAllText(Path.Combine(folder,"smtp-state.json")).Contains("synthetic-secret"),"Repeated uncertain delivery or leaked secret");
  });
  Test("controller-uncertain-ticks-preserve-attempts-and-cause",()=>{
   string folder=Path.GetFullPath(Path.Combine(root,"controller"));Directory.CreateDirectory(folder);
   var json=new JavaScriptSerializer();string day=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date.AddDays(-1).ToString("yyyy-MM-dd");
   // Simulate today's due period after 08:00 (the live repair runs after that deadline).
   if(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Hour<8)day=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date.AddDays(-2).ToString("yyyy-MM-dd");
   string key="daily:"+day;
   var cfg=new PaulatimDispatch.Config {enabled=true,recipient="recipient@example.com",first_daily_report="2026-01-01",first_weekly_end="2099-01-03",max_attempts_per_period=3,timeout_minutes=10,transport="qq_smtp"};
   var state=new PaulatimDispatch.State {signature=key,attempts=1};
   var ledger=new PaulatimSmtp.Ledger();ledger.deliveries[key]=new PaulatimSmtp.Delivery {phase="uncertain",stage="commit",diagnostic="TransportFailure:commit > EndOfStreamException"};
   File.WriteAllText(Path.Combine(folder,"mail-config.json"),json.Serialize(cfg));File.WriteAllText(Path.Combine(folder,"mail-state.json"),json.Serialize(state));File.WriteAllText(Path.Combine(folder,"smtp-state.json"),json.Serialize(ledger));
   var type=typeof(PaulatimDispatch);var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;var field=type.GetField("Root",flags);var saved=field.GetValue(null);
   try{field.SetValue(null,folder);for(int i=0;i<3;i++)Assert((int)type.GetMethod("Dispatch",flags).Invoke(null,new object[]{false})==1,"Uncertain tick succeeded");}finally{field.SetValue(null,saved);}
   var after=json.Deserialize<PaulatimDispatch.State>(File.ReadAllText(Path.Combine(folder,"mail-state.json")));Assert(after.attempts==1 && after.receipts.Count==0,"Guard ticks spent attempts or fabricated receipt");
   Assert(File.ReadAllText(Path.Combine(folder,"mail-status.json")).Contains("EndOfStreamException"),"Status replaced original cause");
  });
 }
 static int Main(string[] args) {root=args[0];Directory.CreateDirectory(root);MainTest();return failures==0?0:1;}
}
