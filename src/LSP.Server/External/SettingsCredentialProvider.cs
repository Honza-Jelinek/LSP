using LSP.Server.Data;

namespace LSP.Server.External;

public sealed class SettingsCredentialProvider(SettingsService settings) : ICredentialProvider
{
    public async Task<string?> GetTmdbApiKeyAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingsService.TmdbApiKey, ct))?.Trim();

    public async Task<string?> GetLlmApiKeyAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingsService.LlmApiKey, ct))?.Trim();

    public Task<string> GetLlmProviderAsync(CancellationToken ct) =>
        settings.GetOrDefaultAsync(SettingsService.LlmProvider, "openrouter", ct);

    public Task<string> GetLlmModelAsync(CancellationToken ct) =>
        settings.GetOrDefaultAsync(SettingsService.LlmModel, "anthropic/claude-haiku-4-5", ct);
}
