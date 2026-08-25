using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Library;

/// <summary>
/// Aplikuje ruční korekce (TMDB re-match + překlasifikace film↔epizoda). Autoritativní nad enrichmentem
/// (nastavuje IsManual) a přežívá rescan (ManualMatch je klíčovaný cestou souboru / názvem seriálu).
/// </summary>
public sealed class ManualMatchService(
    LibraryDbContext db,
    IMetadataProvider tmdb,
    SeasonEpisodeCache seasonCache,
    SettingsService settings,
    ILogger<ManualMatchService> log)
{
    public const string ShowKeyPrefix = "show:";

    /// <summary>Uloží ruční korekci souboru (film/epizoda) a hned ji aplikuje na DB.</summary>
    public async Task ApplyFileAsync(
        int mediaFileId, string kind, int tmdbId, string mediaType, int? season, int? episode, CancellationToken ct)
    {
        var file = await db.MediaFiles
            .Include(f => f.Movie).Include(f => f.Episode).ThenInclude(e => e!.Show)
            .FirstOrDefaultAsync(f => f.Id == mediaFileId, ct);
        if (file is null) return;

        await UpsertMatchAsync(file.Path, kind, tmdbId, mediaType, season, episode, null, ct);
        await ApplyFileMatchAsync(file, kind, tmdbId, mediaType, season, episode, ct);

        // Auto-create alias pro příští automatický match
        var dirName = Path.GetFileName(Path.GetDirectoryName(file.Path));
        if (dirName is not null)
            await UpsertAliasAsync($"folder:{dirName}", tmdbId, mediaType, ct);
        await UpsertAliasAsync($"title:{MatchScorer.Normalize(file.Episode?.Show?.Title ?? file.Movie?.Title ?? kind)}", tmdbId, mediaType, ct);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Ruční korekce: '{File}' → {Kind} (tmdbId {Id}{SE})",
            file.FileName, kind, tmdbId, kind == "episode" ? $", S{season}E{episode}" : "");
    }

    /// <summary>Uloží ruční re-match celého seriálu a aplikuje.</summary>
    public async Task ApplyShowAsync(int showId, int tmdbId, CancellationToken ct)
    {
        var show = await db.Shows.Include(s => s.Episodes).ThenInclude(e => e.MediaFile)
            .FirstOrDefaultAsync(s => s.Id == showId, ct);
        if (show is null) return;

        await UpsertMatchAsync(ShowKeyPrefix + show.Title, "show", tmdbId, "tv", null, null, null, ct);
        var details = await tmdb.GetDetailsAsync(tmdbId, "tv", ct);

        // Merge: pokud už existuje jiný Show se stejným TmdbId (auto-match "South Park" vs. ruční "south park 27"),
        // přepoj epizody na něj a tenhle smaž — jinak vzniknou duplicitní seriály.
        var episodes = show.Episodes.ToList();
        var target = await db.Shows.FirstOrDefaultAsync(s => s.TmdbId == tmdbId && s.Id != showId, ct);
        if (target is not null)
        {
            foreach (var ep in episodes) ep.Show = target;
            db.Shows.Remove(show);
        }
        else
        {
            target = show;
        }
        await ApplyShowMetadataAsync(target, details, ct);
        target.IsManual = true;

        // Doplň názvy epizod z TMDB (přepiš — starý titul je z parseru špatné shody).
        foreach (var season in episodes.Select(e => e.Season).Distinct())
        {
            var episodeMap = await seasonCache.GetAsync(tmdbId, season, ct);
            foreach (var ep in episodes.Where(e => e.Season == season))
                if (episodeMap.TryGetValue(ep.Number, out var info) && info.Title is { Length: > 0 } title)
                    ep.Title = title;
        }

        // Auto-create alias pro příští automatický match
        await UpsertAliasAsync($"title:{MatchScorer.Normalize(show.Title)}", tmdbId, "tv", ct);

        // Alias na skutečné content foldery epizod (ne na Show.Title — ten je jen grouping key,
        // ne nutně stejný jako název složky na disku, viz "south park 27" vs. "South Park").
        var roots = await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct);
        var contentFolders = episodes
            .Select(e => SeasonFolderDetector.GetContentFolderFromPath(e.MediaFile.Path, roots))
            .Where(f => f is not null)
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in contentFolders)
            await UpsertAliasAsync($"folder:{folder}", tmdbId, "tv", ct);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Ruční re-match seriálu '{Show}' → tmdbId {Id}", show.Title, tmdbId);
    }

    /// <summary>
    /// Přiřadí soubor jako epizodu "lazy" série. Název epizody z parseru (ParseCache / název souboru), auto-číslování.
    /// Volitelně měkké napojení na TMDB (softTmdbId &gt; 0) = jen banner + zobrazovaný název seriálu, epizody zůstávají lazy.
    /// </summary>
    public async Task ApplyLazyEpisodeAsync(int mediaFileId, string showTitle, int softTmdbId, int? season, int? episode, CancellationToken ct)
    {
        var file = await db.MediaFiles
            .Include(f => f.Movie).Include(f => f.Episode).ThenInclude(e => e!.Show)
            .FirstOrDefaultAsync(f => f.Id == mediaFileId, ct);
        if (file is null) return;

        // Napřed aplikuj (auto-číslování přidělí S/E), pak ulož výsledná čísla do ManualMatch → replay po rescanu je deterministický.
        var (s, e) = await ApplyLazyMatchAsync(file, showTitle, softTmdbId, season, episode, ct);
        await UpsertMatchAsync(file.Path, "lazy-episode", softTmdbId, softTmdbId > 0 ? "tv" : "none", s, e, showTitle, ct);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Lazy epizoda: '{File}' → '{Show}' S{S}E{E}", file.FileName, showTitle, s, e);
    }

    /// <summary>Ponechá seriál bez TMDB (jen IsManual) → zmizí z fronty, parsované názvy epizod zůstanou.</summary>
    public async Task ApplyLazyShowAsync(int showId, CancellationToken ct)
    {
        var show = await db.Shows.FirstOrDefaultAsync(s => s.Id == showId, ct);
        if (show is null) return;

        await UpsertMatchAsync(ShowKeyPrefix + show.Title, "lazy-show", 0, "none", null, null, null, ct);
        show.IsManual = true;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Lazy seriál (bez TMDB): '{Show}'", show.Title);
    }

    /// <summary>
    /// Smaže celý záznam seriálu (show + epizody + ruční korekce + aliasy). Soubory zůstávají a převedou se
    /// na nepřiřazené filmy (TmdbId null, !IsManual) → objeví se ve frontě „Ke kontrole", kde je uživatel
    /// znovu seskupí (lazy/normální série). Aliasy se mažou, aby enrichment nezopakoval stejný špatný match.
    /// </summary>
    public async Task DeleteRecordShowAsync(int showId, CancellationToken ct)
    {
        var show = await db.Shows.Include(s => s.Episodes).ThenInclude(e => e.MediaFile)
            .FirstOrDefaultAsync(s => s.Id == showId, ct);
        if (show is null) return;

        var files = show.Episodes.Select(e => e.MediaFile).ToList();
        await RemoveMatchesAndAliasesAsync(
            files.Select(f => f.Path).Append(ShowKeyPrefix + show.Title), show.TmdbId, show.SoftTmdbId, ct);

        foreach (var file in files)
            await ConvertToUnassignedMovieAsync(file, ct);

        db.Episodes.RemoveRange(show.Episodes);
        db.Shows.Remove(show);

        // Jedno SaveChanges = atomicky (proto tracked Remove, ne ExecuteDelete).
        await db.SaveChangesAsync(ct);
        log.LogInformation("Smazán záznam seriálu '{Show}' ({N} souborů → Ke kontrole).", show.Title, files.Count);
    }

    /// <summary>Smaže záznam jednoho souboru (film/epizoda) → nepřiřazený film ve frontě. Prázdný seriál se dočistí.</summary>
    public async Task DeleteRecordFileAsync(int mediaFileId, CancellationToken ct)
    {
        var file = await db.MediaFiles
            .Include(f => f.Movie)
            .Include(f => f.Episode).ThenInclude(e => e!.Show).ThenInclude(s => s.Episodes)
            .FirstOrDefaultAsync(f => f.Id == mediaFileId, ct);
        if (file is null) return;

        var tmdbId = file.Movie?.TmdbId ?? file.Episode?.Show.TmdbId;
        var softTmdbId = file.Episode?.Show.SoftTmdbId;
        var keys = new List<string> { file.Path };

        if (file.Episode is { Show: { } show } ep)
        {
            db.Episodes.Remove(ep);
            file.Episode = null;
            if (show.Episodes.All(x => x.Id == ep.Id))
            {
                keys.Add(ShowKeyPrefix + show.Title);
                db.Shows.Remove(show);
            }
        }
        if (file.Movie is not null) { db.Movies.Remove(file.Movie); file.Movie = null; }

        await RemoveMatchesAndAliasesAsync(keys, tmdbId, softTmdbId, ct);
        await ConvertToUnassignedMovieAsync(file, ct);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Smazán záznam souboru '{File}' (→ Ke kontrole).", file.FileName);
    }

    /// <summary>Označí ManualMatch + MatchAlias řádky ke smazání (tracked; smaže je SaveChanges volajícího).</summary>
    private async Task RemoveMatchesAndAliasesAsync(
        IEnumerable<string> matchKeys, int? tmdbId, int? softTmdbId, CancellationToken ct)
    {
        var keys = matchKeys.ToList();
        db.ManualMatches.RemoveRange(await db.ManualMatches.Where(m => keys.Contains(m.Key)).ToListAsync(ct));
        var ids = new[] { tmdbId ?? 0, softTmdbId ?? 0 }.Where(id => id > 0).ToList();
        if (ids.Count > 0)
            db.MatchAliases.RemoveRange(await db.MatchAliases.Where(a => ids.Contains(a.TmdbId)).ToListAsync(ct));
    }

    /// <summary>Vytvoří na souboru placeholder Movie (bez TMDB, !IsManual) → spadne do fronty „Ke kontrole".</summary>
    private async Task ConvertToUnassignedMovieAsync(MediaFile file, CancellationToken ct)
    {
        var parsed = await db.ParseCaches.FirstOrDefaultAsync(p => p.Path == file.Path, ct);
        var movie = new Movie
        {
            Title = parsed?.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(file.FileName),
            Year = parsed?.Year,
            ImdbId = parsed?.ImdbId,
            MediaFile = file,
            MediaFileId = file.Id,
        };
        db.Movies.Add(movie);
        file.Movie = movie;
        file.Kind = MediaKind.Movie;
    }

    private async Task<(int Season, int Number)> ApplyLazyMatchAsync(
        MediaFile file, string showTitle, int softTmdbId, int? season, int? episode, CancellationToken ct)
    {
        if (file.Movie is not null) { db.Movies.Remove(file.Movie); file.Movie = null; }

        // Sdružování lazy sérií: nejdřív podle měkkého TMDB indexu (stabilní i když se napsaný název liší
        // mezi dávkami / verzemi), teprve pak podle názvu. Bez toho vznikaly oddělené série pod stejným indexem.
        // EF Local tracker nejdřív (bulk více souborů jedné série ještě před SaveChanges), pak DB.
        Show? show = null;
        if (softTmdbId > 0)
            show = db.Shows.Local.FirstOrDefault(s => s.SoftTmdbId == softTmdbId)
                ?? await db.Shows.FirstOrDefaultAsync(s => s.SoftTmdbId == softTmdbId, ct);
        show ??= db.Shows.Local.FirstOrDefault(s => s.Title == showTitle)
            ?? await db.Shows.FirstOrDefaultAsync(s => s.Title == showTitle, ct);
        if (show is null)
        {
            show = new Show { Title = showTitle, IsManual = true };
            db.Shows.Add(show);
        }
        else show.IsManual = true;

        // Měkké napojení: banner + zobrazovaný název + index pro sdružování (TmdbId zůstává null → epizody se needrichnou).
        // Guard na DisplayTitle = null → v bulku se stáhne jen jednou (tracked entita).
        if (softTmdbId > 0)
        {
            show.SoftTmdbId = softTmdbId;
            if (show.DisplayTitle is null)
            {
                var details = await tmdb.GetDetailsAsync(softTmdbId, "tv", ct);
                if (details is not null)
                {
                    show.DisplayTitle = details.Title;
                    show.PosterFile = await MaybeDownloadPosterAsync(details, ct);
                }
            }
        }

        var ep = file.Episode;
        var isNew = ep is null;
        if (ep is null)
        {
            ep = new Episode { MediaFile = file, MediaFileId = file.Id, Show = show };
            db.Episodes.Add(ep);
            file.Episode = ep;
        }
        var wasInShow = !isNew && ReferenceEquals(ep.Show, show) && ep.Number > 0;
        ep.Show = show;

        if (season.HasValue || episode.HasValue)
        {
            ep.Season = season ?? 1;
            ep.Number = episode ?? 1;
        }
        else if (!wasInShow)
        {
            // Auto-číslování: S1, další volné číslo (DB max i netrackované nové epizody v tomto běhu).
            var maxDb = show.Id > 0
                ? await db.Episodes.Where(x => x.ShowId == show.Id && x.Season == 1).MaxAsync(x => (int?)x.Number, ct) ?? 0
                : 0;
            var maxLocal = db.Episodes.Local
                .Where(x => ReferenceEquals(x.Show, show) && x.Season == 1 && !ReferenceEquals(x, ep))
                .Select(x => (int?)x.Number).Max() ?? 0;
            ep.Season = 1;
            ep.Number = Math.Max(maxDb, maxLocal) + 1;
        }
        // wasInShow bez explicitních S/E → čísla zůstávají (idempotentní re-apply)

        var parsed = await db.ParseCaches.FirstOrDefaultAsync(p => p.Path == file.Path, ct);
        ep.Title = parsed?.EpisodeTitle is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(file.FileName);
        file.Kind = MediaKind.Episode;
        return (ep.Season, ep.Number);
    }

    /// <summary>Zruší ruční korekci souboru → vrátí se na automatické (plně po rescanu).</summary>
    public async Task ResetFileAsync(int mediaFileId, CancellationToken ct)
    {
        var file = await db.MediaFiles.Include(f => f.Movie).FirstOrDefaultAsync(f => f.Id == mediaFileId, ct);
        if (file is null) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.ManualMatches.Where(x => x.Key == file.Path).ExecuteDeleteAsync(ct);
        if (file.Movie is not null)
        {
            file.Movie.IsManual = false;
            file.Movie.TmdbId = null;
            file.Movie.DisplayTitle = null;
            file.Movie.PosterFile = null;
            file.Movie.Overview = null;
            file.Movie.Rating = null;
            file.Movie.Genres = null;
            file.Movie.Cast = null;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        log.LogInformation("Reset ruční korekce souboru '{File}' (plný návrat po rescanu).", file.FileName);
    }

    /// <summary>Zruší ruční re-match seriálu.</summary>
    public async Task ResetShowAsync(int showId, CancellationToken ct)
    {
        var show = await db.Shows.FirstOrDefaultAsync(s => s.Id == showId, ct);
        if (show is null) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.ManualMatches.Where(x => x.Key == ShowKeyPrefix + show.Title).ExecuteDeleteAsync(ct);
        show.IsManual = false;
        show.TmdbId = null;
        show.DisplayTitle = null;
        show.PosterFile = null;
        show.Overview = null;
        show.Rating = null;
        show.Genres = null;
        show.Cast = null;
        show.SoftTmdbId = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        log.LogInformation("Reset ruční korekce seriálu '{Show}'.", show.Title);
    }

    /// <summary>Znovu aplikuje VŠECHNY ruční korekce (volá se po rescanu).</summary>
    public async Task ApplyAllAsync(CancellationToken ct = default)
    {
        var matches = await db.ManualMatches.ToListAsync(ct);
        if (matches.Count == 0) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var m in matches)
        {
            ct.ThrowIfCancellationRequested();
            if (m.TargetKind == "lazy-show" && m.Key.StartsWith(ShowKeyPrefix, StringComparison.Ordinal))
            {
                var title = m.Key[ShowKeyPrefix.Length..];
                var show = await db.Shows.FirstOrDefaultAsync(s => s.Title == title, ct);
                if (show is null) continue;
                show.IsManual = true;
            }
            else if (m.TargetKind == "lazy-episode")
            {
                var file = await db.MediaFiles
                    .Include(f => f.Movie).Include(f => f.Episode)
                    .FirstOrDefaultAsync(f => f.Path == m.Key, ct);
                if (file is null || m.ShowTitle is not { Length: > 0 } showTitle) continue;
                await ApplyLazyMatchAsync(file, showTitle, m.TmdbId, m.Season, m.Episode, ct);
            }
            else if (m.TargetKind == "show" && m.Key.StartsWith(ShowKeyPrefix, StringComparison.Ordinal))
            {
                var title = m.Key[ShowKeyPrefix.Length..];
                var show = await db.Shows.FirstOrDefaultAsync(s => s.Title == title, ct);
                if (show is null) continue;
                var details = await tmdb.GetDetailsAsync(m.TmdbId, "tv", ct);
                await ApplyShowMetadataAsync(show, details, ct);
                show.IsManual = true;
            }
            else
            {
                var file = await db.MediaFiles
                    .Include(f => f.Movie).Include(f => f.Episode)
                    .FirstOrDefaultAsync(f => f.Path == m.Key, ct);
                if (file is null) continue;
                await ApplyFileMatchAsync(file, m.TargetKind, m.TmdbId, m.MediaType, m.Season, m.Episode, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        log.LogInformation("Ruční korekce znovu aplikovány: {Count}", matches.Count);
    }

    // --- vnitřní ---

    private async Task ApplyFileMatchAsync(
        MediaFile file, string kind, int tmdbId, string mediaType, int? season, int? episode, CancellationToken ct)
    {
        var details = await tmdb.GetDetailsAsync(tmdbId, mediaType, ct);

        if (kind == "movie")
        {
            if (file.Episode is not null) { db.Episodes.Remove(file.Episode); file.Episode = null; }

            var movie = file.Movie;
            if (movie is null)
            {
                movie = new Movie { Title = details?.Title ?? file.FileName, MediaFile = file, MediaFileId = file.Id };
                db.Movies.Add(movie);
                file.Movie = movie;
            }
            await ApplyMovieMetadataAsync(movie, details, ct);
            movie.IsManual = true;
            file.Kind = MediaKind.Movie;
        }
        else // episode
        {
            if (file.Movie is not null) { db.Movies.Remove(file.Movie); file.Movie = null; }

            var show = await ResolveShowByTmdbAsync(tmdbId, details, ct);
            var ep = file.Episode;
            if (ep is null)
            {
                ep = new Episode { MediaFile = file, MediaFileId = file.Id, Show = show };
                db.Episodes.Add(ep);
                file.Episode = ep;
            }
            ep.Show = show;
            ep.Season = season ?? 1;
            ep.Number = episode ?? 1;
            ep.Title = await GetEpisodeTitleAsync(tmdbId, ep.Season, ep.Number, ct) ?? ep.Title;
            file.Kind = MediaKind.Episode;
        }
    }

    private async Task<string?> GetEpisodeTitleAsync(int tmdbId, int season, int number, CancellationToken ct)
    {
        var map = await seasonCache.GetAsync(tmdbId, season, ct);
        return map.TryGetValue(number, out var info) && info.Title is { Length: > 0 } t ? t : null;
    }

    private async Task<Show> ResolveShowByTmdbAsync(int tmdbId, TmdbSearchResult? details, CancellationToken ct)
    {
        var existing = db.Shows.Local.FirstOrDefault(s => s.TmdbId == tmdbId)
            ?? await db.Shows.FirstOrDefaultAsync(s => s.TmdbId == tmdbId, ct);
        if (existing is not null) return existing;

        var title = details?.Title is { Length: > 0 } dt ? dt : $"TMDB {tmdbId}";
        var byTitle = db.Shows.Local.FirstOrDefault(s => s.Title == title)
            ?? await db.Shows.FirstOrDefaultAsync(s => s.Title == title, ct);
        if (byTitle is not null)
        {
            await ApplyShowMetadataAsync(byTitle, details, ct);
            byTitle.IsManual = true;
            return byTitle;
        }

        var show = new Show { Title = title };
        db.Shows.Add(show);
        await ApplyShowMetadataAsync(show, details, ct);
        show.IsManual = true;
        return show;
    }

    private async Task ApplyMovieMetadataAsync(Movie m, TmdbSearchResult? d, CancellationToken ct)
    {
        if (d is null) return;
        m.DisplayTitle = d.Title;
        m.TmdbId = d.TmdbId;
        m.Overview = d.Overview;
        m.Rating = d.Rating;
        m.Genres = GenreFormat.ExtractNames(d.Genres);
        m.PosterFile = await MaybeDownloadPosterAsync(d, ct);
    }

    private async Task ApplyShowMetadataAsync(Show s, TmdbSearchResult? d, CancellationToken ct)
    {
        if (d is null) return;
        s.DisplayTitle = d.Title;
        s.TmdbId = d.TmdbId;
        s.Overview = d.Overview;
        s.Rating = d.Rating;
        s.Genres = GenreFormat.ExtractNames(d.Genres);
        s.PosterFile = await MaybeDownloadPosterAsync(d, ct);
    }


    private async Task<string?> MaybeDownloadPosterAsync(TmdbSearchResult d, CancellationToken ct)
    {
        if (d.PosterPath is null) return null;
        if (!await settings.GetBoolAsync(SettingsService.FetchPosters, true, ct)) return null;
        return await tmdb.DownloadPosterAsync(d.PosterPath, $"{d.TmdbId}.jpg", ct);
    }

    private async Task UpsertMatchAsync(
        string key, string kind, int tmdbId, string mediaType, int? season, int? episode, string? showTitle, CancellationToken ct)
    {
        var existing = await db.ManualMatches.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (existing is null)
        {
            db.ManualMatches.Add(new ManualMatch
            {
                Key = key,
                TargetKind = kind,
                TmdbId = tmdbId,
                MediaType = mediaType,
                Season = season,
                Episode = episode,
                ShowTitle = showTitle,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.TargetKind = kind;
            existing.TmdbId = tmdbId;
            existing.MediaType = mediaType;
            existing.Season = season;
            existing.Episode = episode;
            existing.ShowTitle = showTitle;
            existing.CreatedAt = DateTime.UtcNow;
        }
    }

    private async Task UpsertAliasAsync(string key, int tmdbId, string mediaType, CancellationToken ct)
    {
        var existing = await db.MatchAliases.FirstOrDefaultAsync(a => a.Key == key, ct);
        if (existing is null)
        {
            db.MatchAliases.Add(new MatchAlias
            {
                Key = key,
                TmdbId = tmdbId,
                MediaType = mediaType,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.TmdbId = tmdbId;
            existing.MediaType = mediaType;
            existing.CreatedAt = DateTime.UtcNow;
        }
    }
}
