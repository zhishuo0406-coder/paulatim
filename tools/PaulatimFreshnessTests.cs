using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal static class FreshnessTests {
    private static readonly List<object> Results = new List<object>();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T08:48:06+08:00");
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Assert(rejected); }
    private static void Test(string name, Action action) { try { action(); Results.Add(new { name=name, passed=true, detail="" }); } catch (Exception e) { Results.Add(new { name=name, passed=false, detail=e.Message }); } }
    private static void Valid(string source, string snapshot) { PaulatimFreshness.ValidateTimes(source,snapshot,Now); }
    private static int Main(string[] args) {
        string root=args[0]; Directory.CreateDirectory(root);
        Test("before-midnight-snapshot-blocked",()=>Reject(()=>Valid("2026-10-13T20:00:00+08:00","2026-10-13T23:59:59+08:00")));
        Test("exact-000000-snapshot-rejected",()=>Reject(()=>Valid("2026-10-13T20:00:00+08:00","2026-10-14T00:00:00+08:00")));
        Test("first-tick-after-midnight-snapshot-accepts-yesterday-data",()=>Valid("2026-10-13T20:00:00+08:00","2026-10-14T00:00:00.0000001+08:00"));
        Test("utc-to-beijing-boundary",()=>{
            Reject(()=>Valid("2026-10-13T12:00:00Z","2026-10-13T16:00:00Z"));
            Valid("2026-10-13T12:00:00Z","2026-10-13T16:00:01Z");
        });
        Test("offset-independent-same-instant",()=>Valid("2026-10-13T12:00:00Z","2026-10-13T17:00:01+01:00"));
        Test("future-source-or-snapshot-rejected",()=>{
            Reject(()=>Valid(Now.AddSeconds(1).ToString("o"),Now.ToString("o")));
            Reject(()=>Valid("2026-10-14T01:00:00+08:00",Now.AddSeconds(1).ToString("o")));
        });
        Test("snapshot-before-source-update-rejected",()=>Reject(()=>Valid("2026-10-14T08:00:00+08:00","2026-10-14T07:00:00+08:00")));
        Test("previous-day-snapshot-rejected",()=>Reject(()=>Valid("2026-10-14T01:00:00+08:00","2026-10-13T23:00:00+08:00")));
        Test("missing-invalid-or-unqualified-time-rejected",()=>{
            foreach(string invalid in new[]{null,"","2026-10-14T01:00:00","2026-10-14T01:00:00Z\n","not-a-date","2026-13-14T01:00:00Z"}) Reject(()=>Valid(invalid,Now.ToString("o")));
        });
        Test("midnight-crossing-rechecks-today-before-send",()=>{
            var time=DateTimeOffset.Parse("2026-10-15T00:00:01+08:00");
            Reject(()=>PaulatimFreshness.ValidateTimes(Now.ToString("o"),Now.ToString("o"),time));
        });
        Test("leap-day-and-year-rollover",()=>{
            PaulatimFreshness.ValidateTimes("2028-02-29T00:00:01+08:00","2028-02-29T08:00:00+08:00",DateTimeOffset.Parse("2028-02-29T08:00:01+08:00"));
            Reject(()=>PaulatimFreshness.ValidateTimes("2026-12-31T12:00:00+08:00","2026-12-31T23:59:59+08:00",DateTimeOffset.Parse("2027-01-01T08:00:01+08:00")));
        });
        Test("24-second-late-acquisition-then-eligible",()=>{
            Reject(()=>Valid("2026-10-13T20:00:00+08:00","2026-10-13T23:00:00+08:00"));
            var later=Now.AddSeconds(24);
            PaulatimFreshness.ValidateTimes("2026-10-13T20:00:00+08:00",later.AddSeconds(1).ToString("o"),later.AddSeconds(2));
        });
        Test("source-read-lease-prevents-replacement-and-write",()=>{
            string path=Path.Combine(root,"source.csv"); File.WriteAllText(path,"old");
            using(var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) { Reject(()=>File.WriteAllText(path,"new")); Reject(()=>File.Delete(path)); }
            Assert(File.ReadAllText(path)=="old");
        });
        var config=new PaulatimSmtp.Config {enabled=true,sender="fictional@qq.com",recipient="fictional@example.com",credential_target="Paulatim/SMTP/TEST"};
        Func<string,PaulatimSmtp.Mail> mail=csv=>PaulatimSmtp.Freeze("daily:2026-10-13",config,"[Paulatim test]","<p>synthetic</p>",Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)));
        Test("stale-snapshot-creates-no-intent-or-send",()=>{
            string folder=Path.Combine(root,"blocked"); Directory.CreateDirectory(folder); int sends=0;
            Reject(()=>PaulatimSmtp.DeliverGuarded(folder,mail("old"),m=>sends++,m=>Valid("2026-10-13T20:00:00+08:00","2026-10-13T23:00:00+08:00")));
            Assert(sends==0 && !File.Exists(Path.Combine(folder,"smtp-state.json")));
        });
        Test("fresh-snapshot-of-yesterday-data-sends-once",()=>{
            string folder=Path.Combine(root,"accepted"); Directory.CreateDirectory(folder); int sends=0;
            Action<PaulatimSmtp.Mail> guard=m=>Valid("2026-10-13T23:00:00+08:00",Now.ToString("o"));
            PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,guard);
            PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,guard); Assert(sends==1);
        });
        Test("old-frozen-payload-preserved-and-not-sent",()=>{
            string folder=Path.Combine(root,"prepared"); Directory.CreateDirectory(folder); var old=mail("old");
            var ledger=new PaulatimSmtp.Ledger(); ledger.deliveries.Add(old.key,new PaulatimSmtp.Delivery {phase="prepared",message_id=old.message_id,frozen=old});
            string path=Path.Combine(folder,"smtp-state.json"); File.WriteAllText(path,new JavaScriptSerializer().Serialize(ledger)); string before=File.ReadAllText(path); int sends=0;
            Reject(()=>PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,m=>{
                if(PaulatimFreshness.Hash(Convert.FromBase64String(m.csv_base64))!=PaulatimFreshness.Hash(Encoding.UTF8.GetBytes("new"))) throw new InvalidDataException("Older frozen source");
            })); Assert(sends==0 && File.ReadAllText(path)==before);
        });
        Test("uncertain-outcome-still-blocks-with-fresh-source",()=>{
            string folder=Path.Combine(root,"uncertain"); Directory.CreateDirectory(folder); int sends=0;
            Reject(()=>PaulatimSmtp.DeliverGuarded(folder,mail("old"),m=>{sends++;throw new IOException("Synthetic failure");},m=>{}));
            Reject(()=>PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,m=>{})); Assert(sends==1);
        });
        File.WriteAllText(Path.Combine(root,"results.json"),new JavaScriptSerializer().Serialize(Results));
        Console.WriteLine("Freshness checks: "+Results.Count+"; passed: "+Results.Count(x=>(bool)x.GetType().GetProperty("passed").GetValue(x,null)));
        return Results.Any(x=>!(bool)x.GetType().GetProperty("passed").GetValue(x,null))?1:0;
    }
}
