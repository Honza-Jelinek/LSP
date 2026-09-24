using System.Net;
using System.Text.Json;
using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class LlmCandidateValidationTests
{
    [Theory]
    [InlineData(999, "movie", 111, "movie", false, false)] // ID not offered
    [InlineData(222, "tv", 111, "movie", false, false)] // offered only for a different media kind
    [InlineData(111, "movie", 999, "movie", true, false)] // provider returned wrong detail ID
    [InlineData(111, "movie", 111, "tv", true, false)] // provider returned wrong detail kind
    [InlineData(111, "movie", 111, "movie", false, true)] // dictionary key and echoed key differ
    [InlineData(null, "movie", 111, "movie", false, false)] // no answer
    [InlineData(111, "movie", 111, "movie", true, false)] // valid choice
    public async Task Service_OnlyAppliesOfferedIdentityWithMatchingDetail(
        int? chosenId, string offeredType, int detailId, string detailType,
        bool expectsChosenDetailCall, bool wrongOutputKey)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options;
        await using var db = new LibraryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.MediaFiles.Add(new MediaFile
        {
            Path = "Example.2020.mkv", FileName = "Example.2020.mkv", Extension = ".mkv",
            Kind = LSP.Server.Library.Parsing.MediaKind.Movie, AddedAt = DateTimeOffset.UtcNow,
            Movie = new Movie { Title = "Example", Year = 2020 },
        });
        var settings = new SettingsService(db);
        await settings.SetAsync(SettingsService.TmdbApiKey, "fake");
        await settings.SetAsync(SettingsService.LlmApiKey, "fake");
        await settings.SetAsync(SettingsService.FetchPosters, "false");

        var provider = new CandidateProvider(offeredType, detailId, detailType);
        var llm = new ChoiceClient(chosenId, wrongOutputKey);
        var service = new EnrichmentService(db, provider, llm,
            new SeasonEpisodeCache(db, provider), settings, NullLogger<EnrichmentService>.Instance);
        await service.EnrichAsync();

        Assert.Single(llm.Inputs);
        Assert.Equal(offeredType == "tv" ? 2 : 1, llm.Inputs[0].Candidates.Count);
        Assert.Equal(expectsChosenDetailCall ? 2 : 1, provider.DetailCalls);
        var movie = await db.Movies.AsNoTracking().SingleAsync();
        var valid = chosenId == 111 && detailId == 111 && detailType == "movie" && !wrongOutputKey;
        Assert.Equal(valid ? 111 : null, movie.TmdbId);
        if (valid)
        {
            Assert.Equal("Example", movie.DisplayTitle);
            var cache = await db.TmdbCaches.AsNoTracking().SingleAsync();
            Assert.Equal(111, cache.TmdbId);
            Assert.Equal(MatchScorer.Score(provider.ChosenDetail, "Example", 2020, "movie"), cache.Score);
            Assert.True(cache.Score < 0.85);
        }
        else
        {
            Assert.Null(movie.DisplayTitle);
            Assert.DoesNotContain(await db.TmdbCaches.AsNoTracking().ToListAsync(), c =>
                c.TmdbId == chosenId && c.Score >= 0.85);
        }
    }

    [Theory]
    [InlineData("other", 111, false)]
    [InlineData("movie:1", 999, false)]
    [InlineData("movie:1", 222, false)] // offered only as tv
    [InlineData("movie:1", 111, true)]
    [InlineData("movie:1", null, true)]
    public async Task OpenRouter_ValidatesKeyCandidateAndKind(string itemKey, int? id, bool accepted)
    {
        var response = JsonSerializer.Serialize(new[] { new { itemKey, tmdbId = id } });
        var handler = new ReplyHandler(response);
        var client = new OpenRouterLlmClient(new Credentials(), new HttpClient(handler), NullLogger<OpenRouterLlmClient>.Instance);
        var results = await client.ChooseCandidateBatchAsync([ChooseInput()]);
        Assert.Equal(accepted, results.ContainsKey("movie:1"));
        if (accepted) Assert.Equal(id, results["movie:1"].ChosenTmdbId);
    }

    [Fact]
    public async Task OpenRouter_RejectsDuplicateAnswers()
    {
        var client = new OpenRouterLlmClient(new Credentials(),
            new HttpClient(new ReplyHandler("[{\"itemKey\":\"movie:1\",\"tmdbId\":111},{\"itemKey\":\"movie:1\",\"tmdbId\":null}]")),
            NullLogger<OpenRouterLlmClient>.Instance);
        Assert.Empty(await client.ChooseCandidateBatchAsync([ChooseInput()]));
    }

    private static LlmChooseInput ChooseInput() => new("movie:1", "Example", 2020, "movie", null, null,
        [new LlmCandidate(111, "Example", 2022, "movie", null),
         new LlmCandidate(222, "Example", 2022, "tv", null)]);

    private sealed class ChoiceClient(int? chosenId, bool wrongOutputKey) : ILlmClient
    {
        public IReadOnlyList<LlmChooseInput> Inputs { get; private set; } = [];
        public Task<IReadOnlyDictionary<string, LlmChooseOutput>> ChooseCandidateBatchAsync(
            IReadOnlyList<LlmChooseInput> items, CancellationToken ct = default)
        {
            Inputs = items;
            return Task.FromResult<IReadOnlyDictionary<string, LlmChooseOutput>>(
                new Dictionary<string, LlmChooseOutput> { [items[0].ItemKey] = new(wrongOutputKey ? "other" : items[0].ItemKey, chosenId) });
        }
        public Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(IReadOnlyList<LlmParseInput> items,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LlmParseOutput?>>([]);
    }

    private sealed class CandidateProvider(string offeredType, int detailId, string detailType) : IMetadataProvider
    {
        public int DetailCalls { get; private set; }
        public TmdbSearchResult ChosenDetail => new(detailId, detailType, "Example", null, null, null, null, null, 2022);
        public Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(string query, string type,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TmdbSearchResult>>(
                type == "movie" ? offeredType == "tv"
                    ? [new TmdbSearchResult(111, "movie", "Example", null, null, null, null, null, 2022),
                       new TmdbSearchResult(222, "tv", "Example", null, null, null, null, null, 2022)]
                    : [new TmdbSearchResult(111, "movie", "Example", null, null, null, null, null, 2022)]
                    : []);
        public Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct = default)
        {
            DetailCalls++;
            // First detail is fetched for the scored search; second for the LLM choice.
            return Task.FromResult<TmdbSearchResult?>(DetailCalls == 1
                ? new TmdbSearchResult(111, "movie", "Example", null, null, null, null, null, 2022)
                : ChosenDetail);
        }
        public Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int id, int season, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, TmdbEpisodeInfo>>(new Dictionary<int, TmdbEpisodeInfo>());
        public Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<string?> DownloadPosterAsync(string path, string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    private sealed class ReplyHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } },
                })),
            });
    }

    private sealed class Credentials : ICredentialProvider
    {
        public Task<string?> GetTmdbApiKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>("fake");
        public Task<string?> GetLlmApiKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>("fake");
        public Task<string> GetLlmProviderAsync(CancellationToken ct = default) => Task.FromResult("openrouter");
        public Task<string> GetLlmModelAsync(CancellationToken ct = default) => Task.FromResult("test");
    }
}
