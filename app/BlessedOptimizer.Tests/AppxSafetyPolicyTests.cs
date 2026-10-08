using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class AppxSafetyPolicyTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void PackageProtectionMetadataAlwaysWins(bool isFramework, bool isResourcePackage, bool nonRemovable)
    {
        var package = new AppxPackage("Example.App", "Example.App_1.0_x64__abc", "1.0", "Publisher", isFramework, isResourcePackage, nonRemovable);

        Assert.True(AppxSafetyPolicy.Assess(package).IsProtected);
    }

    [Theory]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.SecHealthUI")]
    public void KnownSharedAndShellPackagesAreProtected(string name)
    {
        var package = new AppxPackage(name, $"{name}_1.0_x64__abc", "1.0");

        Assert.True(package.Safety.IsProtected);
    }

    [Fact]
    public void OrdinaryPerUserAppsRemainAnExplicitUserChoice()
    {
        var package = new AppxPackage("Contoso.PhotoEditor", "Contoso.PhotoEditor_2.0_x64__abc", "2.0", "Contoso Ltd.");

        var assessment = package.Safety;

        Assert.False(assessment.IsProtected);
        Assert.Equal("Aplikacja konta", assessment.Category);
        Assert.Contains("decyduj", assessment.Explanation, StringComparison.OrdinalIgnoreCase);
    }
}
