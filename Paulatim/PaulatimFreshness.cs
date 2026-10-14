using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// A stable local file is not evidence of cloud completeness. Readiness is an
// explicit assertion from the owner, bound to exact bytes and a closed period.
internal static class PaulatimFreshness
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    public sealed class Confirmation {
        public int schema_version { get; set; }
        public string source_sha256 { get; set; }
        public string confirmed_through_date { get; set; }
        public string confirmed_at_utc { get; set; }
    }
    public sealed class Observation {
        public string source_sha256 { get; set; }
        public string since_utc { get; set; }
        public string last_seen_utc { get; set; }
    }
    public static string Hash(byte[] bytes) {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    public static bool Observe(Observation previous, string hash, DateTimeOffset now, out Observation current) {
        DateTimeOffset since, last;
        bool continuous = previous != null && previous.source_sha256 == hash &&
            DateTimeOffset.TryParse(previous.since_utc, out since) &&
            DateTimeOffset.TryParse(previous.last_seen_utc, out last) && since <= last &&
            last <= now && (now - last).TotalSeconds <= 90;
        current = new Observation { source_sha256 = hash, since_utc = continuous ? previous.since_utc : now.ToString("o"), last_seen_utc = now.ToString("o") };
        return (now - DateTimeOffset.Parse(current.since_utc)).TotalSeconds >= 120;
    }
    public static void Validate(Confirmation ready, string hash, string endDate, DateTimeOffset now) {
        DateTime through, end;
        DateTimeOffset confirmed;
        if (ready == null || ready.schema_version != 1 || ready.source_sha256 == null ||
            !Regex.IsMatch(ready.source_sha256, "\\A[0-9a-f]{64}\\z") || ready.source_sha256 != hash ||
            !DateTime.TryParseExact(ready.confirmed_through_date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out through) ||
            !DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end) || through < end ||
            !DateTimeOffset.TryParse(ready.confirmed_at_utc, CultureInfo.InvariantCulture, DateTimeStyles.None, out confirmed) ||
            confirmed > now || confirmed.ToOffset(TimeSpan.FromHours(8)).Date <= through)
            throw new InvalidDataException("Source readiness is missing, stale or does not cover this closed report period. Confirm the complete phone/iCloud export; local stability alone cannot authorize sending.");
    }
    public static void RequireReady(string root, string hash, string endDate, DateTimeOffset now) {
        string path = Path.Combine(root, "source-ready.json");
        if (!File.Exists(path)) throw new InvalidDataException("No source-ready.json: report held until the owner confirms the complete synchronized export.");
        Validate(Json.Deserialize<Confirmation>(File.ReadAllText(path)), hash, endDate, now);
    }
    public static bool ObserveFile(string root, string hash, DateTimeOffset now) {
        string path = Path.Combine(root, "source-observation.json");
        Observation previous = null, current;
        // Malformed observation loses only the waiting window, never receipts.
        if (File.Exists(path)) { try { previous = Json.Deserialize<Observation>(File.ReadAllText(path)); } catch { } }
        bool stable = Observe(previous, hash, now, out current);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Json.Serialize(current)); file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
        return stable;
    }
}
