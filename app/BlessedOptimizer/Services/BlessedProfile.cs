using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlessedOptimizer.Services;

public enum BlessedPriority
{
    Gaming,
    Work,
    Battery,
    Quiet
}

/// <summary>
/// What matters to the user and how much Blessed may do on its own.
/// Stored next to the other local Blessed data in the user profile.
/// </summary>
public sealed class BlessedProfile
{
    public bool OnboardingCompleted { get; set; }

    /// <summary>The last time the user completed the explicit, read-only welcome audit.</summary>
    public DateTimeOffset? FirstBlessingCompletedAt { get; set; }

    public BlessedPriority Priority { get; set; } = BlessedPriority.Gaming;

    public bool WatchInBackground { get; set; } = true;

    public bool AllowTempCleanup { get; set; }

    /// <summary>True only after the user has explicitly chosen the TEMP-cleanup setting.</summary>
    public bool TempCleanupConsentSet { get; set; }

    public DateTimeOffset? LastCleanupAt { get; set; }

    public double TotalFreedMb { get; set; }

    public int HandledCount { get; set; }

    /// <summary>Finding ids the user muted; those cards stop appearing in the watch list.</summary>
    public List<string> MutedFindingIds { get; set; } = new();

    /// <summary>Process names the user marked as important; matching rows are excluded from close actions.</summary>
    public List<string> ImportantProcessNames { get; set; } = new();

    /// <summary>
    /// v1.0.3 enabled TEMP cleanup for new profiles without a separate opt-in.
    /// Require a fresh, explicit choice before allowing unattended deletion.
    /// </summary>
    internal void EnforceConsentDefaults()
    {
        if (!TempCleanupConsentSet)
            AllowTempCleanup = false;
    }

    public string PriorityLabel => Priority switch
    {
        BlessedPriority.Work => "Praca i skupienie",
        BlessedPriority.Battery => "Długa praca na baterii",
        BlessedPriority.Quiet => "Cisza i chłód",
        _ => "Granie i maksymalna płynność"
    };

    public string PriorityPromise => Priority switch
    {
        BlessedPriority.Work => "Pilnuję, żeby nic nie zwalniało Ci pracy i żeby komputer startował szybko.",
        BlessedPriority.Battery => "Pilnuję zużycia energii i podpowiadam, co niepotrzebnie zjada baterię.",
        BlessedPriority.Quiet => "Pilnuję obciążenia, żeby wentylatory nie miały powodu do pracy.",
        _ => "Pilnuję, żeby procesor i pamięć były gotowe na grę, zanim ją odpalisz."
    };
}

public static class BlessedProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string ProfileFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlessedOptimizer",
        "blessed-profile.json");

    public static BlessedProfile Load()
    {
        try
        {
            if (!File.Exists(ProfileFile))
                return new BlessedProfile();
            var json = File.ReadAllText(ProfileFile);
            var profile = JsonSerializer.Deserialize<BlessedProfile>(json, SerializerOptions) ?? new BlessedProfile();
            profile.EnforceConsentDefaults();
            return profile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new BlessedProfile();
        }
    }

    public static void Save(BlessedProfile profile)
    {
        try
        {
            var directory = Path.GetDirectoryName(ProfileFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(ProfileFile, JsonSerializer.Serialize(profile, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Preferences are a convenience; a locked profile folder must never break the app.
        }
    }
}
