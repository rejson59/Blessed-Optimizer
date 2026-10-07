using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

namespace BlessedOptimizer.Services;

public sealed record TempScanResult(int FileCount, double SizeMb);

public sealed record CleanupResult(int DeletedFiles, double FreedMb, int SkippedFiles);

public sealed record RecycleBinScan(long FileCount, double SizeMb);

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

    /// <summary>
    /// Sums the recycle bins of all fixed drives. Read-only; bins that belong to other
    /// accounts and refuse enumeration are simply skipped.
    /// </summary>
    public static RecycleBinScan ScanRecycleBin()
    {
        long bytes = 0;
        long count = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
                continue;
            string binRoot;
            try
            {
                binRoot = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!Directory.Exists(binRoot))
                continue;
            MeasureRecycleBinDirectory(new DirectoryInfo(binRoot), ref bytes, ref count);
        }
        return new RecycleBinScan(count, bytes / 1024d / 1024d);
    }

    private static void MeasureRecycleBinDirectory(DirectoryInfo directory, ref long bytes, ref long count)
    {
        FileInfo[] files;
        try
        {
            files = directory.GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return;
        }

        foreach (var file in files)
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

        DirectoryInfo[] children;
        try
        {
            children = directory.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return;
        }

        foreach (var child in children)
        {
            try
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }
            MeasureRecycleBinDirectory(child, ref bytes, ref count);
        }
    }

    /// <summary>
    /// Empties every user's recycle bin through the shell. Permanent — the caller must
    /// have shown its own consent dialog first. Returns false when the shell refused.
    /// </summary>
    public static bool EmptyRecycleBin() =>
        SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);

    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
