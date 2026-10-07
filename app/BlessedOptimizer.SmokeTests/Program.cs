using BlessedOptimizer.Services;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Te kontrole są przeznaczone wyłącznie dla Windows.");

var plan = PowerSettingsService.ReadActivePlan();
if (plan.SchemeId == Guid.Empty)
    throw new InvalidOperationException("Windows nie zwrócił GUID aktywnego planu zasilania.");

var processorDescriptor = PowerSettingsService.SupportedSettings.Single(setting => setting.Key == "processor-max");
var expectedOptions = Enumerable.Range(10, 11).Select(index => (uint)(index * 5)).ToArray();
var actualOptions = processorDescriptor.Options.Select(option => option.Value).ToArray();
if (!actualOptions.SequenceEqual(expectedOptions))
    throw new InvalidOperationException($"Opcje limitu procesora są nieprawidłowe: {string.Join(",", actualOptions)}.");

var processes = new ProcessOverviewService().ReadSnapshot();
if (processes.Count == 0 || processes.All(process => process.ProcessId != Environment.ProcessId))
    throw new InvalidOperationException("Nie udało się odczytać lokalnego procesu testowego.");

var startupEntries = StartupManagerService.ReadEntries();
Console.WriteLine($"Windows read-only smoke checks passed: plan={plan.SchemeId:D}, settings={plan.Settings.Count}, processes={processes.Count}, startupEntries={startupEntries.Count}.");

var display = DisplayDiagnostics.ReadPrimaryDisplay();
if (display is { CurrentHz: <= 0 })
    throw new InvalidOperationException("Windows zwrócił nieprawidłową częstotliwość odświeżania ekranu.");
if (display is not null && display.MaximumHz < display.CurrentHz)
    throw new InvalidOperationException("Maksymalne odświeżanie nie może być niższe od bieżącego.");

var tempScan = MaintenanceService.ScanTemp(TimeSpan.FromDays(2));
if (tempScan.FileCount < 0 || tempScan.SizeMb < 0)
    throw new InvalidOperationException("Skan plików tymczasowych zwrócił ujemny wynik.");

var freshProfile = new BlessedProfile();
if (freshProfile.AllowTempCleanup || freshProfile.TempCleanupConsentSet)
    throw new InvalidOperationException("Automatyczne sprzątanie plików TEMP musi wymagać osobnej zgody użytkownika.");

var profile = new BlessedProfile { Priority = BlessedPriority.Work };
if (string.IsNullOrWhiteSpace(profile.PriorityLabel) || string.IsNullOrWhiteSpace(profile.PriorityPromise))
    throw new InvalidOperationException("Profil Blessed nie opisuje priorytetu użytkownika.");

var report = await new BlessedWatchService().InspectAsync(null, profile, null, CancellationToken.None);
if (report.Findings.Count == 0)
    throw new InvalidOperationException("Przegląd Blessed musi zwrócić przynajmniej jedną informację.");
if (report.ProblemCount < 0)
    throw new InvalidOperationException("Liczba znalezionych spraw nie może być ujemna.");

var recycleScan = MaintenanceService.ScanRecycleBin();
if (recycleScan.FileCount < 0 || recycleScan.SizeMb < 0)
    throw new InvalidOperationException("Skan koszy zwrócił ujemny wynik.");

var appxPackages = await AppxInventoryService.ReadUserPackagesAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
if (appxPackages.Any(package => string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.PackageFullName)))
    throw new InvalidOperationException("Lista aplikacji AppX zawiera pozycję bez nazwy lub pełnej nazwy pakietu.");

var rebootPending = SecurityDiagnostics.IsUpdateRebootPending();
var lastUpdate = SecurityDiagnostics.TryGetLastUpdateInstallDate();
if (lastUpdate > DateTime.Now)
    throw new InvalidOperationException("Data ostatniej aktualizacji nie może być z przyszłości.");

var defender = SecurityDiagnostics.ReadDefenderStatus();
var disabledFirewallProfiles = SecurityDiagnostics.ReadDisabledFirewallProfiles();
var timeSyncDisabled = SecurityDiagnostics.IsTimeSyncDisabled();

var mutedProfile = new BlessedProfile { MutedFindingIds = new List<string> { "all-clear" } };
var mutedReport = await new BlessedWatchService().InspectAsync(null, mutedProfile, null, CancellationToken.None);
if (mutedReport.Findings.Any(finding => mutedProfile.MutedFindingIds.Contains(finding.Id)))
    throw new InvalidOperationException("Wyciszona sprawa nie może pojawić się w raporcie.");

Console.WriteLine($"Blessed watch smoke checks passed: findings={report.Findings.Count}, problems={report.ProblemCount}, tempFiles={tempScan.FileCount}, display={(display is null ? "n/a" : $"{display.CurrentHz}/{display.MaximumHz} Hz")}.");
Console.WriteLine($"Security read-only smoke checks passed: rebootPending={rebootPending}, lastUpdate={(lastUpdate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}, defender={(defender is null ? "n/a" : $"realTime={defender.RealTimeProtectionEnabled}, signature={(defender.SignatureUpdatedAt?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}")}, disabledFirewallProfiles={disabledFirewallProfiles.Count}, timeSyncDisabled={timeSyncDisabled}, recycleBinFiles={recycleScan.FileCount}, recycleBinMb={recycleScan.SizeMb:0}, mutedFindings={mutedReport.Findings.Count}, appxPackages={appxPackages.Count}.");
