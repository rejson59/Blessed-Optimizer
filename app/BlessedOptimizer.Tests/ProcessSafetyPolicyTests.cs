using System.IO;
using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class ProcessSafetyPolicyTests
{
    [Fact]
    public void SameSessionVisibleUserAppCanBeClosedAfterSelection()
    {
        var windows = Path.Combine(Path.GetTempPath(), "Blessed-Test-Windows");
        var appPath = Path.Combine(Path.GetTempPath(), "Blessed-Test-Apps", "editor.exe");

        var result = ProcessSafetyPolicy.Assess(500, "editor", appPath, true, 1, 900, 1, windows);

        Assert.True(result.CanClose);
        Assert.False(result.IsProtected);
        Assert.Equal("Aplikacja z oknem", result.RoleLabel);
    }

    [Theory]
    [InlineData("svchost")]
    [InlineData("lsass")]
    [InlineData("explorer")]
    public void KnownWindowsProcessesAreAlwaysProtected(string name)
    {
        var result = ProcessSafetyPolicy.Assess(
            500, name, Path.Combine(Path.GetTempPath(), "app.exe"), true,
            1, 900, 1, Path.Combine(Path.GetTempPath(), "Windows"));

        Assert.False(result.CanClose);
        Assert.True(result.IsProtected);
    }

    [Fact]
    public void ProcessRunningInsideWindowsDirectoryIsProtected()
    {
        var windows = Path.Combine(Path.GetTempPath(), "Windows");
        var result = ProcessSafetyPolicy.Assess(
            500, "sample", Path.Combine(windows, "System32", "sample.exe"), true,
            1, 900, 1, windows);

        Assert.False(result.CanClose);
        Assert.Contains("Windows", result.RoleLabel);
    }

    [Fact]
    public void BackgroundAndUnknownProcessesAreNotCloseCandidates()
    {
        var windows = Path.Combine(Path.GetTempPath(), "Windows");
        var appPath = Path.Combine(Path.GetTempPath(), "Apps", "helper.exe");
        var background = ProcessSafetyPolicy.Assess(500, "helper", appPath, false, 1, 900, 1, windows);
        var unknown = ProcessSafetyPolicy.Assess(501, "unknown", null, true, 1, 900, 1, windows);

        Assert.False(background.CanClose);
        Assert.False(unknown.CanClose);
        Assert.True(unknown.IsProtected);
    }

    [Fact]
    public void BlessedAndProcessesFromOtherSessionsAreProtected()
    {
        var path = Path.Combine(Path.GetTempPath(), "app.exe");
        var windows = Path.Combine(Path.GetTempPath(), "Windows");

        var self = ProcessSafetyPolicy.Assess(900, "blessed", path, true, 1, 900, 1, windows);
        var otherSession = ProcessSafetyPolicy.Assess(901, "editor", path, true, 0, 900, 1, windows);

        Assert.False(self.CanClose);
        Assert.False(otherSession.CanClose);
    }
}
