using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class PaulatimDataTests
{
    private static string Root;
    private static List<Dictionary<string,object>> Results=new List<Dictionary<string,object>>();
    private static int FixtureNumber;
    private const string A="2030-01-01 09:00,Audit A,30,20";
    private const string B="2030-01-01 10:00,Audit B,40,35";
    private const string C="2030-01-01 11:00,Audit C,10,5";
    private const string Header="timestamp,task,actual_minutes,effective_minutes";
    private sealed class Fixture {
        internal string Dir,Source,Store,SourceId;
        internal FileRecordStore Data;
        internal void Write(string csv) {File.WriteAllText(Source,csv,DataJson.Utf8);}
        internal DataGeneration Import() {return Data.Import(Source,SourceId);}
    }
    private static Fixture New(string name,string csv) {
        string dir=Path.Combine(Root,(++FixtureNumber).ToString("D3"));Directory.CreateDirectory(dir);
        var f=new Fixture {Dir=dir,Source=Path.Combine(dir,"source.csv"),Store=Path.Combine(dir,"data")};f.Write(csv);
        f.SourceId=FileRecordStore.Initialize(f.Store);f.Data=new FileRecordStore(f.Store);return f;
    }
    private static void Assert(bool condition,string message) {if(!condition)throw new Exception(message);}
    private static void Reject(Action action,string message) {bool failed=false;try{action();}catch{failed=true;}Assert(failed,message);}
    private static void Check(string name,Action body) {
        try{body();Results.Add(new Dictionary<string,object>{{"name",name},{"passed",true}});Console.WriteLine("PASS "+name);}
        catch(Exception error){Results.Add(new Dictionary<string,object>{{"name",name},{"passed",false},{"error",error.ToString()}});Console.WriteLine("FAIL "+name+": "+error.Message);}
    }
    private static string[] Ids(DataGeneration g){return g.Records.Select(x=>DataJson.Text(x,"record_id")).ToArray();}
    private static string HashFile(string path){return DataJson.Hash(File.ReadAllBytes(path));}
    private static Dictionary<string,object> JsonFile(string path){return DataJson.Object(File.ReadAllText(path,DataJson.Utf8));}
    private static void MutateAndRehash(Fixture f,DataGeneration generation,string filename,string text) {
        string dir=Path.Combine(f.Store,"generations",generation.Id),path=Path.Combine(dir,filename);
        File.WriteAllText(path,text,DataJson.Utf8);var manifest=JsonFile(Path.Combine(dir,"manifest.json"));var files=(Dictionary<string,object>)manifest["files"];files[filename]=HashFile(path);
        if(filename=="records.jsonl")manifest["semantic_sha256"]=DataJson.HashText(DataJson.Encode(File.ReadAllLines(path).Select(x=>DataJson.Object(x)).OrderBy(x=>DataJson.Text(x,"record_id"),StringComparer.Ordinal).ToArray()));
        File.WriteAllText(Path.Combine(dir,"manifest.json"),DataJson.Encode(manifest),DataJson.Utf8);
        var pointer=JsonFile(Path.Combine(f.Store,"active.json"));pointer["manifest_sha256"]=HashFile(Path.Combine(dir,"manifest.json"));File.WriteAllText(Path.Combine(f.Store,"active.json"),DataJson.Encode(pointer),DataJson.Utf8);
    }
    private static void CopyStore(string source,string destination) {
        Directory.CreateDirectory(destination);foreach(var file in Directory.GetFiles(source))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)));
        foreach(var dir in Directory.GetDirectories(source))CopyStore(dir,Path.Combine(destination,Path.GetFileName(dir)));
    }
    private static void StripViewIds(Dictionary<string,object> view) {
        foreach(var row in DataJson.Rows(view["rows"]))row.Remove("record_id");
        foreach(var key in new[]{"daily","weekly"})foreach(var row in DataJson.Rows(((Dictionary<string,object>)view[key])["rows"]))row.Remove("record_id");
    }
    private static int RunProgram(string program,string arguments) {
        var start=new ProcessStartInfo(program,arguments);start.UseShellExecute=false;start.CreateNoWindow=true;
        using(var process=Process.Start(start)){if(!process.WaitForExit(30000)){process.Kill();throw new Exception("Isolated program timed out.");}return process.ExitCode;}
    }
    private static void SameIds(DataGeneration a,DataGeneration b){Assert(Ids(a).SequenceEqual(Ids(b)),"Permanent IDs changed.");}
    private static int Main(string[] args) {
        if(args.Length==6&&args[0]=="crash-import") {
            var store=new FileRecordStore(args[1]);store.Fault=p=>{if(p==args[5])Environment.Exit(73);};store.Import(args[2],args[3]);return 0;
        }
        Root=Path.GetFullPath(args[0]);Directory.CreateDirectory(Root);
        Check("legacy-roundtrip-and-permanent-id",()=>{
            var f=New("roundtrip",A+"\n"+B);string sourceHash=HashFile(f.Source);var g=f.Import();
            Assert(g.Schema==1&&g.Records.Count==2&&Ids(g).Distinct().Count()==2,"Identity assignment failed.");Assert(HashFile(f.Source)==sourceHash,"Source modified.");
            Assert(HashFile(Path.Combine(f.Store,"raw",sourceHash+".bin"))==sourceHash,"Original archive changed.");
            SameIds(g,f.Data.Read());Assert(FileRecordStore.Initialize(f.Store)==f.SourceId,"Source namespace changed on init replay.");
        });
        Check("same-snapshot-import-is-no-op",()=>{var f=New("idempotent",A);var g=f.Import();Assert(f.Import().Id==g.Id&&Directory.GetDirectories(Path.Combine(f.Store,"generations")).Length==1,"Reimport created another generation or record.");});
        Check("append-preserves-prior-identities",()=>{var f=New("append",A);var a=f.Import();f.Write(A+"\n"+B);var b=f.Import();Assert(Ids(a)[0]==Ids(b)[0]&&b.Records.Count==2,"Append replaced IDs.");Assert(b.Imports.Count==2,"Import ledger missing.");});
        Check("header-and-line-ending-change-preserves-identities",()=>{var f=New("header",A+"\n"+B);var a=f.Import();f.Write(Header+"\r\n"+A+"\r\n"+B+"\r\n");var b=f.Import();SameIds(a,b);f.Write(A+"\n"+B);SameIds(a,f.Import());});
        Check("path-change-with-explicit-source-id-preserves-identities",()=>{var f=New("rename",A);var a=f.Import();string moved=Path.Combine(f.Dir,"moved.csv");File.Move(f.Source,moved);var b=f.Data.Import(moved,f.SourceId);SameIds(a,b);Assert(a.Id==b.Id,"Identical relocated input was reimported.");});
        Check("wrong-source-id-rejected",()=>{var f=New("wrong-source",A);var a=f.Import();Reject(()=>f.Data.Import(f.Source,Guid.NewGuid().ToString("D")),"Wrong source namespace accepted.");Assert(f.Data.Read().Id==a.Id,"Active generation changed.");});
        Check("identical-occurrences-have-distinct-ids",()=>{var f=New("duplicates",A+"\n"+A);var a=f.Import();Assert(a.Records.Count==2&&Ids(a).Distinct().Count()==2,"Legitimate identical occurrences merged.");SameIds(a,f.Import());});
        Check("equivalent-numeric-content-is-flagged-not-merged",()=>{var f=New("equivalent",A+"\n2030-01-01 09:00,Audit A,30.0,20.0");var a=f.Import();string output=Path.Combine(f.Dir,"view");f.Data.ExportView(output,DateTimeOffset.Parse("2030-01-02T08:00:00+08:00"));var view=JsonFile(Path.Combine(output,"report-data.json"));Assert(a.Records.Count==2&&DataJson.Strings(view["warnings"]).Any(x=>x.StartsWith("Equivalent-content")),"Equivalent-content duplicate detection missing.");});
        foreach(var change in new[]{"shorten","edit","reorder","insert"})Check("history-"+change+"-refuses-activation",()=>{
            var f=New(change,A+"\n"+B);var a=f.Import();string replacement=change=="shorten"?A:change=="edit"?A.Replace("30,20","31,20")+"\n"+B:change=="reorder"?B+"\n"+A:A+"\n"+C+"\n"+B;f.Write(replacement);
            Reject(()=>f.Import(),"Ambiguous history modification accepted.");Assert(f.Data.Read().Id==a.Id,"Failed import activated new data.");Assert(File.Exists(Path.Combine(f.Store,"raw",HashFile(f.Source)+".bin")),"Conflict snapshot not archived.");
        });
        Check("five-column-invalid-input-is-archived-but-not-activated",()=>{var f=New("invalid",A);var a=f.Import();f.Write(A+",IELTS");Reject(()=>f.Import(),"Five columns accepted.");Assert(f.Data.Read().Id==a.Id&&File.Exists(Path.Combine(f.Store,"raw",HashFile(f.Source)+".bin")),"Invalid input replaced data or archive missing.");});
        Check("empty-and-header-only-cannot-delete-data",()=>{var f=New("empty",A);var a=f.Import();foreach(var csv in new[]{"",Header}){f.Write(csv);Reject(()=>f.Import(),"Empty dataset accepted.");Assert(f.Data.Read().Id==a.Id,"History deleted.");}});
        Check("unicode-quotes-multiline-and-exact-decimals",()=>{var f=New("unicode","2030-01-01 09:00,\"中文,\"\"任务\"\"\n第二行\",0.1234567890123456789012345678,0.1");var a=f.Import();Assert(DataJson.Text(a.Records[0],"task")=="中文,\"任务\"\n第二行"&&DataJson.Text(a.Records[0],"actual_minutes")=="0.1234567890123456789012345678","CSV or decimal facts changed.");f.Data.Migrate(2);});
        Check("minute-and-second-precision-remain-distinct",()=>{var f=New("precision",A+"\n2030-01-01T10:00:07,Audit B,0,0");var a=f.Import();Assert(DataJson.Text(a.Records[0],"timestamp_precision")=="minute"&&DataJson.Text(a.Records[1],"timestamp_precision")=="second"&&DataJson.Text(a.Records[0],"timestamp_input")=="2030-01-01 09:00","Precision invented or discarded.");});
        Check("schema-1-to-2-is-null-and-idempotent",()=>{var f=New("migrate",A+"\n"+B);var a=f.Import();var b=f.Data.Migrate(2);SameIds(a,b);Assert(b.Schema==2&&b.Records.All(x=>x["category"]==null&&DataJson.Text(x,"category_missing_reason")=="not_collected"),"Historical Category fabricated.");Assert(f.Data.Migrate(2).Id==b.Id,"Migration repeated.");Assert(b.Migrations.Count==1&&b.Migrations[0]["implementation_sha256"]!=null,"Migration provenance missing.");});
        Check("append-after-schema-migration-preserves-null-and-ids",()=>{var f=New("append-ds2",A);f.Import();var a=f.Data.Migrate(2);f.Write(A+"\n"+B);var b=f.Import();Assert(Ids(a)[0]==Ids(b)[0]&&b.Schema==2&&b.Records[1]["category"]==null,"Post-migration append failed.");});
        Check("adjacent-migration-resolver-rejects-gaps-and-downgrades",()=>{
            var steps=new[]{new DataMigration(1,2,"one","hash",r=>{}),new DataMigration(2,3,"two","hash",r=>{})};Assert(DataMigrations.Resolve(1,3,steps).Count==2,"Skipped adjacent step.");Reject(()=>DataMigrations.Resolve(1,3,steps.Take(1)),"Missing step accepted.");Reject(()=>DataMigrations.Resolve(2,1,steps),"Implicit downgrade accepted.");Reject(()=>DataMigrations.Resolve(1,2,new[]{steps[0],steps[0]}),"Ambiguous migration accepted.");
        });
        Check("future-version-and-implicit-downgrade-rejected",()=>{var f=New("future",A);f.Import();Reject(()=>f.Data.Migrate(999),"Future schema accepted.");var a=f.Data.Migrate(2);Reject(()=>f.Data.Migrate(1),"Reverse migration accepted.");Assert(f.Data.Read().Id==a.Id,"Rejected migration changed active data.");});
        Check("reviewed-rollback-keeps-all-ids-and-can-remigrate",()=>{var f=New("rollback",A+"\n"+B);var a=f.Import();var b=f.Data.Migrate(2);var restored=f.Data.Rollback(a.Id);SameIds(b,restored);Assert(restored.Schema==1,"Rollback did not restore schema.");SameIds(b,f.Data.Migrate(2));});
        Check("rollback-after-new-records-is-blocked",()=>{var f=New("rollback-new",A);var a=f.Import();f.Data.Migrate(2);f.Write(A+"\n"+B);var b=f.Import();Reject(()=>f.Data.Rollback(a.Id),"Rollback discarded new record.");Assert(f.Data.Read().Id==b.Id,"Rollback failure changed active data.");});
        Check("source-change-before-commit-is-blocked",()=>{var f=New("concurrent-source",A);var a=f.Import();f.Write(A+"\n"+B);f.Data.Fault=p=>{if(p=="before-activate")f.Write(A+"\n"+B+"\n"+C);};Reject(()=>f.Import(),"Changed source activated stale snapshot.");f.Data.Fault=null;Assert(f.Data.Read().Id==a.Id,"Source race changed generation.");Assert(f.Import().Records.Count==3,"Could not recover current source.");});
        Check("active-pointer-write-failure-keeps-verified-old-data",()=>{var f=New("pointer-lock",A);var a=f.Import();f.Write(A+"\n"+B);FileStream held=null;f.Data.Fault=p=>{if(p=="before-activate")held=new FileStream(Path.Combine(f.Store,"active.json"),FileMode.Open,FileAccess.Read,FileShare.None);};try{Reject(()=>f.Import(),"Locked active pointer was overwritten.");}finally{if(held!=null)held.Dispose();f.Data.Fault=null;}Assert(f.Data.Read().Id==a.Id,"Failed pointer write exposed new/partial data.");Assert(f.Import().Records.Count==2,"Pointer failure could not recover.");});
        Check("concurrent-writer-is-blocked",()=>{var f=New("lock",A);var a=f.Import();using(var lease=new FileStream(Path.Combine(f.Store,"store.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))Reject(()=>f.Data.Migrate(2),"Writer ignored lock.");Assert(f.Data.Read().Id==a.Id,"Lock refusal changed data.");});
        Check("corrupt-original-archive-refuses-read-and-overwrite",()=>{var f=New("raw-corrupt",A);var a=f.Import();string original=Path.Combine(f.Store,"raw",HashFile(f.Source)+".bin");File.WriteAllText(original,"corrupt");Reject(()=>f.Data.Read(),"Corrupt archive accepted.");Reject(()=>f.Import(),"Corrupt immutable archive overwritten.");Assert(File.ReadAllText(original)=="corrupt","Archive overwritten.");});
        Check("corrupt-generation-file-refuses-read",()=>{var f=New("file-corrupt",A);var a=f.Import();File.AppendAllText(Path.Combine(f.Store,"generations",a.Id,"records.jsonl"),"\n");Reject(()=>f.Data.Read(),"Corrupt canonical file accepted.");});
        Check("rehashed-duplicate-record-id-is-still-rejected",()=>{var f=New("duplicate-id",A+"\n"+B);var a=f.Import();a.Records[1]["record_id"]=a.Records[0]["record_id"];MutateAndRehash(f,a,"records.jsonl",String.Join("\n",a.Records.Select(x=>DataJson.Encode(x)).ToArray())+"\n");Reject(()=>f.Data.Read(),"Duplicate identity accepted after outer hash update.");});
        Check("rehashed-business-field-change-is-rejected-by-provenance",()=>{var f=New("modified-value",A);var a=f.Import();a.Records[0]["actual_minutes"]="999";MutateAndRehash(f,a,"records.jsonl",DataJson.Encode(a.Records[0])+"\n");Reject(()=>f.Data.Read(),"Silent fact change accepted.");});
        Check("rehashed-first-occurrence-provenance-change-is-rejected",()=>{var f=New("modified-provenance",A);f.Import();f.Write(A+"\n"+B);var a=f.Import();a.Records[0]["source_snapshot_sha256"]=HashFile(f.Source);MutateAndRehash(f,a,"records.jsonl",String.Join("\n",a.Records.Select(x=>DataJson.Encode(x)).ToArray())+"\n");Reject(()=>f.Data.Read(),"First registered provenance silently moved to another snapshot.");});
        Check("rehashed-fabricated-category-is-rejected",()=>{var f=New("fake-category",A);f.Import();var a=f.Data.Migrate(2);a.Records[0]["category"]="IELTS";MutateAndRehash(f,a,"records.jsonl",DataJson.Encode(a.Records[0])+"\n");Reject(()=>f.Data.Read(),"Fabricated legacy classification accepted.");});
        Check("rehashed-changed-migration-code-is-rejected",()=>{var f=New("fake-migration",A);f.Import();var a=f.Data.Migrate(2);a.Migrations[0]["implementation_sha256"]=new string('0',64);MutateAndRehash(f,a,"migration-results.json",DataJson.Encode(new Dictionary<string,object>{{"migration_ledger_version",1},{"steps",a.Migrations}}));Reject(()=>f.Data.Read(),"Changed published migration accepted.");});
        Check("rehashed-unknown-migration-ledger-is-rejected",()=>{var f=New("future-ledger",A);var a=f.Import();MutateAndRehash(f,a,"migration-results.json","{\"migration_ledger_version\":999,\"steps\":[]}");Reject(()=>f.Data.Read(),"Future migration ledger accepted.");});
        Check("rehashed-statistics-change-is-rejected",()=>{var f=New("view-corrupt",A);var a=f.Import();string path=Path.Combine(f.Store,"generations",a.Id,"views","report-data.json");var view=JsonFile(path);((Dictionary<string,object>)view["weekly"])["actual_minutes"]=999;MutateAndRehash(f,a,"views/report-data.json",DataJson.Encode(view));Reject(()=>f.Data.Read(),"Changed statistic accepted.");});
        Check("restored-store-keeps-namespace-ids-and-migration-history",()=>{var f=New("restore",A);f.Import();f.Data.Migrate(2);f.Write(A+"\n"+B);var a=f.Import();string restored=Path.Combine(f.Dir,"restore");CopyStore(f.Store,restored);var store=new FileRecordStore(restored);SameIds(a,store.Read());Assert(FileRecordStore.Initialize(restored)==f.SourceId,"Backup restore changed namespace.");Assert(store.Import(f.Source,f.SourceId).Id==a.Id,"Restored snapshot duplicated.");});
        Check("corrupt-allocation-map-blocks-read-and-reimport",()=>{var f=New("plan-corrupt",A);var a=f.Import();string plan=Path.Combine(f.Store,"imports",HashFile(f.Source),"identity-plan.json");File.WriteAllText(plan,"{}");Reject(()=>f.Data.Read(),"Damaged plan accepted.");Reject(()=>f.Import(),"Damaged identity plan silently reset.");});
        Check("active-pointer-traversal-and-unknown-version-rejected",()=>{var f=New("pointer-corrupt",A);f.Import();File.WriteAllText(Path.Combine(f.Store,"active.json"),"{\"pointer_version\":1,\"generation_id\":\"../../escape\",\"manifest_sha256\":\"x\"}");Reject(()=>f.Data.Read(),"Unsafe generation path accepted.");File.WriteAllText(Path.Combine(f.Store,"active.json"),"{\"pointer_version\":999}");Reject(()=>f.Data.Read(),"Future pointer accepted.");});
        Check("export-preserves-source-and-legacy-report-contract",()=>{var f=New("view",Header+"\n"+A+"\n"+B);var a=f.Import();string output=Path.Combine(f.Dir,"view");f.Data.ExportView(output,DateTimeOffset.Parse("2030-01-02T08:00:00+08:00"));var view=JsonFile(Path.Combine(output,"report-data.json"));var daily=(Dictionary<string,object>)view["daily"];Assert(DataJson.Int(view,"schema_version")==2&&DataJson.Int(view,"data_schema_version")==1&&DataJson.Text(view,"statistics_version")=="rolling_7d_v1","Version meanings conflated.");Assert(Convert.ToDecimal(daily["actual_minutes"])==70&&Convert.ToDecimal(daily["effective_minutes"])==55&&Convert.ToDecimal(daily["effective_percent"])==78.6m,"Legacy statistics changed.");Assert(HashFile(Path.Combine(output,"work_log.csv"))==HashFile(f.Source),"Attachment bytes rewritten.");Reject(()=>f.Data.ExportView(output,DateTimeOffset.UtcNow),"Existing export overwritten.");Assert(f.Data.Read().Id==a.Id,"Export changed active generation.");});
        Check("zero-minutes-and-effective-overrun-are-not-corrected",()=>{var f=New("zero","2030-01-01 09:00,Zero,0,5");f.Import();string output=Path.Combine(f.Dir,"view");f.Data.ExportView(output,DateTimeOffset.Parse("2030-01-02T08:00:00+08:00"));var view=JsonFile(Path.Combine(output,"report-data.json"));var daily=(Dictionary<string,object>)view["daily"];Assert(daily["effective_percent"]==null&&Convert.ToDecimal(daily["effective_minutes"])==5,"Zero or overrun corrected.");});
        Check("failed-activation-does-not-touch-mail-state",()=>{var f=New("mail-separate",A);string path=Path.Combine(f.Dir,"mail-state.json");File.WriteAllText(path,"{\"receipts\":{\"weekly:2030-01-05\":\"fictional\"}}");string hash=HashFile(path);f.Import();f.Data.Migrate(2);Assert(HashFile(path)==hash,"Data migration changed mail receipts.");});
        Check("legacy-statistics-match-original-sync-program",()=>{
            string yesterday=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date.AddDays(-1).ToString("yyyy-MM-dd");var f=New("old-sync",yesterday+" 09:00,Audit A,12.5,10.5\n"+yesterday+" 10:00,Audit B,7.5,5.5");f.Import();
            string oldDir=Path.Combine(f.Dir,"old");Directory.CreateDirectory(oldDir);File.WriteAllText(Path.Combine(oldDir,"config.json"),DataJson.Encode(new Dictionary<string,object>{{"source",f.Source},{"max_bytes",16777216}}),DataJson.Utf8);
            string bin=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);Assert(RunProgram(Path.Combine(bin,"Paulatim.exe"),"--config \""+Path.Combine(oldDir,"config.json")+"\"")==0,"Original sync failed.");
            var old=JsonFile(Path.Combine(oldDir,"report-data.json"));string viewDir=Path.Combine(f.Dir,"view");f.Data.ExportView(viewDir,DateTimeOffset.Parse(DataJson.Text(old,"snapshot_at_utc"),CultureInfo.InvariantCulture));var view=JsonFile(Path.Combine(viewDir,"report-data.json"));StripViewIds(view);
            foreach(var key in new[]{"daily","weekly","rows","raw_csv_base64","has_header","send_weekly_today"})Assert(DataJson.Encode(view[key])==DataJson.Encode(old[key]),"Original sync contract changed: "+key);
        });
        Check("formal-weekly-html-and-attachment-match-original-dispatch",()=>{
            var f=New("old-mail",A+"\n"+B+"\n2030-01-06 10:00,Outside period,10,5");f.Import();string oldDir=Path.Combine(f.Dir,"old");Directory.CreateDirectory(oldDir);File.WriteAllText(Path.Combine(oldDir,"config.json"),DataJson.Encode(new Dictionary<string,object>{{"source",f.Source},{"max_bytes",16777216}}),DataJson.Utf8);
            string bin=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);Assert(RunProgram(Path.Combine(bin,"Paulatim.exe"),"--config \""+Path.Combine(oldDir,"config.json")+"\"")==0,"Original sync failed.");var old=JsonFile(Path.Combine(oldDir,"report-data.json"));
            string viewDir=Path.Combine(f.Dir,"view");f.Data.ExportView(viewDir,DateTimeOffset.Parse(DataJson.Text(old,"snapshot_at_utc"),CultureInfo.InvariantCulture));var view=JsonFile(Path.Combine(viewDir,"report-data.json"));old["source_modified_at_utc"]=null;
            var dispatch=Assembly.LoadFrom(Path.Combine(bin,"PaulatimDispatch.exe")).GetType("PaulatimDispatch");var request=Activator.CreateInstance(dispatch.GetNestedType("Request"));foreach(var kv in new Dictionary<string,string>{{"key","weekly:2030-01-05"},{"kind","weekly"},{"start_date","2029-12-30"},{"end_date","2030-01-05"},{"subject","[Paulatim周报] isolated compatibility test"}})request.GetType().GetProperty(kv.Key).SetValue(request,kv.Value,null);
            var method=dispatch.GetMethod("BuildMail",BindingFlags.NonPublic|BindingFlags.Static);var a=method.Invoke(null,new object[]{request,old});var b=method.Invoke(null,new object[]{request,view});Assert(DataJson.Encode(a)==DataJson.Encode(b),"Formal HTML or original attachment changed.");
        });
        Check("utf8-bom-and-known-header-are-byte-preserved",()=>{var f=New("bom",A);File.WriteAllText(f.Source,Header+"\r\n"+A,new System.Text.UTF8Encoding(true));string hash=HashFile(f.Source);f.Import();f.Data.ExportView(Path.Combine(f.Dir,"view"),DateTimeOffset.UtcNow);Assert(HashFile(Path.Combine(f.Dir,"view","work_log.csv"))==hash,"BOM/source bytes changed.");});
        Check("cli-init-import-verify-migrate-export-are-explicit",()=>{
            var f=New("cli",A);string exe=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"PaulatimData.exe");Assert(RunProgram(exe,"init --store \""+f.Store+"\"")==0,"CLI idempotent init failed.");Assert(RunProgram(exe,"import --store \""+f.Store+"\" --source \""+f.Source+"\" --source-id "+f.SourceId)==0,"CLI import failed.");var baseline=f.Data.Read();Assert(RunProgram(exe,"verify --store \""+f.Store+"\"")==0,"CLI verify failed.");Assert(RunProgram(exe,"migrate --store \""+f.Store+"\" --target 2")==0,"CLI migrate failed.");Assert(RunProgram(exe,"export-view --store \""+f.Store+"\" --output \""+Path.Combine(f.Dir,"view")+"\" --at 2030-01-02T08:00:00.0000000+08:00")==0,"CLI export failed.");Assert(RunProgram(exe,"rollback --store \""+f.Store+"\" --generation "+baseline.Id)==0&&f.Data.Read().Schema==1,"CLI rollback failed.");Assert(RunProgram(exe,"init --store \""+f.Store+"\" --unknown value")!=0,"CLI unknown argument accepted.");
        });
        Check("metadata-and-decimal-contract-do-not-coerce-types",()=>{Reject(()=>DataJson.Int(new Dictionary<string,object>{{"version",1.4m}},"version"),"Fractional version was coerced.");Reject(()=>DataJson.Int(new Dictionary<string,object>{{"version","1"}},"version"),"String version was coerced.");var f=New("numeric-field-type",A);var a=f.Import();a.Records[0]["actual_minutes"]=30;MutateAndRehash(f,a,"records.jsonl",DataJson.Encode(a.Records[0])+"\n");Reject(()=>f.Data.Read(),"Numeric storage silently replaced decimal text contract.");});
        foreach(var point in new[]{"after-archive","after-identity-plan","after-records","after-files","after-manifest","before-activate","after-activate"})Check("abrupt-process-exit-"+point,()=>{
            var f=New("crash-"+point,A);var before=f.Import();f.Write(A+"\n"+B);
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"crash-import \""+f.Store+"\" \""+f.Source+"\" "+f.SourceId+" unused "+point);start.UseShellExecute=false;start.CreateNoWindow=true;
            using(var process=Process.Start(start)){Assert(process.WaitForExit(30000),"Crash host timed out.");Assert(process.ExitCode==73,"Fault point not reached.");}
            var restarted=new FileRecordStore(f.Store);var observed=restarted.Read();Assert(observed.Records.Count==(point=="after-activate"?2:1),"Restart read partial/wrong generation.");
            string plan=Path.Combine(f.Store,"imports",HashFile(f.Source),"identity-plan.json");string allocated=File.Exists(plan)?DataJson.Text(DataJson.Rows(JsonFile(plan)["allocations"])[1],"record_id"):null;
            var recovered=restarted.Import(f.Source,f.SourceId);Assert(recovered.Records.Count==2&&Ids(before)[0]==Ids(recovered)[0],"Recovery duplicated/reidentified history.");if(allocated!=null)Assert(Ids(recovered)[1]==allocated,"Recovery regenerated prepared permanent ID.");Assert(restarted.Import(f.Source,f.SourceId).Id==recovered.Id,"Recovered import not idempotent.");
        });
        Check("migration-step-failure-preserves-active-and-retries",()=>{var f=New("migration-fail",A);var a=f.Import();f.Data.Fault=p=>{if(p=="after-migration-2")throw new IOException("Injected failure");};Reject(()=>f.Data.Migrate(2),"Migration fault ignored.");f.Data.Fault=null;Assert(f.Data.Read().Id==a.Id,"Failed step activated.");SameIds(a,f.Data.Migrate(2));});
        Check("rollback-before-pointer-failure-preserves-current",()=>{var f=New("rollback-fail",A);var a=f.Import();var b=f.Data.Migrate(2);f.Data.Fault=p=>{if(p=="before-rollback")throw new IOException("Injected failure");};Reject(()=>f.Data.Rollback(a.Id),"Rollback fault ignored.");f.Data.Fault=null;Assert(f.Data.Read().Id==b.Id,"Failed rollback changed current.");});
        var summary=new Dictionary<string,object>{{"checks",Results.Count},{"passed",Results.Count(x=>(bool)x["passed"])},{"results",Results}};File.WriteAllText(Path.Combine(Root,"data-test-results.json"),DataJson.Encode(summary),DataJson.Utf8);
        Console.WriteLine("Data foundation checks="+Results.Count+"; passed="+Results.Count(x=>(bool)x["passed"]));return Results.All(x=>(bool)x["passed"])?0:1;
    }
}
