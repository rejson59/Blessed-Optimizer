namespace BlessedOptimizer.Models;

public sealed record NetworkAdapterSnapshot(string Name, string Type, string Status, string Speed);

public sealed record DeviceSnapshot(
    string OperatingSystem,
    string OperatingSystemBuild,
    string ProcessorName,
    int LogicalProcessorCount,
    double TotalMemoryGb,
    double AvailableMemoryGb,
    string GraphicsAdapters,
    double? SystemDriveFreeGb,
    IReadOnlyList<NetworkAdapterSnapshot> NetworkAdapters,
    DateTimeOffset CapturedAt)
{
    public double UsedMemoryGb => Math.Max(0, TotalMemoryGb - AvailableMemoryGb);
    public double MemoryUsagePercent => TotalMemoryGb <= 0 ? 0 : UsedMemoryGb / TotalMemoryGb * 100;

    public IReadOnlyList<string> GetInstallerFacts()
    {
        var facts = new List<string>
        {
            $"Czy Twój PC odpali 300 kalkulatorów bez zacięcia? Nie będę zgadywać — pokazuję prawdziwe parametry, bez uruchamiania benchmarku.",
            $"Windows widzi {LogicalProcessorCount} logicznych wątków procesora. Sama ich liczba nie przewiduje FPS — liczą się też grafika, pamięć i temperatury.",
            $"Wykryta pamięć RAM: {TotalMemoryGb:0.#} GB. Wolna pamięć to ważny sygnał, ale nie jedyna miara szybkości komputera."
        };

        if (!string.Equals(GraphicsAdapters, "Nie udało się odczytać", StringComparison.OrdinalIgnoreCase))
            facts.Add($"Wykryta grafika: {GraphicsAdapters}. Blessed niczego nie podkręca ani nie zmienia jej ustawień.");
        if (SystemDriveFreeGb is { } freeGb)
            facts.Add($"Na dysku systemowym jest około {freeGb:0.#} GB wolnego miejsca. Samo zwolnienie miejsca nie zawsze przyspiesza Windows.");
        facts.Add("Ciekawostki opierają się na odczycie lokalnym. Instalator nie wysyła parametrów urządzenia do internetu.");
        return facts;
    }
}
