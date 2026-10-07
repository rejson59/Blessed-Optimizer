using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using BlessedOptimizer.Models;

namespace BlessedOptimizer.Services;

public enum FindingSeverity
{
    Good,
    Info,
    Warning,
    Critical
}

public enum FindingAction
{
    None,
    BlessedHandlesIt,
    OpenPage,
    OpenSettings
}

public sealed record BlessedFinding(
    string Id,
    FindingSeverity Severity,
    string Title,
    string Message,
    string? ActionLabel,
    FindingAction Action,
    string? ActionTarget);

public sealed record WatchReport(
    DateTimeOffset CompletedAt,
    IReadOnlyList<BlessedFinding> Findings)
{
    public int ProblemCount => Findings.Count(finding => finding.Severity is FindingSeverity.Warning or FindingSeverity.Critical);

    public string Headline => ProblemCount switch
    {
        0 => "Wszystko gra — nic nie wymaga Twojej uwagi.",
        1 => "Znalazłem 1 rzecz wartą zajęcia się.",
        _ => $"Znalazłem {ProblemCount} rzeczy warte zajęcia się."
    };
}

/// <summary>
/// The proactive side of Blessed: a single sweep over everything Windows usually
/// stays quiet about. Read-only — the caller decides what gets applied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BlessedWatchService
{
    private static readonly TimeSpan TempFileAge = TimeSpan.FromDays(2);
    private static readonly TimeSpan TempScanInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AppxScanInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AppxScanTimeout = TimeSpan.FromSeconds(15);

    private TempScanResult? _cachedTempScan;
    private DateTimeOffset _cachedTempScanAt = DateTimeOffset.MinValue;
    private IReadOnlyList<AppxPackage>? _cachedAppxPackages;
    private DateTimeOffset _cachedAppxScanAt = DateTimeOffset.MinValue;

    public TempScanResult? LastTempScan => _cachedTempScan;

    public void InvalidateTempScan() => _cachedTempScan = null;

    public void InvalidateAppxScan() => _cachedAppxPackages = null;

    public async Task<WatchReport> InspectAsync(DeviceSnapshot? snapshot, BlessedProfile profile, LiveUsage? usage, CancellationToken cancellationToken = default)
    {
        var findings = new List<BlessedFinding>();

        await Task.Run(() =>
        {
            InspectDiskSpace(snapshot, findings);
            InspectMemory(usage, findings);
            InspectProcessors(usage, profile, findings);
            InspectUptime(findings);
            InspectStartup(profile, findings);
            InspectDisplay(findings);
            InspectPowerPlan(profile, findings);
            InspectBattery(profile, findings);
            InspectDriveHealth(findings);
            InspectWindowsUpdate(findings);
            InspectDefender(findings);
            InspectFirewall(findings);
            InspectTimeSync(findings);
            InspectHiberfile(findings);
            InspectRecycleBin(findings);
        }, cancellationToken).ConfigureAwait(false);

        await InspectTempAsync(profile, findings, cancellationToken).ConfigureAwait(false);
        await InspectAppxAsync(findings, cancellationToken).ConfigureAwait(false);

        findings = WithoutMuted(findings, profile);

        if (findings.Count == 0)
        {
            findings.Add(new BlessedFinding(
                "all-clear",
                FindingSeverity.Good,
                "Czysto — nie mam się do czego przyczepić",
                $"Sprawdziłem dysk, pamięć, autostart, ekran, baterię, zasilanie, aktualizacje i ochronę. {profile.PriorityPromise}",
                null,
                FindingAction.None,
                null));
        }

        var ordered = findings
            .OrderByDescending(finding => (int)finding.Severity)
            .ToArray();

        return new WatchReport(DateTimeOffset.Now, ordered);
    }

    /// <summary>Removes findings the user muted. Kept separate so the rule stays unit-testable.</summary>
    internal static List<BlessedFinding> WithoutMuted(IReadOnlyList<BlessedFinding> findings, BlessedProfile profile)
    {
        if (profile.MutedFindingIds.Count == 0)
            return findings.ToList();
        return findings.Where(finding => !profile.MutedFindingIds.Contains(finding.Id)).ToList();
    }

    /// <summary>Findings Blessed can apply on its own during a one-click optimization run.</summary>
    internal static IReadOnlyList<BlessedFinding> AutoApplicableFindings(WatchReport report) =>
        report.Findings.Where(finding => finding.Action == FindingAction.BlessedHandlesIt).ToArray();

    /// <summary>True when the power-plan finding should be fixed automatically (full CPU on AC for gaming/work).</summary>
    internal static bool IsOneClickPowerFixApplicable(BlessedFinding finding, BlessedProfile profile) =>
        finding.Id == "power-cpu-limit" && profile.Priority is BlessedPriority.Gaming or BlessedPriority.Work;

    private static void InspectDiskSpace(DeviceSnapshot? snapshot, List<BlessedFinding> findings)
    {
        double freeGb;
        double totalGb;
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrWhiteSpace(systemRoot))
                return;
            var drive = new DriveInfo(systemRoot);
            if (!drive.IsReady)
                return;
            freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            totalGb = drive.TotalSize / 1024d / 1024d / 1024d;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            if (snapshot?.SystemDriveFreeGb is not { } fallback)
                return;
            freeGb = fallback;
            totalGb = 0;
        }

        var percentFree = totalGb > 0 ? freeGb / totalGb * 100 : 100;
        if (freeGb >= 25 && percentFree >= 12)
            return;

        var severity = freeGb < 10 || percentFree < 5 ? FindingSeverity.Critical : FindingSeverity.Warning;
        findings.Add(new BlessedFinding(
            "disk-space",
            severity,
            $"Na dysku systemowym zostało {freeGb:0.#} GB",
            "Windows potrzebuje zapasu na aktualizacje i plik stronicowania. Zacznę od plików tymczasowych, a resztę pokażę Ci w Ustawieniach pamięci.",
            "Pokaż, co zajmuje miejsce",
            FindingAction.OpenSettings,
            "ms-settings:storagesense"));
    }

    private async Task InspectTempAsync(BlessedProfile profile, List<BlessedFinding> findings, CancellationToken cancellationToken)
    {
        if (_cachedTempScan is null || DateTimeOffset.Now - _cachedTempScanAt > TempScanInterval)
        {
            try
            {
                _cachedTempScan = await MaintenanceService.ScanTempAsync(TempFileAge, cancellationToken).ConfigureAwait(false);
                _cachedTempScanAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return;
            }
        }

        if (_cachedTempScan is not { } scan || scan.SizeMb < 300)
            return;

        findings.Add(new BlessedFinding(
            "temp-files",
            scan.SizeMb > 4000 ? FindingSeverity.Warning : FindingSeverity.Info,
            $"{scan.SizeMb:0} MB śmieci po instalatorach i aplikacjach",
            $"W folderze tymczasowym leży {scan.FileCount} plików starszych niż dwa dni. Windows o tym nie powie, a ja sprzątnę to w kilka sekund — pliki w użyciu zostawiam nietknięte.",
            profile.AllowTempCleanup ? "Sprzątnij to za mnie" : "Pokaż podgląd i sprzątnij",
            FindingAction.BlessedHandlesIt,
            "temp-cleanup"));
    }

    private static void InspectMemory(LiveUsage? usage, List<BlessedFinding> findings)
    {
        if (usage is not { } live || live.MemoryTotalGb <= 0)
            return;
        if (live.MemoryPercent < 85)
            return;

        var hog = FindTopMemoryProcess();
        var detail = hog is null
            ? "Zamknięcie kilku nieużywanych okien od razu da oddech."
            : $"Najwięcej bierze teraz {hog.Value.Name} ({hog.Value.MemoryMb:0} MB).";

        findings.Add(new BlessedFinding(
            "memory-pressure",
            live.MemoryPercent >= 93 ? FindingSeverity.Critical : FindingSeverity.Warning,
            $"Pamięć RAM zajęta w {live.MemoryPercent:0}%",
            $"Przy takim zapełnieniu Windows zaczyna przerzucać dane na dysk i wszystko zwalnia. {detail}",
            "Pokaż, co zjada pamięć",
            FindingAction.OpenPage,
            "processes"));
    }

    private static void InspectProcessors(LiveUsage? usage, BlessedProfile profile, List<BlessedFinding> findings)
    {
        if (usage?.CpuPercent is not { } cpu || cpu < 88)
            return;

        var hog = FindTopCpuProcess();
        var detail = hog is null ? string.Empty : $" Na czele jest {hog}.";
        findings.Add(new BlessedFinding(
            "cpu-pressure",
            FindingSeverity.Warning,
            $"Procesor pracuje na {cpu:0}%",
            $"Coś mocno obciąża komputer w tle.{detail} {(profile.Priority == BlessedPriority.Quiet ? "Przy takim obciążeniu wentylatory na pewno dadzą o sobie znać." : "Przy takim obciążeniu gra lub praca potrafi się zacinać.")}",
            "Zobacz listę procesów",
            FindingAction.OpenPage,
            "processes"));
    }

    private static void InspectUptime(List<BlessedFinding> findings)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (uptime.TotalDays < 7)
            return;

        findings.Add(new BlessedFinding(
            "uptime",
            FindingSeverity.Info,
            $"Windows działa bez restartu od {uptime.TotalDays:0} dni",
            "Po tylu dniach sterowniki i pamięć potrafią się zapchać drobiazgami, a część aktualizacji czeka na ponowne uruchomienie. Zwykły restart załatwia sprawę.",
            null,
            FindingAction.None,
            null));
    }

    private static void InspectStartup(BlessedProfile profile, List<BlessedFinding> findings)
    {
        IReadOnlyList<StartupEntry> entries;
        try
        {
            entries = StartupManagerService.ReadEntries();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        var active = entries.Count(entry => entry.IsEnabled);
        if (active < 6)
            return;

        findings.Add(new BlessedFinding(
            "startup-crowd",
            active >= 10 ? FindingSeverity.Warning : FindingSeverity.Info,
            $"{active} programów startuje razem z Windowsem",
            "Każdy z nich zabiera czas przy logowaniu i pamięć przez cały dzień. Przejrzyjmy je razem — każdy wyłączony wpis mogę przywrócić jednym kliknięciem.",
            "Zajmij się autostartem",
            FindingAction.OpenPage,
            "startup"));
    }

    private static void InspectDisplay(List<BlessedFinding> findings)
    {
        DisplayModeInfo? display;
        try
        {
            display = DisplayDiagnostics.ReadPrimaryDisplay();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return;
        }

        if (display is not { CanGoFaster: true } mode)
            return;

        findings.Add(new BlessedFinding(
            "display-hz",
            FindingSeverity.Warning,
            $"Ekran chodzi na {mode.CurrentHz} Hz, a potrafi {mode.MaximumHz} Hz",
            "To jedna z tych rzeczy, o których Windows milczy — po zmianie kabla, sterownika albo aktualizacji system potrafi zostawić niższe odświeżanie. Wyższa wartość to od razu płynniejszy ruch myszki i obrazu.",
            "Ustaw wyższe odświeżanie",
            FindingAction.OpenSettings,
            "ms-settings:display-advanced"));
    }

    private static void InspectPowerPlan(BlessedProfile profile, List<BlessedFinding> findings)
    {
        PowerPlanSnapshot plan;
        try
        {
            plan = PowerSettingsService.ReadActivePlan();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return;
        }

        var processor = plan.Settings.FirstOrDefault(state => string.Equals(state.Descriptor.Key, "processor-max", StringComparison.Ordinal));
        if (processor is null)
            return;

        if (profile.Priority is BlessedPriority.Gaming or BlessedPriority.Work && processor.AcValue < 100)
        {
            findings.Add(new BlessedFinding(
                "power-cpu-limit",
                FindingSeverity.Warning,
                $"Plan „{plan.SchemeName}” trzyma procesor na {processor.AcValue}%",
                "Przy zasilaniu z gniazdka nie ma powodu, żeby oddawać moc, za którą zapłaciłeś. Podniosę limit do 100% — oryginalne wartości zapiszę, żeby dało się wrócić.",
                "Zajmij się zasilaniem",
                FindingAction.OpenPage,
                "power"));
        }
        else if (profile.Priority == BlessedPriority.Battery && processor.DcValue > 90)
        {
            findings.Add(new BlessedFinding(
                "power-battery",
                FindingSeverity.Info,
                $"Na baterii procesor może pracować na {processor.DcValue}%",
                "Skoro zależy Ci na długim dniu bez ładowarki, warto zejść niżej — komputer będzie chłodniejszy i wytrzyma dłużej.",
                "Zajmij się zasilaniem",
                FindingAction.OpenPage,
                "power"));
        }
    }

    private static void InspectBattery(BlessedProfile profile, List<BlessedFinding> findings)
    {
        if (!GetSystemPowerStatus(out var status))
            return;
        var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
        if (!hasBattery)
            return;

        if (status.ACLineStatus == 0 && status.BatteryLifePercent <= 25)
        {
            findings.Add(new BlessedFinding(
                "battery-low",
                FindingSeverity.Info,
                $"Bateria na poziomie {status.BatteryLifePercent}%",
                "Przy tym poziomie Windows sam zacznie przycinać wydajność. Jeśli masz przed sobą dłuższą pracę, podłącz ładowarkę albo pozwól mi zejść z apetytem procesora.",
                "Zajmij się zasilaniem",
                FindingAction.OpenPage,
                "power"));
        }
        else if (profile.Priority == BlessedPriority.Battery && status.ACLineStatus == 1 && status.BatteryLifePercent >= 98)
        {
            findings.Add(new BlessedFinding(
                "battery-full",
                FindingSeverity.Info,
                "Bateria stale naładowana w 100%",
                "Ciągłe trzymanie ogniw na maksimum przyspiesza ich zużycie. Jeśli komputer stoi na biurku, warto czasem odpiąć ładowarkę — Windows o tym nie przypomni.",
                null,
                FindingAction.None,
                null));
        }
    }

    private static void InspectDriveHealth(List<BlessedFinding> findings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\wmi"),
                new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"));
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using var record = (ManagementObject)item;
                if (record["PredictFailure"] is not bool predicted || !predicted)
                    continue;
                var instance = record["InstanceName"] as string ?? "dysk systemowy";
                findings.Add(new BlessedFinding(
                    "drive-health",
                    FindingSeverity.Critical,
                    "Dysk zgłasza ostrzeżenie S.M.A.R.T.",
                    $"Kontroler raportuje przewidywaną awarię nośnika ({instance}). Windows pokaże to dopiero, gdy będzie za późno. Zrób kopię ważnych plików już teraz.",
                    "Otwórz ustawienia kopii zapasowej",
                    FindingAction.OpenSettings,
                    "ms-settings:backup"));
                break;
            }
        }
        catch (ManagementException)
        {
            // Not every controller exposes failure prediction.
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException)
        {
            // Reading SMART is a bonus; never let it break the sweep.
        }
    }

    private static void InspectWindowsUpdate(List<BlessedFinding> findings)
    {
        bool rebootPending;
        DateTime? lastInstalled;
        try
        {
            rebootPending = SecurityDiagnostics.IsUpdateRebootPending();
            lastInstalled = SecurityDiagnostics.TryGetLastUpdateInstallDate();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        if (rebootPending)
        {
            findings.Add(new BlessedFinding(
                "update-reboot-pending",
                FindingSeverity.Warning,
                "Aktualizacje Windows czekają na restart",
                "System pobrał ważne aktualizacje i dokończy je instalować dopiero przy ponownym uruchomieniu. Do tego czasu komputer pracuje bez części łatek — a Windows pokazuje to tylko jako małą ikonkę.",
                "Otwórz Windows Update",
                FindingAction.OpenSettings,
                "ms-settings:windowsupdate-action"));
            return;
        }

        if (lastInstalled is { } installed && DateTime.Now - installed > TimeSpan.FromDays(35))
        {
            findings.Add(new BlessedFinding(
                "update-stale",
                FindingSeverity.Info,
                $"Ostatnia aktualizacja: {installed:dd MMMM yyyy}",
                "Od ponad miesiąca na komputerze nie pojawiła się żadna aktualizacja. Zwykle wszystko dzieje się samo w tle — warto jednak rzucić okiem, czy Windows Update nie utknął.",
                "Sprawdź aktualizacje",
                FindingAction.OpenSettings,
                "ms-settings:windowsupdate"));
        }
    }

    private static void InspectDefender(List<BlessedFinding> findings)
    {
        DefenderStatus? status;
        try
        {
            status = SecurityDiagnostics.ReadDefenderStatus();
        }
        catch (Exception ex) when (ex is ManagementException or SecurityException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException)
        {
            return;
        }
        if (status is null)
            return;

        if (!status.RealTimeProtectionEnabled)
        {
            findings.Add(new BlessedFinding(
                "defender-realtime-off",
                FindingSeverity.Warning,
                "Ochrona w czasie rzeczywistym jest wyłączona",
                "Windows Defender nie skanuje plików na bieżąco, więc komputer nie zauważa zagrożeń, dopóki ich nie otworzysz. Windows o tym nie powie — a wyłączenie często zostaje po próbie innego programu.",
                "Otwórz ustawienia zabezpieczeń",
                FindingAction.OpenSettings,
                "ms-settings:windowsdefender"));
            return;
        }

        if (status.SignatureUpdatedAt is { } updated && DateTime.Now - updated > TimeSpan.FromDays(3))
        {
            var ageDays = Math.Max(1, (int)(DateTime.Now - updated).TotalDays);
            findings.Add(new BlessedFinding(
                "defender-signature-stale",
                FindingSeverity.Info,
                $"Definicje antywirusa mają {ageDays} dni",
                "Windows Defender działa, ale jego baza zagrożeń jest nieaktualna — świeże zagrożenia mogą przejść niezauważone. Definicje zwykle aktualizują się same, gdy komputer ma dostęp do sieci.",
                "Otwórz Windows Defender",
                FindingAction.OpenSettings,
                "ms-settings:windowsdefender"));
        }
    }

    private static void InspectFirewall(List<BlessedFinding> findings)
    {
        IReadOnlyList<string> disabled;
        try
        {
            disabled = SecurityDiagnostics.ReadDisabledFirewallProfiles();
        }
        catch (Exception ex) when (ex is ManagementException or SecurityException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException)
        {
            return;
        }
        if (disabled.Count == 0)
            return;

        var names = string.Join(", ", disabled.Select(name => name switch
        {
            "Domain" => "profil domenowy",
            "Private" => "profil prywatny",
            "Public" => "profil publiczny",
            _ => $"profil „{name}”"
        }));
        findings.Add(new BlessedFinding(
            "firewall-off",
            disabled.Count > 1 ? FindingSeverity.Critical : FindingSeverity.Warning,
            $"Zapora Windows wyłączona — {names}",
            "Zapora filtruje ruch sieciowy, zanim dotrze on do komputera. Gdy jest wyłączona, każdy program może swobodnie rozmawiać z internetem. Windows czasem wyłącza ją po instalacji innego zabezpieczenia.",
            "Otwórz ustawienia zapory",
            FindingAction.OpenSettings,
            "ms-settings:windowsdefender-firewall"));
    }

    private static void InspectTimeSync(List<BlessedFinding> findings)
    {
        bool disabled;
        try
        {
            disabled = SecurityDiagnostics.IsTimeSyncDisabled();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }
        if (!disabled)
            return;

        findings.Add(new BlessedFinding(
            "time-sync-off",
            FindingSeverity.Warning,
            "Synchronizacja czasu jest wyłączona",
            "Gdy zegar komputera się rozjedzie, przestają działać certyfikaty, podpisy cyfrowe i logowanie do części kont. Windows zwykle sam trzyma czas w ryzach — tutaj usługa jest jednak wyłączona całkowicie.",
            "Otwórz ustawienia daty i godziny",
            FindingAction.OpenSettings,
            "ms-settings:dateandtime"));
    }

    private static void InspectHiberfile(List<BlessedFinding> findings)
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrWhiteSpace(systemRoot))
                return;
            var hiberfil = Path.Combine(systemRoot, "hiberfil.sys");
            if (!File.Exists(hiberfil))
                return;
            var sizeGb = new FileInfo(hiberfil).Length / 1024d / 1024d / 1024d;
            if (sizeGb < 3)
                return;
            findings.Add(new BlessedFinding(
                "hiberfile-size",
                FindingSeverity.Info,
                $"Plik hibernacji zajmuje {sizeGb:0.#} GB",
                "Windows trzyma w nim kopię całej pamięci na wypadek hibernacji. Jeśli komputer zawsze zamykasz, a nie usypiasz, to miejsce można odzyskać w ustawieniach zasilania — Windows o tym nie wspomni.",
                "Otwórz ustawienia zasilania",
                FindingAction.OpenSettings,
                "ms-settings:powersleep"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // hiberfil.sys is a protected system file; when it cannot be measured, stay quiet.
        }
    }

    private static void InspectRecycleBin(List<BlessedFinding> findings)
    {
        RecycleBinScan scan;
        try
        {
            scan = MaintenanceService.ScanRecycleBin();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return;
        }
        if (scan.SizeMb < 1000)
            return;

        var sizeGb = scan.SizeMb / 1024d;
        findings.Add(new BlessedFinding(
            "recycle-bin",
            scan.SizeMb > 8000 ? FindingSeverity.Warning : FindingSeverity.Info,
            sizeGb >= 1 ? $"W koszach leży {sizeGb:0.#} GB" : $"{scan.SizeMb:0} MB w koszach",
            $"Na dyskach nazbierało się {scan.FileCount:N0} plików w koszu — to dokładnie to, co robi przycisk „Opróżnij kosz”, tylko z Twoim wyraźnym potwierdzeniem. Pliki znikają trwale.",
            "Opróżnij kosz za mnie",
            FindingAction.BlessedHandlesIt,
            "recycle-bin-empty"));
    }

    private async Task InspectAppxAsync(List<BlessedFinding> findings, CancellationToken cancellationToken)
    {
        if (_cachedAppxPackages is null || DateTimeOffset.Now - _cachedAppxScanAt > AppxScanInterval)
        {
            try
            {
                _cachedAppxPackages = await AppxInventoryService.ReadUserPackagesAsync(AppxScanTimeout, cancellationToken).ConfigureAwait(false);
                _cachedAppxScanAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return;
            }
        }

        var count = _cachedAppxPackages?.Count ?? 0;
        if (count <= 25)
            return;

        findings.Add(new BlessedFinding(
            "appx-crowd",
            FindingSeverity.Info,
            $"Masz {count} aplikacji z Microsoft Store",
            "Część aplikacji mogła zostać po próbowaniu — zajmują miejsce i aktualizują się w tle. Przejrzyj listę i odinstaluj to, czego nie używasz; każdą aplikację wgrasz z powrotem przez Microsoft Store.",
            "Przejrzyj aplikacje",
            FindingAction.OpenPage,
            "cleanup"));
    }

    private static (string Name, double MemoryMb)? FindTopMemoryProcess()
    {
        (string Name, double MemoryMb)? best = null;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var memoryMb = process.WorkingSet64 / 1024d / 1024d;
                    if (best is null || memoryMb > best.Value.MemoryMb)
                        best = (process.ProcessName, memoryMb);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or SecurityException)
                {
                    // The process may exit while the list is being read.
                }
            }
        }
        return best;
    }

    private static string? FindTopCpuProcess()
    {
        try
        {
            var service = new ProcessOverviewService();
            service.ReadSnapshot();
            Thread.Sleep(400);
            var row = service.ReadSnapshot().FirstOrDefault(entry => entry.CpuPercent is > 1);
            return row is null ? null : $"{row.Name} ({row.CpuPercent:0}% CPU)";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
