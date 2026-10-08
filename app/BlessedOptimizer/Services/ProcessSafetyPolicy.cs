using System.IO;

namespace BlessedOptimizer.Services;

/// <summary>
/// Conservative classification for the process viewer. Unknown/background processes are
/// never suggested for closure; only same-session GUI applications outside Windows can be
/// closed, and every operation still needs the user's explicit selection and confirmation.
/// </summary>
public sealed record ProcessSafetyAssessment(
    bool IsProtected,
    bool CanClose,
    string RoleLabel,
    string Explanation);

public static class ProcessSafetyPolicy
{
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon",
        "svchost", "dwm", "fontdrvhost", "audiodg", "spoolsv", "explorer", "sihost",
        "runtimebroker", "taskhostw", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "applicationframehost", "securityhealthservice", "msmpeng",
        "mssense", "trustedinstaller", "tiworker", "wudfhost", "ctfmon"
    };

    public static ProcessSafetyAssessment Assess(
        int processId,
        string processName,
        string? executablePath,
        bool hasMainWindow,
        int processSessionId,
        int currentProcessId,
        int currentSessionId,
        string? windowsDirectory)
    {
        var name = Path.GetFileNameWithoutExtension(processName.Trim());
        if (processId <= 4 || processId == currentProcessId)
            return Protected("Proces krytyczny lub sam Blessed", "Zabezpieczam ten proces — nie pojawi się jako opcja zamknięcia.");

        if (processSessionId != currentSessionId)
            return Protected("Inna sesja Windows", "Ten proces działa poza Twoją sesją. Blessed nie będzie go zamykał.");

        if (ProtectedProcessNames.Contains(name))
            return Protected("Windows / pulpit", "To znany proces systemu lub pulpitu. Blessed zawsze zostawia go w spokoju.");

        if (string.IsNullOrWhiteSpace(executablePath))
            return Protected("Nieznane pochodzenie", "Windows nie udostępnił ścieżki programu, więc nie mogę bezpiecznie ocenić, czy wolno go zamknąć.");

        if (string.IsNullOrWhiteSpace(windowsDirectory))
            return Protected("Nieznany katalog Windows", "Nie mogę zweryfikować położenia programu względem katalogu Windows.");

        if (IsInsideDirectory(executablePath, windowsDirectory))
            return Protected("Składnik Windows", "Program znajduje się w katalogu Windows. Blessed nie proponuje jego zamykania.");

        if (!hasMainWindow)
            return new ProcessSafetyAssessment(
                IsProtected: false,
                CanClose: false,
                RoleLabel: "Proces w tle",
                Explanation: "Nie ma widocznego okna. Może obsługiwać synchronizację, dźwięk lub inne zadanie; Blessed nie zamyka procesów w tle.");

        return new ProcessSafetyAssessment(
            IsProtected: false,
            CanClose: true,
            RoleLabel: "Aplikacja z oknem",
            Explanation: "To aplikacja użytkownika z widocznym oknem. Zaznacz ją samodzielnie, zapisz pracę i zdecyduj, czy ją zamknąć.");
    }

    private static ProcessSafetyAssessment Protected(string roleLabel, string explanation) =>
        new(IsProtected: true, CanClose: false, RoleLabel: roleLabel, Explanation: explanation);

    private static bool IsInsideDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // A malformed path is untrusted, so keep it out of close candidates.
            return true;
        }
    }
}
