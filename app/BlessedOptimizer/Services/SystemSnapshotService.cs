using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using BlessedOptimizer.Models;

namespace BlessedOptimizer.Services;

[SupportedOSPlatform("windows")]
public static class SystemSnapshotService
{
    public static Task<DeviceSnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(cancellationToken), cancellationToken);

    public static DeviceSnapshot Capture(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var os = ReadRegistryValue(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "ProductName") ?? "Windows";
        var displayVersion = ReadRegistryValue(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "DisplayVersion") ?? "";
        var build = ReadRegistryValue(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "CurrentBuild") ?? "";
        var osDescription = string.Join(" ", new[] { os, displayVersion }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var processorName = ReadRegistryValue(
            RegistryHive.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString") ?? "Nie udało się odczytać";

        var memory = ReadMemoryStatus();
        var gpuNames = ReadGraphicsAdapters(cancellationToken);
        var freeSpace = ReadSystemDriveFreeSpace();
        var adapters = ReadNetworkAdapters();

        return new DeviceSnapshot(
            osDescription,
            build,
            processorName.Trim(),
            Math.Max(1, Environment.ProcessorCount),
            memory.TotalGb,
            memory.AvailableGb,
            gpuNames,
            freeSpace,
            adapters,
            DateTimeOffset.Now);
    }

    private static string? ReadRegistryValue(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName)?.ToString()?.Trim();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static (double TotalGb, double AvailableGb) ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
            return (0, 0);
        return (BytesToGb(status.TotalPhysical), BytesToGb(status.AvailablePhysical));
    }

    private static string ReadGraphicsAdapters(CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            searcher.Options.Timeout = TimeSpan.FromSeconds(6);
            using var results = searcher.Get();
            var names = new List<string>();
            foreach (ManagementBaseObject item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (item)
                {
                    var name = item["Name"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }
            return names.Count == 0 ? "Nie wykryto nazwy karty" : string.Join(", ", names.Take(3));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return "Nie udało się odczytać";
        }
    }

    private static double? ReadSystemDriveFreeSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return drive.IsReady ? BytesToGb((ulong)drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static IReadOnlyList<NetworkAdapterSnapshot> ReadNetworkAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .OrderByDescending(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Select(adapter => new NetworkAdapterSnapshot(
                    string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name,
                    adapter.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                        _ when adapter.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) => "Bluetooth",
                        _ => adapter.NetworkInterfaceType.ToString()
                    },
                    adapter.OperationalStatus == OperationalStatus.Up ? "Połączono" : "Rozłączono",
                    adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000d:0.#} Mb/s" : "Nieznana szybkość"))
                .Take(12)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkAdapterSnapshot>();
        }
    }

    private static double BytesToGb(ulong bytes) => bytes / 1024d / 1024d / 1024d;

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
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
