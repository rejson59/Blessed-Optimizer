using BlessedOptimizer.Models;
using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class FirstBlessingTests
{
    [Fact]
    public void ReportsHigherRefreshRateAsAnOptionalReviewNotAnAppliedChange()
    {
        var snapshot = Snapshot(freeGb: 120);
        var checks = FirstBlessingService.BuildChecks(
            snapshot,
            new DisplayModeInfo(2560, 1440, 60, 144),
            Array.Empty<PeripheralDevice>(),
            deviceInventoryAvailable: true,
            deviceInventoryMessage: null,
            powerPlan: null,
            usage: new LiveUsage(12, 8, 16, 50));

        var display = Assert.Single(checks, check => check.Id == "display");
        Assert.Equal(FirstBlessingStatus.Review, display.Status);
        Assert.Equal("devices", display.ActionPage);
        Assert.Contains("ręcznie", display.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LowSystemDriveSpaceIsReportedWithoutDeletingFiles()
    {
        var checks = FirstBlessingService.BuildChecks(
            Snapshot(freeGb: 4.5),
            display: null,
            devices: Array.Empty<PeripheralDevice>(),
            deviceInventoryAvailable: true,
            deviceInventoryMessage: null,
            powerPlan: null,
            usage: new LiveUsage(5, 4, 16, 25));

        var storage = Assert.Single(checks, check => check.Id == "storage");
        Assert.Equal(FirstBlessingStatus.Attention, storage.Status);
        Assert.Equal("cleanup", storage.ActionPage);
        Assert.Contains("niczego nie usuwa", storage.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LowerProcessorLimitIsOnlyAUserReviewSuggestion()
    {
        var processor = PowerSettingsService.SupportedSettings.Single(setting => setting.Key == "processor-max");
        var plan = new PowerPlanSnapshot(
            Guid.NewGuid(),
            "Zrównoważony",
            new[] { new PowerSettingState(processor, 70, 95, null) });

        var checks = FirstBlessingService.BuildChecks(
            Snapshot(freeGb: 120),
            display: null,
            devices: Array.Empty<PeripheralDevice>(),
            deviceInventoryAvailable: true,
            deviceInventoryMessage: null,
            powerPlan: plan,
            usage: new LiveUsage(5, 4, 16, 25));

        var power = Assert.Single(checks, check => check.Id == "power");
        Assert.Equal(FirstBlessingStatus.Review, power.Status);
        Assert.Equal("power", power.ActionPage);
        Assert.Contains("decyzję zostawiam Tobie", power.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedPriorityReordersReadyRecommendationsAfterUrgentItems()
    {
        var snapshot = Snapshot(freeGb: 120);
        var usage = new LiveUsage(10, 4, 16, 25);
        var gaming = FirstBlessingService.BuildChecks(
            snapshot, null, Array.Empty<PeripheralDevice>(), true, null, null, usage, BlessedPriority.Gaming);
        var work = FirstBlessingService.BuildChecks(
            snapshot, null, Array.Empty<PeripheralDevice>(), true, null, null, usage, BlessedPriority.Work);

        Assert.Equal("baseline", gaming.First().Id);
        Assert.Equal("storage", work.First().Id);
    }

    [Fact]
    public void DeviceInventoryErrorIsNotPresentedAsAnEmptyOrHealthyDeviceList()
    {
        var checks = FirstBlessingService.BuildChecks(
            Snapshot(freeGb: 120),
            display: null,
            devices: Array.Empty<PeripheralDevice>(),
            deviceInventoryAvailable: false,
            deviceInventoryMessage: "Testowy brak dostępu",
            powerPlan: null,
            usage: new LiveUsage(null, 4, 16, 25));

        var devices = Assert.Single(checks, check => check.Id == "peripherals");
        Assert.Equal(FirstBlessingStatus.Unavailable, devices.Status);
        Assert.Equal("Testowy brak dostępu", devices.Detail);
    }

    private static DeviceSnapshot Snapshot(double freeGb) => new(
        "Windows 11",
        "26100",
        "Test CPU",
        8,
        16,
        4,
        "Test GPU",
        freeGb,
        new[] { new NetworkAdapterSnapshot("Wi-Fi", "Wi-Fi", "Połączono", "866 Mb/s") },
        DateTimeOffset.Now);
}
