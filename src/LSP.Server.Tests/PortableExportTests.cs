using LSP.Server.Data;
using LSP.Server.Library;
using LSP.Server.Library.Parsing;
using LSP.Server.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class PortableExportTests
{
    [Fact]
    public void SanitizeFileName_RemovesInvalidCharactersAndTrailingDots()
    {
        var input = "Film: Zakazany?   .";

        var result = ExportService.SanitizeFileName(input);

        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('?', result);
        Assert.False(result.EndsWith('.'));
        Assert.False(result.EndsWith(' '));
    }

    [Fact]
    public void BuildNamingPlan_CoversMoviesEpisodesUnmatchedCollisionsAndSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lsp-export-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);

            var movie1Path = CreateFile(source, "movie-one.mkv", 3);
            var movie2Path = CreateFile(source, "movie-two.mkv", 4);
            var episodePath = CreateFile(source, "episode.mkv", 5);
            var unmatchedPath = CreateFile(source, "Puvodni nazev.mp4", 6);
            CreateFile(source, "movie-one.cs.srt", 2);

            var show = new Show { Title = "The Show", DisplayTitle = "Serial", TmdbId = 20 };
            var files = new[]
            {
                MovieFile(1, movie1Path, 3, "Film", 2020),
                MovieFile(2, movie2Path, 4, "Film", 2020),
                new MediaFile
                {
                    Id = 3,
                    Path = episodePath,
                    FileName = Path.GetFileName(episodePath),
                    Extension = ".mkv",
                    SizeBytes = 5,
                    Kind = MediaKind.Episode,
                    Episode = new Episode { Show = show, Season = 1, Number = 2, Title = "Pilot" },
                },
                new MediaFile
                {
                    Id = 4,
                    Path = unmatchedPath,
                    FileName = Path.GetFileName(unmatchedPath),
                    Extension = ".mp4",
                    SizeBytes = 6,
                    Kind = MediaKind.Movie,
                    Movie = new Movie { Title = "Bez TMDB", TmdbId = null },
                },
            };

            var plan = ExportService.BuildNamingPlan(files, target).ToDictionary(x => x.MediaFileId);

            Assert.EndsWith(Path.Combine("Library", "Movies", "Film (2020)", "Film (2020).mkv"), plan[1].TargetPath);
            Assert.EndsWith(Path.Combine("Library", "Movies", "Film (2020)", "Film (2020) (2).mkv"), plan[2].TargetPath);
            Assert.EndsWith(Path.Combine("Library", "Shows", "Serial", "Season 01", "Serial - S01E02 - Pilot.mkv"), plan[3].TargetPath);
            Assert.EndsWith(Path.Combine("Library", "_Nezarazeno", "Puvodni nazev.mp4"), plan[4].TargetPath);
            Assert.False(plan[4].IsMatched);
            var sidecar = Assert.Single(plan[1].Sidecars);
            Assert.EndsWith("Film (2020).cs.srt", sidecar.TargetPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildNamingPlan_SameLengthDifferentContentDoesNotReuseExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lsp-collision-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = CreateFile(root, "source.mkv", 4);
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            var targetDirectory = Path.Combine(root, "target", "Library", "Movies", "Film (2024)");
            Directory.CreateDirectory(targetDirectory);
            var collision = Path.Combine(targetDirectory, "Film (2024).mkv");
            File.WriteAllBytes(collision, [4, 3, 2, 1]);

            var item = MovieFile(1, source, 4, "Film", 2024);
            var plan = Assert.Single(ExportService.BuildNamingPlan([item], Path.Combine(root, "target")));

            Assert.EndsWith("Film (2024) (2).mkv", plan.TargetPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RewriteRoot_UpdatesEveryPathColumnAndSkipsNonPathManualKeys()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        CreatePortableSchema(connection);

        const string oldRoot = @"E:\LSP";
        const string newRoot = @"F:\LSP";
        Execute(connection, "INSERT INTO MediaFiles (Path) VALUES ('E:\\LSP\\Filmy\\film.mkv');");
        Execute(connection, "INSERT INTO LibraryFolders (Path) VALUES ('E:\\LSP\\Filmy');");
        Execute(connection, "INSERT INTO PlaybackProgress (Path) VALUES ('E:\\LSP\\Filmy\\film.mkv');");
        Execute(connection, "INSERT INTO ParseCaches (Path) VALUES ('E:\\LSP\\Filmy\\film.mkv');");
        Execute(connection, "INSERT INTO ManualMatches (Key) VALUES ('E:\\LSP\\Filmy\\film.mkv'), ('show:Serial');");
        Execute(connection, "INSERT INTO Movies (PosterFile) VALUES ('E:\\LSP\\data\\posters\\1.jpg');");
        Execute(connection, "INSERT INTO Shows (PosterFile) VALUES ('E:\\LSP\\data\\posters\\2.jpg');");
        Execute(connection, "INSERT INTO TmdbCaches (PosterFile, BackdropFile) VALUES ('E:\\LSP\\data\\posters\\3.jpg', 'E:\\LSP\\data\\posters\\4.jpg');");

        PortablePathRewriter.RewriteRoot(connection, oldRoot, newRoot);

        Assert.Equal(@"F:\LSP\Filmy\film.mkv", Scalar(connection, "SELECT Path FROM MediaFiles;"));
        Assert.Equal(@"F:\LSP\Filmy", Scalar(connection, "SELECT Path FROM LibraryFolders;"));
        Assert.Equal(@"F:\LSP\Filmy\film.mkv", Scalar(connection, "SELECT Path FROM PlaybackProgress;"));
        Assert.Equal(@"F:\LSP\Filmy\film.mkv", Scalar(connection, "SELECT Path FROM ParseCaches;"));
        Assert.Equal(@"F:\LSP\Filmy\film.mkv", Scalar(connection, "SELECT Key FROM ManualMatches WHERE Key LIKE 'F:%';"));
        Assert.Equal("show:Serial", Scalar(connection, "SELECT Key FROM ManualMatches WHERE Key LIKE 'show:%';"));
        Assert.Equal(@"F:\LSP\data\posters\1.jpg", Scalar(connection, "SELECT PosterFile FROM Movies;"));
        Assert.Equal(@"F:\LSP\data\posters\2.jpg", Scalar(connection, "SELECT PosterFile FROM Shows;"));
        Assert.Equal(@"F:\LSP\data\posters\3.jpg", Scalar(connection, "SELECT PosterFile FROM TmdbCaches;"));
        Assert.Equal(@"F:\LSP\data\posters\4.jpg", Scalar(connection, "SELECT BackdropFile FROM TmdbCaches;"));
    }

    [Fact]
    public async Task ExportAsync_SelectedIds_PrunesUnselectedRowsFromPackageDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lsp-selected-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourceDbPath = Path.Combine(root, "source.db");
            var packageRoot = Path.Combine(root, "package");
            var packageDbPath = Path.Combine(packageRoot, "data", "library.db");
            Directory.CreateDirectory(Path.Combine(packageRoot, "app"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "data"));
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "app", "portable.txt"), "portable");

            await using var sourceDb = CreateContext(sourceDbPath);
            await sourceDb.Database.MigrateAsync();
            await using (var packageDb = CreateContext(packageDbPath))
                await packageDb.Database.MigrateAsync();

            var selectedPath = CreateFile(root, "selected.mkv", 8);
            var unselectedPath = CreateFile(root, "unselected.mkv", 9);
            var selected = MovieFile(0, selectedPath, 8, "Selected", 2024);
            selected.Movie!.TmdbId = 101;
            var unselected = MovieFile(0, unselectedPath, 9, "Unselected", 2023);
            unselected.Movie!.TmdbId = 202;
            sourceDb.MediaFiles.AddRange(selected, unselected);
            sourceDb.PlaybackProgress.AddRange(
                new PlaybackProgress { Path = selectedPath, UpdatedAt = DateTime.UtcNow },
                new PlaybackProgress { Path = unselectedPath, UpdatedAt = DateTime.UtcNow });
            sourceDb.ParseCaches.AddRange(
                new ParseCache { Path = selectedPath, Kind = MediaKind.Movie, Title = "Selected" },
                new ParseCache { Path = unselectedPath, Kind = MediaKind.Movie, Title = "Unselected" });
            sourceDb.ManualMatches.AddRange(
                new ManualMatch { Key = selectedPath, TargetKind = "movie", TmdbId = 101, MediaType = "movie" },
                new ManualMatch { Key = unselectedPath, TargetKind = "movie", TmdbId = 202, MediaType = "movie" });
            sourceDb.TmdbCaches.AddRange(
                new TmdbCache { QueryKey = "selected||movie", TmdbId = 101, FetchedAt = DateTime.UtcNow },
                new TmdbCache { QueryKey = "unselected||movie", TmdbId = 202, FetchedAt = DateTime.UtcNow });
            await sourceDb.SaveChangesAsync();

            var service = new ExportService(
                sourceDb,
                new FfmpegLocator(new ConfigurationBuilder().Build()),
                NullLogger<ExportService>.Instance);
            var report = await service.ExportAsync(
                new ExportRequest(packageRoot, [selected.Id], IncludePosters: false),
                new TestProgress<ExportProgress>());

            Assert.Equal(1, report.NewFiles);
            await using var resultDb = CreateContext(packageDbPath);
            var exported = Assert.Single(await resultDb.MediaFiles.AsNoTracking().ToListAsync());
            Assert.Contains("Selected (2024)", exported.Path);
            Assert.Single(await resultDb.Movies.AsNoTracking().ToListAsync());
            Assert.Single(await resultDb.PlaybackProgress.AsNoTracking().ToListAsync());
            Assert.Single(await resultDb.ParseCaches.AsNoTracking().ToListAsync());
            Assert.Single(await resultDb.ManualMatches.AsNoTracking().ToListAsync());
            Assert.Equal(101, Assert.Single(await resultDb.TmdbCaches.AsNoTracking().ToListAsync()).TmdbId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_InvalidPackageLeavesLiveDatabaseUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lsp-invalid-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var liveDbPath = Path.Combine(root, "live", "library.db");
            var packageRoot = Path.Combine(root, "package");
            var packageDbPath = Path.Combine(packageRoot, "data", "library.db");
            Directory.CreateDirectory(Path.GetDirectoryName(liveDbPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(packageDbPath)!);
            await File.WriteAllTextAsync(packageDbPath, "not a sqlite database");

            await using (var liveDb = CreateContext(liveDbPath))
            {
                await liveDb.Database.MigrateAsync();
                liveDb.Settings.Add(new Setting { Key = "marker", Value = "original" });
                await liveDb.SaveChangesAsync();

                var service = CreateExportService(liveDb);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(packageRoot));
            }

            await using var verification = CreateContext(liveDbPath);
            Assert.Equal("original", (await verification.Settings.AsNoTracking().SingleAsync(x => x.Key == "marker")).Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_FileFailureDoesNotPublishDatabaseOrDeleteMoveSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lsp-failed-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourceDbPath = Path.Combine(root, "source.db");
            var packageRoot = Path.Combine(root, "package");
            var packageDbPath = Path.Combine(packageRoot, "data", "library.db");
            Directory.CreateDirectory(Path.Combine(packageRoot, "app"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "data"));
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "app", "portable.txt"), "portable");

            await using var sourceDb = CreateContext(sourceDbPath);
            await sourceDb.Database.MigrateAsync();
            await using (var packageDb = CreateContext(packageDbPath))
            {
                await packageDb.Database.MigrateAsync();
                packageDb.Settings.Add(new Setting { Key = "marker", Value = "unchanged" });
                await packageDb.SaveChangesAsync();
            }

            var existingSource = CreateFile(root, "existing.mkv", 8);
            var missingSource = Path.Combine(root, "missing.mkv");
            sourceDb.MediaFiles.AddRange(
                MovieFile(0, existingSource, 8, "Existing", 2024),
                MovieFile(0, missingSource, 8, "Missing", 2024));
            await sourceDb.SaveChangesAsync();

            var report = await CreateExportService(sourceDb).ExportAsync(
                new ExportRequest(
                    packageRoot,
                    await sourceDb.MediaFiles.Select(x => x.Id).ToListAsync(),
                    Move: true,
                    IncludePosters: false),
                new TestProgress<ExportProgress>());

            Assert.NotEmpty(report.Failures);
            Assert.True(File.Exists(existingSource));
            await using var verification = CreateContext(packageDbPath);
            Assert.Empty(await verification.MediaFiles.AsNoTracking().ToListAsync());
            Assert.Equal("unchanged", (await verification.Settings.AsNoTracking().SingleAsync(x => x.Key == "marker")).Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_ValidPackagePublishesMigratedDatabaseAndKeepsBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lsp-valid-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var liveDirectory = Path.Combine(root, "live");
            var liveDbPath = Path.Combine(liveDirectory, "library.db");
            var packageRoot = Path.Combine(root, "package");
            var packageDbPath = Path.Combine(packageRoot, "data", "library.db");
            Directory.CreateDirectory(liveDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(packageDbPath)!);

            await using (var packageDb = CreateContext(packageDbPath))
            {
                await packageDb.Database.MigrateAsync();
                packageDb.Settings.Add(new Setting { Key = "marker", Value = "imported" });
                await packageDb.SaveChangesAsync();
            }

            await using (var liveDb = CreateContext(liveDbPath))
            {
                await liveDb.Database.MigrateAsync();
                liveDb.Settings.Add(new Setting { Key = "marker", Value = "original" });
                await liveDb.SaveChangesAsync();

                var result = await CreateExportService(liveDb).ImportAsync(packageRoot);
                Assert.Equal(Path.GetFullPath(packageRoot), result.PackageRoot);
            }

            await using var verification = CreateContext(liveDbPath);
            Assert.Equal("imported", (await verification.Settings.AsNoTracking().SingleAsync(x => x.Key == "marker")).Value);
            Assert.Single(Directory.EnumerateFiles(liveDirectory, "library.before-import-*.bak"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static MediaFile MovieFile(int id, string path, long size, string title, int year) => new()
    {
        Id = id,
        Path = path,
        FileName = Path.GetFileName(path),
        Extension = ".mkv",
        SizeBytes = size,
        Kind = MediaKind.Movie,
        Movie = new Movie { Title = title, DisplayTitle = title, Year = year, TmdbId = 10 },
    };

    private static string CreateFile(string directory, string name, int length)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static void CreatePortableSchema(SqliteConnection connection)
    {
        Execute(connection, "CREATE TABLE MediaFiles (Id INTEGER PRIMARY KEY, Path TEXT);");
        Execute(connection, "CREATE TABLE LibraryFolders (Id INTEGER PRIMARY KEY, Path TEXT);");
        Execute(connection, "CREATE TABLE PlaybackProgress (Id INTEGER PRIMARY KEY, Path TEXT);");
        Execute(connection, "CREATE TABLE ParseCaches (Id INTEGER PRIMARY KEY, Path TEXT);");
        Execute(connection, "CREATE TABLE ManualMatches (Id INTEGER PRIMARY KEY, Key TEXT);");
        Execute(connection, "CREATE TABLE Movies (Id INTEGER PRIMARY KEY, PosterFile TEXT);");
        Execute(connection, "CREATE TABLE Shows (Id INTEGER PRIMARY KEY, PosterFile TEXT);");
        Execute(connection, "CREATE TABLE TmdbCaches (Id INTEGER PRIMARY KEY, PosterFile TEXT, BackdropFile TEXT);");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static LibraryDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;
        return new LibraryDbContext(options);
    }

    private static ExportService CreateExportService(LibraryDbContext db) => new(
        db,
        new FfmpegLocator(new ConfigurationBuilder().Build()),
        NullLogger<ExportService>.Instance);

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private sealed class TestProgress<T> : IProgress<T>
    {
        public void Report(T value) { }
    }
}
