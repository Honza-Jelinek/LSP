using LSP.Server.Library.Parsing;

namespace LSP.Server.Data;

/// <summary>Fyzický soubor na disku + výsledek rozpoznání a (později) info o kodecích.</summary>
public sealed class MediaFile
{
    public int Id { get; set; }

    public required string Path { get; set; }
    public required string FileName { get; set; }
    public required string Extension { get; set; }
    public long SizeBytes { get; set; }
    public MediaKind Kind { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    // Vyplní ffprobe v M3 (rozhoduje direct play vs. remux vs. transkód).
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    public Movie? Movie { get; set; }
    public Episode? Episode { get; set; }
}

/// <summary>Film – 1:1 k souboru.</summary>
public sealed class Movie
{
    public int Id { get; set; }
    public required string Title { get; set; }   // parsovaný název (z regexu)
    public int? Year { get; set; }
    public string? ImdbId { get; set; }          // extrahováno z názvu souboru (tt1234567)

    // TMDB enrichment (nullable — plněno z TmdbCache při enrichmentu).
    public string? DisplayTitle { get; set; }    // oficiální název z TMDB (zobrazuje se přednostně)
    public int? TmdbId { get; set; }
    public string? PosterFile { get; set; }
    public string? Overview { get; set; }
    public double? Rating { get; set; }
    public string? Genres { get; set; }
    public string? Cast { get; set; }

    public bool IsManual { get; set; }           // ruční korekce → enrichment nepřepisuje

    public int MediaFileId { get; set; }
    public MediaFile MediaFile { get; set; } = null!;
}

/// <summary>Seriál – seskupuje epizody. Title pochází z názvu složky (stabilní seskupení).</summary>
public sealed class Show
{
    public int Id { get; set; }
    public required string Title { get; set; }   // grouping key (z názvu složky) – NEMĚNIT (unique index)
    public string? ImdbId { get; set; }          // extrahováno z názvu souboru (tt1234567)

    // TMDB enrichment.
    public string? DisplayTitle { get; set; }    // oficiální název z TMDB (zobrazuje se přednostně)
    public int? TmdbId { get; set; }
    public string? PosterFile { get; set; }
    public string? Overview { get; set; }
    public double? Rating { get; set; }
    public string? Genres { get; set; }
    public string? Cast { get; set; }

    // Měkké napojení lazy série na TMDB (banner + DisplayTitle). NENÍ plný match — epizody zůstávají
    // z parseru (TmdbId null). Slouží ke sdružování lazy sérií podle indexu, ne jen podle názvu.
    public int? SoftTmdbId { get; set; }

    public bool IsManual { get; set; }           // ruční korekce → enrichment nepřepisuje

    public List<Episode> Episodes { get; set; } = [];
}

/// <summary>
/// Pozice přehrávání („pokračovat v přehrávání"). Klíčováno přes cestu souboru (stabilní
/// napříč rescany, které mění MediaFile.Id), ne přes MediaFileId.
/// </summary>
public sealed class PlaybackProgress
{
    public int Id { get; set; }

    public required string Path { get; set; }
    public double PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public bool Finished { get; set; }

    // DateTime (UTC), ne DateTimeOffset – SQLite neumí ORDER BY na DateTimeOffset.
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Kořenová složka knihovny — uživatel jich může mít víc.</summary>
public sealed class LibraryFolder
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>KV store pro konfiguraci (API klíče, preferences, plán).</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>
/// Cache výsledku parsování per soubor. Klíčováno cestou (přežívá rescan).
/// Enrichment z něj čte → LLM se neplatí znovu.
/// </summary>
public sealed class ParseCache
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public MediaKind Kind { get; set; }
    public required string Title { get; set; }
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Number { get; set; }
    public string? EpisodeTitle { get; set; }
    public string? ImdbId { get; set; }
    public string Source { get; set; } = "regex"; // "regex" | "llm"
    public double Confidence { get; set; } = 1.0;
    public string? NormalizedQuery { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Cache TMDB výsledku per název. TTL max 6 měsíců (TMDB TOS).
/// </summary>
public sealed class TmdbCache
{
    public int Id { get; set; }
    public required string QueryKey { get; set; } // normalizedTitle|year|type
    public int? TmdbId { get; set; }
    public string? MediaType { get; set; } // "movie" | "tv"
    public string? Title { get; set; }
    public string? Overview { get; set; }
    public string? PosterFile { get; set; }
    public string? BackdropFile { get; set; }
    public string? Genres { get; set; }   // JSON array string
    public string? Cast { get; set; }     // JSON array string
    public double? Rating { get; set; }
    public int? ReleaseYear { get; set; }
    public double? Score { get; set; }    // MatchScorer skóre v době vyhledání (null = starý řádek, ber jako 1.0)
    public DateTime FetchedAt { get; set; }
}

/// <summary>Cache TMDB season dat - epizody jedné sezóny.</summary>
public sealed class TmdbSeasonCache
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public int Season { get; set; }
    public string? Data { get; set; } // JSON serializace seznamu epizod
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// Ruční korekce klasifikace/TMDB matche. Autoritativní (enrichment nepřepíše) a přežívá rescan.
/// Key = cesta souboru (movie/episode korekce) nebo "show:&lt;Title&gt;" (re-match celého seriálu).
/// </summary>
public sealed class ManualMatch
{
    public int Id { get; set; }
    public required string Key { get; set; }      // cesta souboru | "show:<title>"
    public required string TargetKind { get; set; } // "movie" | "episode" | "show" | "lazy-episode" | "lazy-show"
    public int TmdbId { get; set; }               // 0 pro lazy (bez TMDB)
    public required string MediaType { get; set; }  // "movie" | "tv" | "none"
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? ShowTitle { get; set; }        // lazy-episode: název série (bez TMDB)
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Alias pro automatický match: folder:název, title:název, imdb:tt1234567 → TmdbId.
/// Vytváří se automaticky po ruční korekci. Příští scan stejné složky → auto match bez TMDB search.
/// </summary>
public sealed class MatchAlias
{
    public int Id { get; set; }
    public required string Key { get; set; }      // "folder:Breaking Bad" | "title:avatar last airbender" | "imdb:tt0468569"
    public int TmdbId { get; set; }
    public required string MediaType { get; set; } // "movie" | "tv"
    public DateTime CreatedAt { get; set; }
}

/// <summary>Epizoda seriálu – 1:1 k souboru, řazená podle (Season, Number).</summary>
public sealed class Episode
{
    public int Id { get; set; }

    public int ShowId { get; set; }
    public Show Show { get; set; } = null!;

    public int Season { get; set; }
    public int Number { get; set; }
    public string? Title { get; set; }

    public int MediaFileId { get; set; }
    public MediaFile MediaFile { get; set; } = null!;
}
