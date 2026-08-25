using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Api;

public sealed record TmdbCandidateDto(int TmdbId, string Title, int? Year, string? PosterUrl, string? Overview);
public sealed record SeasonEpisodeDto(int Number, string? Title);
public sealed record TvEpisodeDto(int Season, int Number, string? Title);
public sealed record ManualMatchRequest(int MediaFileId, string Kind, int TmdbId, string MediaType, int? Season, int? Episode, string? ShowTitle);
public sealed record ManualMatchShowRequest(int ShowId, int TmdbId);

public sealed record ReviewItemDto(
    int Id, int MediaFileId, string Kind, string Title, int? Year, string FileName, int? TmdbId,
    string? MediaType, string? FilePath, int? Season, int? Episode,
    int? ShowId, string? ShowTitle);

/// <summary>Stav enrichment jobu pro UI polling. State: "idle" | "running" | "completed" | "failed" | "cancelled".</summary>
public sealed record EnrichStatusDto(
    string State, bool? Started, bool Force,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    EnrichmentProgress? Progress, EnrichmentSummary? Summary);

public static class EnrichmentEndpoints
{
    public static void MapEnrichmentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/enrich", StartEnrich);
        app.MapGet("/api/enrich/status", GetEnrichStatus);
        app.MapPost("/api/enrich/cancel", CancelEnrich);
        app.MapGet("/api/poster/movie/{id:int}", GetMoviePoster);
        app.MapGet("/api/poster/show/{id:int}", GetShowPoster);

        app.MapGet("/api/tmdb/search", SearchTmdb);
        app.MapGet("/api/tmdb/tv/{tmdbId:int}/season/{season:int}/episodes", GetSeasonEpisodes);
        app.MapGet("/api/tmdb/tv/{tmdbId:int}/episodes", GetAllEpisodes);
        app.MapPost("/api/library/manual-match", ManualMatchFile);
        app.MapPost("/api/library/manual-match-show", ManualMatchShow);
        app.MapPost("/api/library/lazy-show/{showId:int}", ApplyLazyShow);
        app.MapPost("/api/library/manual-match/reset-file/{mediaFileId:int}", ResetManualFile);
        app.MapPost("/api/library/manual-match/reset-show/{showId:int}", ResetManualShow);
        app.MapPost("/api/library/delete-record/show/{showId:int}", DeleteRecordShow);
        app.MapPost("/api/library/delete-record/file/{mediaFileId:int}", DeleteRecordFile);

