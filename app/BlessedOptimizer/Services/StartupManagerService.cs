using Microsoft.Win32;
using System.IO;
using System.Security;

namespace BlessedOptimizer.Services;

public sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    string Source,
    bool IsEnabled,
    string RegistryPath,
    string ValueName,
    int ValueKind,
    string? BackupId);

public static class StartupManagerService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string BackupPath = @"Software\BlessedOptimizer\StartupBackups";

    public static IReadOnlyList<StartupEntry> ReadEntries()
    {
        var entries = new List<StartupEntry>();
        ReadActiveKey(RunPath, "Autostart użytkownika", entries);
        ReadActiveKey(RunOncePath, "Jednorazowy autostart", entries);
        ReadBackups(entries);
        return entries.OrderBy(entry => entry.IsEnabled ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static void Disable(StartupEntry entry)
    {
        if (!entry.IsEnabled || !IsAllowedPath(entry.RegistryPath))
            throw new InvalidOperationException("Ta pozycja nie jest aktywnym wpisem autostartu bieżącego użytkownika.");
        if (string.IsNullOrWhiteSpace(entry.ValueName) || !IsSupportedKind((RegistryValueKind)entry.ValueKind))
            throw new InvalidOperationException("Ten typ wpisu nie może być bezpiecznie archiwizowany.");

        using var source = Registry.CurrentUser.OpenSubKey(entry.RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Wpis autostartu już nie istnieje. Odśwież listę.");
        var currentKind = source.GetValueKind(entry.ValueName);
        var currentData = source.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (currentKind != (RegistryValueKind)entry.ValueKind || !string.Equals(currentData, entry.Command, StringComparison.Ordinal))
            throw new InvalidOperationException("Wpis zmienił się od ostatniego odświeżenia. Odśwież listę i sprawdź go ponownie.");

        var backupId = Guid.NewGuid().ToString("N");
        using var backupRoot = Registry.CurrentUser.CreateSubKey(BackupPath, writable: true)
            ?? throw new InvalidOperationException("Nie udało się utworzyć kopii przywracania w profilu użytkownika.");
        using (var backup = backupRoot.CreateSubKey(backupId, writable: true)
            ?? throw new InvalidOperationException("Nie udało się zapisać kopii wpisu autostartu."))
        {
            backup.SetValue("OriginalPath", entry.RegistryPath, RegistryValueKind.String);
            backup.SetValue("OriginalName", entry.ValueName, RegistryValueKind.String);
            backup.SetValue("OriginalKind", entry.ValueKind, RegistryValueKind.DWord);
            backup.SetValue("OriginalData", entry.Command, RegistryValueKind.String);
            backup.SetValue("DisplayName", entry.Name, RegistryValueKind.String);
            backup.SetValue("SavedAt", DateTimeOffset.Now.ToString("O"), RegistryValueKind.String);
        }

        try
        {
            source.DeleteValue(entry.ValueName, throwOnMissingValue: true);
        }
        catch
        {
            backupRoot.DeleteSubKeyTree(backupId, throwOnMissingSubKey: false);
            throw;
        }
    }

    public static void Restore(StartupEntry entry)
    {
        if (entry.IsEnabled || string.IsNullOrWhiteSpace(entry.BackupId) ||
            entry.BackupId.Length != 32 || !entry.BackupId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Nieprawidłowa kopia wpisu autostartu.");

        using var backupRoot = Registry.CurrentUser.OpenSubKey(BackupPath, writable: true)
            ?? throw new InvalidOperationException("Nie znaleziono kopii do przywrócenia.");
        string? originalPath;
        string? originalName;
        string? originalData;
        RegistryValueKind originalKind;
        using (var backup = backupRoot.OpenSubKey(entry.BackupId, writable: false)
            ?? throw new InvalidOperationException("Kopia została już przywrócona albo usunięta."))
        {
            originalPath = backup.GetValue("OriginalPath") as string;
            originalName = backup.GetValue("OriginalName") as string;
            originalKind = backup.GetValue("OriginalKind") is int kind ? (RegistryValueKind)kind : RegistryValueKind.Unknown;
            originalData = backup.GetValue("OriginalData") as string;
        }
        if (originalPath is null || originalName is null || originalData is null ||
            !IsAllowedPath(originalPath) || !IsSupportedKind(originalKind))
            throw new InvalidDataException("Kopia nie zawiera prawidłowego wpisu użytkownika.");

        using var target = Registry.CurrentUser.CreateSubKey(originalPath, writable: true)
            ?? throw new InvalidOperationException("Nie udało się otworzyć autostartu bieżącego użytkownika.");
        if (target.GetValueNames().Contains(originalName, StringComparer.OrdinalIgnoreCase))
        {
            var currentKind = target.GetValueKind(originalName);
            var currentData = target.GetValue(originalName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (currentKind == originalKind && string.Equals(currentData, originalData, StringComparison.Ordinal))
            {
                // A previous restore may have completed just before the app exited.
                backupRoot.DeleteSubKeyTree(entry.BackupId, throwOnMissingSubKey: false);
                return;
            }
            throw new InvalidOperationException("W tym miejscu istnieje już nowy wpis o tej samej nazwie. Nie nadpisuję go.");
        }

        target.SetValue(originalName, originalData, originalKind);
        backupRoot.DeleteSubKeyTree(entry.BackupId, throwOnMissingSubKey: false);
    }

    private static void ReadActiveKey(string path, string source, List<StartupEntry> entries)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        if (key is null) return;

        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(valueName)) continue;
            var kind = key.GetValueKind(valueName);
            if (!IsSupportedKind(kind)) continue;
            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(value)) continue;
            entries.Add(new StartupEntry(
                $"active:{path}:{valueName}", valueName, value, source, true, path, valueName, (int)kind, null));
        }
    }

    private static void ReadBackups(List<StartupEntry> entries)
    {
        using var root = Registry.CurrentUser.OpenSubKey(BackupPath, writable: false);
        if (root is null) return;

        foreach (var backupId in root.GetSubKeyNames())
        {
            if (backupId.Length != 32 || !backupId.All(Uri.IsHexDigit)) continue;
            using var backup = root.OpenSubKey(backupId, writable: false);
            if (backup is null) continue;
            var path = backup.GetValue("OriginalPath") as string;
            var name = backup.GetValue("OriginalName") as string;
            var data = backup.GetValue("OriginalData") as string;
            var displayName = backup.GetValue("DisplayName") as string;
            var kind = backup.GetValue("OriginalKind") is int rawKind ? rawKind : (int)RegistryValueKind.Unknown;
            if (path is null || name is null || data is null || !IsAllowedPath(path) || !IsSupportedKind((RegistryValueKind)kind))
                continue;
            entries.Add(new StartupEntry(
                $"disabled:{backupId}", displayName ?? name, data, "Wyłączono · można przywrócić", false, path, name, kind, backupId));
        }
    }

    private static bool IsAllowedPath(string path) =>
        string.Equals(path, RunPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, RunOncePath, StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedKind(RegistryValueKind kind) =>
        kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
}
