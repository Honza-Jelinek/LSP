using LSP.Server.Library.Parsing;
using LSP.Server.Library.Parsing.Parsers;

namespace LSP.Server.Tests;

public class ParserTests
{
    private static readonly MediaParserChain Chain = new(
    [
        new SeasonEpisodeParser(),
        new ThreeDigitEpisodeParser(),
        new MovieParser(),
    ]);

    private static MediaParseResult Parse(params string[] segments)
    {
        var ctx = new MediaParseContext
        {
            FullPath = string.Join("\\", segments),
            FileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(segments[^1]),
            RootRelativeSegments = segments,
        };
        return Chain.Parse(ctx);
    }

    public static IEnumerable<object[]> EpisodeCases()
    {
        yield return
        [
            new[] { "HIMYM 1-9 cz", "Season 1 cz", "Jak jsem poznal vaší matku - 01x01 - Pilot.avi" },
            "HIMYM", 1, 1,
        ];
        yield return
        [
            new[] { "HIMYM 1-9 cz", "Season 9 cz", "Jak jsem poznal vaší matku - 09x01 - Poslední díl.avi" },
            "HIMYM", 9, 1,
        ];
        yield return
        [
            new[] { "Zlatá sedmdesátá", "Zlatá sedmdesátá 1", "zlata sedmdesata cz 1x01 - Pilot_dvdrip.hboaudio.avi" },
            "Zlatá sedmdesátá", 1, 1,
        ];
        yield return
        [
            new[] { "Brickleberry", "Sezóna 01", "Brickleberry - 01x01 - Vítejte v Brickleberry (Welcome to Brickleberry).mkv" },
            "Brickleberry", 1, 1,
        ];
        yield return
        [
            new[] { "Mr.Robot", "Season 1", "Mr Robot S01E01.mkv" },
            "Mr Robot", 1, 1,
        ];
        yield return
        [
            new[] { "south park 27", "South Park S27 E01.mkv" },
            "south park", 27, 1,
        ];
        yield return
        [
            new[] { "south park 27", "South Park Season 27 and 28 Mp4 1080p", "Season 27", "South Park S27 E01.mkv" },
            "South Park", 27, 1,
        ];
        yield return
        [
            new[] { "Perníkový táta = Breaking Bad komplet série (CZ+EN)[HEVC][1080p]", "Breaking Bad 101 Pilot (CZ+EN_h265_1080p).mkv" },
            "Perníkový táta = Breaking Bad", 1, 1,
        ];
        yield return
        [
            new[] { "Spongebob v kalhotách 4x6-Středověk.avi" },
            "Spongebob v kalhotách", 4, 6,
        ];
    }

    [Theory]
    [MemberData(nameof(EpisodeCases))]
    public void Parse_Episodes_ProducesUnifiedShowTitle(string[] segments, string expectedTitle, int expectedSeason, int expectedEpisode)
    {
        var result = Parse(segments);
        Assert.Equal(MediaKind.Episode, result.Kind);
        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(expectedSeason, result.Season);
        Assert.Equal(expectedEpisode, result.Episode);
    }

    [Theory]
    [InlineData(new object[] { new[] { "Doba ledová", "Doba ledová 2 Obleva.mkv" } })]
    [InlineData(new object[] { new[] { "matrix", "The Matrix I.mkv" } })]
    [InlineData(new object[] { new[] { "Karate Kid - Komplet", "Karate Kid (1984) CZ-EN.mkv" } })]
    [InlineData(new object[] { new[] { "The Expendables - Collection (2010-2014)", "The Expendables (2010).mkv" } })]
    [InlineData(new object[] { new[] { "Datel Woody", "Datel Woody - Beagle v soubyznysu.mp4" } })]
    public void Parse_MovieCollectionFolders_StayMovies(string[] segments)
    {
        var result = Parse(segments);
        Assert.Equal(MediaKind.Movie, result.Kind);
    }

    [Fact]
    public void Parse_TwoSouthParkFolderVariants_ProduceSameTitleCaseInsensitively()
    {
        var loose = Parse("south park 27", "South Park S27 E01.mkv");
        var nested = Parse(
            "south park 27", "South Park Season 27 and 28 Mp4 1080p", "Season 27", "South Park S27 E01.mkv");

        Assert.Equal(loose.Title, nested.Title, StringComparer.OrdinalIgnoreCase);
    }
}
