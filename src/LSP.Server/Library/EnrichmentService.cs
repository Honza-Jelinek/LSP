using System.Text.Json;
using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Library;

public sealed record EnrichmentSummary(
    int TmdbHits, int TmdbMisses, int LlmFallbacks, int LlmRecovered,
    int Reclassified, int PostersDownloaded, int Skipped, long ElapsedMs,
    int ReviewQueue, string? Error = null);

/// <summary>Průběžný stav enrichmentu pro polling z UI. Phase 2.5 = LLM výběr, 4 = úklid.</summary>
public sealed record EnrichmentProgress(double Phase, string PhaseName, int Processed, int Total);

/// <summary>
/// Kaskáda s vahami a confidence:
///   alias/folder → ParseCache → TMDB kandidáti + scoring → LLM → review queue
/// Pro seriály: match show podle složky → season API pro epizody (1 call/sezóna místo N/search).
/// </summary>
public sealed class EnrichmentService(
    LibraryDbContext db,
    IMetadataProvider tmdb,
    ILlmClient llm,
    SeasonEpisodeCache seasonCache,
    SettingsService settings,
    ILogger<EnrichmentService> log)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(180);
    private static readonly double AutoApplyThreshold = 0.85;
    private static readonly double LlmChooseThreshold = 0.75; // pod tím rozhoduje LLM; 0.75–0.85 se aplikuje s review flagem
    private static readonly double ReviewThreshold = 0.60;
    private bool _forceRefresh;

    public async Task<EnrichmentSummary> EnrichAsync(
        bool force = false, IProgress<EnrichmentProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fetchPosters = await settings.GetBoolAsync(SettingsService.FetchPosters, true, ct);
        var hasTmdb = await settings.HasKeyAsync(SettingsService.TmdbApiKey, ct);
        var hasLlm = await settings.HasKeyAsync(SettingsService.LlmApiKey, ct);

        log.LogInformation("Enrichment start (force={Force}) — TMDB: {Tmdb}, LLM: {Llm}, postery: {P}",
            force, hasTmdb, hasLlm, fetchPosters);

        if (!hasTmdb)
        {
            log.LogWarning("Enrichment přeskočen — není TMDB API klíč.");
            return new EnrichmentSummary(0, 0, 0, 0, 0, 0, 0, sw.ElapsedMilliseconds, 0,
                Error: "Není nastaven TMDB API klíč.");
        }

        try { await tmdb.SearchMovieAsync("inception", 2010, ct); }
        catch (TmdbUnauthorizedException)
        {
            return new EnrichmentSummary(0, 0, 0, 0, 0, 0, 0, sw.ElapsedMilliseconds, 0,
                Error: "Neplatný TMDB API klíč (401).");
        }

        _forceRefresh = force;

        // Načti aliasy do paměti (folder→TMDB, title→TMDB)
        var aliases = await db.MatchAliases.ToListAsync(ct);
        var roots = await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct);

        int hits = 0, posters = 0, skipped = 0, reclassified = 0, reviewQueue = 0;
        var showByTmdbId = new Dictionary<int, Show>();
        var pendingMovies = new Dictionary<string, List<Movie>>();  // itemKey → filmy sdílející stejný dotaz
        var movieChoiceKeys = new Dictionary<string, string>();     // queryKey → itemKey (dedup dotazů pro LLM)
        var pendingShows = new Dictionary<string, Show>();
        var chooseInputs = new List<LlmChooseInput>();

        // --- Fáze 1: Filmy ---
        var movies = await db.Movies.Include(m => m.MediaFile).ToListAsync(ct);

        for (var movieIndex = 0; movieIndex < movies.Count; movieIndex++)
        {
            var movie = movies[movieIndex];
            ct.ThrowIfCancellationRequested();
            progress?.Report(new EnrichmentProgress(1, "Filmy", movieIndex, movies.Count));
            if (movie.IsManual) { skipped++; continue; }
            if (force) ClearMetadata(movie);
            if (movie.TmdbId is not null) { skipped++; continue; }

            // Zkus alias
            var alias = ResolveAlias(aliases, movie.Title, movie.MediaFile.Path, "movie", roots);
            if (alias is not null)
            {
                var detail = await tmdb.GetDetailsAsync(alias.Value.TmdbId, "movie", ct);
                if (detail is not null)
                {
                    await ApplyToMovieAsync(movie, detail, fetchPosters, ct);
                    if (detail.PosterPath is not null && fetchPosters) posters++;
                    hits++;
                    continue;
                }
            }

            // Zkus IMDB ID z názvu souboru (tt1234567)
            if (movie.ImdbId is not null)
            {
                var imdbResult = await tmdb.FindByImdbAsync(movie.ImdbId, ct);
                if (imdbResult is not null)
                {
                    await ApplyToMovieAsync(movie, imdbResult, fetchPosters, ct);
                    if (imdbResult.PosterPath is not null && fetchPosters) posters++;
                    hits++;
                    continue;
                }
            }

            var (cached, score, candidates) = await TryTmdbMovieThenTvScoredAsync(movie.Title, movie.Year, "movie", fetchPosters, ct);
            if (cached is not null && score >= AutoApplyThreshold)
            {
                ApplyToMovie(movie, cached);
                if (cached.PosterFile is not null) posters++;
                hits++;
            }
            else if (cached is not null && score >= ReviewThreshold)
            {
                // 0.75–0.85: dost jistá shoda → aplikuj s review flagem; 0.60–0.75: ať vybere LLM.
                if (score >= LlmChooseThreshold || !hasLlm ||
                    !await TryQueuePendingMovieChoiceAsync(chooseInputs, pendingMovies, movieChoiceKeys, movie, movie.Title, movie.Year, candidates, roots, ct))
                {
                    ApplyToMovie(movie, cached);
                    hits++;
                    reviewQueue++;
                }
            }
            else
            {
                // Low score: zkus TV fallback se scoringem
                var (tvCached, tvScore, tvCandidates) = await TryTmdbMovieThenTvScoredAsync(movie.Title, null, "tv", fetchPosters, ct);
                if (tvCached is not null && tvScore >= AutoApplyThreshold)
                {
                    ApplyToMovie(movie, tvCached);
                    if (tvCached.PosterFile is not null) posters++;
                    hits++;
                }
                else if (tvCached is not null && tvScore >= ReviewThreshold)
                {
                    if (tvScore >= LlmChooseThreshold || !hasLlm ||
                        !await TryQueuePendingMovieChoiceAsync(chooseInputs, pendingMovies, movieChoiceKeys, movie, movie.Title, movie.Year, tvCandidates, roots, ct))
                    {
                        ApplyToMovie(movie, tvCached);
                        hits++;
                        reviewQueue++;
                    }
                }
            }
        }

        // --- Fáze 2: Seriály (show-first) ---
        var shows = await db.Shows.Include(s => s.Episodes).ThenInclude(e => e.MediaFile).ToListAsync(ct);

        for (var showIndex = 0; showIndex < shows.Count; showIndex++)
        {
            var show = shows[showIndex];
            ct.ThrowIfCancellationRequested();
            progress?.Report(new EnrichmentProgress(2, "Seriály", showIndex, shows.Count));
            if (show.IsManual) { skipped++; continue; }
            if (force) ClearMetadata(show);
            if (show.TmdbId is not null) { skipped++; continue; }

            // Zkus alias (přes cestu první epizody, aby fungovaly i folder aliasy)
            var firstEpisodePath = show.Episodes.Count > 0 ? show.Episodes[0].MediaFile.Path : null;
            var alias = ResolveAlias(aliases, show.Title, firstEpisodePath, "tv", roots);
            if (alias is not null)
            {
                var detail = await tmdb.GetDetailsAsync(alias.Value.TmdbId, "tv", ct);
                if (detail is not null)
                {
                    await ApplyToShowAsync(show, detail, fetchPosters, ct);
                    if (detail.PosterPath is not null && fetchPosters) posters++;
                    hits++;
                    await EnrichSeasonEpisodesAsync(show, detail.TmdbId, ct);
                    continue;
                }
            }

            // Zkus IMDB ID z názvu souboru
            if (show.ImdbId is not null)
            {
                var imdbResult = await tmdb.FindByImdbAsync(show.ImdbId, ct);
                if (imdbResult is not null)
                {
                    await ApplyToShowAsync(show, imdbResult, fetchPosters, ct);
                    if (imdbResult.PosterPath is not null && fetchPosters) posters++;
                    hits++;
                    await EnrichSeasonEpisodesAsync(show, imdbResult.TmdbId, ct);
                    continue;
                }
            }

            var (cached, score, candidates) = await TryBestTmdbScoredVariantAsync(BuildShowTitleVariants(show), fetchPosters, ct);
            if (cached is null || score < ReviewThreshold)
            {
                continue; // show zůstává bez match → LLM (Fáze 3) nebo review
            }

            // Slabší shoda (0.60–0.75): má-li LLM, ať vybere z kandidátů místo automatické aplikace.
            if (score < LlmChooseThreshold && hasLlm &&
                await TryQueuePendingShowChoiceAsync(chooseInputs, pendingShows, show, show.Title, candidates, roots, ct))
            {
                continue;
            }

            ApplyToShow(show, cached);
            if (cached.PosterFile is not null) posters++;
            hits++;
            if (score < AutoApplyThreshold) reviewQueue++;

            // Season API pro epizody
            await EnrichSeasonEpisodesAsync(show, cached.TmdbId!.Value, ct);
        }

        // --- Fáze 2.5: LLM rozhodne u nejistých TMDB shodů (review pásmo) ---
        if (hasLlm && chooseInputs.Count > 0)
        {
            log.LogInformation("Fáze 2.5 (LLM disambiguace): {Count} nejistých shod", chooseInputs.Count);
            var choices = await llm.ChooseCandidateBatchAsync(chooseInputs, ct);

            for (var chooseIndex = 0; chooseIndex < chooseInputs.Count; chooseIndex++)
            {
                var input = chooseInputs[chooseIndex];
                ct.ThrowIfCancellationRequested();
                progress?.Report(new EnrichmentProgress(2.5, "LLM výběr", chooseIndex, chooseInputs.Count));
                var groupSize = pendingMovies.TryGetValue(input.ItemKey, out var groupMovies) ? groupMovies.Count : 1;

                if (!choices.TryGetValue(input.ItemKey, out var choice) || choice.ChosenTmdbId is not { } chosenId)
                {
                    reviewQueue += groupSize; // LLM řekl "žádný" nebo neodpověděl → zůstává bez TmdbId (review fronta)
                    continue;
                }

                var detail = await tmdb.GetDetailsAsync(chosenId, input.ExpectedKind, ct);
                if (detail is null) { reviewQueue += groupSize; continue; }

                if (groupMovies is not null)
                {
                    foreach (var pendingMovie in groupMovies)
                    {
                        await ApplyToMovieAsync(pendingMovie, detail, fetchPosters, ct);
                        if (detail.PosterPath is not null && fetchPosters) posters++;
                        hits++;
                    }
                    await UpsertTmdbCacheChoiceAsync(input.ParsedTitle, input.ParsedYear, "movie", detail, ct);
                }
                else if (pendingShows.TryGetValue(input.ItemKey, out var pendingShow))
                {
                    await ApplyToShowAsync(pendingShow, detail, fetchPosters, ct);
                    if (detail.PosterPath is not null && fetchPosters) posters++;
                    await UpsertTmdbCacheChoiceAsync(input.ParsedTitle, null, "tv", detail, ct);
                    hits++;
                    await EnrichSeasonEpisodesAsync(pendingShow, detail.TmdbId, ct);
                }
            }
        }

        // --- Fáze 3: LLM fallback — skupinově po složkách ---
        int llmFallbacks = 0, llmRecovered = 0;
        if (hasLlm)
        {
            var missingMovies = await db.Movies
                .Include(m => m.MediaFile)
                .Where(m => !m.IsManual && m.TmdbId == null)
                .ToListAsync(ct);
            var missingShows = await db.Shows
                .Where(s => !s.IsManual && s.TmdbId == null)
                .ToListAsync(ct);

            if (missingMovies.Count > 0 || missingShows.Count > 0)
            {
                llmFallbacks = missingMovies.Count + missingShows.Count;
                log.LogInformation("Fáze 3 (LLM fallback): {Count} položek", llmFallbacks);
                var llmProcessed = 0;

                if (missingMovies.Count > 0)
                {
                    // Jedna velká dávka pro všechny chybějící filmy (klient si ji rozdělí po BatchSize).
                    // Folder kontext nese každá položka sama — per-složkové cally dělaly desítky
                    // malých requestů a narážely na rate limit.
                    var inputs = missingMovies
                        .Select(m => new LlmParseInput(
                            m.MediaFile.FileName,
                            SeasonFolderDetector.GetContentFolderFromPath(m.MediaFile.Path, roots),
                            m.Title, "movie")).ToList();

                    var results = await llm.ParseBatchAsync(inputs, ct);

                    for (var i = 0; i < missingMovies.Count && i < results.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new EnrichmentProgress(3, "LLM fallback", llmProcessed++, llmFallbacks));
                        var parsed = results[i];
                        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Title)) continue;

                        var movie = missingMovies[i];
                        if (parsed.Kind == "episode" && parsed is { Season: { } se, Episode: { } ep })
                        {
                            var show = await ResolveShowAsync(showByTmdbId, parsed.Title, fetchPosters, ct);
                            db.Episodes.Add(new Episode
                            {
                                Show = show,
                                Season = se,
                                Number = ep,
                                Title = parsed.EpisodeTitle,
                                MediaFileId = movie.MediaFileId,
                            });
                            db.Movies.Remove(movie);
                            if (show.TmdbId is { } tid)
                                await EnrichSeasonEpisodesAsync(show, tid, ct);
                            reclassified++;
                            llmRecovered++;
                        }
                        else
                        {
                            movie.Title = parsed.Title;
                            if (parsed.Year is not null) movie.Year = parsed.Year;
                            var (cached, score, _) = await TryTmdbMovieThenTvScoredAsync(parsed.Title, parsed.Year, "movie", fetchPosters, ct);
                            if (cached is not null && score >= AutoApplyThreshold)
                            {
                                ApplyToMovie(movie, cached);
                                if (cached.PosterFile is not null) posters++;
                                llmRecovered++;
                            }
                        }
                    }
                }

                // Seriály (obvykle jeden na složku, ale pro konzistenci stejný pattern)
                if (missingShows.Count > 0)
                {
                    // Seriály nemají jednoznačnou cestu, pošleme všechny jako jednu dávku
                    var showInputs = missingShows.Select(s => new LlmParseInput(s.Title, null, s.Title, "tv")).ToList();
                    var showResults = await llm.ParseBatchAsync(showInputs, ct);
                    for (var i = 0; i < missingShows.Count && i < showResults.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new EnrichmentProgress(3, "LLM fallback", llmProcessed++, llmFallbacks));
                        var parsed = showResults[i];
                        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Title)) continue;

                        var show = missingShows[i];
                        var (cached, score, _) = await TryTmdbScoredAsync(parsed.Title, null, "tv", fetchPosters, ct);
                        if (cached is not null && score >= ReviewThreshold)
                        {
                            ApplyToShow(show, cached);
                            if (cached.PosterFile is not null) posters++;
                            llmRecovered++;
                            if (score < AutoApplyThreshold) reviewQueue++;
                            if (cached.TmdbId is { } tid)
                                await EnrichSeasonEpisodesAsync(show, tid, ct);
                        }
                    }
                }
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);

        // --- Fáze 4: sjednocení duplicitních shows se stejným TmdbId + úklid prázdných shows ---
        progress?.Report(new EnrichmentProgress(4, "Úklid", 0, 0));
        var merged = await MergeShowsByTmdbIdAsync(ct);
        var emptyDeleted = await DeleteEmptyShowsAsync(ct);
        if (merged > 0 || emptyDeleted > 0)
            log.LogInformation("Fáze 4: sloučeno {Merged} duplicitních shows, smazáno {Deleted} prázdných", merged, emptyDeleted);

        await transaction.CommitAsync(ct);

        sw.Stop();

        // Spočítej skutečné misses (položky co stále nemají TmdbId a nejsou manual)
        var finalMovieMisses = await db.Movies.CountAsync(m => !m.IsManual && m.TmdbId == null, ct);
        var finalShowMisses = await db.Shows.CountAsync(s => !s.IsManual && s.TmdbId == null, ct);
        var tmdbMisses = finalMovieMisses + finalShowMisses;

        log.LogInformation(
            "Enrichment: {Hits} TMDB, {LLM}→{Rec}, {Recl} reclass, {Posters} posterů, {RQ} v review, {Miss} stále chybí ({Ms} ms)",
            hits, llmFallbacks, llmRecovered, reclassified, posters, reviewQueue, tmdbMisses, sw.ElapsedMilliseconds);

        return new EnrichmentSummary(hits, tmdbMisses, llmFallbacks, llmRecovered, reclassified, posters, skipped, sw.ElapsedMilliseconds, reviewQueue);
    }

    /// <summary>Episode enrichment přes season API — jeden call na sezónu.</summary>
    private async Task EnrichSeasonEpisodesAsync(Show show, int tmdbId, CancellationToken ct)
    {
        var seasons = show.Episodes
            .GroupBy(e => e.Season)
            .Select(g => g.Key)
            .ToList();

        foreach (var season in seasons)
        {
            ct.ThrowIfCancellationRequested();

            var episodeMap = await seasonCache.GetAsync(tmdbId, season, ct, forceRefresh: _forceRefresh);
            if (episodeMap.Count == 0) continue;

            // Aplikuj názvy epizod z TMDB
            foreach (var ep in show.Episodes.Where(e => e.Season == season))
            {
                if (episodeMap.TryGetValue(ep.Number, out var tmdbEp) && tmdbEp.Title is { Length: > 0 } title)
                {
                    if (string.IsNullOrWhiteSpace(ep.Title))
                        ep.Title = title;
                }
            }
        }
    }

    private (int TmdbId, string MediaType)? ResolveAlias(
        List<MatchAlias> aliases, string title, string? filePath, string expectedType, IReadOnlyCollection<string>? roots = null)
    {
        // Nejdřív zkus nejbližší rodičovskou složku
        if (filePath is not null)
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (dirName is not null)
            {
                var folderAlias = aliases.FirstOrDefault(a => a.Key == $"folder:{dirName}");
                if (folderAlias is not null) return (folderAlias.TmdbId, folderAlias.MediaType);
            }

            // Pak content folder (u seriálů se nejbližší rodič obvykle liší od Season-skipnutého content folderu)
            var contentFolder = SeasonFolderDetector.GetContentFolderFromPath(filePath, roots);
            if (contentFolder is not null && !string.Equals(contentFolder, dirName, StringComparison.OrdinalIgnoreCase))
            {
                var contentAlias = aliases.FirstOrDefault(a => a.Key == $"folder:{contentFolder}");
                if (contentAlias is not null) return (contentAlias.TmdbId, contentAlias.MediaType);
            }
        }

        // Pak normalizovaný title
        var norm = MatchScorer.Normalize(title);
        var titleAlias = aliases.FirstOrDefault(a => a.Key == $"title:{norm}");
        if (titleAlias is not null) return (titleAlias.TmdbId, titleAlias.MediaType);

        return null;
    }

    /// <summary>Skórovaný TMDB search: vyber nejlepšího kandidáta s vahou. Vrací i kandidáty (pro LLM disambiguaci).</summary>
    private async Task<(TmdbCache? best, double score, IReadOnlyList<TmdbSearchResult> candidates)> TryTmdbScoredAsync(
        string title, int? year, string mediaType, bool fetchPosters, CancellationToken ct)
    {
        var queryKey = NormalizeQueryKey(title, year, mediaType);
        var cached = await FindTmdbCacheAsync(queryKey, ct);
        if (!_forceRefresh && cached is not null && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            return (cached.TmdbId is not null ? cached : null, cached.TmdbId is not null ? (cached.Score ?? 1.0) : 0.0, []);

        // Načti kandidáty
        var candidates = await tmdb.SearchCandidatesAsync(title, mediaType, ct);
        if (candidates.Count == 0) return (null, 0.0, []);

        // Vyber nejlepšího podle skóre
        TmdbSearchResult? bestCandidate = null;
        double bestScore = 0;
        foreach (var c in candidates)
        {
            var s = MatchScorer.Score(c, title, year, mediaType);
            if (s > bestScore) { bestScore = s; bestCandidate = c; }
        }

        if (bestCandidate is null) return (null, 0.0, candidates);

        // Stáhni detail (pro poster, overview atd.)
        var detail = await tmdb.GetDetailsAsync(bestCandidate.TmdbId, mediaType, ct);
        if (detail is null) return (null, 0.0, candidates);

        string? posterFile = null;
        if (fetchPosters && detail.PosterPath is not null)
            posterFile = await tmdb.DownloadPosterAsync(detail.PosterPath, $"{detail.TmdbId}.jpg", ct);

        if (cached is null)
        {
            cached = new TmdbCache { QueryKey = queryKey };
            db.TmdbCaches.Add(cached);
        }
        cached.TmdbId = detail.TmdbId;
        cached.MediaType = detail.MediaType;
        cached.Title = detail.Title;
        cached.Overview = detail.Overview;
        cached.Rating = detail.Rating;
        cached.Genres = GenreFormat.ExtractNames(detail.Genres);
        cached.ReleaseYear = detail.ReleaseYear;
        cached.PosterFile = posterFile;
        cached.Score = bestScore;
        cached.FetchedAt = DateTime.UtcNow;

        log.LogInformation("TMDB scored '{Title}' ({Type}) → '{Best}' id={Id} score={Score:F2}",
            title, mediaType, bestCandidate.Title, bestCandidate.TmdbId, bestScore);

        return (cached, bestScore, candidates);
    }

    /// <summary>Zkusí film; pokud nenajde vůbec nic, zkusí seriál (levný fallback). Vyhýbá se zbytečným API callům.</summary>
    private async Task<(TmdbCache? best, double score, IReadOnlyList<TmdbSearchResult> candidates)> TryTmdbMovieThenTvScoredAsync(
        string title, int? year, string mediaType, bool fetchPosters, CancellationToken ct)
    {
        var (movie, movieScore, movieCandidates) = await TryTmdbScoredAsync(title, year, "movie", fetchPosters, ct);
        // Pokud film našel něco s alespoň review skóre, ber ho
        if (movie is not null && movieScore >= ReviewThreshold)
            return (movie, movieScore, movieCandidates);
        // TV fallback jen pokud movie nenašlo VŮBEC nic (nebo je skóre hrozné)
        if (movie is null || movieScore < 0.30)
        {
            var (tv, tvScore, tvCandidates) = await TryTmdbScoredAsync(title, null, "tv", fetchPosters, ct);
            if (tv is not null && tvScore > movieScore)
                return (tv, tvScore, tvCandidates);
        }
        return (movie, movieScore, movieCandidates);
    }

    /// <summary>Zkusí TMDB "tv" search pro víc titulových variant, vrátí nejlepší skóre; skončí dřív při jistém zásahu.</summary>
    private async Task<(TmdbCache? best, double score, IReadOnlyList<TmdbSearchResult> candidates)> TryBestTmdbScoredVariantAsync(
        IReadOnlyList<string> titleVariants, bool fetchPosters, CancellationToken ct)
    {
        TmdbCache? bestCached = null;
        var bestScore = 0.0;
        IReadOnlyList<TmdbSearchResult> bestCandidates = [];
        foreach (var variant in titleVariants)
        {
            var (cached, score, candidates) = await TryTmdbScoredAsync(variant, null, "tv", fetchPosters, ct);
            if (score > bestScore) { bestCached = cached; bestScore = score; bestCandidates = candidates; }
            if (bestScore >= AutoApplyThreshold) break;
        }
        return (bestCached, bestScore, bestCandidates);
    }

    /// <summary>
    /// Zařadí film s nejistou TMDB shodou do fronty pro LLM disambiguaci (Fáze 2.5).
    /// Stejný dotaz (titul|rok) se LLM posílá jen jednou — rozhodnutí se pak aplikuje na
    /// všechny soubory, které ho sdílejí. Vrací false (a nic nezařadí), když se nepodaří
    /// sehnat kandidáty — volající pak aplikuje původní nejistou shodu rovnou.
    /// </summary>
    private async Task<bool> TryQueuePendingMovieChoiceAsync(
        List<LlmChooseInput> chooseInputs, Dictionary<string, List<Movie>> pendingMovies,
        Dictionary<string, string> movieChoiceKeys,
        Movie movie, string parsedTitle, int? parsedYear, IReadOnlyList<TmdbSearchResult> candidates,
        IReadOnlyCollection<string> roots, CancellationToken ct)
    {
        var queryKey = NormalizeQueryKey(parsedTitle, parsedYear, "movie");
        if (movieChoiceKeys.TryGetValue(queryKey, out var existingKey))
        {
            pendingMovies[existingKey].Add(movie);
            return true;
        }

        if (candidates.Count == 0)
            candidates = await tmdb.SearchCandidatesAsync(parsedTitle, "movie", ct);
        if (candidates.Count == 0) return false;

        var itemKey = $"movie:{movie.Id}";
        movieChoiceKeys[queryKey] = itemKey;
        pendingMovies[itemKey] = [movie];
        chooseInputs.Add(new LlmChooseInput(
            itemKey, parsedTitle, parsedYear, "movie",
            SeasonFolderDetector.GetContentFolderFromPath(movie.MediaFile.Path, roots),
            movie.MediaFile.FileName,
            BuildLlmCandidates(candidates)));
        return true;
    }

    /// <summary>Jako <see cref="TryQueuePendingMovieChoiceAsync"/>, ale pro seriály.</summary>
    private async Task<bool> TryQueuePendingShowChoiceAsync(
        List<LlmChooseInput> chooseInputs, Dictionary<string, Show> pendingShows,
        Show show, string parsedTitle, IReadOnlyList<TmdbSearchResult> candidates,
        IReadOnlyCollection<string> roots, CancellationToken ct)
    {
        if (candidates.Count == 0)
            candidates = await tmdb.SearchCandidatesAsync(parsedTitle, "tv", ct);
        if (candidates.Count == 0) return false;

        var itemKey = $"show:{show.Id}";
        pendingShows[itemKey] = show;
        var firstEpisode = show.Episodes.FirstOrDefault();
        chooseInputs.Add(new LlmChooseInput(
            itemKey, parsedTitle, null, "tv",
            firstEpisode is not null ? SeasonFolderDetector.GetContentFolderFromPath(firstEpisode.MediaFile.Path, roots) : null,
            firstEpisode?.MediaFile.FileName,
            BuildLlmCandidates(candidates)));
        return true;
    }

    private static List<LlmCandidate> BuildLlmCandidates(IReadOnlyList<TmdbSearchResult> candidates) =>
        candidates.Take(5).Select(c => new LlmCandidate(
            c.TmdbId, c.Title, c.ReleaseYear, c.MediaType,
            c.Overview is { Length: > 200 } ov ? ov[..200] : c.Overview)).ToList();

    /// <summary>
    /// Najde TmdbCache podle QueryKey — nejdřív v EF change trackeru (nově přidané, ještě
    /// neuložené řádky DB dotaz nevidí → hrozila by duplicitní QueryKey), pak v DB.
    /// </summary>
    private async Task<TmdbCache?> FindTmdbCacheAsync(string queryKey, CancellationToken ct)
    {
        var local = db.TmdbCaches.Local.FirstOrDefault(c => c.QueryKey == queryKey);
        if (local is not null) return local;
        return await db.TmdbCaches.FirstOrDefaultAsync(c => c.QueryKey == queryKey, ct);
    }

    /// <summary>Zapíše rozhodnutí LLM disambiguace do TmdbCache (Score=0.95), aby ho příští scan/enrichment znovu nezkoušel.</summary>
    private async Task UpsertTmdbCacheChoiceAsync(string title, int? year, string mediaType, TmdbSearchResult detail, CancellationToken ct)
    {
        var queryKey = NormalizeQueryKey(title, year, mediaType);
        var cached = await FindTmdbCacheAsync(queryKey, ct);
        if (cached is null)
        {
            cached = new TmdbCache { QueryKey = queryKey };
            db.TmdbCaches.Add(cached);
        }
        cached.TmdbId = detail.TmdbId;
        cached.MediaType = detail.MediaType;
        cached.Title = detail.Title;
        cached.Overview = detail.Overview;
        cached.Rating = detail.Rating;
        cached.Genres = GenreFormat.ExtractNames(detail.Genres);
        cached.ReleaseYear = detail.ReleaseYear;
        cached.Score = 0.95;
        cached.FetchedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Sloučí shows se stejným TmdbId (typicky vzniklé z různých názvů složek stejného seriálu,
    /// např. "South Park" + "south park 27") do jednoho — přesune epizody, doplní chybějící
    /// metadata, smaže duplikáty. Přeživší: IsManual > nejvíc epizod > nejnižší Id.
    /// </summary>
    private async Task<int> MergeShowsByTmdbIdAsync(CancellationToken ct)
    {
        var shows = await db.Shows
            .Include(s => s.Episodes)
            .Where(s => s.TmdbId != null)
            .ToListAsync(ct);

        var groups = shows.GroupBy(s => s.TmdbId!.Value).Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0) return 0;

        var mergedCount = 0;
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(s => s.IsManual)
                .ThenByDescending(s => s.Episodes.Count)
                .ThenBy(s => s.Id)
                .ToList();
            var survivor = ordered[0];
            var duplicates = ordered.Skip(1).ToList();

            foreach (var duplicate in duplicates)
            {
                foreach (var ep in duplicate.Episodes.ToList())
                    ep.ShowId = survivor.Id;

                survivor.DisplayTitle ??= duplicate.DisplayTitle;
                survivor.Overview ??= duplicate.Overview;
                survivor.PosterFile ??= duplicate.PosterFile;
                survivor.Rating ??= duplicate.Rating;
                survivor.Genres ??= duplicate.Genres;
                survivor.Cast ??= duplicate.Cast;
                survivor.ImdbId ??= duplicate.ImdbId;
                survivor.SoftTmdbId ??= duplicate.SoftTmdbId;
                survivor.IsManual |= duplicate.IsManual;
            }

            await db.SaveChangesAsync(ct); // epizody přesunuty dřív, než duplikáty smažeme (FK)

            foreach (var duplicate in duplicates)
            {
                await db.ManualMatches
                    .Where(m => m.Key == ManualMatchService.ShowKeyPrefix + duplicate.Title)
                    .ExecuteDeleteAsync(ct);
                db.Shows.Remove(duplicate);
                mergedCount++;
            }

            log.LogInformation("Sloučen show '{Survivor}' (tmdbId {Id}) ← {Duplicates}",
                survivor.Title, survivor.TmdbId, string.Join(", ", duplicates.Select(d => $"'{d.Title}'")));
        }

        await db.SaveChangesAsync(ct);
        return mergedCount;
    }

    /// <summary>Smaže shows bez epizod — zbytky po reklasifikaci film→epizoda nebo po merge.</summary>
    private async Task<int> DeleteEmptyShowsAsync(CancellationToken ct)
    {
        var empty = await db.Shows.Where(s => !s.Episodes.Any()).ToListAsync(ct);
        if (empty.Count == 0) return 0;

        foreach (var show in empty)
            log.LogInformation("Mažu prázdný show '{Title}' (0 epizod)", show.Title);

        db.Shows.RemoveRange(empty);
        await db.SaveChangesAsync(ct);
        return empty.Count;
    }

    /// <summary>
    /// Odvodí titulové varianty pro TMDB vyhledávání seriálu: raw titul, části kombinovaného
    /// CZ/EN názvu ("A = B"), a nejčastější titul odvozený z názvů souborů epizod (fallback,
    /// když je titul odvozený ze složky nepoužitelný, např. český název seriálu ze zahraniční
    /// zkratky složky "HIMYM").
    /// </summary>
    private static List<string> BuildShowTitleVariants(Show show)
    {
        var variants = new List<string>();

        void AddDistinct(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (!variants.Any(v => string.Equals(v, candidate, StringComparison.OrdinalIgnoreCase)))
                variants.Add(candidate);
        }

        AddDistinct(show.Title);
        foreach (var part in NameCleaner.SplitAlternativeTitles(show.Title))
            AddDistinct(part);

        var filenameTitle = show.Episodes
            .Take(5)
            .Select(e => EpisodePatterns.TitleBeforeToken(Path.GetFileNameWithoutExtension(e.MediaFile.FileName)))
            .Where(t => t is not null)
            .GroupBy(t => t!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        AddDistinct(filenameTitle);

        return variants;
    }

    private async Task<Show> ResolveShowAsync(Dictionary<int, Show> byTmdbId, string llmTitle, bool fetchPosters, CancellationToken ct)
    {
        var (cached, score, _) = await TryTmdbScoredAsync(llmTitle, null, "tv", fetchPosters, ct);

        if (cached?.TmdbId is { } tmdbId)
        {
            if (byTmdbId.TryGetValue(tmdbId, out var hit)) return hit;
            var existing = db.Shows.Local.FirstOrDefault(s => s.TmdbId == tmdbId)
                ?? await db.Shows.FirstOrDefaultAsync(s => s.TmdbId == tmdbId, ct);
            if (existing is not null) { byTmdbId[tmdbId] = existing; return existing; }

            // Titul může kolidovat s jiným (zatím TMDB-nematchnutým) Show — unikátní index na Title.
            var byTitle = await FindShowByTitleAsync(cached.Title ?? llmTitle, ct);
            if (byTitle is not null)
            {
                ApplyToShow(byTitle, cached);
                byTmdbId[tmdbId] = byTitle;
                return byTitle;
            }

            var show = new Show { Title = cached.Title ?? llmTitle };
            db.Shows.Add(show);
            ApplyToShow(show, cached);
            byTmdbId[tmdbId] = show;
            return show;
        }

        var fallbackExisting = await FindShowByTitleAsync(llmTitle, ct);
        if (fallbackExisting is not null) return fallbackExisting;

        var fallback = new Show { Title = llmTitle };
        db.Shows.Add(fallback);
        return fallback;
    }

    /// <summary>
    /// Najde Show podle titulu — nejdřív v EF change trackeru (nově přidané, ještě neuložené
    /// shows z Fáze 3 reklasifikace DB dotaz nevidí → hrozil duplicitní Title), pak v DB.
    /// </summary>
    private async Task<Show?> FindShowByTitleAsync(string title, CancellationToken ct)
    {
        var local = db.Shows.Local.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));
        if (local is not null) return local;
        return await db.Shows.FirstOrDefaultAsync(s => s.Title == title, ct);
    }

    private static void ApplyToMovie(Movie m, TmdbCache c)
    {
        m.DisplayTitle = c.Title;
        m.TmdbId = c.TmdbId;
        m.Overview = c.Overview;
        m.Rating = c.Rating;
        m.Genres = c.Genres;
        m.PosterFile = c.PosterFile;
    }

    private static void ClearMetadata(Movie movie)
    {
        movie.TmdbId = null;
        movie.DisplayTitle = null;
        movie.PosterFile = null;
        movie.Overview = null;
        movie.Rating = null;
        movie.Genres = null;
        movie.Cast = null;
    }

    private static void ClearMetadata(Show show)
    {
        show.TmdbId = null;
        show.DisplayTitle = null;
        show.PosterFile = null;
        show.Overview = null;
        show.Rating = null;
        show.Genres = null;
        show.Cast = null;
    }

    private static void ApplyToShow(Show s, TmdbCache c)
    {
        s.DisplayTitle = c.Title;
        s.TmdbId = c.TmdbId;
        s.Overview = c.Overview;
        s.Rating = c.Rating;
        s.Genres = c.Genres;
        s.PosterFile = c.PosterFile;
    }

    private async Task ApplyToMovieAsync(Movie m, TmdbSearchResult d, bool fetchPosters, CancellationToken ct)
    {
        m.DisplayTitle = d.Title;
        m.TmdbId = d.TmdbId;
        m.Overview = d.Overview;
        m.Rating = d.Rating;
        m.Genres = GenreFormat.ExtractNames(d.Genres);
        if (fetchPosters && d.PosterPath is not null)
            m.PosterFile = await tmdb.DownloadPosterAsync(d.PosterPath, $"{d.TmdbId}.jpg", ct);
    }

    private async Task ApplyToShowAsync(Show s, TmdbSearchResult d, bool fetchPosters, CancellationToken ct)
    {
        s.DisplayTitle = d.Title;
        s.TmdbId = d.TmdbId;
        s.Overview = d.Overview;
        s.Rating = d.Rating;
        s.Genres = GenreFormat.ExtractNames(d.Genres);
        if (fetchPosters && d.PosterPath is not null)
            s.PosterFile = await tmdb.DownloadPosterAsync(d.PosterPath, $"{d.TmdbId}.jpg", ct);
    }

    private static string NormalizeQueryKey(string title, int? year, string type) =>
        $"{MatchScorer.Normalize(title)}|{year}|{type}";
}
