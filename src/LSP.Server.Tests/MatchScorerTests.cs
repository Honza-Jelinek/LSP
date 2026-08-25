using LSP.Server.External;
using LSP.Server.Library;

namespace LSP.Server.Tests;

public class MatchScorerTests
{
    private static TmdbSearchResult Candidate(string title, int? year = null, string mediaType = "movie") =>
        new(1, mediaType, title, null, null, null, null, null, year);

    [Theory]
    // Diakritika/velikost písmen nesmí srážet skóre jasné shody pod auto-apply práh (0.85)
    [InlineData("Vzhůru do oblak", "Vzhuru do oblak")]
    [InlineData("Šípková Růženka", "Šípková růženka")]
    [InlineData("Deset důvodů, proč tě nenávidím", "Deset důvodů, proč Tě nenávidím")]
    [InlineData("Doba ledová 2: Obleva", "Doba ledová 2 Obleva")]
    public void Score_DiacriticsAndCaseVariants_ReachAutoApply(string tmdbTitle, string parsedTitle)
    {
        var score = MatchScorer.Score(Candidate(tmdbTitle), parsedTitle, null, "movie");
        Assert.True(score >= 0.85, $"score {score:F2} < 0.85 pro '{parsedTitle}' vs. '{tmdbTitle}'");
    }

    [Fact]
    public void Score_DifferentTitle_StaysLow()
    {
        var score = MatchScorer.Score(Candidate("Matrix Mind: The Image of Disease"), "The Matrix", null, "movie");
        Assert.True(score < 0.85, $"score {score:F2} nečekaně vysoké");
    }

    [Fact]
    public void Normalize_StripsDiacriticsPunctuationAndThe()
    {
        Assert.Equal("vzhuru do oblak", MatchScorer.Normalize("Vzhůru do oblak"));
        Assert.Equal("matrix", MatchScorer.Normalize("The Matrix"));
    }
}
