using System.Text.RegularExpressions;
using LSP.Server.Library;

namespace LSP.Server.Library.Parsing;

/// <summary>
/// Jediný zdroj pravdy pro rozpoznání „sezónní" složky (Season 1, Sezóna 01, 1.série, S01…),
/// aby se od ní odlišila skutečná složka obsahu (název seriálu). Používá parser i enrichment.
/// </summary>
public static partial class SeasonFolderDetector
{
    // Keyword první: "season 1", "sezóna 01", "s01", povoluje balast AŽ ZA číslem ("Season 1 cz").
    [GeneratedRegex(
        @"^(?:the\s+)?(?:seasons?|series|serie|seria|sezony|sezona|sezon|rady|rada|staffel|s)[\s._-]*\d{1,3}(?:\s*(?:[-–+&,]|and|az|a)\s*\d{1,3})*(?:[\s._-].*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeywordFirst();

    // Číslo první: "1.série", "1. řada", "2 season".
    [GeneratedRegex(
        @"^\d{1,3}\s*\.?\s*(?:seasons?|series|serie|seria|sezony|sezona|sezon|rady|rada|cast|part)(?:[\s._-].*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberFirst();

    // Disk/část – striktní, žádný balast: "CD1", "část 2".
    [GeneratedRegex(@"^(?:cd|dvd|disc|disk|part|cast)[\s._-]*\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DiscOrPart();

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearFolder();

    // Zbytek za "<název rodiče> " – číslo/rozsah s volitelným season keywordem: "Zlatá sedmdesátá 1".
    [GeneratedRegex(
        @"^(?:-\s*)?(?:(?:seasons?|series|serie|seria|sezony|sezona|sezon|rady|rada|s)\s*)?\d{1,3}(?:\s*(?:[-–+&,]|and|az|a)\s*\d{1,3})*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParentSuffix();

    public static bool IsSeasonFolder(string segment) => IsSeasonFolder(segment, null);

    /// <summary>
    /// Rozpozná sezónní složku podle klíčových slov/čísla, nebo (má-li rodiče) podle vzoru
    /// „název rodiče + číslo/rozsah" (např. "Zlatá sedmdesátá 1" pod "Zlatá sedmdesátá").
    /// </summary>
    public static bool IsSeasonFolder(string segment, string? parentSegment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        var norm = Normalize(segment);

        if (KeywordFirst().IsMatch(norm)) return true;
        if (NumberFirst().IsMatch(norm)) return true;
        if (DiscOrPart().IsMatch(norm)) return true;
        if (YearFolder().IsMatch(norm)) return true;

        if (!string.IsNullOrWhiteSpace(parentSegment))
        {
            var normParent = Normalize(parentSegment);
            if (normParent.Length > 0 && norm.StartsWith(normParent + " ", StringComparison.Ordinal))
            {
                var remainder = norm[(normParent.Length + 1)..];
                if (ParentSuffix().IsMatch(remainder))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Zjistí "content folder" (název seriálu/filmu) z cesty souboru — jde od souboru nahoru,
    /// přeskakuje sezónní složky, zastaví se u kořene knihovny. Nahrazuje starší
    /// EnrichmentService.GetContentFolder (duplicitní logiku).
    /// </summary>
    public static string? GetContentFolderFromPath(string filePath, IReadOnlyCollection<string>? roots = null, int maxLevels = 4)
    {
        var dir = Path.GetDirectoryName(filePath);
        var segments = new List<string>();

        while (!string.IsNullOrEmpty(dir) && segments.Count < maxLevels)
        {
            if (roots is not null && roots.Any(r => PathsEqual(r, dir)))
                break;

            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) break;

            segments.Add(name);
            dir = Path.GetDirectoryName(dir);
        }

        // segments[0] = nejbližší rodič souboru, segments[1] = jeho rodič, …
        for (var i = 0; i < segments.Count; i++)
        {
            var parent = i + 1 < segments.Count ? segments[i + 1] : null;
            if (IsSeasonFolder(segments[i], parent)) continue;
            return segments[i];
        }

        return segments.Count > 0 ? segments[^1] : null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string input) =>
        MatchScorer.RemoveDiacritics(input).ToLowerInvariant().Trim();
}
