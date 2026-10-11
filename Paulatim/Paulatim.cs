using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.VisualBasic.FileIO;

// Built as a Windows-subsystem application: no console, UI, or child process.
internal static class Paulatim
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 33554432 };
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static string Root;

    private sealed class Row
    {
        public string timestamp { get; set; }
        public string task { get; set; }
        public decimal actual_minutes { get; set; }
        public decimal effective_minutes { get; set; }
        public int record_number { get; set; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // All errors go to local status/log files, never a dialog box.
        Root = AppDomain.CurrentDomain.BaseDirectory;
        string config = Path.Combine(Root, "config.json");
        try
        {
            if (args.Length != 0)
            {
                if (args.Length != 2 || args[0] != "--config") throw new ArgumentException("Expected --config <file>.");
                config = Path.GetFullPath(args[1]);
                Root = Path.GetDirectoryName(config);
            }
            using (FileStream lease = AcquireLease())
            {
                if (lease == null) return 0; // Another copy owns the lease.
                try { return Sync(config); }
                catch (Exception error) { Failure(error); return 1; }
            }
        }
        catch (Exception error) { Failure(error); return 1; }
    }

    private static FileStream AcquireLease()
    {
        try { return new FileStream(Path.Combine(Root, "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        {
            int code = error.HResult & 65535;
            if (code == 32 || code == 33) return null;
            throw;
        }
    }

    private static int Sync(string configPath)
    {
        var config = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath, Utf8));
        string source = Path.GetFullPath(Convert.ToString(config["source"], CultureInfo.InvariantCulture));
        string destination = Path.Combine(Root, "work_log.csv");
        if (String.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source and destination must differ.");
        int limit = config.ContainsKey("max_bytes") ? Convert.ToInt32(config["max_bytes"], CultureInfo.InvariantCulture) : 16777216;
        if (limit < 1 || limit > 16777216) throw new InvalidDataException("max_bytes must be between 1 and 16777216.");
        byte[] bytes = null;
        string modified = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // FileShare.Read excludes concurrent writes and replacement while reading.
                using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length == 0 || file.Length > limit) throw new InvalidDataException("Source is empty or exceeds the configured size limit; last good copy preserved.");
                    bytes = new byte[(int)file.Length];
                    int position = 0;
                    while (position < bytes.Length)
                    {
                        int count = file.Read(bytes, position, bytes.Length - position);
                        if (count == 0) throw new IOException("Source read was incomplete.");
                        position += count;
                    }
                    modified = File.GetLastWriteTimeUtc(source).ToString("o", CultureInfo.InvariantCulture);
                }
                break;
            }
            catch (IOException) { if (attempt == 2) throw; Thread.Sleep(150 * (attempt + 1)); }
        }
        var warnings = new List<string>();
        bool header;
        var rows = Parse(bytes, warnings, out header);
        string hash = Hash(bytes);
        bool changed = !File.Exists(destination) || Hash(File.ReadAllBytes(destination)) != hash;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTime today = now.ToOffset(TimeSpan.FromHours(8)).Date;
        var report = new Dictionary<string, object> {
            { "schema_version", 2 }, { "timezone", "Asia/Shanghai" },
            { "snapshot_at_utc", now.ToString("o") }, { "source_modified_at_utc", modified },
            { "source_sha256", hash }, { "has_header", header }, { "rows", rows }, { "warnings", warnings },
            { "raw_csv", Decode(bytes) }, { "raw_csv_base64", Convert.ToBase64String(bytes) },
            { "daily", Period(rows, today.AddDays(-1), today) },
            { "weekly", Period(rows, today.AddDays(-7), today) },
            { "send_weekly_today", today.DayOfWeek == DayOfWeek.Sunday }
        };
        string reportJson = Json.Serialize(report);
        if (changed) AtomicBytes(destination, bytes, true);
        AtomicText(Path.Combine(Root, "report-data.json"), reportJson);
        AtomicText(Path.Combine(Root, "sync-status.json"), Json.Serialize(new Dictionary<string, object> {
            { "schema_version", 2 }, { "success", true }, { "checked_at_utc", now.ToString("o") },
            { "last_success_at_utc", now.ToString("o") }, { "source_modified_at_utc", modified },
            { "source", source }, { "destination", destination }, { "sha256", hash },
            { "bytes", bytes.Length }, { "changed", changed }, { "record_count", rows.Count }, { "warnings", warnings }
        }));
        return 0;
    }

    private static string Decode(byte[] bytes)
    {
        using (var input = new MemoryStream(bytes))
        using (var reader = new StreamReader(input, Utf8, true)) return reader.ReadToEnd();
    }

    private static List<Row> Parse(byte[] bytes, List<string> warnings, out bool hasHeader)
    {
        var rows = new List<Row>();
        var seen = new HashSet<string>();
        hasHeader = false;
        using (var input = new StringReader(Decode(bytes)))
        using (var parser = new TextFieldParser(input))
        {
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            parser.TrimWhiteSpace = false;
            int number = 0;
            while (!parser.EndOfData)
            {
                string[] fields = parser.ReadFields();
                if (fields == null) continue;
                number++;
                if (fields.Length != 4) throw new InvalidDataException("CSV record " + number + " must have exactly four fields.");
                if (number == 1 && IsHeader(fields)) { hasHeader = true; continue; }
                DateTime timestamp;
                string[] formats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" };
                if (!DateTime.TryParseExact(fields[0].Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
                    throw new InvalidDataException("Invalid timestamp at CSV record " + number + ".");
                decimal actual, effective;
                if (!Decimal.TryParse(fields[2].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out actual) ||
                    !Decimal.TryParse(fields[3].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out effective) ||
                    actual < 0 || effective < 0 || String.IsNullOrWhiteSpace(fields[1]))
                    throw new InvalidDataException("Invalid task or nonnegative minute values at CSV record " + number + ".");
                if (effective > actual) warnings.Add("Effective exceeds actual at record " + number + "; retained without correction.");
                string key = Json.Serialize(fields);
                if (!seen.Add(key)) warnings.Add("Possible duplicate at record " + number + "; retained without deduplication.");
                if (timestamp.Date > DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date) warnings.Add("Future date at record " + number + ".");
                rows.Add(new Row { timestamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss"), task = fields[1], actual_minutes = actual, effective_minutes = effective, record_number = number });
            }
        }
        if (rows.Count == 0) throw new InvalidDataException("No valid data rows; last good copy preserved.");
        return rows;
    }

    private static bool IsHeader(string[] fields)
    {
        string joined = String.Join(",", fields.Select(x => x.Trim()).ToArray());
        return joined == "时间,事项,实际分钟,有效分钟" || joined == "日期时间,事项,实际分钟,有效分钟" ||
            joined.Equals("timestamp,task,actual_minutes,effective_minutes", StringComparison.OrdinalIgnoreCase) ||
            joined.Equals("datetime,task,actual_minutes,effective_minutes", StringComparison.OrdinalIgnoreCase);
    }

    private static object Period(List<Row> rows, DateTime start, DateTime end)
    {
        var selected = rows.Where(x => DateTime.ParseExact(x.timestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) >= start &&
            DateTime.ParseExact(x.timestamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) < end).ToList();
        decimal actual = selected.Sum(x => x.actual_minutes), effective = selected.Sum(x => x.effective_minutes);
        var days = new List<object>();
        for (var day = start; day < end; day = day.AddDays(1))
        {
            string date = day.ToString("yyyy-MM-dd");
            var dayRows = selected.Where(x => x.timestamp.StartsWith(date, StringComparison.Ordinal)).ToList();
            days.Add(new { date = date, record_count = dayRows.Count, actual_minutes = dayRows.Sum(x => x.actual_minutes), effective_minutes = dayRows.Sum(x => x.effective_minutes) });
        }
        return new { start_date = start.ToString("yyyy-MM-dd"), end_date = end.AddDays(-1).ToString("yyyy-MM-dd"),
            record_count = selected.Count, actual_minutes = actual, effective_minutes = effective,
            effective_percent = actual == 0 ? (decimal?)null : Math.Round(effective / actual * 100, 1), rows = selected, days = days,
            by_task = selected.GroupBy(x => x.task).Select(g => new { task = g.Key, actual_minutes = g.Sum(x => x.actual_minutes), effective_minutes = g.Sum(x => x.effective_minutes), record_count = g.Count() }).ToList() };
    }

    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static void AtomicText(string path, string content) { AtomicBytes(path, Utf8.GetBytes(content), false); }

    private static void AtomicBytes(string path, byte[] bytes, bool keepPrevious)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, keepPrevious ? path + ".previous" : null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Failure(Exception error)
    {
        try
        {
            string lastSuccess = null;
            string statusPath = Path.Combine(Root, "sync-status.json");
            if (File.Exists(statusPath))
            {
                try
                {
                    var previous = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(statusPath, Utf8));
                    if (previous != null && previous.ContainsKey("last_success_at_utc")) lastSuccess = Convert.ToString(previous["last_success_at_utc"]);
                }
                catch { /* A damaged old status must not prevent recording this failure. */ }
            }
            AtomicText(statusPath, Json.Serialize(new { schema_version = 2, success = false, checked_at_utc = DateTimeOffset.UtcNow.ToString("o"), last_success_at_utc = lastSuccess, error = error.GetType().Name + ": " + error.Message, previous_copy_preserved = true }));
        }
        catch { /* A read-only destination must also fail silently. */ }
        try
        {
            string log = Path.Combine(Root, "sync-errors.log");
            if (File.Exists(log) && new FileInfo(log).Length > 1048576) { File.Copy(log, log + ".previous", true); File.WriteAllText(log, "", Utf8); }
            File.AppendAllText(log, DateTimeOffset.UtcNow.ToString("o") + " " + error.GetType().Name + ": " + error.Message + Environment.NewLine, Utf8);
        }
        catch { }
    }
}
