using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace BlessedOptimizer.Services;

/// <summary>A per-user Microsoft Store app (AppX package) visible in the cleanup list.</summary>
public sealed record AppxPackage(
    string Name,
    string PackageFullName,
    string Version,
    string PublisherDisplayName = "",
    bool IsFramework = false,
    bool IsResourcePackage = false,
    bool NonRemovable = false)
{
    public AppxSafetyAssessment Safety => AppxSafetyPolicy.Assess(this);
}

/// <summary>
/// Reads and removes the current user's Store apps (AppX packages). Everything here is
/// scoped to the logged-in account: no admin rights, no system components, no other
/// users. Availability of a later reinstall depends on the publisher and the Microsoft Store.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppxInventoryService
{
    private const string ListCommand =
        "Get-AppxPackage | Select-Object Name, PackageFullName, @{n='Version';e={$_.Version.ToString()}}, PublisherDisplayName, IsFramework, IsResourcePackage, NonRemovable | ConvertTo-Json -Compress";

    /// <summary>Lists the current user's installed Store apps. Empty when PowerShell is unavailable.</summary>
    public static async Task<IReadOnlyList<AppxPackage>> ReadUserPackagesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            process = StartPowerShell(ListCommand);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var output = await process.StandardOutput.ReadToEndAsync(timeoutSource.Token).ConfigureAwait(false);
            _ = await process.StandardError.ReadToEndAsync(timeoutSource.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return Array.Empty<AppxPackage>();
            return ParsePackagesJson(output);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TryKill(process);
            return Array.Empty<AppxPackage>();
        }
    }

    /// <summary>Removes the given packages one by one. Returns the PackageFullNames that failed.</summary>
    public static async Task<IReadOnlyList<string>> RemovePackagesAsync(
        IEnumerable<AppxPackage> packages,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selected = packages.ToArray();
        var failed = new List<string>();
        var currentPackages = await ReadUserPackagesAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var currentByFullName = currentPackages
            .GroupBy(package => package.PackageFullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var package in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageFullName = package.PackageFullName;
            var stillInstalled = currentByFullName.TryGetValue(packageFullName, out var current);
            if (!stillInstalled || current!.Safety.IsProtected || !await RemovePackageAsync(packageFullName, cancellationToken).ConfigureAwait(false))
                failed.Add(packageFullName);
            progress?.Report(packageFullName);
        }
        return failed;
    }

    private static async Task<bool> RemovePackageAsync(string packageFullName, CancellationToken cancellationToken)
    {
        var escaped = packageFullName.Replace("'", "''", StringComparison.Ordinal);
        Process? process = null;
        try
        {
            process = StartPowerShell($"Remove-AppxPackage -Package '{escaped}'");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(60));
            _ = await process.StandardOutput.ReadToEndAsync(timeoutSource.Token).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(timeoutSource.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return process.ExitCode == 0 && string.IsNullOrWhiteSpace(error);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TryKill(process);
            return false;
        }
    }

    /// <summary>Parses the ConvertTo-Json output of the package list (array or single object).</summary>
    internal static IReadOnlyList<AppxPackage> ParsePackagesJson(string? json)
    {
        var packages = new List<AppxPackage>();
        if (string.IsNullOrWhiteSpace(json))
            return packages;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                    Add(element, packages);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                Add(root, packages);
            }
        }
        catch (JsonException)
        {
            // A half-written or empty payload is not a finding; the list simply stays empty.
        }
        return packages;

        static void Add(JsonElement element, List<AppxPackage> packages)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;
            var name = element.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var fullName = element.TryGetProperty("PackageFullName", out var fullNameElement) && fullNameElement.ValueKind == JsonValueKind.String
                ? fullNameElement.GetString()
                : null;
            var version = element.TryGetProperty("Version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;
            var publisher = element.TryGetProperty("PublisherDisplayName", out var publisherElement) && publisherElement.ValueKind == JsonValueKind.String
                ? publisherElement.GetString()
                : string.Empty;
            var isFramework = ReadBoolean(element, "IsFramework");
            var isResourcePackage = ReadBoolean(element, "IsResourcePackage");
            var nonRemovable = ReadBoolean(element, "NonRemovable");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(fullName))
                packages.Add(new AppxPackage(name, fullName, version ?? string.Empty, publisher ?? string.Empty, isFramework, isResourcePackage, nonRemovable));
        }

        static bool ReadBoolean(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return false;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Null)
                return false;
            return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed;
        }
    }

    private static Process StartPowerShell(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Nie udało się uruchomić PowerShell.");
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process already exited; nothing to clean up.
        }
    }
}
