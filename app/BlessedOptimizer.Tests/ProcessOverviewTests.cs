using System.ComponentModel;
using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class ProcessOverviewTests
{
    [Fact]
    public void ImportantPreferenceUpdatesTheRoleAndMakesAnAppUnclosable()
    {
        var safety = new ProcessSafetyAssessment(
            IsProtected: false,
            CanClose: true,
            RoleLabel: "Aplikacja z oknem",
            Explanation: "Testowa aplikacja użytkownika.");
        var row = new ProcessUsageSnapshot(500, "editor", 1, 32, DateTime.Now, safety);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.IsImportant = true;

        Assert.Equal("Ważny dla Ciebie", row.ImportanceLabel);
        Assert.False(row.CanClose);
        Assert.Contains(nameof(ProcessUsageSnapshot.IsImportant), changed);
        Assert.Contains(nameof(ProcessUsageSnapshot.ImportanceLabel), changed);
        Assert.Contains(nameof(ProcessUsageSnapshot.CanClose), changed);

        row.IsImportant = false;

        Assert.True(row.CanClose);
        Assert.Equal("Aplikacja z oknem", row.ImportanceLabel);
    }
}
