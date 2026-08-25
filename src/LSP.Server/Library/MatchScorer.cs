using FuzzySharp;

namespace LSP.Server.Library;

public static class MatchScorer
{
    /// <summary>
    /// Ohodnotí kandidáta z TMDB proti parsovanému názvu.
    /// score = fuzzy(title, parsedTitle) * 0.5 + yearMatch * 0.3 + typeMatch * 0.2
    /// </summary>
    public static double Score(External.TmdbSearchResult candidate, string parsedTitle, int? parsedYear, string? expectedType)
    {
        // Porovnávej bez diakritiky — 'Vzhuru do oblak' vs. 'Vzhůru do oblak' je stejný titul,
        // jinak česká lokalizace TMDB sráží skóre jasných shod pod auto-apply práh.
        var normCandidate = Normalize(candidate.Title);
        var normParsed = Normalize(parsedTitle);

        // TokenSetRatio je tolerantní k podmnožinám (podtitul navíc je OK), ale samotný dává 100 %
        // i pro 'Ted 2' vs. 'Tractor Ted Big Machines 2'. Průměr s TokenSortRatio (trestá
        // chybějící/přebývající slova) brání těmto false-positive auto-matchům.
        var titleScore = (Fuzz.TokenSetRatio(normCandidate, normParsed)
                          + Fuzz.TokenSortRatio(normCandidate, normParsed)) / 200.0;

        double yearScore = 0;
        if (parsedYear is { } py && candidate.ReleaseYear is { } ry)
            yearScore = py == ry ? 1.0 : (Math.Abs(py - ry) <= 1 ? 0.5 : 0.0);
        else if (parsedYear is null)
            yearScore = 0.5; // neutral — nemáme rok k porovnání

        double typeScore = 0;
        if (expectedType is not null)
            typeScore = candidate.MediaType == expectedType ? 1.0 : 0.0;

        return titleScore * 0.5 + yearScore * 0.3 + typeScore * 0.2;
    }

    /// <summary>Normalizace pro porovnání: lowercase, bez diakritiky, bez "The", bez interpunkce.</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = RemoveDiacritics(input.ToLowerInvariant());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^\w\s]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.StartsWith("the ", System.StringComparison.Ordinal)) s = s[4..];
        if (s.EndsWith(" the", System.StringComparison.Ordinal)) s = s[..^4];
        return s.Trim();
    }

    internal static string RemoveDiacritics(string text)
    {
        var formD = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
