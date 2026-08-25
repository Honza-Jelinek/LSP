using System.Text.RegularExpressions;

namespace LSP.Server.Library.Parsing;

/// <summary>
/// Sdílené regexy pro rozpoznání epizodního tokenu v názvu souboru (S01E02, 01x02, 101).
/// Používají je parsery i enrichment (pro odvození titulu ze jména souboru).
/// </summary>
public static partial class EpisodePatterns
{
    // S01E02 / s1e2 / S01.E02 …
    [GeneratedRegex(@"[Ss](?<s>\d{1,2})[\s._-]*[Ee](?<e>\d{1,3})", RegexOptions.CultureInvariant)]
    public static partial Regex SxxExx();

    // 01x02 / 1x2 / 01x2 – ohraničeno tak, aby nechytalo rozlišení typu 1920x800.
    [GeneratedRegex(@"(?<![\dxX])(?<s>\d{1,2})[xX](?<e>\d{1,2})(?![\dxX])", RegexOptions.CultureInvariant)]
    public static partial Regex NxNN();

    // Breaking Bad 101 – 1 = sezóna, 01 = díl, ohraničeno mezerou/separátorem.
    [GeneratedRegex(@"(?<=[\s._-]|^)(?<s>[1-9])(?<e>\d{2})(?=[\s._-]|$)", RegexOptions.CultureInvariant)]
    public static partial Regex ThreeDigit();

    /// <summary>První S01E02/01x02 token v názvu souboru (bez třímístného formátu — ten je kontextově vázaný na složku).</summary>
    public static (int Season, int Episode, int Index, int Length)? MatchEpisodeToken(string name)
    {
        var match = SxxExx().Match(name);
        if (!match.Success)
            match = NxNN().Match(name);
        if (!match.Success)
            return null;

        return (int.Parse(match.Groups["s"].Value), int.Parse(match.Groups["e"].Value), match.Index, match.Length);
    }

    /// <summary>Titul odvozený z textu před epizodním tokenem v názvu souboru ("South Park S27 E01" → "South Park"), nebo null.</summary>
    public static string? TitleBeforeToken(string fileNameWithoutExtension)
    {
        var token = MatchEpisodeToken(fileNameWithoutExtension);
        if (token is null)
            return null;

        var title = NameCleaner.CleanTitle(fileNameWithoutExtension[..token.Value.Index]);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
}
