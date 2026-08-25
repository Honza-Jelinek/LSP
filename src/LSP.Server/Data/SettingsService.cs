using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Data;

/// <summary>
/// Čte a zapisuje konfiguraci z/do Setting tabulky (KV store v SQLite).
/// Centrální přístupový bod — vnější kód nepracuje s Setting entitou přímo.
/// </summary>
public sealed class SettingsService(LibraryDbContext db)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task<string> GetOrDefaultAsync(string key, string defaultValue, CancellationToken ct = default) =>
        await GetAsync(key, ct) ?? defaultValue;

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var v) ? v : defaultValue;

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is not null)
        {
            db.Settings.Remove(setting);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> HasKeyAsync(string key, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await GetAsync(key, ct));

    // Well-known klíče.
    public const string TmdbApiKey = "tmdb.apiKey";
    public const string LlmProvider = "llm.provider";   // "openrouter" (default)
    public const string LlmApiKey = "llm.apiKey";       // OpenRouter API klíč
    public const string LlmModel = "llm.model";         // OpenRouter model ID (e.g. "anthropic/claude-haiku-4-5")
    public const string PlanMode = "plan.mode";
    public const string EnrichAuto = "enrich.auto";
    public const string FetchPosters = "tmdb.fetch.posters";
    public const string FetchBackdrops = "tmdb.fetch.backdrops";
    public const string FetchOverview = "tmdb.fetch.overview";
    public const string FetchRating = "tmdb.fetch.rating";
    public const string FetchGenres = "tmdb.fetch.genres";
    public const string FetchCast = "tmdb.fetch.cast";
    public const string PlayerAudioLanguage = "player.audioLanguage";
}
