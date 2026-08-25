namespace LSP.Server.External;

/// <summary>
/// Odkud se berou API klíče. Free: z lokálních Settings (SQLite).
/// Subscription (budoucí): session token z LSP cloud serveru.
/// </summary>
public interface ICredentialProvider
{
    Task<string?> GetTmdbApiKeyAsync(CancellationToken ct = default);
    Task<string?> GetLlmApiKeyAsync(CancellationToken ct = default);
    Task<string> GetLlmProviderAsync(CancellationToken ct = default);
    Task<string> GetLlmModelAsync(CancellationToken ct = default);
}
