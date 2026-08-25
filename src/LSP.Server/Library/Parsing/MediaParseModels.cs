namespace LSP.Server.Library.Parsing;

/// <summary>Druh média rozpoznaný parserem.</summary>
public enum MediaKind
{
    Unknown = 0,
    Movie = 1,
    Episode = 2,
}

/// <summary>
/// Vstup pro parsery. Drží cestu k souboru a segmenty relativní cesty od kořene knihovny,
/// aby parser mohl využít název složky seriálu (spolehlivější než název souboru).
/// </summary>
public sealed record MediaParseContext
{
    public required string FullPath { get; init; }
    public required string FileNameWithoutExtension { get; init; }

    /// <summary>Segmenty cesty relativní ke kořeni knihovny, včetně názvu souboru (poslední prvek).</summary>
    public required IReadOnlyList<string> RootRelativeSegments { get; init; }

    /// <summary>Název top-level složky pod kořenem (např. "Filmy", "Seriály"), nebo null.</summary>
    public string? TopFolderName =>
        RootRelativeSegments.Count >= 2 ? RootRelativeSegments[0] : null;

    /// <summary>
    /// Název složky obsahu — nejhlubší relevantní adresář.
    /// Přeskakuje "Season *", "S*" a kolekční složky, aby vrátil název seriálu/filmu.
    /// </summary>
    public string? ContentFolderName
    {
        get
        {
            if (RootRelativeSegments.Count < 2) return null;
            // Jdi od konce (od souboru nahoru), najdi první neseasonovou složku.
            // Sezónní detekce je parent-aware, takže potřebuje i segment o úroveň výš.
            for (var i = RootRelativeSegments.Count - 2; i >= 0; i--)
            {
                var seg = RootRelativeSegments[i];
                var parent = i > 0 ? RootRelativeSegments[i - 1] : null;
                if (SeasonFolderDetector.IsSeasonFolder(seg, parent)) continue;
                return seg;
            }
            return RootRelativeSegments[0];
        }
    }

    /// <summary>Počet úrovní složek pod kořenem.</summary>
    public int Depth => RootRelativeSegments.Count - 1;
}

/// <summary>Výsledek parsování jednoho souboru.</summary>
public sealed record MediaParseResult(
    MediaKind Kind,
    string Title,
    int? Season = null,
    int? Episode = null,
    string? EpisodeTitle = null,
    int? Year = null,
    string? ImdbId = null,
    double Confidence = 1.0,
    string Source = "regex");
