using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class SecurityDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseWmiDateTimeReturnsNullForEmptyValues(string? value)
    {
        Assert.Null(SecurityDiagnostics.ParseWmiDateTime(value));
    }

    [Fact]
    public void ParseWmiDateTimeReadsStandardValue()
    {
        var parsed = SecurityDiagnostics.ParseWmiDateTime("20261005143000.000000+120");

        Assert.NotNull(parsed);
        Assert.Equal(2026, parsed.Value.Year);
        Assert.Equal(10, parsed.Value.Month);
        Assert.Equal(5, parsed.Value.Day);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026")]
    public void ParseWmiDateTimeReturnsNullForMalformedValues(string value)
    {
        Assert.Null(SecurityDiagnostics.ParseWmiDateTime(value));
    }
}
