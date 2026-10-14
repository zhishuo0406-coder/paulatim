using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal static class FreshnessTests {
    private static readonly List<object> Results = new List<object>();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T00:48:06Z");
    private static readonly string Old = PaulatimFreshness.Hash(Encoding.UTF8.GetBytes("old"));
    private static readonly string New = PaulatimFreshness.Hash(Encoding.UTF8.GetBytes("new"));
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Assert(rejected); }
    private static void Test(string name, Action action) { try { action(); Results.Add(new { name=name, passed=true, detail="" }); } catch (Exception e) { Results.Add(new { name=name, passed=false, detail=e.Message }); } }
    private static PaulatimFreshness.Confirmation Ready(string hash, string through) {
        return new PaulatimFreshness.Confirmation { schema_version=1, source_sha256=hash, confirmed_through_date=through, confirmed_at_utc=Now.ToString("o") };
    }
    private static int Main(string[] args) {
        string root = args[0]; Directory.CreateDirectory(root);
        Test("24-second-late-row-restarts-window", () => {
            PaulatimFreshness.Observation observation;
            Assert(!PaulatimFreshness.Observe(null, Old, Now, out observation));
            Assert(!PaulatimFreshness.Observe(observation, New, Now.AddSeconds(24), out observation));
            Assert(!PaulatimFreshness.Observe(observation, New, Now.AddSeconds(84), out observation));
            Assert(PaulatimFreshness.Observe(observation, New, Now.AddSeconds(144), out observation));
            Reject(() => PaulatimFreshness.Validate(Ready(Old,"2026-10-13"),New,"2026-10-13",Now.AddSeconds(144)));
        });
        Test("stable-old-file-without-confirmation-never-ready", () => {
            PaulatimFreshness.Observation observation = null;
            for (int i=0; i<12; i++) PaulatimFreshness.Observe(observation,Old,Now.AddSeconds(i*60),out observation);
            Reject(() => PaulatimFreshness.Validate(null,Old,"2026-10-13",Now.AddMinutes(12)));
        });
        Test("continuous-changing-source-never-stable", () => {
            PaulatimFreshness.Observation observation=null;
            for (int i=0;i<20;i++) Assert(!PaulatimFreshness.Observe(observation,i%2==0?Old:New,Now.AddSeconds(i*60),out observation));
        });
        Test("offline-gap-and-clock-reversal-reset-window", () => {
            PaulatimFreshness.Observation observation;
            PaulatimFreshness.Observe(null,Old,Now,out observation);
            Assert(!PaulatimFreshness.Observe(observation,Old,Now.AddMinutes(10),out observation));
            Assert(!PaulatimFreshness.Observe(observation,Old,Now.AddMinutes(-1),out observation));
        });
        Test("valid-complete-closed-period-ready", () => PaulatimFreshness.Validate(Ready(New,"2026-10-13"),New,"2026-10-13",Now));
        Test("unchanged-no-work-day-can-be-explicitly-confirmed", () => PaulatimFreshness.Validate(Ready(Old,"2026-10-13"),Old,"2026-10-13",Now));
        Test("prior-period-confirmation-does-not-authorize-new-day", () => Reject(() => PaulatimFreshness.Validate(Ready(New,"2026-10-12"),New,"2026-10-13",Now)));
        Test("unclosed-and-future-confirmations-rejected", () => {
            Reject(() => PaulatimFreshness.Validate(Ready(New,"2026-10-14"),New,"2026-10-13",Now));
            var ready=Ready(New,"2026-10-13"); ready.confirmed_at_utc=Now.AddMinutes(1).ToString("o");
            Reject(() => PaulatimFreshness.Validate(ready,New,"2026-10-13",Now));
        });
        Test("schema-date-and-hash-corruption-rejected", () => {
            var ready=Ready(New,"2026-10-13"); ready.schema_version=2; Reject(() => PaulatimFreshness.Validate(ready,New,"2026-10-13",Now));
            ready=Ready(New,"2026-10-13\n"); Reject(() => PaulatimFreshness.Validate(ready,New,"2026-10-13",Now));
            ready=Ready(New+"\n","2026-10-13"); Reject(() => PaulatimFreshness.Validate(ready,New,"2026-10-13",Now));
        });
        Test("weekly-coverage-checked-independently", () => {
            PaulatimFreshness.Validate(Ready(New,"2026-10-13"),New,"2026-10-10",Now);
            Reject(() => PaulatimFreshness.Validate(Ready(New,"2026-10-09"),New,"2026-10-10",Now));
        });
        Test("corrupt-observation-restarts-safely", () => {
            File.WriteAllText(Path.Combine(root,"source-observation.json"),"{broken");
            Assert(!PaulatimFreshness.ObserveFile(root,Old,Now));
            Assert(!PaulatimFreshness.ObserveFile(root,Old,Now.AddSeconds(60)));
            Assert(PaulatimFreshness.ObserveFile(root,Old,Now.AddSeconds(120)));
        });
        Test("missing-and-corrupt-ready-file-block", () => {
            Reject(() => PaulatimFreshness.RequireReady(root,Old,"2026-10-13",Now));
            File.WriteAllText(Path.Combine(root,"source-ready.json"),"{broken");
            Reject(() => PaulatimFreshness.RequireReady(root,Old,"2026-10-13",Now));
        });
        Test("source-read-lease-prevents-replacement-and-write", () => {
            string path=Path.Combine(root,"source.csv"); File.WriteAllText(path,"old");
            using (var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Reject(() => File.WriteAllText(path,"new"));
                Reject(() => File.Delete(path));
            }
            Assert(File.ReadAllText(path)=="old");
        });
        var config=new PaulatimSmtp.Config {enabled=true,sender="fictional@qq.com",recipient="fictional@example.com",credential_target="Paulatim/SMTP/TEST"};
        Func<string,PaulatimSmtp.Mail> mail=csv => PaulatimSmtp.Freeze("daily:2026-10-13",config,"[Paulatim test]","<p>synthetic</p>",Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)));
        Test("blocked-source-creates-no-send-intent", () => {
            string folder=Path.Combine(root,"blocked"); Directory.CreateDirectory(folder); int sends=0;
            Reject(() => PaulatimSmtp.DeliverGuarded(folder,mail("old"),m=>sends++,m=> { throw new InvalidDataException("Not ready"); }));
            Assert(sends==0 && !File.Exists(Path.Combine(folder,"smtp-state.json")));
        });
        Test("ready-current-source-sends-once", () => {
            string folder=Path.Combine(root,"accepted"); Directory.CreateDirectory(folder); int sends=0;
            Action<PaulatimSmtp.Mail> guard=m=>PaulatimFreshness.Validate(Ready(New,"2026-10-13"),PaulatimFreshness.Hash(Convert.FromBase64String(m.csv_base64)),"2026-10-13",Now);
            PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,guard);
            PaulatimSmtp.DeliverGuarded(folder,mail("old"),m=>sends++,guard);
            Assert(sends==1);
        });
        Test("older-prepared-payload-preserved-and-not-sent", () => {
            string folder=Path.Combine(root,"prepared"); Directory.CreateDirectory(folder); var old=mail("old");
            var ledger=new PaulatimSmtp.Ledger(); ledger.deliveries.Add(old.key,new PaulatimSmtp.Delivery {phase="prepared",message_id=old.message_id,frozen=old});
            string path=Path.Combine(folder,"smtp-state.json"); var json=new JavaScriptSerializer(); File.WriteAllText(path,json.Serialize(ledger)); string before=File.ReadAllText(path); int sends=0;
            Reject(() => PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,m=> {
                if (PaulatimFreshness.Hash(Convert.FromBase64String(m.csv_base64))!=New) throw new InvalidDataException("Older frozen source");
            }));
            Assert(sends==0 && File.ReadAllText(path)==before);
        });
        Test("uncertain-outcome-still-blocks-even-with-new-readiness", () => {
            string folder=Path.Combine(root,"uncertain"); Directory.CreateDirectory(folder); int sends=0;
            Reject(() => PaulatimSmtp.DeliverGuarded(folder,mail("old"),m=> { sends++; throw new IOException("Synthetic failure"); },m=>{}));
            Reject(() => PaulatimSmtp.DeliverGuarded(folder,mail("new"),m=>sends++,m=>{})); Assert(sends==1);
        });
        File.WriteAllText(Path.Combine(root,"results.json"),new JavaScriptSerializer().Serialize(Results));
        Console.WriteLine("Freshness checks: "+Results.Count+"; passed: "+Results.Count(x=>(bool)x.GetType().GetProperty("passed").GetValue(x,null)));
        return Results.Any(x=>!(bool)x.GetType().GetProperty("passed").GetValue(x,null))?1:0;
    }
}
