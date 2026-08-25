using LSP.Server.Library.Parsing;

namespace LSP.Server.Tests;

public class NameCleanerTests
{
    [Theory]
    [InlineData("south park 27", 27, "south park")]
    [InlineData("HIMYM 1 9", 1, "HIMYM")]
    [InlineData("South Park Season 27 and 28", 27, "South Park")]
    [InlineData("Mr Robot", 1, "Mr Robot")]
    [InlineData("Zlatá sedmdesátá", 1, "Zlatá sedmdesátá")]
    [InlineData("24", 1, "24")] // chráněný titul — číslo je název, ne sezóna
    public void StripTrailingSeasonMarker_StripsOnlyWhenSeasonMatches(string title, int season, string expected)
    {
        Assert.Equal(expected, NameCleaner.StripTrailingSeasonMarker(title, season));
    }

    [Fact]
    public void StripTrailingSeasonMarker_NeverReturnsEmpty()
    {
        var result = NameCleaner.StripTrailingSeasonMarker("Season 1", 1);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Theory]
    [InlineData("HIMYM 1-9 cz", "HIMYM 1 9")]
    [InlineData("south park 27", "south park 27")]
    [InlineData("Karate Kid (1984) CZ-EN", "Karate Kid")]
    public void CleanTitle_StripsSeparatorsYearsAndTags(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.CleanTitle(input));
    }

    [Fact]
    public void SplitAlternativeTitles_SplitsOnEquals()
    {
        var parts = NameCleaner.SplitAlternativeTitles("Perníkový táta = Breaking Bad");
        Assert.Equal(["Perníkový táta", "Breaking Bad"], parts);
    }

    [Fact]
    public void SplitAlternativeTitles_ReturnsSingleWhenNoSeparator()
    {
        var parts = NameCleaner.SplitAlternativeTitles("South Park");
        Assert.Equal(["South Park"], parts);
    }
}
