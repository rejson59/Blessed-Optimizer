using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class WatchReportTests
{
    private static BlessedFinding Finding(string id, FindingSeverity severity) =>
        new(id, severity, "Tytuł", "Wiadomość", null, FindingAction.None, null);

    [Fact]
    public void ProblemCountIgnoresGoodAndInfo()
    {
        var report = new WatchReport(DateTimeOffset.Now, new[]
        {
            Finding("good", FindingSeverity.Good),
            Finding("info", FindingSeverity.Info),
            Finding("warning", FindingSeverity.Warning),
            Finding("critical", FindingSeverity.Critical)
        });

        Assert.Equal(2, report.ProblemCount);
    }

    [Theory]
    [InlineData(0, "Wszystko gra — nic nie wymaga Twojej uwagi.")]
    [InlineData(1, "Znalazłem 1 rzecz wartą zajęcia się.")]
    [InlineData(3, "Znalazłem 3 rzeczy warte zajęcia się.")]
    public void HeadlineMatchesProblemCount(int problems, string expected)
    {
        var findings = Enumerable.Range(0, problems)
            .Select(index => Finding($"finding-{index}", FindingSeverity.Warning))
            .ToArray();
        var report = new WatchReport(DateTimeOffset.Now, findings);

        Assert.Equal(expected, report.Headline);
    }
}
