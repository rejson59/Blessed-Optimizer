using BlessedOptimizer.Services;

namespace BlessedOptimizer.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("1.0.4.0", "1.0.4")]
    [InlineData("2.1", "2.1.0")]
    [InlineData("1.1.0.0", "1.1.0")]
    public void NormalizeDropsRevisionAndPadsBuild(string input, string expected)
    {
        var version = Version.Parse(input);

        Assert.Equal(Version.Parse(expected), UpdateService.Normalize(version));
    }

    [Fact]
    public void ReadExpectedHashParsesStandardLine()
    {
        var hash = new string('a', 64);
        var checksums = $"{hash}  BlessedOptimizer-Setup.exe\n";

        Assert.Equal(hash.ToUpperInvariant(), UpdateService.ReadExpectedHash(checksums, "BlessedOptimizer-Setup.exe"));
    }

    [Fact]
    public void ReadExpectedHashParsesBinaryMarkerLine()
    {
        var hash = new string('B', 64);
        var checksums = $"{hash} *BlessedOptimizer-Setup.exe\n";

        Assert.Equal(hash, UpdateService.ReadExpectedHash(checksums, "BlessedOptimizer-Setup.exe"));
    }

    [Fact]
    public void ReadExpectedHashRejectsMissingAsset()
    {
        var checksums = $"{new string('b', 64)}  inny-plik.exe\n";

        Assert.Throws<InvalidDataException>(() => UpdateService.ReadExpectedHash(checksums, "BlessedOptimizer-Setup.exe"));
    }

    [Fact]
    public void ReadExpectedHashRejectsMalformedHash()
    {
        Assert.Throws<InvalidDataException>(() => UpdateService.ReadExpectedHash("abc  BlessedOptimizer-Setup.exe", "BlessedOptimizer-Setup.exe"));
    }

    [Fact]
    public void ReadExpectedHashRejectsNonHexHash()
    {
        var checksums = $"{new string('z', 64)}  BlessedOptimizer-Setup.exe\n";

        Assert.Throws<InvalidDataException>(() => UpdateService.ReadExpectedHash(checksums, "BlessedOptimizer-Setup.exe"));
    }
}
