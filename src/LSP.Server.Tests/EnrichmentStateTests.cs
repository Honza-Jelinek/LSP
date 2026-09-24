using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class EnrichmentStateTests
{
    [Fact]
    public async Task AcceptedMatchesDoNotReenterFallback()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateDb(connection);
        AddMovie(db, "Alias", "alias.mkv");
        AddMovie(db, "Imdb", "imdb.mkv", imdbId: "tt1234567");
        AddMovie(db, "Exact", "exact.mkv", year: 2024);
        db.MatchAliases.Add(new MatchAlias { Key = "title:alias", TmdbId = 101, MediaType = "movie" });
        await db.SaveChangesAsync();

        var metadata = new Metadata();
        metadata.Details[101] = Result(101, "movie", "Alias", 2024);
        metadata.ImdbResult = Result(102, "movie", "Imdb", 2024);
        metadata.Candidates[("Exact", "movie")] = [Result(103, "movie", "Exact", 2024)];
        var llm = new Llm();

        var summary = await Service(db, metadata, llm).EnrichAsync();

        Assert.Empty(llm.ParsedInputs);
        Assert.Equal(0, summary.LlmFallbacks);
        Assert.Equal(0, summary.ReviewQueue);
        Assert.Equal(new[] { 101, 103, 102 }, (await db.Movies.OrderBy(m => m.Title).ToListAsync()).Select(m => m.TmdbId!.Value).ToArray());
    }

    [Fact]
    public async Task ForceRefreshIncludesNewlyClearedUnmatchedMovieInFallback()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateDb(connection);
        var movie = AddMovie(db, "Unmatched", "unmatched.mkv");
        movie.TmdbId = 7;
        movie.DisplayTitle = "Old metadata";
        await db.SaveChangesAsync();
        var llm = new Llm();

        var summary = await Service(db, new Metadata(), llm).EnrichAsync(force: true);

        Assert.Single(llm.ParsedInputs);
        Assert.Equal(1, summary.LlmFallbacks);
        Assert.Equal(1, summary.ReviewQueue);
        Assert.Null(movie.TmdbId);
        Assert.Null(movie.DisplayTitle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WeakMovieAndShowRemainUnmergedAndInReviewQueue(bool useLlm)
    {
        await using var connection = await OpenDatabaseAsync(useLlm);
        await using var db = CreateDb(connection);
        var movie = AddMovie(db, "Original Movie", "weak.mkv", year: 2024);
        var show = AddShow(db, "Original Show", "show.S01E01.mkv");
        await db.SaveChangesAsync();
        var metadata = new Metadata();
        // Exact title with a conflicting year scores 0.70; a show without year scores 0.85,
        // so give the show a different title to keep it below the automatic threshold.
        metadata.Candidates[("Original Movie", "movie")] = [Result(111, "movie", "Original Movie", 2000)];
        metadata.Candidates[("Original Show", "tv")] = [Result(222, "tv", "Another Show", 2024)];
        Assert.InRange(MatchScorer.Score(metadata.Candidates[("Original Movie", "movie")][0], "Original Movie", 2024, "movie"), 0.60, 0.849999);
        Assert.InRange(MatchScorer.Score(metadata.Candidates[("Original Show", "tv")][0], "Original Show", null, "tv"), 0.60, 0.849999);
        var summary = await Service(db, metadata, new Llm()).EnrichAsync();

        Assert.Null(movie.TmdbId);
        Assert.Null(show.TmdbId);
        Assert.Equal("Original Movie", movie.Title);
        Assert.Equal("Original Show", show.Title);
        Assert.Equal(2, summary.ReviewQueue);
        Assert.Equal(2, summary.TmdbMisses);
        Assert.Equal(2, await db.Movies.CountAsync(m => !m.IsManual && m.TmdbId == null)
            + await db.Shows.CountAsync(s => !s.IsManual && s.TmdbId == null));
    }

    [Fact]
    public async Task FallbackAcceptsStrongMatchButPreservesWeakParsedTitle()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateDb(connection);
        var strong = AddMovie(db, "Unparsed A", "a.mkv", year: 2024);
        var weak = AddMovie(db, "Unparsed B", "b.mkv", year: 2024);
        await db.SaveChangesAsync();
        var metadata = new Metadata();
        metadata.Candidates[("Correct A", "movie")] = [Result(301, "movie", "Correct A", 2024)];
        metadata.Candidates[("Wrong B", "movie")] = [Result(302, "movie", "Wrong B", 2000)];
        var llm = new Llm
        {
            Parser = input => input.FileName == "a.mkv"
                ? new LlmParseOutput("movie", "Correct A", 2024, null, null, null)
                : new LlmParseOutput("movie", "Wrong B", 2024, null, null, null)
        };

        var summary = await Service(db, metadata, llm).EnrichAsync();

        Assert.Equal(2, summary.LlmFallbacks);
        Assert.Equal(1, summary.LlmRecovered);
        Assert.Equal(301, strong.TmdbId);
        Assert.Equal("Correct A", strong.Title);
        Assert.Null(weak.TmdbId);
        Assert.Equal("Unparsed B", weak.Title);
        Assert.Equal(1, summary.ReviewQueue);
    }

    [Fact]
    public async Task WeakEpisodeFallbackDoesNotAttachToAcceptedShowWithSameTitle()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateDb(connection);
        var movie = AddMovie(db, "Unparsed", "episode.mkv");
        var existing = AddShow(db, "Shared Title", "existing.S01E01.mkv");
        existing.TmdbId = 500;
        await db.SaveChangesAsync();
        var metadata = new Metadata();
        metadata.Candidates[("Shared Title", "tv")] = [Result(600, "tv", "Other Title", 2024)];
        var llm = new Llm { Parser = _ => new LlmParseOutput("episode", "Shared Title", null, 1, 2, null) };

        var summary = await Service(db, metadata, llm).EnrichAsync();

        Assert.Equal(0, summary.Reclassified);
        Assert.Equal(500, existing.TmdbId);
        Assert.Equal(1, await db.Episodes.CountAsync());
        Assert.Equal("Unparsed", movie.Title);
        Assert.Null(movie.TmdbId);
        Assert.Equal(1, summary.ReviewQueue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrongEpisodeFallbackKeepsDistinctIdentityWhenShowTitleCollides(bool manualExisting)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateDb(connection);
        AddMovie(db, "Unparsed", "episode.mkv");
        var existing = AddShow(db, "Shared Title", "existing.S01E01.mkv");
        existing.IsManual = manualExisting;
        if (!manualExisting) existing.TmdbId = 500;
        await db.SaveChangesAsync();
        var metadata = new Metadata();
        metadata.Candidates[("Shared Title", "tv")] = [Result(600, "tv", "Shared Title", 2024)];
        var llm = new Llm { Parser = _ => new LlmParseOutput("episode", "Shared Title", null, 1, 2, null) };

        var summary = await Service(db, metadata, llm).EnrichAsync();

        Assert.Equal(1, summary.Reclassified);
        Assert.Equal(manualExisting ? null : 500, existing.TmdbId);
        Assert.Equal(manualExisting, existing.IsManual);
        var created = await db.Shows.SingleAsync(s => s.TmdbId == 600);
        Assert.StartsWith("Shared Title (TMDB 600)", created.Title);
        Assert.Equal(1, await db.Episodes.CountAsync(e => e.ShowId == created.Id));
        Assert.Equal(0, summary.ReviewQueue);
    }

    private static EnrichmentService Service(LibraryDbContext db, Metadata metadata, Llm llm) =>
        new(db, metadata, llm, new SeasonEpisodeCache(db, metadata), new SettingsService(db),
            NullLogger<EnrichmentService>.Instance);

    private static async Task<SqliteConnection> OpenDatabaseAsync(bool useLlm = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var settings = new SettingsService(db);
        await settings.SetAsync(SettingsService.TmdbApiKey, "test");
        if (useLlm) await settings.SetAsync(SettingsService.LlmApiKey, "test");
        await settings.SetAsync(SettingsService.FetchPosters, "false");
        return connection;
    }

    private static LibraryDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options);

    private static Movie AddMovie(LibraryDbContext db, string title, string path, int? year = null, string? imdbId = null)
    {
        var file = new MediaFile { Path = path, FileName = path, Extension = ".mkv", Kind = LSP.Server.Library.Parsing.MediaKind.Movie };
        var movie = new Movie { Title = title, Year = year, ImdbId = imdbId, MediaFile = file };
        file.Movie = movie;
        db.MediaFiles.Add(file);
        return movie;
    }

    private static Show AddShow(LibraryDbContext db, string title, string path)
    {
        var file = new MediaFile { Path = path, FileName = path, Extension = ".mkv", Kind = LSP.Server.Library.Parsing.MediaKind.Episode };
        var show = new Show { Title = title };
        var episode = new Episode { Show = show, Season = 1, Number = 1, MediaFile = file };
        file.Episode = episode;
        db.MediaFiles.Add(file);
        return show;
    }

    private static TmdbSearchResult Result(int id, string type, string title, int year) =>
        new(id, type, title, null, null, null, null, null, year);

    private sealed class Llm : ILlmClient
    {
        public List<LlmParseInput> ParsedInputs { get; } = [];
        public Func<LlmParseInput, LlmParseOutput?> Parser { get; init; } = _ => null;

        public Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(IReadOnlyList<LlmParseInput> items, CancellationToken ct = default)
        {
            ParsedInputs.AddRange(items);
            return Task.FromResult<IReadOnlyList<LlmParseOutput?>>(items.Select(Parser).ToList());
        }
    }

    private sealed class Metadata : IMetadataProvider
    {
        public Dictionary<(string, string), IReadOnlyList<TmdbSearchResult>> Candidates { get; } = [];
        public Dictionary<int, TmdbSearchResult> Details { get; } = [];
        public TmdbSearchResult? ImdbResult { get; set; }
        public Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(string query, string type, CancellationToken ct = default) =>
            Task.FromResult(Candidates.GetValueOrDefault((query, type)) ?? (IReadOnlyList<TmdbSearchResult>)[]);
        public Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct = default) =>
            Task.FromResult(Details.GetValueOrDefault(tmdbId) ?? Candidates.Values.SelectMany(x => x).FirstOrDefault(x => x.TmdbId == tmdbId));
        public Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, TmdbEpisodeInfo>>(new Dictionary<int, TmdbEpisodeInfo>());
        public Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct = default) => Task.FromResult(ImdbResult);
        public Task<string?> DownloadPosterAsync(string posterPath, string localFileName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }
}
