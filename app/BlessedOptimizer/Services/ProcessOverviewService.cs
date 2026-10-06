using System.Diagnostics;
using System.Security;

namespace BlessedOptimizer.Services;

public sealed record ProcessUsageSnapshot(
    int ProcessId,
    string Name,
    double? CpuPercent,
    double WorkingSetMb,
    DateTime? StartedAt);

public sealed class ProcessOverviewService
{
    private sealed record CpuSample(TimeSpan TotalProcessorTime, long Timestamp);
    private Dictionary<int, CpuSample> _previousSamples = new();

    public IReadOnlyList<ProcessUsageSnapshot> ReadSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        var nextSamples = new Dictionary<int, CpuSample>();
        var rows = new List<ProcessUsageSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
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

                    rows.Add(new ProcessUsageSnapshot(id, process.ProcessName, cpuPercent, workingSetMb, startedAt));
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
