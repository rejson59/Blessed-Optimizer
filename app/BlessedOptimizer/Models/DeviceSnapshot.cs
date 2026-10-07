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
            $"Czy Twój PC odpali 300 kalkulatorów naraz? Oto jego prawdziwe parametry — prosto z systemu.",
            $"Windows widzi {LogicalProcessorCount} logicznych wątków procesora — tyle zadań Twój CPU bierze na raz.",
            $"Wykryta pamięć RAM: {TotalMemoryGb:0.#} GB. To Twój zapas na gry, karty przeglądarki i pracę naraz."
        };

        if (!string.Equals(GraphicsAdapters, "Nie udało się odczytać", StringComparison.OrdinalIgnoreCase))
            facts.Add($"Wykryta grafika: {GraphicsAdapters}. To ona rysuje każdą klatkę Twojej rozgrywki.");
        if (SystemDriveFreeGb is { } freeGb)
            facts.Add($"Na dysku systemowym jest około {freeGb:0.#} GB wolnego miejsca — Windows lubi mieć zapas na aktualizacje.");
        facts.Add("Wszystkie ciekawostki pochodzą z odczytu lokalnego — parametry zostają na Twoim komputerze.");
        return facts;
    }
}
