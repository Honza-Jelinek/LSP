namespace LSP.Server.External;

/// <summary>Vstup pro LLM parser — jeden soubor k rozpoznání.</summary>
public sealed record LlmParseInput(string FileName, string? FolderName, string? RegexGuessTitle, string? RegexGuessKind);

/// <summary>Strukturovaný výstup z LLM parseru.</summary>
public sealed record LlmParseOutput(
    string Kind,            // "movie" | "episode"
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    string? EpisodeTitle);

/// <summary>Jeden TMDB kandidát nabídnutý LLM k výběru.</summary>
public sealed record LlmCandidate(int TmdbId, string Title, int? Year, string MediaType, string? Overview);

/// <summary>Vstup pro LLM disambiguaci nejisté TMDB shody — jeden film/seriál + jeho kandidáti.</summary>
public sealed record LlmChooseInput(
    string ItemKey,              // "movie:{id}" | "show:{id}" — echované zpět, žádný poziční alignment
    string ParsedTitle,
    int? ParsedYear,
    string ExpectedKind,          // "movie" | "tv"
    string? FolderName,
    string? SampleFileName,
    IReadOnlyList<LlmCandidate> Candidates);

/// <summary>Výsledek LLM disambiguace pro jednu položku. ChosenTmdbId == null = "žádný kandidát nesedí".</summary>
public sealed record LlmChooseOutput(string ItemKey, int? ChosenTmdbId);

/// <summary>
/// Posílá dávku názvů do LLM pro čištění/rozpoznání. Free: OpenRouter API.
/// Subscription (budoucí): LSP cloud server.
/// Výstup je ZAROVNANÝ 1:1 se vstupem (results[i] ↔ items[i]); null = LLM položku nevrátil.
/// </summary>
public interface ILlmClient
{
    Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(
        IReadOnlyList<LlmParseInput> items, CancellationToken ct = default);

    /// <summary>
    /// Jako ParseBatchAsync, ale s kontextem složky — LLM dostane explicitně název složky,
    /// do které všechny soubory patří (zlepšuje přesnost u seriálů).
    /// Výchozí implementace ignoruje folderContext a volá ParseBatchAsync.
    /// </summary>
    async Task<IReadOnlyList<LlmParseOutput?>> ParseBatchWithFolderContextAsync(
        IReadOnlyList<LlmParseInput> items, string? folderContext, CancellationToken ct = default)
        => await ParseBatchAsync(items, ct);

    /// <summary>
    /// Rozhodne u položek s nejistou TMDB shodou, který kandidát (pokud vůbec nějaký) odpovídá.
    /// Klíčováno přes <see cref="LlmChooseInput.ItemKey"/> — žádný poziční alignment.
    /// Výchozí implementace vrací prázdný výsledek (žádná disambiguace).
    /// </summary>
    Task<IReadOnlyDictionary<string, LlmChooseOutput>> ChooseCandidateBatchAsync(
        IReadOnlyList<LlmChooseInput> items, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, LlmChooseOutput>>(new Dictionary<string, LlmChooseOutput>());
}
