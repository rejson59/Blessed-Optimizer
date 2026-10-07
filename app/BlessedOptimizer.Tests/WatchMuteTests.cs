using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class WatchMuteTests
{
    private static BlessedFinding Finding(string id, FindingSeverity severity = FindingSeverity.Warning) =>
        new(id, severity, "Tytuł", "Wiadomość", null, FindingAction.None, null);

    [Fact]
    public void WithoutMutedRemovesOnlyMutedFindings()
    {
        var findings = new List<BlessedFinding>
        {
            Finding("disk-space"),
            Finding("uptime", FindingSeverity.Info),
            Finding("temp-files", FindingSeverity.Info)
        };
        var profile = new BlessedProfile { MutedFindingIds = new List<string> { "uptime" } };

        var result = BlessedWatchService.WithoutMuted(findings, profile);

        Assert.Equal(new[] { "disk-space", "temp-files" }, result.Select(finding => finding.Id).ToArray());
    }

    [Fact]
    public void WithoutMutedKeepsEverythingWhenNothingIsMuted()
    {
        var findings = new List<BlessedFinding> { Finding("disk-space") };

        var result = BlessedWatchService.WithoutMuted(findings, new BlessedProfile());

        Assert.Single(result);
        Assert.Equal("disk-space", result[0].Id);
    }

    [Fact]
    public void WithoutMutedReturnsCopySoOriginalListStaysUntouched()
    {
        var findings = new List<BlessedFinding> { Finding("disk-space"), Finding("uptime") };
        var profile = new BlessedProfile { MutedFindingIds = new List<string> { "uptime" } };

        _ = BlessedWatchService.WithoutMuted(findings, profile);

        Assert.Equal(2, findings.Count);
    }
}
