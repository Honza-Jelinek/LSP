using LSP.Server.Library.Parsing;

namespace LSP.Server.Tests;

public class MediaParseContextTests
{
    private static MediaParseContext Context(params string[] segments) => new()
    {
        FullPath = string.Join("\\", segments),
        FileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(segments[^1]),
        RootRelativeSegments = segments,
    };

    [Fact]
    public void ContentFolderName_SkipsSeasonFolderWithTrailingJunk()
    {
        var ctx = Context("HIMYM 1-9 cz", "Season 1 cz", "Jak jsem poznal vaší matku - 01x01 - Pilot.avi");
        Assert.Equal("HIMYM 1-9 cz", ctx.ContentFolderName);
    }

    [Fact]
    public void ContentFolderName_ParentAware_SkipsShowNamePlusNumber()
    {
        var ctx = Context("Zlatá sedmdesátá", "Zlatá sedmdesátá 1", "zlata sedmdesata cz 1x01 - Pilot.avi");
        Assert.Equal("Zlatá sedmdesátá", ctx.ContentFolderName);
    }

    [Fact]
    public void ContentFolderName_SkipsCzechSezonaWithDiacritics()
    {
        var ctx = Context("Brickleberry", "Sezóna 01", "Brickleberry - 01x01 - Vítejte.mkv");
        Assert.Equal("Brickleberry", ctx.ContentFolderName);
    }

    [Fact]
    public void ContentFolderName_NestedSeasonInsideNonSeasonSubfolder()
    {
        var ctx = Context(
            "south park 27",
            "South Park Season 27 and 28 Mp4 1080p",
            "Season 27",
            "South Park S27 E01.mkv");
        // "Season 27" je season folder; "South Park Season 27 and 28 Mp4 1080p" NENÍ
        // (rodič "south park 27" nesedí jako prefix), takže se vrátí jako content folder.
        Assert.Equal("South Park Season 27 and 28 Mp4 1080p", ctx.ContentFolderName);
    }

    [Fact]
    public void ContentFolderName_LooseFileHasNoFolder()
    {
        var ctx = Context("Spongebob v kalhotách 4x6-Středověk.avi");
        Assert.Null(ctx.ContentFolderName);
    }

    [Theory]
    [InlineData("Doba ledová", "Doba ledová 2 Obleva.mkv")]
    [InlineData("matrix", "The Matrix I.mkv")]
    [InlineData("piráti z karibiku", "Pirates of the Caribbean At World's End [1080p].mkv")]
    [InlineData("Karate Kid - Komplet", "Karate Kid (1984) CZ-EN.mkv")]
    [InlineData("Datel Woody", "Datel Woody - Beagle v soubyznysu.mp4")]
    public void ContentFolderName_MovieCollectionFoldersUnaffected(string folder, string file)
    {
        var ctx = Context(folder, file);
        Assert.Equal(folder, ctx.ContentFolderName);
    }
}
