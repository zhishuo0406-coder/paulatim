using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.VisualBasic.FileIO;

public interface IRecordStore
{
    DataGeneration Read();
    DataGeneration Import(string source, string sourceId);
    DataGeneration Migrate(int targetVersion);
    DataGeneration Rollback(string generationId);
    void ExportView(string newDirectory, DateTimeOffset at);
}

public sealed class DataGeneration
{
    public string Id;
    public string ManifestHash;
    public int Schema;
    public Dictionary<string,object> Manifest;
    public List<Dictionary<string,object>> Records;
    public List<Dictionary<string,object>> Imports;
    public List<Dictionary<string,object>> Migrations;
}

public static class DataJson
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=134217728, RecursionLimit=100 };
    public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false,true);
    public static Dictionary<string,object> Object(string text) {return (Dictionary<string,object>)Json.DeserializeObject(text);}
    public static string Encode(object value) {
        if(value==null)return "null";
        var map=value as IDictionary;
        if(map!=null) {
            var pairs=new List<string>();
            foreach(string key in map.Keys.Cast<string>().OrderBy(x=>x,StringComparer.Ordinal)) pairs.Add(Json.Serialize(key)+":"+Encode(map[key]));
            return "{"+String.Join(",",pairs.ToArray())+"}";
        }
        if(!(value is string) && value is IEnumerable) {
            var items=new List<string>();foreach(var item in (IEnumerable)value)items.Add(Encode(item));
            return "["+String.Join(",",items.ToArray())+"]";
        }
        if(value is string || value is decimal || value.GetType().IsPrimitive)return Json.Serialize(value);
        // Anonymous statistics objects must use the same sorted representation after JSON readback.
        return Encode(Json.DeserializeObject(Json.Serialize(value)));
    }
    public static string Hash(byte[] bytes) {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    public static string HashText(string text) {return Hash(Utf8.GetBytes(text));}
    public static string Text(Dictionary<string,object> value,string key) {if(!(value[key] is string))throw new InvalidDataException("Expected string field: "+key);return (string)value[key];}
    public static int Int(Dictionary<string,object> value,string key) {if(!(value[key] is int))throw new InvalidDataException("Expected integer field: "+key);return (int)value[key];}
    public static List<Dictionary<string,object>> Rows(object value) {return ((IEnumerable)value).Cast<object>().Select(x=>(Dictionary<string,object>)x).ToList();}
    public static string[] Strings(object value) {return ((IEnumerable)value).Cast<object>().Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture)).ToArray();}
}

