using LSP.Server.Data;
using LSP.Server.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Api;

public sealed record SaveProgressRequest(int MediaFileId, double PositionSeconds, double? DurationSeconds,
    Guid? SessionId = null, long? Sequence = null);
public sealed record ProgressDto(double PositionSeconds, double? DurationSeconds, bool Finished);

public sealed record ContinueItemDto(
    int MediaFileId,
    string Kind,            // "movie" | "episode"
    int? ShowId,
    string Title,
    string? Subtitle,
    string? PosterUrl,
    double PositionSeconds,
    double? DurationSeconds,
    int Percent);

public static class ProgressEndpoints
{
    // Považuj za dokoukané od 92 % délky (vyhne se to nekonečným titulkům na konci).
    private const double FinishedThreshold = 0.92;

    public static void MapProgressEndpoints(this WebApplication app)
    {
        app.MapPost("/api/progress", SaveProgress);
        app.MapGet("/api/progress/{mediaFileId:int}", GetProgress);
        app.MapDelete("/api/progress/{mediaFileId:int}", DeleteProgress);
        app.MapDelete("/api/progress/show/{showId:int}", DeleteShowProgress);
        app.MapGet("/api/continue-watching", GetContinueWatching);
    }

    private static async Task<IResult> SaveProgress(
        SaveProgressRequest req, LibraryDbContext db, ProgressWriteCoordinator writers, CancellationToken ct)
    {
        if (!double.IsFinite(req.PositionSeconds) || req.PositionSeconds < 0 ||
            (req.DurationSeconds is { } duration && (!double.IsFinite(duration) || duration <= 0)) ||
            (req.SessionId.HasValue != req.Sequence.HasValue) || req.SessionId == Guid.Empty || req.Sequence <= 0)
            return Results.BadRequest("Neplatná pozice nebo identifikace přehrávání.");

        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == req.MediaFileId, ct);
        if (file is null) return Results.NotFound();

