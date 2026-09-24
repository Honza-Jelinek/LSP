using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using LSP.Server.Library.Parsing;
using LSP.Server.Library.Parsing.Parsers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class EnrichmentWorkflowTests
{
    [Fact]
    public async Task ManualCorrection_RescanAndForceEnrichment_KeepUnrelatedFilmsAndProgressSeparate()
    {
        var root = Directory.CreateTempSubdirectory("lsp-enrichment-workflow-");
        try
        {
            var layoverPath = Path.Combine(root.FullName, "The.Layover.2017.mkv");
            var adventurelandPath = Path.Combine(root.FullName, "Adventureland.2009.mkv");
            await File.WriteAllTextAsync(layoverPath, "fixture");
            await File.WriteAllTextAsync(adventurelandPath, "fixture");
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var metadata = new Metadata();
            var llm = new UnexpectedFallback();
            var watchedAt = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

            await using (var db = CreateDb(connection))
            {
                await db.Database.EnsureCreatedAsync();
                await new SettingsService(db).SetAsync(SettingsService.TmdbApiKey, "fixture");
                await new SettingsService(db).SetAsync(SettingsService.LlmApiKey, "fixture");
                await new SettingsService(db).SetAsync(SettingsService.FetchPosters, "false");
                db.LibraryFolders.Add(new LibraryFolder { Path = root.FullName });
                db.PlaybackProgress.Add(new PlaybackProgress
                {
                    Path = adventurelandPath, PositionSeconds = 1234, DurationSeconds = 6000, UpdatedAt = watchedAt,
                });
                // An existing installation may retain a broad rule from the old algorithm.
                db.MatchAliases.Add(new MatchAlias { Key = $"folder:{root.Name}", TmdbId = 339404, MediaType = "movie" });
                await db.SaveChangesAsync();
            }

            await using (var db = CreateDb(connection))
                await Scanner(db).ScanAllAsync();
            await using (var db = CreateDb(connection))
            {
                var file = await db.MediaFiles.SingleAsync(f => f.Path == layoverPath);
                await Manual(db, metadata).ApplyFileAsync(file.Id, "movie", 339404, "movie", null, null, default);
            }

            // Fresh contexts mimic independent API requests and a later rescan.
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    await using (var db = CreateDb(connection))
                        await Scanner(db).ScanAllAsync();
                    await using (var db = CreateDb(connection))
                        await Manual(db, metadata).ApplyAllAsync();
                }
                await using (var db = CreateDb(connection))
                {
                    var service = new EnrichmentService(db, metadata, llm,
                        new SeasonEpisodeCache(db, metadata), new SettingsService(db), NullLogger<EnrichmentService>.Instance);
                    var summary = await service.EnrichAsync(force: pass == 1);
                    Assert.Equal(0, summary.ReviewQueue);
                    Assert.Equal(0, summary.LlmFallbacks);
                }
                await using (var db = CreateDb(connection))
                {
                    var films = await db.Movies.Include(m => m.MediaFile).ToListAsync();
                    Assert.Equal(2, films.Count);
                    Assert.Equal(2, films.Select(m => m.TmdbId).Distinct().Count());
                    var layover = Assert.Single(films, m => m.MediaFile.Path == layoverPath);
                    var adventureland = Assert.Single(films, m => m.MediaFile.Path == adventurelandPath);
                    Assert.Equal(339404, layover.TmdbId);
                    Assert.True(layover.IsManual);
                    Assert.Equal(16614, adventureland.TmdbId);
                    Assert.False(adventureland.IsManual);
                    var progress = await db.PlaybackProgress.SingleAsync();
                    Assert.Equal(adventurelandPath, progress.Path);
                    Assert.Equal(1234, progress.PositionSeconds);
                    Assert.Equal(watchedAt, progress.UpdatedAt);
                }
            }
            Assert.Equal(0, llm.Calls);
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root.FullName).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                root.Delete(recursive: true);
        }
    }

    private static LibraryDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options);

    private static LibraryScanner Scanner(LibraryDbContext db) => new(db,
        new MediaParserChain([new MovieParser()]), NullLogger<LibraryScanner>.Instance);

    private static ManualMatchService Manual(LibraryDbContext db, IMetadataProvider metadata) =>
        new(db, metadata, new SeasonEpisodeCache(db, metadata), new SettingsService(db), NullLogger<ManualMatchService>.Instance);

    private sealed class UnexpectedFallback : ILlmClient
    {
        public int Calls;
        public Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(IReadOnlyList<LlmParseInput> items, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<LlmParseOutput?>>(items.Select(_ =>
                (LlmParseOutput?)new LlmParseOutput("movie", "The Layover", 2017, null, null, null)).ToArray());
        }
    }

    private sealed class Metadata : IMetadataProvider
    {
        private static readonly TmdbSearchResult Layover = new(339404, "movie", "The Layover", null, null, null, null, null, 2017);
        private static readonly TmdbSearchResult Adventureland = new(16614, "movie", "Adventureland", null, null, null, null, null, 2009);
        public Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(string query, string type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbSearchResult>>(type == "movie"
                ? new[] { Layover, Adventureland }.Where(m => MatchScorer.Normalize(m.Title) == MatchScorer.Normalize(query)).ToArray() : []);
        public Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct = default) =>
            Task.FromResult(type == "movie" ? new[] { Layover, Adventureland }.FirstOrDefault(m => m.TmdbId == tmdbId) : null);
        public Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct = default) => Task.FromResult<TmdbSearchResult?>(null);
        public Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, TmdbEpisodeInfo>>(new Dictionary<int, TmdbEpisodeInfo>());
        public Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<string?> DownloadPosterAsync(string posterPath, string localFileName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }
}