internal sealed class LegacyCsv
{
    internal byte[] Bytes;
    internal string Hash, Text, EncodingName;
    internal bool Header;
    internal List<string[]> Fields=new List<string[]>();
    internal List<int> RecordNumbers=new List<int>();
    internal List<string> Warnings=new List<string>();
    internal static LegacyCsv Parse(byte[] bytes) {
        if(bytes.Length==0 || bytes.Length>16777216)throw new InvalidDataException("CSV is empty or exceeds 16 MiB; no records will be skipped.");
        var csv=new LegacyCsv {Bytes=bytes,Hash=DataJson.Hash(bytes)};
        using(var input=new MemoryStream(bytes))
        using(var reader=new StreamReader(input,DataJson.Utf8,true)) {csv.Text=reader.ReadToEnd();csv.EncodingName=reader.CurrentEncoding.WebName;}
        using(var input=new StringReader(csv.Text))
        using(var parser=new TextFieldParser(input)) {
            parser.TextFieldType=FieldType.Delimited;parser.SetDelimiters(",");parser.HasFieldsEnclosedInQuotes=true;parser.TrimWhiteSpace=false;
            int number=0;var seen=new HashSet<string>(StringComparer.Ordinal);
            while(!parser.EndOfData) {
                var fields=parser.ReadFields();if(fields==null)continue;number++;
                if(fields.Length!=4)throw new InvalidDataException("Legacy CSV requires exactly four fields at record "+number+".");
                if(number==1 && IsHeader(fields)) {csv.Header=true;continue;}
                DateTime time=Time(fields[0]);decimal actual=Minutes(fields[2]),effective=Minutes(fields[3]);
                if(String.IsNullOrWhiteSpace(fields[1]))throw new InvalidDataException("Empty task at record "+number+".");
                if(effective>actual)csv.Warnings.Add("Effective exceeds actual at record "+number+"; retained without correction.");
                if(!seen.Add(DataJson.Encode(fields)))csv.Warnings.Add("Possible duplicate at record "+number+"; retained without deduplication.");
                // Date-sensitive warnings are generated in the view with its explicit clock.
                csv.Fields.Add(fields);csv.RecordNumbers.Add(number);
            }
        }
        if(csv.Fields.Count==0)throw new InvalidDataException("No data records; empty or header-only input cannot delete history.");
        return csv;
    }
    internal static DateTime Time(string value) {
        DateTime time;
        if(!DateTime.TryParseExact(value.Trim(),new[]{"yyyy-MM-dd HH:mm","yyyy-MM-dd HH:mm:ss","yyyy-MM-ddTHH:mm","yyyy-MM-ddTHH:mm:ss"},CultureInfo.InvariantCulture,DateTimeStyles.None,out time))throw new InvalidDataException("Invalid legacy timestamp.");
        return time;
    }
    internal static decimal Minutes(string value) {
        decimal result;
        if(!Decimal.TryParse(value.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out result)||result<0)throw new InvalidDataException("Invalid nonnegative decimal minutes.");return result;
    }
    private static bool IsHeader(string[] fields) {
        string header=String.Join(",",fields.Select(x=>x.Trim()).ToArray());
        return header=="时间,事项,实际分钟,有效分钟"||header=="日期时间,事项,实际分钟,有效分钟"||header.Equals("timestamp,task,actual_minutes,effective_minutes",StringComparison.OrdinalIgnoreCase)||header.Equals("datetime,task,actual_minutes,effective_minutes",StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DataMigration
{
    public readonly int From,To;
    public readonly string Id,ImplementationHash;
    public readonly Action<IList<Dictionary<string,object>>> Transform;
    public DataMigration(int from,int to,string id,string implementationHash,Action<IList<Dictionary<string,object>>> transform) {From=from;To=to;Id=id;ImplementationHash=implementationHash;Transform=transform;}
}

public static class DataMigrations
{
    internal static readonly DataMigration[] Catalog={new DataMigration(1,2,"data-1-to-2-category-null-v1",PaulatimDataBuildInfo.Migration12Hash,AddCategoryV2.Apply)};
    public static IList<DataMigration> Resolve(int from,int to,IEnumerable<DataMigration> catalog) {
        if(to<from)throw new InvalidDataException("Schema downgrade requires a reviewed rollback, not a reverse migration.");
        var all=catalog.ToList();var path=new List<DataMigration>();
        while(from<to) {
            var choices=all.Where(x=>x.From==from && x.To==from+1).ToList();
            if(choices.Count!=1)throw new InvalidDataException("Missing or ambiguous adjacent schema migration.");
            path.Add(choices[0]);from++;
        }
        return path;
    }
}

public sealed class FileRecordStore : IRecordStore
{
    private readonly string Root;
    // Fault injection is available to isolated test hosts; the normal CLI never sets it.
    public Action<string> Fault;
    private static readonly string[] CoreKeys={"record_id","record_revision","timestamp","timestamp_input","timestamp_precision","timestamp_interpretation","task","actual_minutes","effective_minutes","source_id","source_snapshot_sha256","source_data_ordinal","source_fields","extensions"};
    private static readonly string[] FileNames={"records.jsonl","identity-map.jsonl","import-ledger.jsonl","migration-results.json","views/report-data.json","views/work_log.csv"};
    public FileRecordStore(string root) {Root=Path.GetFullPath(root);Info();}
    public static string Initialize(string root) {
        root=Path.GetFullPath(root);
        if(root==Path.GetPathRoot(root))throw new InvalidDataException("Do not use a drive root as a data store.");
        if(File.Exists(Path.Combine(root,"store.json")))return DataJson.Text(new FileRecordStore(root).Info(),"source_id");
        if(Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length!=0)throw new InvalidDataException("Initialize only a new or empty directory.");
        Directory.CreateDirectory(root);
        using(var lease=new FileStream(Path.Combine(root,"store.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            var info=new Dictionary<string,object>{{"storage_format_version",1},{"identity_policy_version",1},{"source_id",Guid.NewGuid().ToString("D")},{"created_at_utc",DateTimeOffset.UtcNow.ToString("o")}};
            Immutable(Path.Combine(root,"store.json"),DataJson.Utf8.GetBytes(DataJson.Encode(info)));
            foreach(var name in new[]{"raw","captures","imports","generations"})Directory.CreateDirectory(Path.Combine(root,name));
            return DataJson.Text(info,"source_id");
        }
    }
    private Dictionary<string,object> Info() {
        var info=DataJson.Object(File.ReadAllText(Path.Combine(Root,"store.json"),DataJson.Utf8));
        if(DataJson.Int(info,"storage_format_version")!=1||DataJson.Int(info,"identity_policy_version")!=1||!Uuid(DataJson.Text(info,"source_id")))throw new InvalidDataException("Unsupported or damaged store registry; never reset source identity.");return info;
    }
    private FileStream Lease() {return new FileStream(Path.Combine(Root,"store.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
    private void Point(string point) {if(Fault!=null)Fault(point);}
    private string GenerationPath(string id) {if(!Regex.IsMatch(id??"","\\A[0-9a-f]{32}\\z"))throw new InvalidDataException("Invalid generation identity.");return Path.Combine(Root,"generations",id);}
    private string RawPath(string sha) {if(!Regex.IsMatch(sha??"","\\A[0-9a-f]{64}\\z"))throw new InvalidDataException("Invalid source digest.");return Path.Combine(Root,"raw",sha+".bin");}
    private static bool Uuid(string id) {Guid parsed;return Guid.TryParseExact(id,"D",out parsed)&&parsed!=Guid.Empty&&id==parsed.ToString("D");}
    private static void Immutable(string path,byte[] bytes) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if(File.Exists(path)) {if(DataJson.Hash(File.ReadAllBytes(path))!=DataJson.Hash(bytes))throw new InvalidDataException("Immutable object already exists with different bytes.");return;}
        // Flush a temporary object before publishing via a non-overwriting rename.
        string temp=Path.Combine(Path.GetDirectoryName(path),".write-"+Guid.NewGuid().ToString("N")+".tmp");
        try {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}
            File.Move(temp,path);
        } finally {if(File.Exists(temp))File.Delete(temp);}
    }
    private static void Pointer(string path,byte[] bytes) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        } finally {if(File.Exists(temp))File.Delete(temp);}
    }
    private byte[] Raw(string sha) {byte[] bytes=File.ReadAllBytes(RawPath(sha));if(DataJson.Hash(bytes)!=sha)throw new InvalidDataException("Original archive digest mismatch.");return bytes;}
    private static byte[] ReadSource(string path) {
        using(var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if(source.Length==0||source.Length>16777216)throw new InvalidDataException("Source is empty or exceeds the legacy source limit.");
            using(var memory=new MemoryStream()){source.CopyTo(memory);return memory.ToArray();}
        }
    }
    private List<Dictionary<string,object>> Lines(string path) {
        var rows=new List<Dictionary<string,object>>();
        foreach(var line in File.ReadAllLines(path,DataJson.Utf8)){if(String.IsNullOrWhiteSpace(line))throw new InvalidDataException("Empty canonical JSONL record.");rows.Add(DataJson.Object(line));}return rows;
    }
    private static byte[] LineBytes(IEnumerable<Dictionary<string,object>> rows) {return DataJson.Utf8.GetBytes(String.Join("\n",rows.Select(x=>DataJson.Encode(x)).ToArray())+(rows.Any()?"\n":""));}
    private static string Semantic(IEnumerable<Dictionary<string,object>> rows) {return DataJson.HashText(DataJson.Encode(rows.OrderBy(x=>DataJson.Text(x,"record_id"),StringComparer.Ordinal).ToArray()));}
    private void ValidateRecords(IList<Dictionary<string,object>> rows,int schema,string sourceId) {
        if(schema!=1&&schema!=2)throw new InvalidDataException("Unsupported data schema; refusing implicit field conversion.");
        if(rows.Count==0)throw new InvalidDataException("Canonical dataset cannot be empty for a registered legacy source.");
        var ids=new HashSet<string>(StringComparer.Ordinal);var archives=new Dictionary<string,LegacyCsv>();
        for(int i=0;i<rows.Count;i++) {
            var row=rows[i];string id=DataJson.Text(row,"record_id");
            var expected=new HashSet<string>(CoreKeys,StringComparer.Ordinal);if(schema==2){expected.Add("category");expected.Add("category_missing_reason");}
            if(!expected.SetEquals(row.Keys)||!Uuid(id)||!ids.Add(id)||DataJson.Int(row,"record_revision")!=1||DataJson.Text(row,"source_id")!=sourceId||DataJson.Int(row,"source_data_ordinal")!=i+1)throw new InvalidDataException("Invalid/duplicate identity, revision, source or schema fields.");
            var fields=DataJson.Strings(row["source_fields"]);if(fields.Length!=4)throw new InvalidDataException("Missing legacy provenance fields.");
            DateTime time=LegacyCsv.Time(fields[0]);decimal actual=LegacyCsv.Minutes(fields[2]),effective=LegacyCsv.Minutes(fields[3]);
            if(String.IsNullOrWhiteSpace(fields[1])||DataJson.Text(row,"timestamp")!=time.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)||DataJson.Text(row,"timestamp_input")!=fields[0]||DataJson.Text(row,"task")!=fields[1]||DataJson.Text(row,"actual_minutes")!=actual.ToString(CultureInfo.InvariantCulture)||DataJson.Text(row,"effective_minutes")!=effective.ToString(CultureInfo.InvariantCulture)||DataJson.Text(row,"timestamp_precision")!=(fields[0].Trim().Length==16?"minute":"second")||DataJson.Text(row,"timestamp_interpretation")!="legacy_beijing_policy_v1"||!(row["extensions"] is IDictionary))throw new InvalidDataException("Canonical values do not match exact legacy provenance.");
            string sha=DataJson.Text(row,"source_snapshot_sha256");LegacyCsv archive;
            if(!archives.TryGetValue(sha,out archive)){archive=LegacyCsv.Parse(Raw(sha));archives.Add(sha,archive);}
            if(archive.Fields.Count<=i||DataJson.Encode(archive.Fields[i])!=DataJson.Encode(fields))throw new InvalidDataException("Record occurrence does not match its immutable source.");
            if(schema==2 && (row["category"]!=null||DataJson.Text(row,"category_missing_reason")!="not_collected"))throw new InvalidDataException("This foundation supports legacy unknown Category only; do not fabricate history.");
        }
    }
    private static void SameCore(IList<Dictionary<string,object>> before,IList<Dictionary<string,object>> after) {
        if(before.Count!=after.Count)throw new InvalidDataException("Migration or rollback changed record count.");
        var byId=before.ToDictionary(x=>DataJson.Text(x,"record_id"),x=>x,StringComparer.Ordinal);
        foreach(var row in after) {
            Dictionary<string,object> old;if(!byId.TryGetValue(DataJson.Text(row,"record_id"),out old))throw new InvalidDataException("Permanent ID set changed.");
            foreach(var key in CoreKeys)if(DataJson.Encode(old[key])!=DataJson.Encode(row[key]))throw new InvalidDataException("Migration changed protected field: "+key);
        }
    }
    private DataGeneration Active() {
        var pointer=DataJson.Object(File.ReadAllText(Path.Combine(Root,"active.json"),DataJson.Utf8));
        if(DataJson.Int(pointer,"pointer_version")!=1)throw new InvalidDataException("Unsupported active pointer.");
        return Load(DataJson.Text(pointer,"generation_id"),DataJson.Text(pointer,"manifest_sha256"));
    }
    public DataGeneration Read() {using(var lease=Lease())return Active();}
    private DataGeneration Load(string id,string expectedManifestHash) {
        string dir=GenerationPath(id);byte[] manifestBytes=File.ReadAllBytes(Path.Combine(dir,"manifest.json"));
        string hash=DataJson.Hash(manifestBytes);if(hash!=expectedManifestHash)throw new InvalidDataException("Generation manifest mismatch.");
        var manifest=DataJson.Object(DataJson.Utf8.GetString(manifestBytes));
        if(DataJson.Text(manifest,"generation_id")!=id||DataJson.Int(manifest,"storage_format_version")!=1||DataJson.Int(manifest,"identity_policy_version")!=1||DataJson.Text(manifest,"input_format")!="legacy_csv"||DataJson.Int(manifest,"input_schema_version")!=1||DataJson.Text(manifest,"source_id")!=DataJson.Text(Info(),"source_id")||DataJson.Text(manifest,"statistics_version")!="formal_calendar_v1"||DataJson.Int(manifest,"report_schema_version")!=2)throw new InvalidDataException("Unsupported generation metadata.");
        var hashes=(Dictionary<string,object>)manifest["files"];
        if(!new HashSet<string>(FileNames,StringComparer.Ordinal).SetEquals(hashes.Keys))throw new InvalidDataException("Unexpected generation file inventory.");
        foreach(var name in FileNames)if(DataJson.Hash(File.ReadAllBytes(Path.Combine(dir,name)))!=Convert.ToString(hashes[name]))throw new InvalidDataException("Generation file mismatch: "+name);
        var migrationLedger=DataJson.Object(File.ReadAllText(Path.Combine(dir,"migration-results.json"),DataJson.Utf8));
        if(DataJson.Int(migrationLedger,"migration_ledger_version")!=1)throw new InvalidDataException("Unsupported migration ledger version.");
        var result=new DataGeneration{Id=id,ManifestHash=hash,Schema=DataJson.Int(manifest,"data_schema_version"),Manifest=manifest,Records=Lines(Path.Combine(dir,"records.jsonl")),Imports=Lines(Path.Combine(dir,"import-ledger.jsonl")),Migrations=DataJson.Rows(migrationLedger["steps"])};
        ValidateRecords(result.Records,result.Schema,DataJson.Text(manifest,"source_id"));
        if(result.Records.Count!=DataJson.Int(manifest,"record_count")||Semantic(result.Records)!=DataJson.Text(manifest,"semantic_sha256"))throw new InvalidDataException("Canonical count or semantic digest mismatch.");
        var map=Lines(Path.Combine(dir,"identity-map.jsonl"));if(map.Count!=result.Records.Count)throw new InvalidDataException("Identity map count mismatch.");
        for(int i=0;i<map.Count;i++)if(DataJson.Int(map[i],"data_ordinal")!=i+1||DataJson.Text(map[i],"record_id")!=DataJson.Text(result.Records[i],"record_id")||DataJson.Text(map[i],"fields_sha256")!=DataJson.HashText(DataJson.Encode(result.Records[i]["source_fields"])))throw new InvalidDataException("Identity map does not match canonical records.");
        var snapshots=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in result.Imports) {
            string sha=DataJson.Text(entry,"source_sha256");if(!snapshots.Add(sha)||DataJson.Text(entry,"source_id")!=DataJson.Text(manifest,"source_id"))throw new InvalidDataException("Duplicate or mismatched import ledger entry.");
            var imported=LegacyCsv.Parse(Raw(sha));
            string planPath=Path.Combine(Root,"imports",sha,"identity-plan.json");
            if(DataJson.Hash(File.ReadAllBytes(planPath))!=DataJson.Text(entry,"identity_plan_sha256"))throw new InvalidDataException("Identity allocation plan mismatch.");
            var plan=DataJson.Object(File.ReadAllText(planPath,DataJson.Utf8));var allocations=DataJson.Rows(plan["allocations"]);
            if(DataJson.Int(plan,"identity_policy_version")!=1||DataJson.Text(plan,"source_id")!=DataJson.Text(manifest,"source_id")||DataJson.Text(plan,"source_sha256")!=sha||allocations.Count!=imported.Fields.Count||DataJson.Int(entry,"record_count")!=imported.Fields.Count||allocations.Count>result.Records.Count)throw new InvalidDataException("Import plan provenance/count mismatch.");
            for(int i=0;i<allocations.Count;i++)if(DataJson.Int(allocations[i],"data_ordinal")!=i+1||DataJson.Text(allocations[i],"fields_sha256")!=DataJson.HashText(DataJson.Encode(imported.Fields[i]))||DataJson.Text(allocations[i],"record_id")!=DataJson.Text(result.Records[i],"record_id")||DataJson.Encode(imported.Fields[i])!=DataJson.Encode(result.Records[i]["source_fields"]))throw new InvalidDataException("Committed identity plan differs from permanent occurrence mapping.");
        }
        for(int i=0;i<result.Records.Count;i++) {
            var first=result.Imports.FirstOrDefault(x=>DataJson.Int(x,"record_count")>i);
            if(first==null||DataJson.Text(result.Records[i],"source_snapshot_sha256")!=DataJson.Text(first,"source_sha256"))throw new InvalidDataException("Record provenance no longer points to its first registered occurrence.");
        }
        string latest=DataJson.Text(manifest,"latest_source_sha256");if(result.Imports.Count==0||!result.Imports.Any(x=>DataJson.Text(x,"source_sha256")==latest))throw new InvalidDataException("Latest source is not a committed import boundary.");
        var csv=LegacyCsv.Parse(Raw(latest));if(csv.Fields.Count!=result.Records.Count)throw new InvalidDataException("Latest source lost or added unmapped records.");
        if(DataJson.Text(manifest,"source_encoding")!=csv.EncodingName)throw new InvalidDataException("Source encoding declaration does not match archived input.");
        for(int i=0;i<csv.Fields.Count;i++)if(DataJson.Encode(csv.Fields[i])!=DataJson.Encode(result.Records[i]["source_fields"]))throw new InvalidDataException("Latest source differs from registered occurrences.");
        if(DataJson.Hash(File.ReadAllBytes(Path.Combine(dir,"views/work_log.csv")))!=latest)throw new InvalidDataException("Compatibility CSV differs from original bytes.");
        ValidateView(DataJson.Object(File.ReadAllText(Path.Combine(dir,"views/report-data.json"),DataJson.Utf8)),result,csv);
        ValidateMigrationHistory(result);
        return result;
    }
    private static void ValidateMigrationHistory(DataGeneration generation) {
        int version=1;
        foreach(var entry in generation.Migrations) {
            var step=DataMigrations.Catalog.SingleOrDefault(x=>x.Id==DataJson.Text(entry,"migration_id"));
            if(step==null||step.From!=version||DataJson.Int(entry,"from")!=step.From||DataJson.Int(entry,"to")!=step.To||DataJson.Text(entry,"implementation_sha256")!=step.ImplementationHash)throw new InvalidDataException("Unsupported or changed published migration.");
            int count=DataJson.Int(entry,"record_count");if(count<1||count>generation.Records.Count)throw new InvalidDataException("Invalid historical migration count.");
            var after=CloneRows(generation.Records.Take(count));var before=CloneRows(after);
            // Only DS1 -> DS2 is published. Future steps must add their own history verifier.
            foreach(var record in before){record.Remove("category");record.Remove("category_missing_reason");}
            if(DataJson.Text(entry,"before_semantic_sha256")!=Semantic(before)||DataJson.Text(entry,"after_semantic_sha256")!=Semantic(after))throw new InvalidDataException("Published migration protected-field verification mismatch.");
            version=step.To;
        }
        if(version!=generation.Schema)throw new InvalidDataException("Schema and adjacent migration history disagree.");
    }
    public DataGeneration Import(string source,string sourceId) {
        using(var lease=Lease()) {
            if(sourceId!=DataJson.Text(Info(),"source_id"))throw new InvalidDataException("Wrong source identity; paths are not record identities.");
            source=Path.GetFullPath(source);if(source.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source must be outside the managed store.");
            byte[] bytes=ReadSource(source);string sha=DataJson.Hash(bytes);
            Immutable(RawPath(sha),bytes);Point("after-archive");
            // Captures are independent evidence; a bad input may be archived but never activated.
            string captureId=Guid.NewGuid().ToString("N");var captured=DateTimeOffset.UtcNow;
            Immutable(Path.Combine(Root,"captures",captureId+".json"),DataJson.Utf8.GetBytes(DataJson.Encode(new Dictionary<string,object>{{"capture_id",captureId},{"source_id",sourceId},{"source_sha256",sha},{"bytes",bytes.Length},{"captured_at_utc",captured.ToString("o")},{"source_path",source},{"input_format","legacy_csv"},{"input_schema_version",1}})));
            var csv=LegacyCsv.Parse(bytes);
            DataGeneration current=File.Exists(Path.Combine(Root,"active.json"))?Active():null;
            if(current!=null) {
                if(csv.Fields.Count<current.Records.Count)throw new InvalidDataException("Source shortened; refusing silent history deletion.");
                for(int i=0;i<current.Records.Count;i++)if(DataJson.Encode(csv.Fields[i])!=DataJson.Encode(current.Records[i]["source_fields"]))throw new InvalidDataException("Legacy history was edited, inserted or reordered; identity reconciliation is required.");
                if(sha==DataJson.Text(current.Manifest,"latest_source_sha256"))return current;
            }
            int oldCount=current==null?0:current.Records.Count;
            string planPath=Path.Combine(Root,"imports",sha,"identity-plan.json");
            Dictionary<string,object> plan;
            if(File.Exists(planPath))plan=DataJson.Object(File.ReadAllText(planPath,DataJson.Utf8));
            else {
                var allocations=new List<Dictionary<string,object>>();
                for(int i=0;i<csv.Fields.Count;i++)allocations.Add(new Dictionary<string,object>{{"data_ordinal",i+1},{"fields_sha256",DataJson.HashText(DataJson.Encode(csv.Fields[i]))},{"record_id",i<oldCount?current.Records[i]["record_id"]:Guid.NewGuid().ToString("D")}});
                plan=new Dictionary<string,object>{{"identity_policy_version",1},{"source_id",sourceId},{"source_sha256",sha},{"allocations",allocations}};
                Immutable(planPath,DataJson.Utf8.GetBytes(DataJson.Encode(plan)));
            }
            var ids=DataJson.Rows(plan["allocations"]);
            if(DataJson.Int(plan,"identity_policy_version")!=1||DataJson.Text(plan,"source_id")!=sourceId||DataJson.Text(plan,"source_sha256")!=sha||ids.Count!=csv.Fields.Count)throw new InvalidDataException("Existing identity plan is incompatible; never silently replace it.");
            var unique=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<ids.Count;i++)if(DataJson.Int(ids[i],"data_ordinal")!=i+1||DataJson.Text(ids[i],"fields_sha256")!=DataJson.HashText(DataJson.Encode(csv.Fields[i]))||!Uuid(DataJson.Text(ids[i],"record_id"))||!unique.Add(DataJson.Text(ids[i],"record_id"))||(i<oldCount&&DataJson.Text(ids[i],"record_id")!=DataJson.Text(current.Records[i],"record_id")))throw new InvalidDataException("Identity plan conflicts with registered occurrences.");
            Point("after-identity-plan");
            var records=current==null?new List<Dictionary<string,object>>():CloneRows(current.Records);
            int schema=current==null?1:current.Schema;
            for(int i=oldCount;i<csv.Fields.Count;i++) {
                var f=csv.Fields[i];var row=new Dictionary<string,object>{{"record_id",ids[i]["record_id"]},{"record_revision",1},{"timestamp",LegacyCsv.Time(f[0]).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)},{"timestamp_input",f[0]},{"timestamp_precision",f[0].Trim().Length==16?"minute":"second"},{"timestamp_interpretation","legacy_beijing_policy_v1"},{"task",f[1]},{"actual_minutes",LegacyCsv.Minutes(f[2]).ToString(CultureInfo.InvariantCulture)},{"effective_minutes",LegacyCsv.Minutes(f[3]).ToString(CultureInfo.InvariantCulture)},{"source_id",sourceId},{"source_snapshot_sha256",sha},{"source_data_ordinal",i+1},{"source_fields",f},{"extensions",new Dictionary<string,object>()}};
                if(schema==2)AddCategoryV2.Apply(new[]{row});records.Add(row);
            }
            var imports=current==null?new List<Dictionary<string,object>>():CloneRows(current.Imports);
            // A previously observed header/line-ending representation may recur with the same records.
            // The prefix/count checks above still reject every history deletion or content change.
            if(!imports.Any(x=>DataJson.Text(x,"source_sha256")==sha))imports.Add(new Dictionary<string,object>{{"source_id",sourceId},{"source_sha256",sha},{"record_count",csv.Fields.Count},{"identity_plan_sha256",DataJson.Hash(File.ReadAllBytes(planPath))}});
            var migrations=current==null?new List<Dictionary<string,object>>():CloneRows(current.Migrations);
            return Commit(records,schema,imports,migrations,sha,current,captured,()=>{if(DataJson.Hash(ReadSource(source))!=sha)throw new IOException("Source changed before activation; retry against a fresh snapshot.");});
        }
    }
    private static List<Dictionary<string,object>> CloneRows(IEnumerable<Dictionary<string,object>> rows) {return rows.Select(x=>DataJson.Object(DataJson.Encode(x))).ToList();}
    public DataGeneration Migrate(int targetVersion) {
        using(var lease=Lease()) {
            var current=Active();if(targetVersion!=1&&targetVersion!=2)throw new InvalidDataException("Unsupported target schema.");
            var path=DataMigrations.Resolve(current.Schema,targetVersion,DataMigrations.Catalog);if(path.Count==0)return current;
            var records=CloneRows(current.Records);var results=CloneRows(current.Migrations);int version=current.Schema;
            foreach(var step in path) {
                ValidateRecords(records,version,DataJson.Text(Info(),"source_id"));string before=Semantic(records);var protectedRows=CloneRows(records);
                step.Transform(records);SameCore(protectedRows,records);version=step.To;
                ValidateRecords(records,version,DataJson.Text(Info(),"source_id"));
                results.Add(new Dictionary<string,object>{{"migration_id",step.Id},{"implementation_sha256",step.ImplementationHash},{"from",step.From},{"to",step.To},{"before_semantic_sha256",before},{"after_semantic_sha256",Semantic(records)},{"record_count",records.Count}});
                Point("after-migration-"+version);
            }
            return Commit(records,version,CloneRows(current.Imports),results,DataJson.Text(current.Manifest,"latest_source_sha256"),current,DateTimeOffset.UtcNow,null);
        }
    }
    public DataGeneration Rollback(string generationId) {
        using(var lease=Lease()) {
            var current=Active();if(current.Id==generationId)return current;
            var ancestor=current;var visited=new HashSet<string>();DataGeneration target=null;
            while(ancestor.Manifest["parent_generation_id"]!=null) {
                string parent=DataJson.Text(ancestor.Manifest,"parent_generation_id");if(!visited.Add(parent))throw new InvalidDataException("Generation ancestry cycle.");
                ancestor=Load(parent,DataJson.Text(ancestor.Manifest,"parent_manifest_sha256"));if(ancestor.Id==generationId){target=ancestor;break;}
            }
            if(target==null)throw new InvalidDataException("Rollback target is not a verified ancestor.");
            SameCore(current.Records,target.Records);
            if(DataJson.Encode(current.Imports)!=DataJson.Encode(target.Imports))throw new InvalidDataException("Rollback would discard new source captures/imports; replay or review is required.");
            Point("before-rollback");Activate(target);Point("after-rollback");return target;
        }
    }
    private DataGeneration Commit(List<Dictionary<string,object>> records,int schema,List<Dictionary<string,object>> imports,List<Dictionary<string,object>> migrations,string latest,DataGeneration parent,DateTimeOffset at,Action sourceCheck) {
        ValidateRecords(records,schema,DataJson.Text(Info(),"source_id"));
        string id=Guid.NewGuid().ToString("N"),dir=GenerationPath(id);Directory.CreateDirectory(dir);Directory.CreateDirectory(Path.Combine(dir,"views"));
        Immutable(Path.Combine(dir,"records.jsonl"),LineBytes(records));Point("after-records");
        var map=records.Select((r,i)=>new Dictionary<string,object>{{"data_ordinal",i+1},{"fields_sha256",DataJson.HashText(DataJson.Encode(r["source_fields"]))},{"record_id",r["record_id"]}}).ToList();
        Immutable(Path.Combine(dir,"identity-map.jsonl"),LineBytes(map));
        Immutable(Path.Combine(dir,"import-ledger.jsonl"),LineBytes(imports));
        Immutable(Path.Combine(dir,"migration-results.json"),DataJson.Utf8.GetBytes(DataJson.Encode(new Dictionary<string,object>{{"migration_ledger_version",1},{"steps",migrations}})));
        var candidate=new DataGeneration{Id=id,Schema=schema,Records=records,Imports=imports,Migrations=migrations};var csv=LegacyCsv.Parse(Raw(latest));
        Immutable(Path.Combine(dir,"views/work_log.csv"),csv.Bytes);
        Immutable(Path.Combine(dir,"views/report-data.json"),DataJson.Utf8.GetBytes(DataJson.Encode(View(candidate,csv,at))));Point("after-files");
        var hashes=new Dictionary<string,object>();foreach(var name in FileNames)hashes.Add(name,DataJson.Hash(File.ReadAllBytes(Path.Combine(dir,name))));
        var manifest=new Dictionary<string,object>{{"generation_id",id},{"parent_generation_id",parent==null?null:parent.Id},{"parent_manifest_sha256",parent==null?null:parent.ManifestHash},{"storage_format_version",1},{"identity_policy_version",1},{"input_format","legacy_csv"},{"input_schema_version",1},{"source_encoding",csv.EncodingName},{"data_schema_version",schema},{"statistics_version","formal_calendar_v1"},{"report_schema_version",2},{"source_id",DataJson.Text(Info(),"source_id")},{"latest_source_sha256",latest},{"record_count",records.Count},{"semantic_sha256",Semantic(records)},{"created_at_utc",at.ToString("o")},{"tool_version","data-foundation-v1"},{"tool_sha256",DataJson.Hash(File.ReadAllBytes(typeof(FileRecordStore).Assembly.Location))},{"files",hashes}};
        byte[] encoded=DataJson.Utf8.GetBytes(DataJson.Encode(manifest));Immutable(Path.Combine(dir,"manifest.json"),encoded);Point("after-manifest");
        var verified=Load(id,DataJson.Hash(encoded));Point("before-activate");if(sourceCheck!=null)sourceCheck();Activate(verified);Point("after-activate");return verified;
    }
    private void Activate(DataGeneration generation) {Pointer(Path.Combine(Root,"active.json"),DataJson.Utf8.GetBytes(DataJson.Encode(new Dictionary<string,object>{{"pointer_version",1},{"generation_id",generation.Id},{"manifest_sha256",generation.ManifestHash}})));}

    public void ExportView(string newDirectory,DateTimeOffset at) {
        using(var lease=Lease()) {
            var generation=Active();newDirectory=Path.GetFullPath(newDirectory);
            if(newDirectory.Equals(Root,StringComparison.OrdinalIgnoreCase)||newDirectory.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Export outside the managed store.");
            if(Directory.Exists(newDirectory)&&Directory.GetFileSystemEntries(newDirectory).Length!=0)throw new InvalidDataException("Export only to a new or empty directory; never overwrite data.");
            Directory.CreateDirectory(newDirectory);var csv=LegacyCsv.Parse(Raw(DataJson.Text(generation.Manifest,"latest_source_sha256")));var view=View(generation,csv,at);ValidateView(view,generation,csv);
            Immutable(Path.Combine(newDirectory,"work_log.csv"),csv.Bytes);Immutable(Path.Combine(newDirectory,"report-data.json"),DataJson.Utf8.GetBytes(DataJson.Encode(view)));
        }
    }
    private static Dictionary<string,object> View(DataGeneration generation,LegacyCsv csv,DateTimeOffset at) {
        var rows=new List<Dictionary<string,object>>();var warnings=new List<string>(csv.Warnings);DateTime today=at.ToOffset(TimeSpan.FromHours(8)).Date;
        var fingerprints=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<generation.Records.Count;i++) {
            var r=generation.Records[i];var row=new Dictionary<string,object>{{"timestamp",r["timestamp"]},{"task",r["task"]},{"actual_minutes",LegacyCsv.Minutes(DataJson.Text(r,"actual_minutes"))},{"effective_minutes",LegacyCsv.Minutes(DataJson.Text(r,"effective_minutes"))},{"record_number",csv.RecordNumbers[i]},{"record_id",r["record_id"]}};rows.Add(row);
            if(LegacyCsv.Time(DataJson.Text(r,"timestamp")).Date>today)warnings.Add("Future date at record "+csv.RecordNumbers[i]+".");
            string content=DataJson.Encode(new object[]{r["timestamp"],r["task"],Convert.ToDecimal(row["actual_minutes"]).ToString("G29",CultureInfo.InvariantCulture),Convert.ToDecimal(row["effective_minutes"]).ToString("G29",CultureInfo.InvariantCulture)});
            if(!fingerprints.Add(content))warnings.Add("Equivalent-content records detected; all permanent IDs retained for review.");
        }
        return new Dictionary<string,object>{{"schema_version",2},{"report_schema_version",2},{"data_schema_version",generation.Schema},{"statistics_version","rolling_7d_v1"},{"formal_statistics_version","formal_calendar_v1"},{"generation_id",generation.Id},{"timezone","Asia/Shanghai"},{"snapshot_at_utc",at.ToUniversalTime().ToString("o")},{"source_modified_at_utc",null},{"source_sha256",csv.Hash},{"has_header",csv.Header},{"rows",rows},{"warnings",warnings},{"raw_csv",csv.Text},{"raw_csv_base64",Convert.ToBase64String(csv.Bytes)},{"daily",Period(rows,today.AddDays(-1),today)},{"weekly",Period(rows,today.AddDays(-7),today)},{"send_weekly_today",today.DayOfWeek==DayOfWeek.Sunday}};
    }
    private static void ValidateView(Dictionary<string,object> view,DataGeneration generation,LegacyCsv csv) {
        if(DataJson.Int(view,"schema_version")!=2||DataJson.Int(view,"data_schema_version")!=generation.Schema||DataJson.Text(view,"generation_id")!=generation.Id||DataJson.Text(view,"statistics_version")!="rolling_7d_v1"||DataJson.Text(view,"formal_statistics_version")!="formal_calendar_v1"||DataJson.Text(view,"source_sha256")!=csv.Hash||DataJson.Text(view,"raw_csv")!=csv.Text||DataJson.Hash(Convert.FromBase64String(DataJson.Text(view,"raw_csv_base64")))!=csv.Hash)throw new InvalidDataException("Compatibility view provenance/version mismatch.");
        DateTimeOffset at=DateTimeOffset.Parse(DataJson.Text(view,"snapshot_at_utc"),CultureInfo.InvariantCulture);var expected=View(generation,csv,at);
        foreach(var key in new[]{"rows","daily","weekly","has_header","warnings","send_weekly_today","timezone","report_schema_version"})if(DataJson.Encode(view[key])!=DataJson.Encode(expected[key]))throw new InvalidDataException("Compatibility view/statistics changed: "+key);
    }
    private static object Period(List<Dictionary<string,object>> rows,DateTime start,DateTime end) {
        var selected=rows.Where(x=>LegacyCsv.Time(DataJson.Text(x,"timestamp"))>=start&&LegacyCsv.Time(DataJson.Text(x,"timestamp"))<end).ToList();decimal actual=selected.Sum(x=>Convert.ToDecimal(x["actual_minutes"],CultureInfo.InvariantCulture)),effective=selected.Sum(x=>Convert.ToDecimal(x["effective_minutes"],CultureInfo.InvariantCulture));
        var days=new List<object>();for(var day=start;day<end;day=day.AddDays(1)){string date=day.ToString("yyyy-MM-dd");var dayRows=selected.Where(x=>DataJson.Text(x,"timestamp").StartsWith(date,StringComparison.Ordinal)).ToList();days.Add(new{date=date,record_count=dayRows.Count,actual_minutes=dayRows.Sum(x=>Convert.ToDecimal(x["actual_minutes"],CultureInfo.InvariantCulture)),effective_minutes=dayRows.Sum(x=>Convert.ToDecimal(x["effective_minutes"],CultureInfo.InvariantCulture))});}
        return new{start_date=start.ToString("yyyy-MM-dd"),end_date=end.AddDays(-1).ToString("yyyy-MM-dd"),record_count=selected.Count,actual_minutes=actual,effective_minutes=effective,effective_percent=actual==0?(decimal?)null:Math.Round(effective/actual*100,1),rows=selected,days=days,by_task=selected.GroupBy(x=>DataJson.Text(x,"task")).Select(g=>new{task=g.Key,actual_minutes=g.Sum(x=>Convert.ToDecimal(x["actual_minutes"],CultureInfo.InvariantCulture)),effective_minutes=g.Sum(x=>Convert.ToDecimal(x["effective_minutes"],CultureInfo.InvariantCulture)),record_count=g.Count()}).ToList()};
    }
}
