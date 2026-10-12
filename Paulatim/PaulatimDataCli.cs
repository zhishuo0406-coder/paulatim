using System;
using System.Collections.Generic;

// Explicit local development commands only. This executable is not a scheduler entry point.
internal static class PaulatimDataCli
{
    private static int Main(string[] args) {
        try {
            if (args.Length < 3 || args.Length % 2 != 1) throw new ArgumentException("Use init|import|verify|migrate|rollback|export-view --store <directory> [--source <csv> --source-id <uuid>|--target 2|--generation <id>|--output <new-directory> --at <ISO-time>].");
            var options = new Dictionary<string,string>(StringComparer.Ordinal);
            for (int i=1;i<args.Length;i+=2) {
                if (!args[i].StartsWith("--",StringComparison.Ordinal) || options.ContainsKey(args[i])) throw new ArgumentException("Invalid or duplicate option.");
                options.Add(args[i],args[i+1]);
            }
            string command=args[0];
            var allowed=new HashSet<string>(new[]{"--store"});
            if (command=="import") { allowed.Add("--source");allowed.Add("--source-id"); }
            if (command=="migrate") allowed.Add("--target");
            if (command=="rollback") allowed.Add("--generation");
            if (command=="export-view") {allowed.Add("--output");allowed.Add("--at");}
            foreach(var key in options.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Unknown option: "+key);
            string root=Required(options,"--store");
            if(command=="init") {Console.WriteLine(FileRecordStore.Initialize(root));return 0;}
            var store=new FileRecordStore(root);
            DataGeneration result=null;
            switch(command) {
                case "import": result=store.Import(Required(options,"--source"),Required(options,"--source-id"));break;
                case "verify": result=store.Read();break;
                case "migrate": result=store.Migrate(Int32.Parse(Required(options,"--target"),System.Globalization.CultureInfo.InvariantCulture));break;
                case "rollback": result=store.Rollback(Required(options,"--generation"));break;
                case "export-view": store.ExportView(Required(options,"--output"),DateTimeOffset.ParseExact(Required(options,"--at"),"o",System.Globalization.CultureInfo.InvariantCulture));result=store.Read();break;
                default: throw new ArgumentException("Unknown command.");
            }
            Console.WriteLine("generation="+result.Id+" data_schema_version="+result.Schema+" records="+result.Records.Count);
            return 0;
        } catch(Exception error) {Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
    }
    private static string Required(Dictionary<string,string> options,string name) {string value;if(!options.TryGetValue(name,out value)||String.IsNullOrWhiteSpace(value))throw new ArgumentException("Missing "+name);return value;}
}
