using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using BlessedOptimizer.Models;

namespace BlessedOptimizer.Services;

public enum FirstBlessingStatus
{
    Ready,
    Review,
    Attention,
    Unavailable
}

public sealed record FirstBlessingCheck(
    string Id,
    string Title,
    FirstBlessingStatus Status,
    string Detail,
    string? ActionPage = null);

public sealed record FirstBlessingReport(
    DateTimeOffset CompletedAt,
    TimeSpan Duration,
    DeviceSnapshot Snapshot,
    DisplayModeInfo? Display,
    IReadOnlyList<PeripheralDevice> Devices,
    bool DeviceInventoryAvailable,
    string? DeviceInventoryMessage,
    PowerPlanSnapshot? PowerPlan,
    LiveUsage Usage,
    IReadOnlyList<FirstBlessingCheck> Checks);

/// <summary>
/// A quick, read-only welcome audit. It samples current usage and reads Windows'
/// reported hardware/configuration; it does not run stress tests or change settings.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FirstBlessingService
{
    public static async Task<FirstBlessingReport> RunAsync(BlessedPriority priority, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var monitor = new PerformanceMonitor();
        _ = monitor.Read();

        var snapshotTask = SystemSnapshotService.CaptureAsync(cancellationToken);
        var deviceTask = ReadDeviceInventoryAsync(cancellationToken);
        var displayTask = ReadDisplayAsync(cancellationToken);
        var powerPlanTask = ReadPowerPlanAsync(cancellationToken);

        // A second low-overhead CPU sample gives a short live baseline, not a
        // synthetic benchmark. The user has explicitly started this welcome audit.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        var usage = monitor.Read();
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var deviceInventory = await deviceTask.ConfigureAwait(false);
        var display = await displayTask.ConfigureAwait(false);
        var powerPlan = await powerPlanTask.ConfigureAwait(false);

        var checks = BuildChecks(snapshot, display, deviceInventory.Devices, deviceInventory.Available, deviceInventory.Message, powerPlan, usage, priority);
        stopwatch.Stop();
        return new FirstBlessingReport(
            DateTimeOffset.Now,
            stopwatch.Elapsed,
            snapshot,
            display,
            deviceInventory.Devices,
            deviceInventory.Available,
            deviceInventory.Message,
            powerPlan,
            usage,
            checks);
    }

    internal static IReadOnlyList<FirstBlessingCheck> BuildChecks(
        DeviceSnapshot snapshot,
        DisplayModeInfo? display,
        IReadOnlyList<PeripheralDevice> devices,
        bool deviceInventoryAvailable,
        string? deviceInventoryMessage,
        PowerPlanSnapshot? powerPlan,
        LiveUsage usage,
        BlessedPriority priority = BlessedPriority.Gaming)
    {
        var checks = new List<FirstBlessingCheck>();

        if (snapshot.TotalMemoryGb <= 0)
        {
            checks.Add(new FirstBlessingCheck("memory", "Pamięć RAM", FirstBlessingStatus.Unavailable, "Windows nie udostępnił odczytu pamięci."));
        }
        else
        {
            var state = snapshot.AvailableMemoryGb < 1 || snapshot.MemoryUsagePercent >= 92
                ? FirstBlessingStatus.Attention
                : snapshot.MemoryUsagePercent >= 80
                    ? FirstBlessingStatus.Review
                    : FirstBlessingStatus.Ready;
            var detail = $"Windows widzi {snapshot.TotalMemoryGb:0.#} GB RAM, w tym {snapshot.AvailableMemoryGb:0.#} GB dostępne ({snapshot.MemoryUsagePercent:0}% użycia w tej próbce). To zależy od uruchomionych aplikacji.";
            checks.Add(new FirstBlessingCheck("memory", "Pamięć RAM", state, detail, state == FirstBlessingStatus.Ready ? null : "processes"));
        }

        if (snapshot.SystemDriveFreeGb is not { } freeGb)
        {
            checks.Add(new FirstBlessingCheck("storage", "Dysk systemowy", FirstBlessingStatus.Unavailable, "Nie udało się odczytać wolnego miejsca."));
        }
        else
        {
            var state = freeGb < 10 ? FirstBlessingStatus.Attention : freeGb < 25 ? FirstBlessingStatus.Review : FirstBlessingStatus.Ready;
            var detail = $"Na dysku systemowym jest około {freeGb:0.#} GB wolnego miejsca. Próg jest tylko wskazówką; Blessed niczego nie usuwa w tym teście.";
            checks.Add(new FirstBlessingCheck("storage", "Dysk systemowy", state, detail, state == FirstBlessingStatus.Ready ? null : "cleanup"));
        }

        if (display is null)
        {
            checks.Add(new FirstBlessingCheck("display", "Ekran i odświeżanie", FirstBlessingStatus.Unavailable, "Nie udało się odczytać trybu głównego ekranu. Możesz sprawdzić to ręcznie w Ustawieniach Windows.", "devices"));
        }
        else
        {
            var state = display.CanGoFaster ? FirstBlessingStatus.Review : FirstBlessingStatus.Ready;
            var detail = display.CanGoFaster
                ? $"Tryb głównego ekranu to {display.Width} × {display.Height} przy {display.CurrentHz} Hz. Windows udostępnia do {display.MaximumHz} Hz dla tego trybu; jeśli chcesz, wybierz częstotliwość ręcznie w ustawieniach ekranu."
                : $"Tryb głównego ekranu to {display.Width} × {display.Height} przy {display.CurrentHz} Hz. Odczytane tryby nie wskazują wyższej częstotliwości dla tej rozdzielczości i głębi koloru.";
            checks.Add(new FirstBlessingCheck("display", "Ekran i odświeżanie", state, detail, display.CanGoFaster ? "devices" : null));
        }

        if (!deviceInventoryAvailable)
        {
            checks.Add(new FirstBlessingCheck(
                "peripherals",
                "Urządzenia peryferyjne",
                FirstBlessingStatus.Unavailable,
                string.IsNullOrWhiteSpace(deviceInventoryMessage) ? "Windows nie udostępnił listy obecnych urządzeń." : deviceInventoryMessage,
                "devices"));
        }
        else
        {
            var devicesWithProblems = devices.Where(device => device.HasReportedProblem).ToArray();
            var categories = devices
                .Select(device => device.Category)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var detail = devices.Count == 0
                ? "Nie znaleziono obecnie urządzeń w obsługiwanych kategoriach. Niektóre urządzenia mogą być opisane przez sterownik inaczej; nie otwierano kamery ani nie odczytywano wejścia."
                : $"Lista obecnych urządzeń ({devices.Count}): {string.Join(", ", categories)}. Ich status można sprawdzić w karcie Urządzenia.";
            var state = devicesWithProblems.Length > 0 ? FirstBlessingStatus.Attention : FirstBlessingStatus.Ready;
            if (devicesWithProblems.Length > 0)
                detail += $" Urządzeń z kodem problemu: {devicesWithProblems.Length}.";
            checks.Add(new FirstBlessingCheck("peripherals", "Urządzenia peryferyjne", state, detail, devicesWithProblems.Length > 0 ? "devices" : null));
        }

        var processorLimit = powerPlan?.Settings.FirstOrDefault(setting => setting.Descriptor.Key == "processor-max");
        if (processorLimit is null)
        {
            checks.Add(new FirstBlessingCheck("power", "Plan zasilania", FirstBlessingStatus.Unavailable, "Nie udało się odczytać limitu procesora aktywnego planu. To nie oznacza awarii.", "power"));
        }
        else
        {
            var state = processorLimit.AcValue < 80 ? FirstBlessingStatus.Review : FirstBlessingStatus.Ready;
            var detail = $"Aktywny plan „{powerPlan!.SchemeName}” ustawia limit procesora na {processorLimit.AcValue}% przy zasilaniu z sieci i {processorLimit.DcValue}% na baterii. Wyższy limit może zwiększyć temperaturę i pobór energii; decyzję zostawiam Tobie.";
            checks.Add(new FirstBlessingCheck("power", "Plan zasilania", state, detail, state == FirstBlessingStatus.Review ? "power" : null));
        }

        var connectedAdapters = snapshot.NetworkAdapters.Count(adapter => string.Equals(adapter.Status, "Połączono", StringComparison.OrdinalIgnoreCase));
        checks.Add(new FirstBlessingCheck(
            "network",
            "Połączenia sieciowe",
            connectedAdapters > 0 ? FirstBlessingStatus.Ready : FirstBlessingStatus.Review,
            connectedAdapters > 0
                ? $"Windows zgłasza {connectedAdapters} połączonych kart sieciowych. Nie wykonywano testu internetowego ani nie zmieniano ustawień sieci."
                : "Nie ma obecnie połączonej karty sieciowej. To może być zamierzone; internet nie był testowany.",
            connectedAdapters > 0 ? null : "connections"));

        var usageDetail = usage.CpuPercent is { } cpu
            ? $"Krótka próbka pokazuje około {cpu:0}% CPU i {usage.MemoryPercent:0}% użycia RAM. To migawka obciążenia, nie wynik benchmarku ani obietnica FPS."
            : $"Krótka próbka pokazuje {usage.MemoryPercent:0}% użycia RAM. CPU jest chwilowo niedostępne; odczyt nie jest benchmarkiem ani obietnicą FPS.";
        checks.Add(new FirstBlessingCheck("baseline", "Bieżące obciążenie", FirstBlessingStatus.Ready, usageDetail));

        var preferenceOrder = priority switch
        {
            BlessedPriority.Work => new[] { "storage", "memory", "baseline", "display", "peripherals", "power", "network" },
            BlessedPriority.Battery => new[] { "power", "network", "storage", "memory", "peripherals", "display", "baseline" },
            BlessedPriority.Quiet => new[] { "power", "memory", "peripherals", "network", "storage", "display", "baseline" },
            _ => new[] { "baseline", "display", "peripherals", "memory", "storage", "power", "network" }
        };
        var priorityRanks = preferenceOrder
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        return checks
            .OrderByDescending(check => check.Status switch
            {
                FirstBlessingStatus.Attention => 3,
                FirstBlessingStatus.Review => 2,
                FirstBlessingStatus.Ready => 1,
                _ => 0
            })
            .ThenBy(check => priorityRanks.GetValueOrDefault(check.Id, int.MaxValue))
            .ToArray();
    }

    private static async Task<DeviceInventoryReadResult> ReadDeviceInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await Task.Run(() => PeripheralDiagnostics.ReadPresentDevices(cancellationToken), cancellationToken).ConfigureAwait(false);
            return new DeviceInventoryReadResult(true, devices, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or SecurityException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new DeviceInventoryReadResult(false, Array.Empty<PeripheralDevice>(), $"Nie udało się odczytać urządzeń Plug and Play: {ex.Message}");
        }
    }

    private static async Task<DisplayModeInfo?> ReadDisplayAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(DisplayDiagnostics.ReadPrimaryDisplay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static async Task<PowerPlanSnapshot?> ReadPowerPlanAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(PowerSettingsService.ReadActivePlan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or SecurityException or InvalidOperationException or IOException or JsonException)
        {
            return null;
        }
    }

    private sealed record DeviceInventoryReadResult(bool Available, IReadOnlyList<PeripheralDevice> Devices, string? Message);
}
