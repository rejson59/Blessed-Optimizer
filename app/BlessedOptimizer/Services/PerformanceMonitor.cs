using System.Runtime.InteropServices;
namespace BlessedOptimizer.Services;

public sealed record LiveUsage(double? CpuPercent, double MemoryUsedGb, double MemoryTotalGb, double MemoryPercent);

public sealed class PerformanceMonitor
{
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;

    public LiveUsage Read()
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0;
        var totalGb = hasMemory ? ToGb(memory.TotalPhysical) : 0;
        var availableGb = hasMemory ? ToGb(memory.AvailablePhysical) : 0;
        var usedGb = Math.Max(0, totalGb - availableGb);
        var memoryPercent = totalGb <= 0 ? 0 : Math.Clamp(usedGb / totalGb * 100, 0, 100);

        double? cpuPercent = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var idleNow = idle.ToUInt64();
            var kernelNow = kernel.ToUInt64();
            var userNow = user.ToUInt64();
            if (_previousIdle is { } previousIdle && _previousKernel is { } previousKernel && _previousUser is { } previousUser)
            {
                var idleDelta = idleNow - previousIdle;
                var totalDelta = (kernelNow - previousKernel) + (userNow - previousUser);
                if (totalDelta > 0 && idleDelta <= totalDelta)
                    cpuPercent = Math.Clamp((1d - (double)idleDelta / totalDelta) * 100, 0, 100);
            }
            _previousIdle = idleNow;
            _previousKernel = kernelNow;
            _previousUser = userNow;
        }

        return new LiveUsage(cpuPercent, usedGb, totalGb, memoryPercent);
    }

    private static double ToGb(ulong bytes) => bytes / 1024d / 1024d / 1024d;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
