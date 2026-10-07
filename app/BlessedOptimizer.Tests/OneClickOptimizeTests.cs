using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class OneClickOptimizeTests
{
    private static BlessedFinding Finding(
        string id,
        FindingAction action = FindingAction.None,
        string? actionTarget = null,
        FindingSeverity severity = FindingSeverity.Warning) =>
        new(id, severity, "Tytuł", "Wiadomość", action == FindingAction.None ? null : "Akcja", action, actionTarget);

    private static WatchReport Report(params BlessedFinding[] findings) =>
        new(DateTimeOffset.Now, findings);

    [Fact]
    public void AutoApplicableFindingsReturnsOnlyBlessedHandledFindings()
    {
        var report = Report(
            Finding("temp-files", FindingAction.BlessedHandlesIt, "temp-cleanup"),
            Finding("disk-space", FindingAction.OpenSettings, "ms-settings:disks"),
            Finding("recycle-bin", FindingAction.BlessedHandlesIt, "recycle-bin-empty"),
            Finding("uptime", FindingAction.None));

        var auto = BlessedWatchService.AutoApplicableFindings(report);

        Assert.Equal(new[] { "temp-files", "recycle-bin" }, auto.Select(finding => finding.Id).ToArray());
    }

    [Fact]
    public void AutoApplicableFindingsIsEmptyWhenNothingIsAutomatic()
    {
        var report = Report(
            Finding("disk-space", FindingAction.OpenSettings, "ms-settings:disks"),
            Finding("uptime"));

        Assert.Empty(BlessedWatchService.AutoApplicableFindings(report));
    }

    [Theory]
    [InlineData(BlessedPriority.Gaming)]
    [InlineData(BlessedPriority.Work)]
    public void OneClickPowerFixAppliesToGamingAndWork(BlessedPriority priority)
    {
        var profile = new BlessedProfile { Priority = priority };
        var finding = Finding("power-cpu-limit", FindingAction.OpenPage, "power");

        Assert.True(BlessedWatchService.IsOneClickPowerFixApplicable(finding, profile));
    }

    [Theory]
    [InlineData(BlessedPriority.Battery)]
    [InlineData(BlessedPriority.Quiet)]
    public void OneClickPowerFixDoesNotApplyToBatteryOrQuiet(BlessedPriority priority)
    {
        var profile = new BlessedProfile { Priority = priority };
        var finding = Finding("power-cpu-limit", FindingAction.OpenPage, "power");

        Assert.False(BlessedWatchService.IsOneClickPowerFixApplicable(finding, profile));
    }

    [Fact]
    public void OneClickPowerFixIgnoresOtherFindings()
    {
        var profile = new BlessedProfile { Priority = BlessedPriority.Gaming };
        var finding = Finding("power-battery", FindingAction.OpenPage, "power");

        Assert.False(BlessedWatchService.IsOneClickPowerFixApplicable(finding, profile));
    }
}
