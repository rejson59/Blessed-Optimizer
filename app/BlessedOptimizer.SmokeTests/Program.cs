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
