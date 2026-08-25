using System.Runtime.InteropServices;
using LSP.Server.Data;
using LSP.Server.Library;
using LSP.Server.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Api;

public sealed record LibraryFolderDto(int Id, string Path);
public sealed record AddLibraryFolderRequest(string Path);
public sealed record SavePlayerAudioLanguageRequest(string? Language);

public sealed record ConfigDto(
    bool HasTmdbKey,
    string LlmProvider,
    string LlmModel,
    bool HasLlmKey,
    string PlanMode,
    bool EnrichAuto,
    FetchPreferencesDto Fetch);

public sealed record FetchPreferencesDto(
    bool Posters, bool Backdrops, bool Overview,
    bool Rating, bool Genres, bool Cast);

public sealed record SaveConfigRequest(
    string? TmdbApiKey,
    string? LlmProvider,
    string? LlmModel,
    string? LlmApiKey,
    string? PlanMode,
    bool? EnrichAuto,
    FetchPreferencesDto? Fetch);

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings/libraries");

        group.MapGet("/", GetFolders);
        group.MapPost("/", AddFolder);
        group.MapDelete("/{id:int}", RemoveFolder);
        group.MapPost("/scan", ScanAll);
        group.MapPost("/browse", BrowseFolder);

        app.MapGet("/api/settings/config", GetConfig);
        app.MapPut("/api/settings/config", SaveConfig);
        app.MapPut("/api/settings/player/audio-language", SavePlayerAudioLanguage);
        app.MapPost("/api/settings/clear-library", ClearLibrary);
    }

    /// <summary>Uloží preferovaný jazyk zvuku pro přehrávač.</summary>
    private static async Task<IResult> SavePlayerAudioLanguage(
        SavePlayerAudioLanguageRequest req, SettingsService settings, CancellationToken ct)
    {
        var normalized = MediaLanguageNormalizer.Normalize(req.Language);
        if (string.IsNullOrWhiteSpace(normalized))
            await settings.DeleteAsync(SettingsService.PlayerAudioLanguage, ct);
        else
            await settings.SetAsync(SettingsService.PlayerAudioLanguage, normalized, ct);

        return Results.NoContent();
    }

    /// <summary>Vymaže media + cache + ruční korekce (čistý start). ZACHOVÁ Settings, složky a progress.</summary>
    private static async Task<IResult> ClearLibrary(
        LibraryDbContext db,
        LibraryOperationCoordinator operations,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Běží jiná operace nad knihovnou.");

        using (lease)
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await db.Episodes.ExecuteDeleteAsync(ct);
                await db.Movies.ExecuteDeleteAsync(ct);
                await db.Shows.ExecuteDeleteAsync(ct);
                await db.MediaFiles.ExecuteDeleteAsync(ct);
                await db.ParseCaches.ExecuteDeleteAsync(ct);
                await db.TmdbCaches.ExecuteDeleteAsync(ct);
                await db.TmdbSeasonCaches.ExecuteDeleteAsync(ct);
                await db.ManualMatches.ExecuteDeleteAsync(ct);
                await db.MatchAliases.ExecuteDeleteAsync(ct);
                await transaction.CommitAsync(ct);
            }
            // Smaž cachované postery z disku.
            var posterDir = AppPaths.PosterDir;
            try
            {
                if (Directory.Exists(posterDir)) Directory.Delete(posterDir, recursive: true);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("LibraryMaintenance")
                    .LogWarning(ex, "Adresar posteru {PosterDir} se nepodarilo odstranit", posterDir);
            }

            return Results.NoContent();
        }
    }

    private static async Task<IResult> GetConfig(SettingsService settings, CancellationToken ct)
    {
        var s = settings;
        return Results.Ok(new ConfigDto(
            HasTmdbKey: await s.HasKeyAsync(SettingsService.TmdbApiKey, ct),
            LlmProvider: await s.GetOrDefaultAsync(SettingsService.LlmProvider, "openrouter", ct),
            LlmModel: await s.GetOrDefaultAsync(SettingsService.LlmModel, "anthropic/claude-haiku-4-5", ct),
            HasLlmKey: await s.HasKeyAsync(SettingsService.LlmApiKey, ct),
            PlanMode: await s.GetOrDefaultAsync(SettingsService.PlanMode, "free", ct),
            EnrichAuto: await s.GetBoolAsync(SettingsService.EnrichAuto, false, ct),
            Fetch: new FetchPreferencesDto(
                Posters: await s.GetBoolAsync(SettingsService.FetchPosters, true, ct),
                Backdrops: await s.GetBoolAsync(SettingsService.FetchBackdrops, true, ct),
                Overview: await s.GetBoolAsync(SettingsService.FetchOverview, true, ct),
                Rating: await s.GetBoolAsync(SettingsService.FetchRating, true, ct),
                Genres: await s.GetBoolAsync(SettingsService.FetchGenres, true, ct),
                Cast: await s.GetBoolAsync(SettingsService.FetchCast, true, ct))));
    }

    private static string CleanKey(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            k = k["Bearer ".Length..].Trim();
        return k;
    }

    private static async Task<IResult> SaveConfig(SaveConfigRequest req, SettingsService settings, CancellationToken ct)
    {
        var s = settings;
        // Klíče ořezat (časté: zalomení/mezera při kopírování → 401) + odstranit případný "Bearer " prefix.
        if (req.TmdbApiKey is not null)
            await s.SetAsync(SettingsService.TmdbApiKey, CleanKey(req.TmdbApiKey), ct);
        if (req.LlmProvider is not null)
            await s.SetAsync(SettingsService.LlmProvider, req.LlmProvider.Trim(), ct);
        if (req.LlmModel is not null)
            await s.SetAsync(SettingsService.LlmModel, req.LlmModel.Trim(), ct);
        if (req.LlmApiKey is not null)
            await s.SetAsync(SettingsService.LlmApiKey, CleanKey(req.LlmApiKey), ct);
        if (req.PlanMode is not null)
            await s.SetAsync(SettingsService.PlanMode, req.PlanMode, ct);
        if (req.EnrichAuto is not null)
            await s.SetAsync(SettingsService.EnrichAuto, req.EnrichAuto.Value.ToString(), ct);
        if (req.Fetch is { } f)
        {
            await s.SetAsync(SettingsService.FetchPosters, f.Posters.ToString(), ct);
            await s.SetAsync(SettingsService.FetchBackdrops, f.Backdrops.ToString(), ct);
            await s.SetAsync(SettingsService.FetchOverview, f.Overview.ToString(), ct);
            await s.SetAsync(SettingsService.FetchRating, f.Rating.ToString(), ct);
            await s.SetAsync(SettingsService.FetchGenres, f.Genres.ToString(), ct);
            await s.SetAsync(SettingsService.FetchCast, f.Cast.ToString(), ct);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> GetFolders(LibraryDbContext db, CancellationToken ct)
    {
        var folders = await db.LibraryFolders
            .OrderBy(f => f.Path)
            .Select(f => new LibraryFolderDto(f.Id, f.Path))
            .ToListAsync(ct);
        return Results.Ok(folders);
    }

    private static async Task<IResult> AddFolder(
        AddLibraryFolderRequest req,
        LibraryDbContext db,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Běží jiná operace nad knihovnou.");
        using (lease)
        {
            var path = req.Path.Trim();
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest("Cesta je prázdná.");
            if (!Directory.Exists(path))
                return Results.BadRequest($"Složka neexistuje: {path}");

            var exists = await db.LibraryFolders.AnyAsync(f => f.Path == path, ct);
            if (exists)
                return Results.Conflict("Tato složka je už v knihovně.");

            var folder = new LibraryFolder { Path = path, AddedAt = DateTimeOffset.UtcNow };
            db.LibraryFolders.Add(folder);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/settings/libraries/{folder.Id}", new LibraryFolderDto(folder.Id, folder.Path));
        }
    }

    private static async Task<IResult> RemoveFolder(
        int id,
        LibraryDbContext db,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginMaintenance(out var lease))
            return Results.Conflict("Běží jiná operace nad knihovnou.");
        using (lease)
        {
            var folder = await db.LibraryFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (folder is null) return Results.NotFound();

            db.LibraryFolders.Remove(folder);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
    }

    private static async Task<IResult> ScanAll(
        LibraryScanner scanner,
        ManualMatchService manual,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (!operations.TryBeginScan(out var lease))
            return Results.Conflict("Bezi export nebo import knihovny.");

        using (lease)
        {
            var summary = await scanner.ScanAllAsync(ct);
            // Ruční korekce znovu aplikuj (přežijí rescan).
            await manual.ApplyAllAsync(ct);
            return Results.Ok(summary);
        }
    }

    /// <summary>Otevře nativní Windows folder picker a vrátí vybranou cestu.</summary>
    private static async Task<IResult> BrowseFolder()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Results.Problem("Folder picker je dostupný jen na Windows.", statusCode: 501);

        var result = await FolderPickerWindows.PickFolderAsync();
        if (result is null) return Results.NoContent();
        return Results.Ok(new { path = result });
    }
}
