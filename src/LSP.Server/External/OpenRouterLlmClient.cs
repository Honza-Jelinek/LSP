using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LSP.Server.External;

/// <summary>
/// Volá OpenRouter API (OpenAI-kompatibilní) pro batch parsing názvů souborů.
/// Strukturovaný JSON výstup přes response_format.
/// </summary>
public sealed class OpenRouterLlmClient(
    ICredentialProvider creds, HttpClient http, ILogger<OpenRouterLlmClient> log) : ILlmClient
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int BatchSize = 30;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(
        IReadOnlyList<LlmParseInput> items, CancellationToken ct = default)
        => await ParseBatchWithFolderContextAsync(items, null, ct);

    /// <summary>
    /// ParseBatchAsync s kontextem složky — všechny položky patří do složky folderContext.
    /// LLM prompt dostane info o názvu složky navrch, což zlepšuje přesnost u seriálů.
    /// </summary>
    public async Task<IReadOnlyList<LlmParseOutput?>> ParseBatchWithFolderContextAsync(
        IReadOnlyList<LlmParseInput> items, string? folderContext, CancellationToken ct = default)
    {
        var results = new LlmParseOutput?[items.Count];

        var key = (await creds.GetLlmApiKeyAsync(ct))?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return results;

        var model = (await creds.GetLlmModelAsync(ct)).Trim();

        for (var offset = 0; offset < items.Count; offset += BatchSize)
        {
            var batch = items.Skip(offset).Take(BatchSize).ToList();
            var batchResults = await CallApiAsync(key, model, batch, folderContext, ct);
            foreach (var (localIndex, output) in batchResults)
            {
                var global = offset + localIndex;
                if (global >= 0 && global < results.Length)
                    results[global] = output;
            }
        }

        return results;
    }

    private async Task<Dictionary<int, LlmParseOutput>> CallApiAsync(
        string apiKey, string model, List<LlmParseInput> batch, string? folderContext, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(
            batch.Select((item, idx) => new
            {
                index = idx,
                fileName = item.FileName,
                folderName = item.FolderName,
                regexGuess = item.RegexGuessTitle,
            }),
            JsonOpts);

        var systemPrompt = """
            You are a media file name parser. Given a batch of file/folder names, extract structured metadata for each.
            Return a JSON array where each element has:
            - "index": matching input index (REQUIRED, must equal the input item's index, integer)
            - "kind": "movie" or "episode"
            - "title": clean title of the movie or show (no year, no codec tags, no release group)
            - "year": release year (integer or null)
            - "season": season number (integer or null, only for episodes)
            - "episode": episode number (integer or null, only for episodes)
            - "episodeTitle": episode title (string or null)

            Rules:
            - Strip quality tags (1080p, x265, HEVC, etc.), codec info, release groups
            - For episodes, the "title" should be the SHOW name, not the episode name
            - Use folder name as context (often contains the show/collection name)
            - Handle Czech/Slovak titles alongside English ones
            - Handle patterns like "Breaking Bad 101" (season 1, episode 01)
            - Handle date-based filenames like "1965.01.01 - Title" (treat as movie with that title)
            - The "index" field is REQUIRED and MUST equal the input item's index. Return every input
              item exactly once. Never renumber, reorder without setting index, or omit an item.

            Respond with ONLY the JSON array, no markdown, no explanation.
            """;

        var userPrompt = string.IsNullOrWhiteSpace(folderContext)
            ? $"Parse these files:\n{inputJson}"
            : $"All files below are from the folder:\n\"{folderContext}\"\n\nParse these files:\n{inputJson}";
        var body = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.0,
            max_tokens = 4000,
        };

        var callId = Guid.NewGuid().ToString("N")[..8];
        log.LogInformation("OpenRouter call {CallId}: {Count} položek, model {Model}, klíč délka {Len} (prompt/odpověď v llm-*.jsonl)",
            callId, batch.Count, model, apiKey.Length);

        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);

        try
        {
            var resp = await SendWithRetryAsync(apiKey, bodyJson, callId, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("OpenRouter call {CallId} HTTP {Code} (model {Model}): {Body}",
                    callId, resp.StatusCode, model, errBody.Length > 400 ? errBody[..400] : errBody);
                WriteLlmLog(callId, model, userPrompt, $"HTTP {(int)resp.StatusCode}: {errBody}");
                return [];
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(respJson);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            WriteLlmLog(callId, model, userPrompt, content ?? "(prázdná odpověď)");

            if (string.IsNullOrWhiteSpace(content))
                return [];

            // Strip markdown code fences if present.
            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var firstNewline = content.IndexOf('\n');
                if (firstNewline > 0) content = content[(firstNewline + 1)..];
                if (content.EndsWith("```")) content = content[..^3];
                content = content.Trim();
            }

            using var parsed = JsonDocument.Parse(content);
            var results = new Dictionary<int, LlmParseOutput>();

            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                // Zarovnání POUZE podle 'index' z LLM — žádný poziční fallback (vynechaná položka
                // by jinak posunula zarovnání všech následujících).
                if (!item.TryGetProperty("index", out var ix) || ix.ValueKind != JsonValueKind.Number)
                {
                    log.LogWarning("OpenRouter: položka bez platného 'index' pole zahozena");
                    continue;
                }
                var idx = ix.GetInt32();
                if (idx < 0 || idx >= batch.Count)
                {
                    log.LogWarning("OpenRouter: 'index' {Idx} mimo rozsah dávky ({Count}) zahozen", idx, batch.Count);
                    continue;
                }
                if (results.ContainsKey(idx))
                {
                    log.LogWarning("OpenRouter: duplicitní 'index' {Idx} — ponechána první položka", idx);
                    continue;
                }

                results[idx] = new LlmParseOutput(
                    Kind: item.TryGetProperty("kind", out var k) ? k.GetString() ?? "movie" : "movie",
                    Title: item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Year: item.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                    Season: item.TryGetProperty("season", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null,
                    Episode: item.TryGetProperty("episode", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null,
                    EpisodeTitle: item.TryGetProperty("episodeTitle", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null);
            }

            log.LogInformation("OpenRouter call {CallId} → {Results} výsledků", callId, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "OpenRouter call {CallId} selhalo", callId);
            WriteLlmLog(callId, model, userPrompt, $"EXCEPTION: {ex.Message}");
            return [];
        }
    }

    private const int ChooseBatchSize = 10;
    private const int MaxCandidatesPerItem = 5;

    public async Task<IReadOnlyDictionary<string, LlmChooseOutput>> ChooseCandidateBatchAsync(
        IReadOnlyList<LlmChooseInput> items, CancellationToken ct = default)
    {
        var results = new Dictionary<string, LlmChooseOutput>();
        if (items.Count == 0) return results;

        var key = (await creds.GetLlmApiKeyAsync(ct))?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return results;

        var model = (await creds.GetLlmModelAsync(ct)).Trim();

        for (var offset = 0; offset < items.Count; offset += ChooseBatchSize)
        {
            var batch = items.Skip(offset).Take(ChooseBatchSize).ToList();
            var batchResults = await CallChooseApiAsync(key, model, batch, ct);
            foreach (var (itemKey, output) in batchResults)
                results[itemKey] = output;
        }

        return results;
    }

    private async Task<Dictionary<string, LlmChooseOutput>> CallChooseApiAsync(
        string apiKey, string model, List<LlmChooseInput> batch, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(
            batch.Select(item => new
            {
                itemKey = item.ItemKey,
                parsedTitle = item.ParsedTitle,
                parsedYear = item.ParsedYear,
                expectedKind = item.ExpectedKind,
                folderName = item.FolderName,
                sampleFileName = item.SampleFileName,
                candidates = item.Candidates.Take(MaxCandidatesPerItem).Select(c => new
                {
                    tmdbId = c.TmdbId,
                    title = c.Title,
                    year = c.Year,
                    mediaType = c.MediaType,
                    overview = c.Overview is { Length: > 200 } ov ? ov[..200] : c.Overview,
                }),
            }),
            JsonOpts);

        var systemPrompt = """
            You match parsed media file/folder names to TMDB entries. For each item, pick the tmdbId
            of the candidate that is the SAME movie/show as the parsed name, using the folder/file name
            as context, or null if none of the candidates match.

            Return ONLY a JSON array where each element has:
            - "itemKey": echoed EXACTLY as given in the input (REQUIRED, string)
            - "tmdbId": the chosen candidate's tmdbId (integer), or null if none match

            Rules:
            - Include every input item exactly once, echoing its itemKey exactly.
            - Titles may be Czech or English — a Czech parsed title can match an English TMDB title.
            - Prefer year proximity and folder/file context when multiple candidates look similar.
            - When genuinely unsure, return null rather than guessing.

            Respond with ONLY the JSON array, no markdown, no explanation.
            """;

        var userPrompt = $"Choose the matching TMDB candidate for each item:\n{inputJson}";
        var body = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.0,
            max_tokens = 4000,
        };

        var callId = Guid.NewGuid().ToString("N")[..8];
        log.LogInformation("OpenRouter choose-call {CallId}: {Count} položek, model {Model}",
            callId, batch.Count, model);

        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
        var validKeys = batch.Select(i => i.ItemKey).ToHashSet();
        var results = new Dictionary<string, LlmChooseOutput>();

        try
        {
            var resp = await SendWithRetryAsync(apiKey, bodyJson, callId, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("OpenRouter choose-call {CallId} HTTP {Code} (model {Model}): {Body}",
                    callId, resp.StatusCode, model, errBody.Length > 400 ? errBody[..400] : errBody);
                WriteLlmLog(callId, model, userPrompt, $"HTTP {(int)resp.StatusCode}: {errBody}");
                return results;
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(respJson);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            WriteLlmLog(callId, model, userPrompt, content ?? "(prázdná odpověď)");

            if (string.IsNullOrWhiteSpace(content))
                return results;

            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var firstNewline = content.IndexOf('\n');
                if (firstNewline > 0) content = content[(firstNewline + 1)..];
                if (content.EndsWith("```")) content = content[..^3];
                content = content.Trim();
            }

            using var parsed = JsonDocument.Parse(content);
            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("itemKey", out var ik) || ik.ValueKind != JsonValueKind.String)
                {
                    log.LogWarning("OpenRouter choose-call {CallId}: položka bez 'itemKey' zahozena", callId);
                    continue;
                }
                var itemKey = ik.GetString()!;
                if (!validKeys.Contains(itemKey))
                {
                    log.LogWarning("OpenRouter choose-call {CallId}: neznámý itemKey '{Key}' zahozen", callId, itemKey);
                    continue;
                }

                int? chosenTmdbId = item.TryGetProperty("tmdbId", out var tid) && tid.ValueKind == JsonValueKind.Number
                    ? tid.GetInt32() : null;

                results[itemKey] = new LlmChooseOutput(itemKey, chosenTmdbId);
            }

            log.LogInformation("OpenRouter choose-call {CallId} → {Results} výsledků", callId, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "OpenRouter choose-call {CallId} selhalo", callId);
            WriteLlmLog(callId, model, userPrompt, $"EXCEPTION: {ex.Message}");
            return results;
        }
    }

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20)];

    /// <summary>
    /// Pošle request s retry na HTTP 429 (rate limit) — free modely na OpenRouteru jsou
    /// limitované a bez retry celá LLM fáze tiše selže. Respektuje Retry-After hlavičku.
    /// HttpRequestMessage nejde poslat opakovaně, proto se staví pro každý pokus znovu.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string apiKey, string bodyJson, string callId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            // Typovaná auth hlavička (spolehlivější než Headers.Add — to u některých klíčů hlavičku neposlalo → 401).
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("HTTP-Referer", "https://github.com/local-stream-player");
            request.Headers.Add("X-Title", "Local Stream Player");

            var resp = await http.SendAsync(request, ct);
            if (resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= RetryDelays.Length)
                return resp;

            var delay = resp.Headers.RetryAfter?.Delta ?? RetryDelays[attempt];
            if (delay <= TimeSpan.Zero || delay > TimeSpan.FromMinutes(2)) delay = RetryDelays[attempt];
            log.LogInformation("OpenRouter call {CallId}: 429 rate-limit, pokus {Attempt}/{Max} za {Delay:F0}s",
                callId, attempt + 1, RetryDelays.Length, delay.TotalSeconds);
            resp.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Zapise prompt + odpoved do AppPaths log adresare (propojeno pres callId s hlavnim logem).</summary>
    private static void WriteLlmLog(string callId, string model, string prompt, string response)
    {
        try
        {
            var dir = AppPaths.SubDir("logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"llm-{DateTime.Now:yyyyMMdd}.jsonl");
            var entry = JsonSerializer.Serialize(new
            {
                id = callId,
                time = DateTime.Now.ToString("HH:mm:ss"),
                model,
                prompt,
                response,
            });
            lock (LlmLogLock)
                File.AppendAllText(file, entry + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }

    private static readonly Lock LlmLogLock = new();
}
