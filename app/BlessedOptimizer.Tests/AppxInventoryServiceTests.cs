using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class AppxInventoryServiceTests
{
    [Fact]
    public void ParsePackagesJsonReadsArrayOfPackages()
    {
        const string json = """
            [
              { "Name": "Microsoft.WindowsCalculator", "PackageFullName": "Microsoft.WindowsCalculator_11.0.0.0_x64__8wekyb3d8bbwe", "Version": "11.0.0.0" },
              { "Name": "Microsoft.Paint", "PackageFullName": "Microsoft.Paint_11.0.0.0_x64__8wekyb3d8bbwe", "Version": "11.0.0.0" }
            ]
            """;

        var packages = AppxInventoryService.ParsePackagesJson(json);

        Assert.Equal(2, packages.Count);
        Assert.Equal("Microsoft.WindowsCalculator", packages[0].Name);
        Assert.Equal("Microsoft.WindowsCalculator_11.0.0.0_x64__8wekyb3d8bbwe", packages[0].PackageFullName);
        Assert.Equal("11.0.0.0", packages[0].Version);
    }

    [Fact]
    public void ParsePackagesJsonReadsSingleObjectWhenOnlyOnePackage()
    {
        // ConvertTo-Json emits a bare object (not an array) for a single element.
        const string json = """{ "Name": "Microsoft.Paint", "PackageFullName": "Microsoft.Paint_11.0.0.0_x64__8wekyb3d8bbwe", "Version": "11.0.0.0" }""";

        var packages = AppxInventoryService.ParsePackagesJson(json);

        Assert.Single(packages);
        Assert.Equal("Microsoft.Paint", packages[0].Name);
    }

    [Fact]
    public void ParsePackagesJsonReturnsEmptyForEmptyOutput()
    {
        Assert.Empty(AppxInventoryService.ParsePackagesJson(null));
        Assert.Empty(AppxInventoryService.ParsePackagesJson(string.Empty));
        Assert.Empty(AppxInventoryService.ParsePackagesJson("   "));
    }

    [Fact]
    public void ParsePackagesJsonReturnsEmptyForInvalidJson()
    {
        Assert.Empty(AppxInventoryService.ParsePackagesJson("{ not json"));
        Assert.Empty(AppxInventoryService.ParsePackagesJson("[] trailing"));
    }

    [Fact]
    public void ParsePackagesJsonSkipsEntriesWithoutNameOrFullName()
    {
        const string json = """
            [
              { "Name": "", "PackageFullName": "x_1.0_x64__abc", "Version": "1.0" },
              { "Name": "Microsoft.Paint", "PackageFullName": "", "Version": "1.0" },
              { "Name": "Microsoft.Paint", "PackageFullName": "Microsoft.Paint_1.0_x64__abc", "Version": "1.0" }
            ]
            """;

        var packages = AppxInventoryService.ParsePackagesJson(json);

        Assert.Single(packages);
        Assert.Equal("Microsoft.Paint", packages[0].Name);
    }

    [Fact]
    public void ParsePackagesJsonDefaultsMissingVersionToEmpty()
    {
        const string json = """{ "Name": "Microsoft.Paint", "PackageFullName": "Microsoft.Paint_1.0_x64__abc" }""";

        var packages = AppxInventoryService.ParsePackagesJson(json);

        Assert.Single(packages);
        Assert.Equal(string.Empty, packages[0].Version);
    }

    [Fact]
    public void ParsePackagesJsonReadsPublisherAndProtectionFlags()
    {
        const string json = """{ "Name": "Microsoft.VCLibs.140", "PackageFullName": "Microsoft.VCLibs.140_1.0_x64__abc", "Version": "1.0", "PublisherDisplayName": "Microsoft Corporation", "IsFramework": true, "IsResourcePackage": false, "NonRemovable": true }""";

        var package = Assert.Single(AppxInventoryService.ParsePackagesJson(json));

        Assert.Equal("Microsoft Corporation", package.PublisherDisplayName);
        Assert.True(package.IsFramework);
        Assert.False(package.IsResourcePackage);
        Assert.True(package.NonRemovable);
        Assert.True(package.Safety.IsProtected);
    }
}
