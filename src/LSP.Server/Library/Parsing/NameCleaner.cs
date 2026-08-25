using System.Text.RegularExpressions;

namespace LSP.Server.Library.Parsing;

/// <summary>
/// Pomocník pro čištění názvů z reálných (nepořádných) souborů.
/// Záměrně jednoduchý – sofistikovanější čištění obstará později LLM parser.
/// </summary>
public static partial class NameCleaner
{
    // Tagy typické pro release názvy (kvalita, kodek, zdroj, jazyk, skupiny…).
    // Záměrně BEZ holých číslic (1/2/5/0), aby nezničily názvy typu „Spiderman 1 2 3".
    private static readonly string[] Tags =
    [
        "2160p", "1080p", "720p", "480p", "4k", "uhd", "hdr", "10bit", "8bit",
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
        "bluray", "blu-ray", "brrip", "bdrip", "bdremux", "remux", "webrip",
        "web-dl", "webdl", "web", "hdtv", "dvdrip", "dvd", "hdrip", "cam",
        "dd5", "ddp5", "ddp", "eac3", "ac3", "aac", "dts", "atmos",
        "multi", "dual", "complete", "komplet", "extended", "cut", "proper", "repack",
        "cz", "sk", "en", "eng", "cze", "dabing", "tit", "titulky",
        "mp4", "mkv", "avi", "sdtv",
    ];

    private static readonly HashSet<string> TagSet = new(Tags, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\[\(\{].*?[\]\)\}]")]
    private static partial Regex BracketGroups();

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(tt\d{7,8})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Klíčové slovo sezóny na konci titulu ("...komplet série", "Show Season 2", "Show S27").
    [GeneratedRegex(
        @"\s*[-–:]?\s*\b(?:komplet\s+)?(?:seasons?|séri[ea]|serie|series|seria|sezó?n[ay]?|sezon|řad[ay]|rad[ay])\b[\s._-]*\d{0,3}.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonKeywordTail();

    [GeneratedRegex(@"\s+[Ss]\d{1,3}(?:\s*[-–+]\s*[Ss]?\d{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SxxTail();

    // Jeden nebo víc číselných tokenů na konci ("HIMYM 1 9", "south park 27").
    [GeneratedRegex(@"(?:[\s._-]+\d{1,3})+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTail();

    [GeneratedRegex(@"\d{1,3}")]
    private static partial Regex DigitToken();

    [GeneratedRegex(@"\s*(?:=|\baka\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlternativeTitleSplit();

    /// <summary>Vytáhne první pravděpodobný rok (1900–2099) z textu, nebo null.</summary>
    /// <summary>Extrahuje IMDB ID (tt1234567) z inputu a zároveň ho z inputu odstraní.</summary>
    /// <returns>(cleaned input, imdbId or null)</returns>
    public static (string Cleaned, string? ImdbId) ExtractImdbId(string input)
    {
        var m = ImdbIdPattern().Match(input);
        if (!m.Success) return (input, null);
        var cleaned = ImdbIdPattern().Replace(input, "").Trim();
        return (cleaned, m.Value);
    }

    public static int? ExtractYear(string input)
    {
        var m = YearPattern().Match(input);
        return m.Success ? int.Parse(m.Value) : null;
    }

    /// <summary>
    /// Agresivní čištění pro názvy filmů/seriálů: odstraní závorkové skupiny, separátory,
    /// rok a release tagy. Zachová běžná slova názvu.
    /// </summary>
    public static string CleanTitle(string input)
    {
        var s = BracketGroups().Replace(input, " ");
        s = s.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        s = YearPattern().Replace(s, " ");

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Vezmi slova od začátku až po první release tag – zbytek je obvykle technický balast.
        var kept = words.TakeWhile(w => !TagSet.Contains(w)).ToArray();

        var result = string.Join(' ', kept).Trim();
        return result.Length > 0 ? result : string.Join(' ', words).Trim();
    }

    /// <summary>
    /// Lehké čištění názvu epizody – jen separátory a okrajové oddělovače,
    /// slova ani závorky nemaže (mohou nést smysl, např. originální název).
    /// </summary>
    public static string CleanEpisodeTitle(string input)
    {
        var s = input.Replace('_', ' ');
        s = Whitespace().Replace(s, " ").Trim();
        return s.Trim(' ', '-', '.', '–');
    }

    /// <summary>
    /// Odstraní z titulu odvozeného z názvu složky sezónní „ocas" – klíčové slovo
    /// ("...komplet série", "Show Season 2", "Show S27") vždy, holé číslo/rozsah
    /// ("south park 27", "HIMYM 1 9") jen když odpovídá parsované sezóně (chrání
    /// tituly jako "24"). Nikdy nevrátí prázdný řetězec — v tom případě vrátí vstup.
    /// </summary>
    public static string StripTrailingSeasonMarker(string title, int? parsedSeason)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;

        var afterKeyword = SeasonKeywordTail().Replace(title, "").TrimEnd();
        if (afterKeyword.Length > 0 && afterKeyword.Length < title.Length)
            return afterKeyword;

        var afterSxx = SxxTail().Replace(title, "").TrimEnd();
        if (afterSxx.Length > 0 && afterSxx.Length < title.Length)
            return afterSxx;

        if (parsedSeason is { } season)
        {
            var match = NumericTail().Match(title);
            if (match.Success)
            {
                var numbers = DigitToken().Matches(match.Value)
                    .Select(m => int.Parse(m.Value))
                    .ToList();

                if (ContainsSeasonNumber(numbers, season))
                {
                    var stripped = title[..match.Index].TrimEnd();
                    if (!string.IsNullOrWhiteSpace(stripped))
                        return stripped;
                }
            }
        }

        return title;
    }

    private static bool ContainsSeasonNumber(IReadOnlyList<int> numbers, int season)
    {
        if (numbers.Count == 0) return false;
        if (numbers.Contains(season)) return true;
        if (numbers.Count >= 2)
        {
            var min = numbers.Min();
            var max = numbers.Max();
            if (season >= min && season <= max) return true;
        }
        return false;
    }

    /// <summary>
    /// Rozdělí kombinovaný CZ/EN název ("Perníkový táta = Breaking Bad") na varianty
    /// ke zkoušení při TMDB vyhledávání. Bez separátoru vrátí vstup jako jedinou položku.
    /// </summary>
    public static IReadOnlyList<string> SplitAlternativeTitles(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return [title];

        var parts = AlternativeTitleSplit().Split(title)
            .Select(p => p.Trim(' ', '=', '-'))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count > 0 ? parts : [title];
    }
}
