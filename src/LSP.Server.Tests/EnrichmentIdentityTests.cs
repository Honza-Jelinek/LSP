using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using LSP.Server.Library.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class EnrichmentIdentityTests
{
    [Fact]
    public async Task FileCorrectionStaysOnFileAndLegacyFolderAliasCannotOverrideMovieTitle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var layover = fixture.AddMovie(@"E:\_Filmy\The Layover.mkv", "The Layover");
        fixture.AddMovie(@"E:\_Filmy\Adventureland.mkv", "Adventureland");
        fixture.Db.MatchAliases.AddRange(
            new MatchAlias { Key = "folder:_Filmy", TmdbId = 339404, MediaType = "movie" },
            new MatchAlias { Key = "title:adventureland", TmdbId = 16614, MediaType = "movie" });
        fixture.Metadata.Details[(339404, "movie")] = Result(339404, "movie", "The Layover");
        fixture.Metadata.Details[(16614, "movie")] = Result(16614, "movie", "Adventureland");
        await fixture.Db.SaveChangesAsync();

        await fixture.Manual.ApplyFileAsync(layover.Id, "movie", 339404, "movie", null, null, default);
        Assert.Equal(2, await fixture.Db.MatchAliases.CountAsync());
        Assert.Equal(339404, await fixture.Db.ManualMatches.Where(x => x.Key == layover.Path).Select(x => x.TmdbId).SingleAsync());

        await fixture.Enrichment.EnrichAsync();
        var movies = await fixture.Db.Movies.AsNoTracking().ToDictionaryAsync(x => x.Title);
        Assert.Equal(339404, movies["The Layover"].TmdbId);
        Assert.Equal(16614, movies["Adventureland"].TmdbId);
        Assert.DoesNotContain((339404, "movie"), fixture.Metadata.DetailCalls.Skip(1));

        var corrected = await fixture.Db.Movies.SingleAsync(x => x.Title == "The Layover");
        corrected.TmdbId = null;
        corrected.IsManual = false;
        await fixture.Db.SaveChangesAsync();
        await fixture.Manual.ApplyAllAsync();
        Assert.Equal(339404, corrected.TmdbId);
        Assert.True(corrected.IsManual);
    }

    [Fact]
    public async Task WrongTypeAliasAndCacheAreIgnoredWhileValidMovieCandidateApplies()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddMovie(@"E:\Films\Avatar.mkv", "Avatar");
        fixture.Db.MatchAliases.Add(new MatchAlias { Key = "title:avatar", TmdbId = 246, MediaType = "tv" });
        fixture.Db.TmdbCaches.Add(new TmdbCache
        {
            QueryKey = "avatar||movie", TmdbId = 246, MediaType = "tv", Title = "Samurai",
            Score = 1, FetchedAt = DateTime.UtcNow,
        });
        fixture.Metadata.Candidates[("Avatar", "movie")] = [Result(246, "tv", "Avatar"), Result(19995, "movie", "Avatar")];
        fixture.Metadata.Details[(19995, "movie")] = Result(19995, "movie", "Avatar");
        await fixture.Db.SaveChangesAsync();

        await fixture.Enrichment.EnrichAsync();

        Assert.Equal(19995, (await fixture.Db.Movies.SingleAsync()).TmdbId);
        Assert.DoesNotContain((246, "movie"), fixture.Metadata.DetailCalls);
    }

    [Fact]
    public async Task WrongTypeImdbAndDetailCannotBecomeMovieIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var file = fixture.AddMovie(@"E:\Films\Unknown.mkv", "Unknown");
        file.Movie!.ImdbId = "tt0000246";
        fixture.Metadata.ImdbResult = Result(246, "tv", "Wrong TV");
        fixture.Metadata.Candidates[("Unknown", "movie")] = [Result(101, "movie", "Unknown")];
        fixture.Metadata.Details[(101, "movie")] = Result(101, "tv", "Wrong detail");
        fixture.Metadata.Candidates[("Unknown", "tv")] = [Result(246, "tv", "Unknown")];
        await fixture.Db.SaveChangesAsync();

        await fixture.Enrichment.EnrichAsync();

        Assert.Null((await fixture.Db.Movies.SingleAsync()).TmdbId);
        Assert.DoesNotContain((246, "movie"), fixture.Metadata.DetailCalls);
    }

    [Fact]
    public async Task MovieAliasCannotBecomeShowIdentityAndValidTvSearchStillApplies()
    {
        await using var fixture = await Fixture.CreateAsync();
        var show = new Show { Title = "Avatar" };
        var episodeFile = new MediaFile
        {
            Path = @"E:\TV\Avatar\Avatar.S01E01.mkv", FileName = "Avatar.S01E01.mkv",
            Extension = ".mkv", Kind = MediaKind.Episode, AddedAt = DateTimeOffset.UtcNow,
        };
        episodeFile.Episode = new Episode { Show = show, MediaFile = episodeFile, Season = 1, Number = 1 };
        fixture.Db.MediaFiles.Add(episodeFile);
        fixture.Db.MatchAliases.Add(new MatchAlias { Key = "title:avatar", TmdbId = 246, MediaType = "movie" });
        fixture.Metadata.Candidates[("Avatar", "tv")] = [Result(246, "movie", "Samurai"), Result(246, "tv", "Avatar")];
        fixture.Metadata.Details[(246, "tv")] = Result(246, "tv", "Avatar");
        await fixture.Db.SaveChangesAsync();

        await fixture.Enrichment.EnrichAsync();

        Assert.Equal(246, (await fixture.Db.Shows.SingleAsync()).TmdbId);
        Assert.Single(fixture.Metadata.DetailCalls);
        Assert.Equal((246, "tv"), fixture.Metadata.DetailCalls[0]);
    }

    private static TmdbSearchResult Result(int id, string type, string title) =>
        new(id, type, title, null, null, null, null, null, null);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public LibraryDbContext Db { get; }
        public MetadataStub Metadata { get; } = new();
        public ManualMatchService Manual { get; }
        public EnrichmentService Enrichment { get; }

        private Fixture(SqliteConnection connection)
        {
            _connection = connection;
            Db = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options);
            var settings = new SettingsService(Db);
            var seasons = new SeasonEpisodeCache(Db, Metadata);
            Manual = new ManualMatchService(Db, Metadata, seasons, settings, NullLogger<ManualMatchService>.Instance);
            Enrichment = new EnrichmentService(Db, Metadata, new NoLlm(), seasons, settings, NullLogger<EnrichmentService>.Instance);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            await fixture.Db.Database.EnsureCreatedAsync();
            await new SettingsService(fixture.Db).SetAsync(SettingsService.TmdbApiKey, "test-key");
            await new SettingsService(fixture.Db).SetAsync(SettingsService.FetchPosters, "false");
            return fixture;
        }

        public MediaFile AddMovie(string path, string title)
        {
            var file = new MediaFile
            {
                Path = path, FileName = Path.GetFileName(path), Extension = ".mkv",
                Kind = MediaKind.Movie, AddedAt = DateTimeOffset.UtcNow,
            };
            file.Movie = new Movie { Title = title, MediaFile = file };
            Db.MediaFiles.Add(file);
            return file;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class NoLlm : ILlmClient
    {
        public Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(IReadOnlyList<LlmParseInput> items, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LlmParseOutput?>>([]);
    }

    private sealed class MetadataStub : IMetadataProvider
    {
        public Dictionary<(string, string), IReadOnlyList<TmdbSearchResult>> Candidates { get; } = new();
        public Dictionary<(int, string), TmdbSearchResult> Details { get; } = new();
        public List<(int, string)> DetailCalls { get; } = [];
        public TmdbSearchResult? ImdbResult { get; set; }

        public Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(string query, string type, CancellationToken ct = default) =>
            Task.FromResult(Candidates.TryGetValue((query, type), out var found) ? found : (IReadOnlyList<TmdbSearchResult>)[]);
        public Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct = default)
        {
            DetailCalls.Add((tmdbId, type));
            return Task.FromResult(Details.GetValueOrDefault((tmdbId, type)));
        }
        public Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct = default) => Task.FromResult(ImdbResult);
        public Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, TmdbEpisodeInfo>>(new Dictionary<int, TmdbEpisodeInfo>());
        public Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<string?> DownloadPosterAsync(string posterPath, string localFileName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }
}
