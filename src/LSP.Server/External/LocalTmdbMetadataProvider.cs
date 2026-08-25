using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LSP.Server.External;

public sealed class LocalTmdbMetadataProvider : IMetadataProvider
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w500";

    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    private readonly ILogger<LocalTmdbMetadataProvider> _log;
    private readonly string _posterDir;
    private static Dictionary<int, string>? _genreCache;

    public LocalTmdbMetadataProvider(
        ICredentialProvider creds, HttpClient http, ILogger<LocalTmdbMetadataProvider> log)
    {
        _creds = creds;
        _http = http;
        _log = log;
        _posterDir = AppPaths.PosterDir;
        Directory.CreateDirectory(_posterDir);
    }

    public async Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct)
    {
        var query = $"query={Uri.EscapeDataString(title)}&language=cs-CZ";
        // primary_release_year je přísnější než year → líp odfiltruje remaky/nové iterace.
        if (year is > 0) query += $"&primary_release_year={year}";
        return await SearchAsync("movie", query, "movie", title, year, ct);
    }

    public async Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct)
    {
        var query = $"query={Uri.EscapeDataString(title)}&language=cs-CZ";
        return await SearchAsync("tv", query, "tv", title, null, ct);
    }

    /// <summary>TMDB klíč může být v3 (32 znaků → query param) nebo v4 token (JWT „eyJ…" → Bearer header).</summary>
    private static bool IsV4Token(string key) => key.StartsWith("eyJ", StringComparison.Ordinal);

    /// <summary>GET na TMDB s korektní auth (v3 query / v4 Bearer). 401 → TmdbUnauthorizedException.</summary>
    private async Task<JsonDocument?> GetJsonAsync(string path, string queryNoAuth, CancellationToken ct)
    {
        var key = await _creds.GetTmdbApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) return null;

        var v4 = IsV4Token(key);
        var sep = string.IsNullOrEmpty(queryNoAuth) ? "" : "&";
        var url = v4
            ? $"{BaseUrl}/{path}?{queryNoAuth}"
            : $"{BaseUrl}/{path}?{queryNoAuth}{sep}api_key={key}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (v4) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var resp = await _http.SendAsync(request, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new TmdbUnauthorizedException();
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("TMDB GET {Path} HTTP {Code}", path, resp.StatusCode);
            return null;
        }
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>Postaví TmdbSearchResult z JSON elementu (search výsledek i detail).</summary>
    private static TmdbSearchResult ParseElement(JsonElement el, string mediaType)
    {
        var id = el.GetProperty("id").GetInt32();
        var titleProp = mediaType == "tv" ? "name" : "title";
        var dateProp = mediaType == "tv" ? "first_air_date" : "release_date";

        var title = el.TryGetProperty(titleProp, out var t) ? t.GetString() : null;
        var overview = el.TryGetProperty("overview", out var o) ? o.GetString() : null;
        var posterPath = el.TryGetProperty("poster_path", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;
        var backdropPath = el.TryGetProperty("backdrop_path", out var b) && b.ValueKind != JsonValueKind.Null ? b.GetString() : null;
        var rating = el.TryGetProperty("vote_average", out var v) ? v.GetDouble() : (double?)null;

        int? releaseYear = null;
        if (el.TryGetProperty(dateProp, out var d) && d.GetString() is { Length: >= 4 } dateStr
            && int.TryParse(dateStr[..4], CultureInfo.InvariantCulture, out var yr))
            releaseYear = yr;

        string? genres = null;
        if (el.TryGetProperty("genre_ids", out var g)) genres = g.ToString();
        else if (el.TryGetProperty("genres", out var gs)) genres = gs.ToString();

        return new TmdbSearchResult(id, mediaType, title ?? "", overview, posterPath, backdropPath, rating, genres, releaseYear);
    }

    private async Task<TmdbSearchResult?> SearchAsync(string kind, string query, string mediaType, string searchTitle, int? searchYear, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"search/{kind}", query, ct);
            if (doc is null) return null;

            var results = doc.RootElement.GetProperty("results");
            var count = results.GetArrayLength();
            if (count == 0)
            {
                _log.LogInformation("TMDB {Kind} '{Title}' (rok {Year}) → 0 výsledků", kind, searchTitle, searchYear);
                return null;
            }

            var result = ParseElement(results[0], mediaType);
            _log.LogInformation("TMDB {Kind} '{Title}' (rok {Year}) → {Count} výsledků, top: '{Result}' (id {Id}, rok {Ry})",
                kind, searchTitle, searchYear, count, result.Title, result.TmdbId, result.ReleaseYear);
            return result;
        }
        catch (TmdbUnauthorizedException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB search selhal");
            return null;
        }
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(string query, string type, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"search/{type}", $"query={Uri.EscapeDataString(query)}&language=cs-CZ", ct);
            if (doc is null) return [];

            var results = doc.RootElement.GetProperty("results");
            var list = new List<TmdbSearchResult>();
            foreach (var el in results.EnumerateArray())
            {
                list.Add(ParseElement(el, type));
                if (list.Count >= 8) break;
            }
            return list;
        }
        catch (TmdbUnauthorizedException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB search candidates selhal");
            return [];
        }
    }

    public async Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{type}/{tmdbId}", "language=cs-CZ", ct);
            if (doc is null) return null;
            return ParseElement(doc.RootElement, type);
        }
        catch (TmdbUnauthorizedException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB detail {Id} selhal", tmdbId);
            return null;
        }
    }

    public async Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct)
    {
        // Overime format
        if (string.IsNullOrWhiteSpace(imdbId) || imdbId.Length < 9) return null;

        try
        {
            using var doc = await GetJsonAsync($"find/{imdbId}", "external_source=imdb_id&language=cs-CZ", ct);
            if (doc is null) return null;

            // /find vraci pole: movie_results, tv_results, person_results, ...
            var movies = doc.RootElement.GetProperty("movie_results");
            if (movies.GetArrayLength() > 0)
                return ParseElement(movies[0], "movie");

            var tv = doc.RootElement.GetProperty("tv_results");
            if (tv.GetArrayLength() > 0)
                return ParseElement(tv[0], "tv");

            return null;
        }
        catch (TmdbUnauthorizedException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB find IMDB {ImdbId} selhal", imdbId);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"tv/{tmdbId}/season/{season}", "language=cs-CZ", ct);
            if (doc is null) return new Dictionary<int, TmdbEpisodeInfo>();

            var episodes = doc.RootElement.GetProperty("episodes");
            var dict = new Dictionary<int, TmdbEpisodeInfo>();
            foreach (var ep in episodes.EnumerateArray())
            {
                var epNum = ep.GetProperty("episode_number").GetInt32();
                var name = ep.TryGetProperty("name", out var n) ? n.GetString() : null;
                var overview = ep.TryGetProperty("overview", out var o) ? o.GetString() : null;
                var still = ep.TryGetProperty("still_path", out var s) && s.ValueKind != JsonValueKind.Null ? s.GetString() : null;
                dict[epNum] = new TmdbEpisodeInfo(epNum, name, overview, still);
            }
            _log.LogInformation("TMDB season {TmdbId} S{Season}: {Count} epizod", tmdbId, season, dict.Count);
            return dict;
        }
        catch (TmdbUnauthorizedException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB season {TmdbId} S{Season} selhal", tmdbId, season);
            return new Dictionary<int, TmdbEpisodeInfo>();
        }
    }

    public async Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int tmdbId, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"tv/{tmdbId}", "language=cs-CZ", ct);
            if (doc is null || !doc.RootElement.TryGetProperty("seasons", out var seasons))
                return [];

            var list = new List<int>();
            foreach (var s in seasons.EnumerateArray())
            {
                if (s.TryGetProperty("season_number", out var n) && n.TryGetInt32(out var num) && num >= 1)
                    list.Add(num);
            }
            list.Sort();
            return list;
        }
        catch (TmdbUnauthorizedException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB seasons {Id} selhal", tmdbId);
            return [];
        }
    }

    public async Task<string?> DownloadPosterAsync(string posterPath, string localFileName, CancellationToken ct)
    {
        try
        {
            var url = $"{ImageBase}{posterPath}";
            var bytes = await _http.GetByteArrayAsync(url, ct);
            var dest = Path.Combine(_posterDir, localFileName);
            await File.WriteAllBytesAsync(dest, bytes, ct);
            return dest;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB poster download selhal: {Path}", posterPath);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct)
    {
        if (_genreCache is not null) return _genreCache;

        var map = new Dictionary<int, string>();
        foreach (var type in new[] { "movie", "tv" })
        {
            try
            {
                using var doc = await GetJsonAsync($"genre/{type}/list", "language=cs-CZ", ct);
                if (doc is null) continue;
                foreach (var g in doc.RootElement.GetProperty("genres").EnumerateArray())
                {
                    var id = g.GetProperty("id").GetInt32();
                    var name = g.GetProperty("name").GetString();
                    if (name is not null) map.TryAdd(id, name);
                }
            }
            catch (TmdbUnauthorizedException) { throw; }
            catch { /* best-effort */ }
        }

        if (map.Count > 0) _genreCache = map;
        return map;
    }

    /// <summary>Přeloží JSON array genre_ids [28,12] → "Akční, Dobrodružný".</summary>
    public async Task<string?> ResolveGenreNamesAsync(string? genreIdsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(genreIdsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(genreIdsJson);
            var map = await GetGenreMapAsync(ct);
            var names = doc.RootElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number && map.TryGetValue(e.GetInt32(), out var n) ? n : null)
                .Where(n => n is not null)
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
        catch { return null; }
    }
}
