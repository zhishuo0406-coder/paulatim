using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

internal static class PaulatimFreshness
{
    public static string Hash(byte[] bytes) {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static DateTimeOffset Timestamp(string value) {
        DateTimeOffset result;
        if (value == null || !Regex.IsMatch(value, "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\\z") ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            throw new InvalidDataException("Missing or invalid timezone-qualified source/snapshot timestamp.");
        return result;
    }
    public static void ValidateTimes(string sourceModified, string snapshotAt, DateTimeOffset now) {
        DateTimeOffset midnight = new DateTimeOffset(now.ToOffset(TimeSpan.FromHours(8)).Date, TimeSpan.FromHours(8));
        DateTimeOffset source = Timestamp(sourceModified), snapshot = Timestamp(snapshotAt);
        // Yesterday's source data is expected in yesterday's daily report. The
        // cutoff applies to successful acquisition, never to the source mtime.
        if (source > now)
            throw new InvalidDataException("Source modification time is in the future; refresh before sending.");
        if (snapshot <= midnight || snapshot > now || snapshot < source)
            throw new InvalidDataException("Acquired snapshot must be strictly after today's Beijing 00:00:00, not in the future and not before the source update. Refresh before sending.");
    }
}
