using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using LSP.Server.Library.Parsing;
using LSP.Server.Library.Parsing.Parsers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class LibraryRescanTests
{
    [Fact]
    public async Task ScanAsync_PreservesMediaMovieAndShowMetadataByPath()
    {
        var root = Directory.CreateTempSubdirectory("lsp-rescan-");
        try
        {
            var moviePath = Path.Combine(root.FullName, "Example.Movie.2024.mkv");
            var seasonDir = Directory.CreateDirectory(Path.Combine(root.FullName, "Example Show", "Season 01"));
            var episodePath = Path.Combine(seasonDir.FullName, "Example.Show.S01E01.mkv");
            await File.WriteAllTextAsync(moviePath, "movie");
            await File.WriteAllTextAsync(episodePath, "episode");

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            await using (var db = CreateDb(connection))
            {
                await db.Database.EnsureCreatedAsync();
                await CreateScanner(db).ScanAsync([root.FullName]);
            }

            var originalAddedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
            await using (var db = CreateDb(connection))
            {
                var movie = await db.Movies.Include(x => x.MediaFile).SingleAsync();
                movie.DisplayTitle = "Enriched movie";
                movie.TmdbId = 101;
                movie.PosterFile = "101.jpg";
                movie.Overview = "Movie overview";
                movie.Rating = 8.1;
                movie.Genres = "Drama";
                movie.Cast = "Actor";
                movie.IsManual = true;
                movie.MediaFile.AddedAt = originalAddedAt;
                movie.MediaFile.Container = "matroska";
                movie.MediaFile.VideoCodec = "hevc";
                movie.MediaFile.AudioCodec = "aac";
                movie.MediaFile.DurationSeconds = 123.5;
                movie.MediaFile.Width = 1920;
                movie.MediaFile.Height = 1080;

                var show = await db.Shows.SingleAsync();
                show.DisplayTitle = "Enriched show";
                show.TmdbId = 202;
                show.PosterFile = "202.jpg";
                show.Overview = "Show overview";
                show.Rating = 9.2;
                show.Genres = "Comedy";
                show.Cast = "Performer";
                show.SoftTmdbId = 303;
                show.IsManual = true;
                await db.SaveChangesAsync();
            }

            await using (var db = CreateDb(connection))
                await CreateScanner(db).ScanAsync([root.FullName]);

            await using (var db = CreateDb(connection))
            {
                var movie = await db.Movies.AsNoTracking().Include(x => x.MediaFile).SingleAsync();
                Assert.Equal("Enriched movie", movie.DisplayTitle);
                Assert.Equal(101, movie.TmdbId);
                Assert.Equal("101.jpg", movie.PosterFile);
                Assert.Equal("Movie overview", movie.Overview);
                Assert.Equal(8.1, movie.Rating);
                Assert.Equal("Drama", movie.Genres);
                Assert.Equal("Actor", movie.Cast);
                Assert.True(movie.IsManual);
                Assert.Equal(originalAddedAt, movie.MediaFile.AddedAt);
                Assert.Equal("matroska", movie.MediaFile.Container);
                Assert.Equal("hevc", movie.MediaFile.VideoCodec);
                Assert.Equal("aac", movie.MediaFile.AudioCodec);
                Assert.Equal(123.5, movie.MediaFile.DurationSeconds);
                Assert.Equal(1920, movie.MediaFile.Width);
                Assert.Equal(1080, movie.MediaFile.Height);

                var show = await db.Shows.AsNoTracking().SingleAsync();
                Assert.Equal("Enriched show", show.DisplayTitle);
                Assert.Equal(202, show.TmdbId);
                Assert.Equal("202.jpg", show.PosterFile);
                Assert.Equal("Show overview", show.Overview);
                Assert.Equal(9.2, show.Rating);
                Assert.Equal("Comedy", show.Genres);
                Assert.Equal("Performer", show.Cast);
                Assert.Equal(303, show.SoftTmdbId);
                Assert.True(show.IsManual);
            }
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAllAsync_ReusesUnsavedShowForMultipleEpisodeMatches()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        AddMovie(db, "first.mkv");
        AddMovie(db, "second.mkv");
        db.ManualMatches.AddRange(
            EpisodeMatch("first.mkv", episode: 1),
            EpisodeMatch("second.mkv", episode: 2));
        await db.SaveChangesAsync();

        var metadata = new StubMetadataProvider();
        var service = new ManualMatchService(
            db,
            metadata,
            new SeasonEpisodeCache(db, metadata),
            new SettingsService(db),
            NullLogger<ManualMatchService>.Instance);

        await service.ApplyAllAsync();

        var show = await db.Shows.AsNoTracking().SingleAsync();
        Assert.Equal(77, show.TmdbId);
        Assert.True(show.IsManual);
        Assert.Equal(2, await db.Episodes.CountAsync());
        Assert.Empty(await db.Movies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ForceEnrichment_FailureKeepsExistingMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var db = CreateDb(connection))
        {
            await db.Database.EnsureCreatedAsync();
            AddMovie(db, "existing.mkv");
            var movie = db.MediaFiles.Local.Single().Movie!;
            movie.TmdbId = 12;
            movie.DisplayTitle = "Existing title";
            movie.Overview = "Existing overview";
            movie.Cast = "Existing cast";
            await new SettingsService(db).SetAsync(SettingsService.TmdbApiKey, "test-key");
            await db.SaveChangesAsync();

            var metadata = new ThrowingSearchMetadataProvider();
            var service = new EnrichmentService(
                db,
                metadata,
                new EmptyLlmClient(),
                new SeasonEpisodeCache(db, metadata),
                new SettingsService(db),
                NullLogger<EnrichmentService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnrichAsync(force: true));
        }

        await using var verification = CreateDb(connection);
        var stored = await verification.Movies.AsNoTracking().SingleAsync();
        Assert.Equal(12, stored.TmdbId);
        Assert.Equal("Existing title", stored.DisplayTitle);
        Assert.Equal("Existing overview", stored.Overview);
        Assert.Equal("Existing cast", stored.Cast);
    }

    [Fact]
    public async Task ApplyAllAsync_FailureRollsBackAllManualMatches()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var db = CreateDb(connection))
        {
            await db.Database.EnsureCreatedAsync();
            db.Shows.AddRange(
                new Show { Title = "First" },
                new Show { Title = "Second" });
            db.ManualMatches.AddRange(
                ShowMatch("First", 101),
                ShowMatch("Second", 202));
            await db.SaveChangesAsync();

            var metadata = new ThrowingSecondDetailsMetadataProvider();
            var service = new ManualMatchService(
                db,
                metadata,
                new SeasonEpisodeCache(db, metadata),
                new SettingsService(db),
                NullLogger<ManualMatchService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAllAsync());
        }

        await using var verification = CreateDb(connection);
        var shows = await verification.Shows.AsNoTracking().OrderBy(x => x.Title).ToListAsync();
        Assert.All(shows, show =>
        {
            Assert.Null(show.TmdbId);
            Assert.Null(show.DisplayTitle);
            Assert.False(show.IsManual);
        });
    }

    private static LibraryDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(connection)
            .Options;
        return new LibraryDbContext(options);
    }

    private static LibraryScanner CreateScanner(LibraryDbContext db)
    {
        var chain = new MediaParserChain(
            [new SeasonEpisodeParser(), new ThreeDigitEpisodeParser(), new MovieParser()]);
        return new LibraryScanner(db, chain, NullLogger<LibraryScanner>.Instance);
    }

    private static void AddMovie(LibraryDbContext db, string path)
    {
        var file = new MediaFile
        {
            Path = path,
            FileName = path,
            Extension = ".mkv",
            Kind = MediaKind.Movie,
            AddedAt = DateTimeOffset.UtcNow,
        };
        file.Movie = new Movie { Title = path, MediaFile = file };
        db.MediaFiles.Add(file);
    }

    private static ManualMatch EpisodeMatch(string path, int episode) => new()
    {
        Key = path,
        TargetKind = "episode",
        TmdbId = 77,
        MediaType = "tv",
        Season = 1,
        Episode = episode,
        CreatedAt = DateTime.UtcNow,
    };

    private static ManualMatch ShowMatch(string title, int tmdbId) => new()
    {
        Key = ManualMatchService.ShowKeyPrefix + title,
        TargetKind = "show",
        TmdbId = tmdbId,
        MediaType = "tv",
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class EmptyLlmClient : ILlmClient
    {
        public Task<IReadOnlyList<LlmParseOutput?>> ParseBatchAsync(
            IReadOnlyList<LlmParseInput> items,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LlmParseOutput?>>([]);
    }

    private sealed class ThrowingSearchMetadataProvider : StubMetadataProvider
    {
        public override Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(
            string query,
            string type,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated TMDB failure.");
    }

    private sealed class ThrowingSecondDetailsMetadataProvider : StubMetadataProvider
    {
        private int _detailsCalls;

        public override Task<TmdbSearchResult?> GetDetailsAsync(
            int tmdbId,
            string type,
            CancellationToken ct = default)
        {
            if (++_detailsCalls == 2)
                throw new InvalidOperationException("Simulated TMDB failure.");

            return Task.FromResult<TmdbSearchResult?>(new TmdbSearchResult(
                tmdbId, type, $"Show {tmdbId}", "Overview", null, null, 8.5, "Drama", 2024));
        }
    }

    private class StubMetadataProvider : IMetadataProvider
    {
        private static readonly TmdbSearchResult Show = new(
            77, "tv", "Shared Show", "Overview", null, null, 8.5, "Drama", 2024);

        public Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year, CancellationToken ct = default) =>
            Task.FromResult<TmdbSearchResult?>(null);

        public Task<TmdbSearchResult?> SearchTvAsync(string title, CancellationToken ct = default) =>
            Task.FromResult<TmdbSearchResult?>(null);

        public virtual Task<IReadOnlyList<TmdbSearchResult>> SearchCandidatesAsync(
            string query, string type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbSearchResult>>([]);

        public virtual Task<TmdbSearchResult?> GetDetailsAsync(int tmdbId, string type, CancellationToken ct = default) =>
            Task.FromResult<TmdbSearchResult?>(Show);

        public Task<IReadOnlyDictionary<int, TmdbEpisodeInfo>> GetSeasonEpisodesAsync(
            int tmdbId, int season, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, TmdbEpisodeInfo>>(new Dictionary<int, TmdbEpisodeInfo>
            {
                [1] = new(1, "One", null, null),
                [2] = new(2, "Two", null, null),
            });

        public Task<IReadOnlyList<int>> GetTvSeasonNumbersAsync(int tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<int>>([1]);

        public Task<TmdbSearchResult?> FindByImdbAsync(string imdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbSearchResult?>(null);

        public Task<string?> DownloadPosterAsync(
            string posterPath, string localFileName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<int, string>> GetGenreMapAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }
}
