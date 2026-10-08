using System.Diagnostics;
using System.Security;

namespace BlessedOptimizer.Services;

public enum ProcessCloseOutcome
{
    Closed,
    StillRunning,
    AlreadyExited,
    Protected,
    ProcessChanged,
    Failed
}

public sealed record ProcessCloseResult(int ProcessId, string Name, ProcessCloseOutcome Outcome, string Detail);

/// <summary>
/// Closes only explicitly selected, same-session GUI applications that pass the safety
/// policy again at action time. Graceful close is the default; force termination is a
/// separate caller-confirmed operation and never kills a process tree.
/// </summary>
public static class ProcessControlService
{
    public static IReadOnlyList<ProcessCloseResult> CloseSelected(
        IEnumerable<ProcessUsageSnapshot> selected,
        bool forceTerminate)
    {
        var results = new List<ProcessCloseResult>();
        foreach (var candidate in selected)
        {
            results.Add(CloseOne(candidate, forceTerminate));
        }
        return results;
    }

    private static ProcessCloseResult CloseOne(ProcessUsageSnapshot candidate, bool forceTerminate)
    {
        if (!candidate.CanClose)
            return Result(candidate, ProcessCloseOutcome.Protected, "Proces jest chroniony albo oznaczony jako ważny.");

        try
        {
            using var process = Process.GetProcessById(candidate.ProcessId);
            if (process.HasExited)
                return Result(candidate, ProcessCloseOutcome.AlreadyExited, "Proces zakończył się przed wykonaniem działania.");

            if (!string.Equals(process.ProcessName, candidate.Name, StringComparison.OrdinalIgnoreCase))
                return Result(candidate, ProcessCloseOutcome.ProcessChanged, "PID wskazuje już inny proces; pominięto go.");

            DateTime actualStartTime;
            try { actualStartTime = process.StartTime; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException)
            {
                return Result(candidate, ProcessCloseOutcome.Protected, "Nie można ponownie zweryfikować czasu uruchomienia procesu.");
            }
            if (candidate.StartedAt is not { } expectedStartTime)
                return Result(candidate, ProcessCloseOutcome.Protected, "Nie można potwierdzić czasu uruchomienia; odśwież listę przed zamknięciem.");
            if (actualStartTime != expectedStartTime)
                return Result(candidate, ProcessCloseOutcome.ProcessChanged, "Proces uruchomił się ponownie; odśwież listę i sprawdź go ponownie.");

            string? executablePath = null;
            try { executablePath = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException or ArgumentException) { }

            var hasMainWindow = process.MainWindowHandle != IntPtr.Zero;
            using var currentProcess = Process.GetCurrentProcess();
            var safety = ProcessSafetyPolicy.Assess(
                process.Id,
                process.ProcessName,
                executablePath,
                hasMainWindow,
                process.SessionId,
                currentProcess.Id,
                currentProcess.SessionId,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!safety.CanClose)
                return Result(candidate, ProcessCloseOutcome.Protected, safety.Explanation);

            if (!forceTerminate)
            {
                if (!process.CloseMainWindow())
                    return Result(candidate, ProcessCloseOutcome.StillRunning, "Aplikacja nie przyjęła prośby o zamknięcie.");
                return process.WaitForExit(2500)
                    ? Result(candidate, ProcessCloseOutcome.Closed, "Aplikacja zamknęła się poprawnie.")
                    : Result(candidate, ProcessCloseOutcome.StillRunning, "Aplikacja nadal działa; sprawdź niezapisane pliki przed wymuszonym zamknięciem.");
            }

            process.Kill(entireProcessTree: false);
            return process.WaitForExit(4000)
                ? Result(candidate, ProcessCloseOutcome.Closed, "Proces został zakończony.")
                : Result(candidate, ProcessCloseOutcome.StillRunning, "Windows nie potwierdził zakończenia procesu.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException)
        {
            return Result(candidate, ProcessCloseOutcome.Failed, ex.Message);
        }
    }

    private static ProcessCloseResult Result(ProcessUsageSnapshot candidate, ProcessCloseOutcome outcome, string detail) =>
        new(candidate.ProcessId, candidate.Name, outcome, detail);
}
