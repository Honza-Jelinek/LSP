using System.Text.Json;
using LSP.Server.Data;
using LSP.Server.External;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Library;

/// <summary>
/// Sdílená cache epizod jedné sezóny (TMDB season endpoint = 1 call/sezóna).
/// Vrstvy: EF Local tracker (stejný běh) → DB (TTL) → TMDB fetch + upsert do trackeru.
/// Neukládá — volající zavolá <c>db.SaveChangesAsync</c>. Používá enrichment, ruční korekce i season endpoint,
/// takže bulk edit více epizod jedné série trefí TMDB jen jednou.
/// </summary>
public sealed class SeasonEpisodeCache(LibraryDbContext db, IMetadataProvider tmdb)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(180);

    public async Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetAsync(
        int tmdbId,
        int season,
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        // Nejdřív EF tracker (jiný show se stejným TmdbId ho mohl přidat v tomtéž běhu ještě před SaveChanges),
        // pak DB. Jinak by vznikl duplicitní (TmdbId, Season).
        var cached = db.TmdbSeasonCaches.Local.FirstOrDefault(s => s.TmdbId == tmdbId && s.Season == season)
            ?? await db.TmdbSeasonCaches.FirstOrDefaultAsync(s => s.TmdbId == tmdbId && s.Season == season, ct);

        if (!forceRefresh && cached is not null && DateTime.UtcNow - cached.FetchedAt < CacheTtl && cached.Data is not null)
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<int, TmdbEpisodeInfo>>(cached.Data);
                if (deserialized is not null) return deserialized;
            }
            catch { }
        }

        var episodeMap = await tmdb.GetSeasonEpisodesAsync(tmdbId, season, ct);
        if (episodeMap.Count == 0)
            return episodeMap;

        var json = JsonSerializer.Serialize(episodeMap);
        if (cached is null)
            db.TmdbSeasonCaches.Add(new TmdbSeasonCache { TmdbId = tmdbId, Season = season, Data = json, FetchedAt = DateTime.UtcNow });
        else
        {
            cached.Data = json;
            cached.FetchedAt = DateTime.UtcNow;
        }
        return episodeMap;
    }

    /// <summary>Všechny epizody seriálu napříč sezónami (flat) pro ruční picker. Každá sezóna přes <see cref="GetAsync"/> (cache).</summary>
    public async Task<IReadOnlyList<(int Season, int Number, string? Title)>> GetAllAsync(int tmdbId, CancellationToken ct = default)
    {
        var seasons = await tmdb.GetTvSeasonNumbersAsync(tmdbId, ct);
        var result = new List<(int, int, string?)>();
        foreach (var s in seasons)
        {
            var map = await GetAsync(tmdbId, s, ct);
            foreach (var ep in map.Values.OrderBy(e => e.EpisodeNumber))
                result.Add((s, ep.EpisodeNumber, ep.Title));
        }
        return result;
    }
}
