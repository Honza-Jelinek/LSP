namespace LSP.Server.External;

/// <summary>TMDB vrátilo 401 — neplatný API klíč (nebo špatný typ: v3 vs v4 token).</summary>
public sealed class TmdbUnauthorizedException() : Exception("TMDB API klíč je neplatný (HTTP 401).");

/// <summary>Výsledek TMDB vyhledávání.</summary>
public sealed record TmdbSearchResult(
    int TmdbId,
    string MediaType,      // "movie" | "tv"
    string Title,
    string? Overview,
    string? PosterPath,     // relativní cesta na TMDB CDN (např. "/xyz.jpg")
    string? BackdropPath,
    double? Rating,
    string? Genres,         // JSON array
    int? ReleaseYear);

/// <summary>Info o jedné epizodě z TMDB season endpointu.</summary>
public sealed record TmdbEpisodeInfo(
    int EpisodeNumber,
    string? Title,
    string? Overview,
    string? StillPath);

/// <summary>
/// Vyhledá metadata ve TMDB. Free: volá api.themoviedb.org přímo.
/// Subscription (budoucí): volá LSP cloud server.
/// </summary>
public interface IMetadataProvider
{
    Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct = default);
    Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct = default);

    /// <summary>Víc kandidátů pro ruční picker (type = "movie"|"tv").</summary>
    Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(string query, string type, CancellationToken ct = default);

    /// <summary>Detail podle TMDB ID (pro ruční přiřazení; type = "movie"|"tv").</summary>
    Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct = default);

    /// <summary>Všechny epizody jedné sezóny seriálu — keyed by episode_number. Jeden API call místo N.</summary>
    Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct = default);

    /// <summary>Čísla sezón seriálu (>=1, bez specials S0) z TMDB detailu.</summary>
    Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int tmdbId, CancellationToken ct = default);

    /// <summary>Vyhledá podle IMDB ID (tt1234567) pomocí TMDB /find endpointu.</summary>
    Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct = default);

    Task<string?> DownloadPosterAsync(string posterPath, string localFileName, CancellationToken ct = default);

    /// <summary>Vrátí mapování TMDB genre ID → název (cachované, CZ lokalizace).</summary>
    Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct = default);
}
