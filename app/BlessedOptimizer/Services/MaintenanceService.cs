using System.IO;
using System.Runtime.Versioning;
using System.Security;

namespace BlessedOptimizer.Services;

public sealed record TempScanResult(int FileCount, double SizeMb);

public sealed record CleanupResult(int DeletedFiles, double FreedMb, int SkippedFiles);

/// <summary>
/// Housekeeping Blessed is allowed to perform on its own: leftovers in the user's
/// TEMP folder. Only files older than the given age are touched, locked files are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MaintenanceService
{
    private const int MaxVisitedFiles = 40000;

    public static Task<TempScanResult> ScanTempAsync(TimeSpan minimumAge, CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanTemp(minimumAge, cancellationToken), cancellationToken);

    public static Task<CleanupResult> CleanTempAsync(TimeSpan minimumAge, CancellationToken cancellationToken = default) =>
        Task.Run(() => CleanTemp(minimumAge, cancellationToken), cancellationToken);

    public static TempScanResult ScanTemp(TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        var count = 0;
        foreach (var file in EnumerateTempFiles(minimumAge, cancellationToken))
        {
            try
            {
                bytes += file.Length;
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file disappeared between the listing and the measurement.
            }
        }
        return new TempScanResult(count, bytes / 1024d / 1024d);
    }

    public static CleanupResult CleanTemp(TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        long freed = 0;
        var deleted = 0;
        var skipped = 0;

        foreach (var file in EnumerateTempFiles(minimumAge, cancellationToken))
        {
            try
            {
                var size = file.Length;
                if (file.IsReadOnly)
                    file.IsReadOnly = false;
                file.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
            {
                // A running program still owns this file; leave it exactly where it is.
                skipped++;
            }
        }

        return new CleanupResult(deleted, freed / 1024d / 1024d, skipped);
    }

    private static string? TryGetTempPath()
    {
        try
        {
            var path = Path.GetTempPath();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is SecurityException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<FileInfo> EnumerateTempFiles(TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        var root = TryGetTempPath();
        if (root is null || !Directory.Exists(root))
            yield break;

        var threshold = DateTime.Now - minimumAge;
        var visited = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            FileInfo[] files;
            try
            {
                files = directory.GetFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (++visited > MaxVisitedFiles)
                    yield break;
                bool stale;
                try
                {
                    stale = file.LastWriteTime < threshold && file.CreationTime < threshold;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (stale)
                    yield return file;
            }

            DirectoryInfo[] children;
            try
            {
                children = directory.GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    continue;
                pending.Push(child);
            }
        }
    }
}