        return await writers.WithLockAsync<IResult>(file.Path, async state =>
        {
            if (!state.CanWrite(req.SessionId, req.Sequence))
                return Results.Conflict("Pozice pochází ze staršího přehrávání nebo požadavku.");
            var progress = await db.PlaybackProgress.FirstOrDefaultAsync(p => p.Path == file.Path, ct);
            if (progress is null)
            {
                progress = new PlaybackProgress { Path = file.Path };
                db.PlaybackProgress.Add(progress);
            }

            progress.PositionSeconds = req.PositionSeconds;
            progress.DurationSeconds = req.DurationSeconds;
            progress.Finished = req.DurationSeconds is > 0 && req.PositionSeconds >= req.DurationSeconds * FinishedThreshold;
            progress.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            state.Commit(req.Sequence);
            return Results.NoContent();
        }, ct);
    }

    private static async Task<IResult> GetProgress(int mediaFileId, Guid? session, LibraryDbContext db,
        ProgressWriteCoordinator writers, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaFileId, ct);
        if (file is null) return Results.NotFound();

        return await writers.WithLockAsync<IResult>(file.Path, async state =>
        {
            var progress = await db.PlaybackProgress.FirstOrDefaultAsync(p => p.Path == file.Path, ct);
            ct.ThrowIfCancellationRequested();
            if (session is { } id && !state.Begin(id))
                return Results.Conflict("Toto přehrávání už bylo nahrazeno novějším.");
            return progress is null ? Results.NoContent()
                : Results.Ok(new ProgressDto(progress.PositionSeconds, progress.DurationSeconds, progress.Finished));
        }, ct);
    }

    /// <summary>Smaže progress jednoho souboru → zmizí z „Pokračovat v přehrávání".</summary>
    private static async Task<IResult> DeleteProgress(
        int mediaFileId, LibraryDbContext db, TranscodeSessionManager sessions, ProgressWriteCoordinator writers, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaFileId, ct);
        if (file is null) return Results.NotFound();

        await writers.WithLockAsync(file.Path, async state =>
        {
            await db.PlaybackProgress.Where(p => p.Path == file.Path).ExecuteDeleteAsync(ct);
            state.Revoke();
            return true;
        }, ct);
        sessions.PurgeSegments(mediaFileId);
        return Results.NoContent();
    }

    /// <summary>Smaže progress všech epizod seriálu → celý seriál zmizí z „Pokračovat v přehrávání".</summary>
    private static async Task<IResult> DeleteShowProgress(
        int showId, LibraryDbContext db, TranscodeSessionManager sessions, ProgressWriteCoordinator writers, CancellationToken ct)
    {
        var files = await db.Episodes.Where(e => e.ShowId == showId)
            .Select(e => new { e.MediaFileId, e.MediaFile.Path }).ToListAsync(ct);
        foreach (var f in files)
        {
            await writers.WithLockAsync(f.Path, async state =>
            {
                await db.PlaybackProgress.Where(p => p.Path == f.Path).ExecuteDeleteAsync(ct);
                state.Revoke();
                return true;
            }, ct);
            sessions.PurgeSegments(f.MediaFileId);
        }
        return Results.NoContent();
    }

    // Co považujeme za „reálně rozkoukané" (vyřazuje náhodné krátké otevření).
    private const double StartedThreshold = 5;

    /// <summary>
    /// „Pokračovat v přehrávání" – Netflix model. Filmy: nedokoukané. Seriály: JEDEN záznam na seriál –
    /// poslední smysluplně sledovaná epizoda, nebo další díl po jejím dokončení.
    /// </summary>
    private static async Task<IResult> GetContinueWatching(LibraryDbContext db, CancellationToken ct) =>
        Results.Ok(await BuildContinueItemsAsync(db, ct));

    public static async Task<List<ContinueItemDto>> BuildContinueItemsAsync(LibraryDbContext db, CancellationToken ct)
    {
        var records = await (
            from p in db.PlaybackProgress
            join f in db.MediaFiles on p.Path equals f.Path
            select new
            {
                f.Id,
                MovieId = (int?)(f.Movie != null ? f.Movie.Id : null),
                MovieTitle = f.Movie != null ? (f.Movie.DisplayTitle ?? f.Movie.Title) : null,
                MovieTmdbId = f.Movie != null ? f.Movie.TmdbId : null,
                MovieHasPoster = f.Movie != null && f.Movie.PosterFile != null,
                ShowId = (int?)(f.Episode != null ? f.Episode.ShowId : null),
                ShowTitle = f.Episode != null ? (f.Episode.Show.DisplayTitle ?? f.Episode.Show.Title) : null,
                ShowTmdbId = f.Episode != null ? f.Episode.Show.TmdbId : null,
                ShowHasPoster = f.Episode != null && f.Episode.Show.PosterFile != null,
                Season = f.Episode != null ? f.Episode.Season : 0,
                Number = f.Episode != null ? f.Episode.Number : 0,
                p.PositionSeconds,
                p.DurationSeconds,
                p.Finished,
                p.UpdatedAt,
            }).ToListAsync(ct);

        var items = new List<(DateTime Updated, ContinueItemDto Dto)>();

        // Filmy – přímo nedokoukané.
        foreach (var r in records.Where(r => r.MovieTitle is not null && !r.Finished && r.PositionSeconds > StartedThreshold))
            items.Add((r.UpdatedAt, new ContinueItemDto(
                r.Id, "movie", null, r.MovieTitle!, null, MoviePosterUrl(r.MovieId, r.MovieTmdbId, r.MovieHasPoster),
                r.PositionSeconds, r.DurationSeconds,
                Percent(r.PositionSeconds, r.DurationSeconds))));

        // Seriály – jeden záznam na seriál.
        foreach (var show in records.Where(r => r.ShowId is not null).GroupBy(r => r.ShowId!.Value))
        {
            // Čas aktivity rozlišuje postup vpřed od úmyslného opětovného sledování.
            var latest = show
                .Where(e => e.Finished || e.PositionSeconds > StartedThreshold)
                .OrderByDescending(e => e.UpdatedAt)
                .ThenByDescending(e => e.Season).ThenByDescending(e => e.Number)
                .FirstOrDefault();
            if (latest is null) continue;
            if (!latest.Finished)
            {
                items.Add((latest.UpdatedAt, new ContinueItemDto(
                    latest.Id, "episode", latest.ShowId, latest.ShowTitle!, $"S{latest.Season:D2}E{latest.Number:D2}",
                    ShowPosterUrl(latest.ShowId, latest.ShowTmdbId, latest.ShowHasPoster),
                    latest.PositionSeconds, latest.DurationSeconds, Percent(latest.PositionSeconds, latest.DurationSeconds))));
                continue;
            }

            var lastFinished = latest;

            var next = await db.Episodes
                .Where(e => e.ShowId == show.Key
                            && (e.Season > lastFinished.Season
                                || (e.Season == lastFinished.Season && e.Number > lastFinished.Number)))
                .OrderBy(e => e.Season).ThenBy(e => e.Number)
                .Join(db.MediaFiles, e => e.MediaFileId, f => f.Id,
                    (e, f) => new { f.Id, e.Season, e.Number })
                .FirstOrDefaultAsync(ct);
            if (next is null) continue; // celý seriál dokoukán

            items.Add((lastFinished.UpdatedAt, new ContinueItemDto(
                next.Id, "episode", lastFinished.ShowId, lastFinished.ShowTitle!, $"S{next.Season:D2}E{next.Number:D2}",
                ShowPosterUrl(lastFinished.ShowId, lastFinished.ShowTmdbId, lastFinished.ShowHasPoster),
                0, null, 0)));
        }

        return items.OrderByDescending(i => i.Updated).Take(20).Select(i => i.Dto).ToList();
    }

    private static int Percent(double position, double? duration) =>
        duration is > 0 ? (int)Math.Clamp(position / duration.Value * 100, 0, 100) : 0;

    private static string? MoviePosterUrl(int? movieId, int? tmdbId, bool hasPoster) =>
        hasPoster && movieId is int id ? $"/api/poster/movie/{id}?v={tmdbId}" : null;

    private static string? ShowPosterUrl(int? showId, int? tmdbId, bool hasPoster) =>
        hasPoster && showId is int id ? $"/api/poster/show/{id}?v={tmdbId}" : null;
}
