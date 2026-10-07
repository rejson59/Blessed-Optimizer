using System.Text.Json;
using System.Text.Json.Serialization;
using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class ProfileTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void NewProfileKeepsAutomaticCleanupOptIn()
    {
        var profile = new BlessedProfile();

        Assert.False(profile.AllowTempCleanup);
        Assert.False(profile.TempCleanupConsentSet);
        Assert.Empty(profile.MutedFindingIds);
    }

    [Fact]
    public void ConsentDefaultsDisableCleanupUntilUserChooses()
    {
        var profile = new BlessedProfile { AllowTempCleanup = true, TempCleanupConsentSet = false };

        profile.EnforceConsentDefaults();

        Assert.False(profile.AllowTempCleanup);
    }

    [Fact]
    public void ConsentDefaultsKeepExplicitChoice()
    {
        var profile = new BlessedProfile { AllowTempCleanup = true, TempCleanupConsentSet = true };

        profile.EnforceConsentDefaults();

        Assert.True(profile.AllowTempCleanup);
    }

    [Fact]
    public void ProfileSurvivesJsonRoundTrip()
    {
        var profile = new BlessedProfile
        {
            Priority = BlessedPriority.Battery,
            AllowTempCleanup = true,
            TempCleanupConsentSet = true,
            MutedFindingIds = new List<string> { "uptime", "battery-full" },
            TotalFreedMb = 1234.5,
            HandledCount = 7
        };

        var restored = JsonSerializer.Deserialize<BlessedProfile>(
            JsonSerializer.Serialize(profile, SerializerOptions), SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal(BlessedPriority.Battery, restored.Priority);
        Assert.True(restored.AllowTempCleanup);
        Assert.True(restored.TempCleanupConsentSet);
        Assert.Equal(new[] { "uptime", "battery-full" }, restored.MutedFindingIds);
        Assert.Equal(1234.5, restored.TotalFreedMb);
        Assert.Equal(7, restored.HandledCount);
    }

    [Fact]
    public void OlderProfileWithoutMutesGetsEmptyList()
    {
        var restored = JsonSerializer.Deserialize<BlessedProfile>("{\"Priority\":\"Work\"}", SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal(BlessedPriority.Work, restored.Priority);
        Assert.NotNull(restored.MutedFindingIds);
        Assert.Empty(restored.MutedFindingIds);
    }
}
