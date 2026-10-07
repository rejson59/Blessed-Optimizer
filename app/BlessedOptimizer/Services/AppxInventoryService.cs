using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace BlessedOptimizer.Services;

/// <summary>A per-user Microsoft Store app (AppX package) visible in the cleanup list.</summary>
public sealed record AppxPackage(string Name, string PackageFullName, string Version);

/// <summary>
/// Reads and removes the current user's Store apps (AppX packages). Everything here is
/// scoped to the logged-in account: no admin rights, no system components, no other
/// users. Removed apps can be installed again from the Microsoft Store at any time.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppxInventoryService
{
    private const string ListCommand =
        "Get-AppxPackage | Select-Object Name, PackageFullName, @{n='Version';e={$_.Version.ToString()}} | ConvertTo-Json -Compress";

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
        IEnumerable<string> packageFullNames,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var failed = new List<string>();
        foreach (var packageFullName in packageFullNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await RemovePackageAsync(packageFullName, cancellationToken).ConfigureAwait(false))
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
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(fullName))
                packages.Add(new AppxPackage(name, fullName, version ?? string.Empty));
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
