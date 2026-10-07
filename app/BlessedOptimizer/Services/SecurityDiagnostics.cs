using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BlessedOptimizer.Services;

/// <summary>Read-only security posture of the machine: updates, Defender, firewall, time sync.</summary>
public sealed record DefenderStatus(bool RealTimeProtectionEnabled, DateTime? SignatureUpdatedAt);

/// <summary>
/// Reads the things Windows keeps to itself: pending update reboots, the age of the
/// last installed update, Defender protection state, firewall profiles and time sync.
/// Everything here is read-only — the caller decides what, if anything, gets changed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecurityDiagnostics
{
    /// <summary>True when Windows Update finished downloading updates and waits for a restart.</summary>
    public static bool IsUpdateRebootPending()
    {
        using var updateKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        if (updateKey is not null)
            return true;
        using var servicingKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        return servicingKey is not null;
    }

    /// <summary>Install date of the newest quick-fix engineering entry, or null when WMI stays quiet.</summary>
    public static DateTime? TryGetLastUpdateInstallDate()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT InstalledOn FROM Win32_QuickFixEngineering"));
            DateTime? latest = null;
            foreach (var item in searcher.Get())
            {
                using var record = (ManagementObject)item;
                // InstalledOn arrives as a "M/D/yyyy" string in the WMI provider.
                if (record["InstalledOn"] is not string raw || string.IsNullOrWhiteSpace(raw))
                    continue;
                if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var installed))
                    continue;
                if (latest is null || installed > latest.Value)
                    latest = installed;
            }
            return latest;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Defender status, or null when the Defender WMI provider is not available.</summary>
    public static DefenderStatus? ReadDefenderStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                new ObjectQuery("SELECT RealTimeProtectionEnabled, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus"));
            foreach (var item in searcher.Get())
            {
                using var record = (ManagementObject)item;
                var realTime = record["RealTimeProtectionEnabled"] is bool enabled && enabled;
                var signatureAt = ParseWmiDateTime(record["AntivirusSignatureLastUpdated"] as string);
                return new DefenderStatus(realTime, signatureAt);
            }
            return null;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException)
        {
            // No Defender provider (third-party AV, stripped-down image) — not a finding.
            return null;
        }
    }

    /// <summary>Names ("Domain"/"Private"/"Public") of firewall profiles that are currently disabled.</summary>
    public static IReadOnlyList<string> ReadDisabledFirewallProfiles()
    {
        var disabled = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\StandardCimv2"),
                new ObjectQuery("SELECT Name, Enabled FROM MSFT_NetFirewallProfile"));
            foreach (var item in searcher.Get())
            {
                using var record = (ManagementObject)item;
                if (record["Enabled"] is not bool enabled || enabled)
                    continue;
                if (record["Name"] as string is { } name && !string.IsNullOrWhiteSpace(name))
                    disabled.Add(name);
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException)
        {
            // The firewall provider is a bonus read; never let it break the sweep.
            return Array.Empty<string>();
        }
        return disabled;
    }

    /// <summary>True when the Windows Time service is fully disabled (start type 4).</summary>
    public static bool IsTimeSyncDisabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time");
        return key?.GetValue("Start") is int start && start == 4;
    }

    /// <summary>Parses a WMI datetime ("yyyymmddHHmmss.ffffff±uuu"); null when empty or malformed.</summary>
    internal static DateTime? ParseWmiDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }
}