        app.MapGet("/api/review-queue", GetReviewQueue);
    }

    private static async Task<IResult> GetReviewQueue(LibraryDbContext db, CancellationToken ct)
    {
        var movies = await db.Movies
            .Where(m => !m.IsManual && m.TmdbId == null)
            .Select(m => new ReviewItemDto(
                m.Id, m.MediaFileId, "movie", m.DisplayTitle ?? m.Title, m.Year,
                m.MediaFile.FileName, m.TmdbId, "movie",
                m.MediaFile.Path, null, null, null, null))
            .ToListAsync(ct);

        var shows = await db.Shows
            .Where(s => !s.IsManual && s.TmdbId == null)
            .Select(s => new ReviewItemDto(
                s.Id, 0, "show", s.DisplayTitle ?? s.Title, null,
                "", s.TmdbId, "tv",
                null, null, null, s.Id, s.DisplayTitle ?? s.Title))
            .ToListAsync(ct);

        return Results.Ok(new { movies, shows });
    }

    /// <summary>Spustí enrichment na pozadí (nebo, pokud už jeden běží, vrátí jeho aktuální stav) a vrátí hned.</summary>
    private static IResult StartEnrich(
        EnrichmentJobService jobs,
        bool force)
    {
        var started = jobs.TryStart(force, out var status);
        return !started && status.State != EnrichmentJobState.Running
            ? Results.Conflict("Bezi jina operace nad knihovnou.")
            : Results.Ok(ToDto(status, started));
    }

    private static IResult GetEnrichStatus(EnrichmentJobService jobs) => Results.Ok(ToDto(jobs.GetStatus(), null));

    private static IResult CancelEnrich(EnrichmentJobService jobs)
    {
        jobs.Cancel();
        return Results.Ok(ToDto(jobs.GetStatus(), null));
    }

    private static EnrichStatusDto ToDto(EnrichmentJobStatus status, bool? started) => new(
        status.State.ToString().ToLowerInvariant(), started, status.Force,
        status.StartedAt, status.FinishedAt, status.Progress, status.Summary);

    private static async Task<IResult> SearchTmdb(string q, string type, IMetadataProvider tmdb, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<TmdbCandidateDto>());
        if (type is not ("movie" or "tv")) type = "movie";

        try
        {
            var results = await tmdb.SearchCandidatesAsync(q, type, ct);
            var dtos = results.Select(r => new TmdbCandidateDto(
                r.TmdbId, r.Title, r.ReleaseYear,
                r.PosterPath is not null ? $"https://image.tmdb.org/t/p/w200{r.PosterPath}" : null,
                r.Overview)).ToList();
            return Results.Ok(dtos);
        }
        catch (TmdbUnauthorizedException)
        {
            return Results.BadRequest("Neplatný TMDB API klíč.");
        }
    }

    private static async Task<IResult> GetSeasonEpisodes(
        int tmdbId,
        int season,
        SeasonEpisodeCache cache,
        LibraryDbContext db,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Bezi jina operace nad knihovnou.");
        using (lease)
        {
            try
            {
                var map = await cache.GetAsync(tmdbId, season, ct);
                await db.SaveChangesAsync(ct); // persistuj čerstvě staženou sezónu do cache
                var episodes = map.Values
                    .OrderBy(e => e.EpisodeNumber)
                    .Select(e => new SeasonEpisodeDto(e.EpisodeNumber, e.Title))
                    .ToList();
                return Results.Ok(episodes);
            }
            catch (TmdbUnauthorizedException)
            {
                return Results.BadRequest("Neplatný TMDB API klíč.");
            }
        }
    }

    private static async Task<IResult> GetAllEpisodes(
        int tmdbId,
        SeasonEpisodeCache cache,
        LibraryDbContext db,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Bezi jina operace nad knihovnou.");
        using (lease)
        {
            try
            {
                var all = await cache.GetAllAsync(tmdbId, ct);
                await db.SaveChangesAsync(ct); // persistuj čerstvě stažené sezóny do cache
                var dtos = all.Select(e => new TvEpisodeDto(e.Season, e.Number, e.Title)).ToList();
                return Results.Ok(dtos);
            }
            catch (TmdbUnauthorizedException)
            {
                return Results.BadRequest("Neplatný TMDB API klíč.");
            }
        }
    }

    private static async Task<IResult> ManualMatchFile(
        ManualMatchRequest req,
        ManualMatchService manual,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Bezi jina operace nad knihovnou.");
        using (lease)
        {
            if (req.Kind == "lazy-episode")
            {
                if (string.IsNullOrWhiteSpace(req.ShowTitle)) return Results.BadRequest("Lazy epizoda vyžaduje ShowTitle.");
                // TmdbId > 0 = měkké napojení (banner + název); jinak čistě lazy.
                await manual.ApplyLazyEpisodeAsync(req.MediaFileId, req.ShowTitle.Trim(), req.TmdbId, req.Season, req.Episode, ct);
                return Results.NoContent();
            }
            if (req.Kind is not ("movie" or "episode")) return Results.BadRequest("Kind musí být movie, episode nebo lazy-episode.");
            await manual.ApplyFileAsync(req.MediaFileId, req.Kind, req.TmdbId, req.MediaType, req.Season, req.Episode, ct);
            return Results.NoContent();
        }
    }

    private static async Task<IResult> ManualMatchShow(
        ManualMatchShowRequest req,
        ManualMatchService manual,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Bezi jina operace nad knihovnou.");
        using (lease)
        {
            await manual.ApplyShowAsync(req.ShowId, req.TmdbId, ct);
            return Results.NoContent();
        }
    }

    private static async Task<IResult> ApplyLazyShow(
        int showId, ManualMatchService manual, LibraryOperationCoordinator operations, CancellationToken ct) =>
        await RunMaintenanceAsync(operations, () => manual.ApplyLazyShowAsync(showId, ct));

    private static async Task<IResult> ResetManualFile(
        int mediaFileId, ManualMatchService manual, LibraryOperationCoordinator operations, CancellationToken ct) =>
        await RunMaintenanceAsync(operations, () => manual.ResetFileAsync(mediaFileId, ct));

    private static async Task<IResult> ResetManualShow(
        int showId, ManualMatchService manual, LibraryOperationCoordinator operations, CancellationToken ct) =>
        await RunMaintenanceAsync(operations, () => manual.ResetShowAsync(showId, ct));

    private static async Task<IResult> DeleteRecordShow(
        int showId, ManualMatchService manual, LibraryOperationCoordinator operations, CancellationToken ct) =>
        await RunMaintenanceAsync(operations, () => manual.DeleteRecordShowAsync(showId, ct));

    private static async Task<IResult> DeleteRecordFile(
        int mediaFileId, ManualMatchService manual, LibraryOperationCoordinator operations, CancellationToken ct) =>
        await RunMaintenanceAsync(operations, () => manual.DeleteRecordFileAsync(mediaFileId, ct));

    private static async Task<IResult> RunMaintenanceAsync(
        LibraryOperationCoordinator operations, Func<Task> action)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Bezi jina operace nad knihovnou.");
        using (lease)
        {
            await action();
            return Results.NoContent();
        }
    }

    private static async Task<IResult> GetMoviePoster(int id, LibraryDbContext db, CancellationToken ct)
    {
        var movie = await db.Movies.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (movie?.PosterFile is null || !File.Exists(movie.PosterFile))
            return Results.NotFound();
        return Results.File(movie.PosterFile, "image/jpeg");
    }

    private static async Task<IResult> GetShowPoster(int id, LibraryDbContext db, CancellationToken ct)
    {
        var show = await db.Shows.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (show?.PosterFile is null || !File.Exists(show.PosterFile))
            return Results.NotFound();
        return Results.File(show.PosterFile, "image/jpeg");
    }
}
