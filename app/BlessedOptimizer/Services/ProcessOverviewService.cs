using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace BlessedOptimizer.Services;

public sealed record ProcessUsageSnapshot(
    int ProcessId,
    string Name,
    double? CpuPercent,
    double WorkingSetMb,
    DateTime? StartedAt,
    ProcessSafetyAssessment Safety) : INotifyPropertyChanged
{
    private bool _isImportant;

    /// <summary>A per-user keep preference; changed only by the user in the process list.</summary>
    public bool IsImportant
    {
        get => _isImportant;
        set
        {
            if (_isImportant == value)
                return;
            _isImportant = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImportant)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImportanceLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanClose)));
        }
    }

    public string ImportanceLabel => IsImportant
        ? (Safety.IsProtected ? "Ważny · chroniony" : "Ważny dla Ciebie")
        : Safety.RoleLabel;
    public bool CanClose => Safety.CanClose && !IsImportant;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProcessOverviewService
{
    private sealed record CpuSample(TimeSpan TotalProcessorTime, long Timestamp);
    private Dictionary<int, CpuSample> _previousSamples = new();

    public IReadOnlyList<ProcessUsageSnapshot> ReadSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        var currentProcessId = Environment.ProcessId;
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var nextSamples = new Dictionary<int, CpuSample>();
        var rows = new List<ProcessUsageSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    var name = process.ProcessName;
                    var cpuTime = process.TotalProcessorTime;
                    var workingSetMb = process.WorkingSet64 / 1024d / 1024d;
                    double? cpuPercent = null;
                    if (_previousSamples.TryGetValue(id, out var previous) && now > previous.Timestamp)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, now);
                        var used = (cpuTime - previous.TotalProcessorTime).TotalSeconds;
                        if (elapsed.TotalSeconds > 0 && used >= 0)
                            cpuPercent = Math.Clamp(used / elapsed.TotalSeconds / logicalProcessors * 100d, 0, 100);
                    }
                    nextSamples[id] = new CpuSample(cpuTime, now);

                    DateTime? startedAt = null;
                    try { startedAt = process.StartTime; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException) { }

                    string? executablePath = null;
                    try { executablePath = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException or ArgumentException) { }

                    var hasMainWindow = false;
                    var sessionId = currentSessionId;
                    try
                    {
                        hasMainWindow = process.MainWindowHandle != IntPtr.Zero;
                        sessionId = process.SessionId;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException or ArgumentException)
                    {
                        // A process whose session/window cannot be verified is classified conservatively below.
                        sessionId = int.MinValue;
                    }

                    var safety = ProcessSafetyPolicy.Assess(
                        id, name, executablePath, hasMainWindow, sessionId,
                        currentProcessId, currentSessionId, windowsDirectory);
                    if (startedAt is null && safety.CanClose)
                    {
                        safety = safety with
                        {
                            IsProtected = true,
                            CanClose = false,
                            RoleLabel = "Niezweryfikowany start",
                            Explanation = "Nie udało się odczytać czasu uruchomienia, potrzebnego do bezpiecznego potwierdzenia tożsamości procesu. Odśwież listę przed ponowną oceną."
                        };
                    }
                    rows.Add(new ProcessUsageSnapshot(id, name, cpuPercent, workingSetMb, startedAt, safety));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException or ArgumentException)
                {
                    // The process may exit or deny access while the snapshot is being read.
                }
            }
        }

        _previousSamples = nextSamples;
        return rows
            .OrderByDescending(row => row.CpuPercent ?? -1)
            .ThenByDescending(row => row.WorkingSetMb)
            .ToArray();
    }
}
