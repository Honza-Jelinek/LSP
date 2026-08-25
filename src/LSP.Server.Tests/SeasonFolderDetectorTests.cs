using LSP.Server.Library.Parsing;

namespace LSP.Server.Tests;

public class SeasonFolderDetectorTests
{
    [Theory]
    // Keyword-first, s balastem za číslem
    [InlineData("Season 1 cz", null, true)]
    [InlineData("Sezóna 01", null, true)]
    [InlineData("Season 1", null, true)]
    [InlineData("S01", null, true)]
    [InlineData("Series 2", null, true)]
    [InlineData("Season 27 and 28 Mp4 1080p", null, true)]
    // Číslo první
    [InlineData("1.série", null, true)]
    [InlineData("1. řada", null, true)]
    [InlineData("2 season", null, true)]
    [InlineData("3.séria", null, true)]
    // Disk/část
    [InlineData("CD1", null, true)]
    [InlineData("část 2", null, true)]
    // Rok
    [InlineData("2010", null, true)]
    // Parent-aware
    [InlineData("Zlatá sedmdesátá 1", "Zlatá sedmdesátá", true)]
    [InlineData("Zlatá sedmdesátá 1", null, false)]
    [InlineData("South Park Season 27 and 28 Mp4 1080p", "south park 27", false)]
    // Nesmí chytat — ukazuje se v reálné knihovně E:\_Filmy
    [InlineData("south park 27", null, false)]
    [InlineData("south park 27", "root", false)]
    [InlineData("matrix", null, false)]
    [InlineData("Doba ledová", null, false)]
    [InlineData("piráti z karibiku", null, false)]
    [InlineData("Karate Kid - Komplet", null, false)]
    [InlineData("The Expendables - Collection (2010-2014)", null, false)]
    [InlineData("Datel Woody", null, false)]
    [InlineData("Season of the Witch", null, false)]
    [InlineData("Species 2", null, false)]
    [InlineData("24", null, false)]
    public void IsSeasonFolder_MatchesExpected(string segment, string? parent, bool expected)
    {
        Assert.Equal(expected, SeasonFolderDetector.IsSeasonFolder(segment, parent));
    }
}
