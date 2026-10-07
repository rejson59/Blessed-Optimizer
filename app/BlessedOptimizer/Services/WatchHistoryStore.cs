using System.IO;
using System.Security;
using System.Text.Json;

namespace BlessedOptimizer.Services;

/// <summary>
/// A small, privacy-friendly history of Blessed checks. It stays in the user's
/// local profile, is retained for at most 30 days, and is never uploaded.
/// </summary>
public static class WatchHistoryStore
{
    private const int MaximumEntries = 1500;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan SameReportInterval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlessedOptimizer",
        "watch-history.json");

    public sealed record Entry(
        DateTimeOffset CompletedAt,
        int ProblemCount,
        double? CpuPercent,
        double MemoryPercent,
        string Headline,
        IReadOnlyList<string> Findings,
        IReadOnlyList<string> FindingIds);

    public static IReadOnlyList<Entry> ReadRecent(int count = 48)
    {
        try
        {
            if (!File.Exists(HistoryFile))
                return Array.Empty<Entry>();

            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistoryFile), SerializerOptions);
            if (entries is null)
                return Array.Empty<Entry>();

            var cutoff = DateTimeOffset.Now - Retention;
            return entries
                .Where(IsValid)
                .Where(entry => entry.CompletedAt >= cutoff)
                .OrderByDescending(entry => entry.CompletedAt)
                .Take(Math.Clamp(count, 1, MaximumEntries))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or NotSupportedException)
        {
            return Array.Empty<Entry>();
        }
    }

    public static void Record(WatchReport report, LiveUsage? usage)
    {
        try
        {
            var entries = ReadAll().Where(IsValid).ToList();
            var importantFindings = report.Findings
                .Where(finding => finding.Severity is FindingSeverity.Warning or FindingSeverity.Critical)
                .Take(12)
                .ToArray();
            var newEntry = new Entry(
                report.CompletedAt,
                Math.Max(0, report.ProblemCount),
                ClampPercent(usage?.CpuPercent),
                ClampPercent(usage?.MemoryPercent) ?? 0,
                Limit(report.Headline, 180),
                importantFindings
                    .Select(finding => Limit(finding.Title, 120))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                importantFindings
                    .Select(finding => finding.Id)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());

            var latest = entries.OrderBy(entry => entry.CompletedAt).LastOrDefault();
            if (latest is not null && newEntry.CompletedAt >= latest.CompletedAt &&
                newEntry.CompletedAt - latest.CompletedAt < SameReportInterval && SameReport(latest, newEntry))
                return;

            entries.Add(newEntry);
            var cutoff = report.CompletedAt - Retention;
            entries = entries
                .Where(entry => entry.CompletedAt >= cutoff)
                .OrderBy(entry => entry.CompletedAt)
                .TakeLast(MaximumEntries)
                .ToList();
            Save(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or NotSupportedException)
        {
            // History is optional and must never interrupt a device check.
        }
    }

    public static bool Clear()
    {
        try
        {
            if (File.Exists(HistoryFile))
                File.Delete(HistoryFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static List<Entry> ReadAll()
    {
        if (!File.Exists(HistoryFile))
            return new List<Entry>();
        return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistoryFile), SerializerOptions) ?? new List<Entry>();
    }

    private static void Save(IReadOnlyList<Entry> entries)
    {
        var directory = Path.GetDirectoryName(HistoryFile);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        var stagedFile = HistoryFile + ".tmp";
        try
        {
            File.WriteAllText(stagedFile, JsonSerializer.Serialize(entries, SerializerOptions));
            File.Move(stagedFile, HistoryFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(stagedFile)) File.Delete(stagedFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsValid(Entry? entry) =>
        entry is not null && entry.ProblemCount >= 0 &&
        double.IsFinite(entry.MemoryPercent) && entry.MemoryPercent is >= 0 and <= 100 &&
        (entry.CpuPercent is null || double.IsFinite(entry.CpuPercent.Value) && entry.CpuPercent.Value is >= 0 and <= 100) &&
        entry.Findings is not null && entry.Findings.Count <= 12 && entry.Findings.All(finding => finding is not null) &&
        entry.FindingIds is not null && entry.FindingIds.Count <= 12 && entry.FindingIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
        !string.IsNullOrWhiteSpace(entry.Headline);

    private static double? ClampPercent(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : null;

    private static string Limit(string value, int length) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= length ? value : value[..(length - 1)] + "…";

    private static bool SameReport(Entry first, Entry second) =>
        first.ProblemCount == second.ProblemCount &&
        string.Equals(first.Headline, second.Headline, StringComparison.Ordinal) &&
        first.FindingIds.SequenceEqual(second.FindingIds, StringComparer.Ordinal);
}
