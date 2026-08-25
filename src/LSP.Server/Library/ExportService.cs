using System.Buffers;
using LSP.Server.Data;
using LSP.Server.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Library;

public sealed record ExportRequest(
    string TargetRoot,
    IReadOnlyList<int> MediaFileIds,
    bool Move = false,
    bool IncludePosters = true);

public sealed record ExportProgress(
    string Phase,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentFile);

public sealed record ExportReport(
    bool Extended,
    int NewFiles,
    int ExistingFiles,
    int SkippedFiles,
    IReadOnlyList<string> UnmatchedFiles,
    IReadOnlyList<string> Failures);

public sealed record ImportResult(string PackageRoot, int PostersCopied);

public sealed record ExportSidecarPlan(string SourcePath, string TargetPath);

public sealed record ExportFilePlan(
    int MediaFileId,
    string SourcePath,
    string TargetPath,
    long SizeBytes,
    bool IsMatched,
    IReadOnlyList<ExportSidecarPlan> Sidecars);

/// <summary>Vytvari a rozsiruje portable baliky a umi jejich databazi importovat jako nahradu knihovny.</summary>
public sealed class ExportService(
    LibraryDbContext db,
    FfmpegLocator ffmpeg,
    ILogger<ExportService> log)
{
    private const int CopyBufferSize = 1024 * 1024;

    public static bool IsExistingPackage(string targetRoot)
    {
        var root = NormalizeRoot(targetRoot);
        return File.Exists(Path.Combine(root, "app", "portable.txt"))
               && File.Exists(Path.Combine(root, "data", "library.db"));
    }

    public async Task<ExportReport> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress> progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        ValidateExportTarget(request.TargetRoot);
        var targetRoot = NormalizeRoot(request.TargetRoot);
        var selectedIds = request.MediaFileIds.Distinct().ToArray();
        if (selectedIds.Length == 0)
            throw new InvalidOperationException("Vyberte alespon jeden film nebo serial.");
        Directory.CreateDirectory(targetRoot);

        var extended = IsExistingPackage(targetRoot);
        var plan = await BuildNamingPlanAsync(targetRoot, selectedIds, ct);
        if (plan.Count != selectedIds.Length)
            throw new InvalidOperationException("Nektere vybrane polozky uz v knihovne neexistuji. Obnovte vyber.");
        var totalBytes = plan.Sum(x => x.SizeBytes);
        progress.Report(new ExportProgress("Priprava", 0, plan.Count, 0, totalBytes, null));

        if (!extended)
        {
            EnsureFreshTargetDoesNotContainDatabase(targetRoot);
            CopyApplication(targetRoot);
        }

        var dataDir = Path.Combine(targetRoot, "data");
        Directory.CreateDirectory(dataDir);
        var packageDbPath = Path.Combine(dataDir, "library.db");
        var exportId = Guid.NewGuid().ToString("N");
        var incomingSnapshot = Path.Combine(dataDir, $".incoming-{exportId}.db");
        var publishSnapshot = extended
            ? Path.Combine(dataDir, $".publish-{exportId}.db")
            : incomingSnapshot;

        try
        {
            progress.Report(new ExportProgress("Databaze", 0, plan.Count, 0, totalBytes, null));
            await CreateRewrittenSnapshotAsync(
                incomingSnapshot,
                targetRoot,
                plan,
                request.IncludePosters,
                ct);

            if (extended)
            {
                SqliteConnection.ClearAllPools();
                await CreateDatabaseSnapshotAsync(packageDbPath, publishSnapshot, ct);
                await MergeSnapshotAsync(incomingSnapshot, publishSnapshot, ct);
            }

            var report = await CopyMediaAsync(request, targetRoot, plan, extended, progress, totalBytes, ct);
            if (report.Failures.Count > 0)
            {
                log.LogWarning("Export DB nebyla publikovana kvuli {Count} chybam souboru", report.Failures.Count);
                return report;
            }

            var postersCopied = request.IncludePosters
                ? CopySelectedPosters(publishSnapshot, Path.Combine(dataDir, "posters"), overwrite: true)
                : 0;
            log.LogInformation("Export posteru: {Count}", postersCopied);

            PublishPackageDatabase(publishSnapshot, packageDbPath);

            if (request.Move)
            {
                var moveFailures = DeleteMovedSources(plan);
                if (moveFailures.Count > 0)
                    report = report with { Failures = moveFailures };
            }

            return report;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(incomingSnapshot);
            if (!PathsEqual(incomingSnapshot, publishSnapshot))
                TryDeleteFile(publishSnapshot);
        }
    }

    public async Task<ImportResult> ImportAsync(string packageRoot, CancellationToken ct = default)
    {
        var root = NormalizeRoot(packageRoot);
        var packageDb = Path.Combine(root, "data", "library.db");
        if (!File.Exists(packageDb))
            throw new InvalidOperationException("Vybrana slozka neobsahuje data\\library.db.");

        var localDb = GetLiveDatabasePath();
        var localDataDir = Path.GetDirectoryName(localDb)
            ?? throw new InvalidOperationException("Nelze urcit datovy adresar aplikace.");
        Directory.CreateDirectory(localDataDir);

        var importId = Guid.NewGuid().ToString("N");
        var stagingDb = Path.Combine(localDataDir, $".import-{importId}.db");
        var stagingPosters = Path.Combine(localDataDir, $".import-posters-{importId}");
        var posterDirectory = Path.Combine(localDataDir, "posters");
        var backupDb = Path.Combine(localDataDir, $"library.before-import-{DateTime.UtcNow:yyyyMMddHHmmss}-{importId}.bak");
        var backupPosters = Path.Combine(localDataDir, $"posters.before-import-{DateTime.UtcNow:yyyyMMddHHmmss}-{importId}");
        var postersSwitched = false;
        var databasePublished = false;

        try
        {
            SqliteConnection.ClearAllPools();
            try
            {
                await CreateDatabaseSnapshotAsync(packageDb, stagingDb, ct);
                await PrepareImportDatabaseAsync(stagingDb, root, posterDirectory, ct);
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException("Databaze baliku je poskozena nebo nekompatibilni.", ex);
            }

            Directory.CreateDirectory(stagingPosters);
            var postersCopied = await CopyDirectoryAsync(
                Path.Combine(root, "data", "posters"),
                stagingPosters,
                ct);

            ct.ThrowIfCancellationRequested();
            SqliteConnection.ClearAllPools();
            DeleteSqliteSidecar(localDb + "-wal");
            DeleteSqliteSidecar(localDb + "-shm");

            if (Directory.Exists(posterDirectory))
                Directory.Move(posterDirectory, backupPosters);
            Directory.Move(stagingPosters, posterDirectory);
            postersSwitched = true;

            if (File.Exists(localDb))
                File.Replace(stagingDb, localDb, backupDb, ignoreMetadataErrors: true);
            else
                File.Move(stagingDb, localDb);
            databasePublished = true;
            SqliteConnection.ClearAllPools();

            log.LogInformation(
                "Import publikovan z {Root}; zaloha DB: {BackupDb}; zaloha posteru: {BackupPosters}",
                root,
                File.Exists(backupDb) ? backupDb : null,
                Directory.Exists(backupPosters) ? backupPosters : null);
            return new ImportResult(root, postersCopied);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (databasePublished && File.Exists(backupDb))
                File.Replace(backupDb, localDb, null, ignoreMetadataErrors: true);

            if (postersSwitched)
            {
                if (Directory.Exists(posterDirectory))
                    Directory.Delete(posterDirectory, recursive: true);
                if (Directory.Exists(backupPosters))
                    Directory.Move(backupPosters, posterDirectory);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(stagingDb);
            TryDeleteDirectory(stagingPosters);
        }
    }

    private async Task PrepareImportDatabaseAsync(
        string stagingDb,
        string packageRoot,
        string posterDirectory,
        CancellationToken ct)
    {
        await using (var stagingContext = CreateContext(stagingDb))
        {
            var knownMigrations = stagingContext.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var appliedMigrations = await stagingContext.Database.GetAppliedMigrationsAsync(ct);
            var unknownMigrations = appliedMigrations.Where(x => !knownMigrations.Contains(x)).ToArray();
            if (unknownMigrations.Length > 0)
                throw new InvalidOperationException(
                    $"Balik pochazi z novejsi verze aplikace ({string.Join(", ", unknownMigrations)}).");

            await stagingContext.Database.MigrateAsync(ct);
        }

        SqliteConnection.ClearAllPools();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = stagingDb }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            var sqliteTransaction = (SqliteTransaction)transaction;
            var storedRoot = PortablePathRewriter.GetSetting(
                connection,
                PortablePathRewriter.LastRootSetting,
                sqliteTransaction);
            if (!string.IsNullOrWhiteSpace(storedRoot)
                && !string.Equals(NormalizeRoot(storedRoot), packageRoot, StringComparison.OrdinalIgnoreCase))
            {
                PortablePathRewriter.RewriteRoot(connection, storedRoot, packageRoot, sqliteTransaction);
            }

            RewritePosterColumns(connection, posterDirectory, includePosters: true, sqliteTransaction);
            await ExecuteAsync(
                connection,
                "DELETE FROM Settings WHERE Key = @key;",
                [new SqliteParameter("@key", PortablePathRewriter.LastRootSetting)],
                sqliteTransaction,
                ct);
            await transaction.CommitAsync(ct);
        }

        await ValidateDatabaseIntegrityAsync(connection, ct);
    }

    private string GetLiveDatabasePath()
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var dataSource = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
            throw new InvalidOperationException("Import vyzaduje souborovou SQLite databazi.");
        return Path.GetFullPath(dataSource);
    }

    private static async Task ValidateDatabaseIntegrityAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await quickCheck.ExecuteScalarAsync(ct));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Kontrola integrity databaze selhala: {result}");
        }

        await using var foreignKeyCheck = connection.CreateCommand();
        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
        await using var violations = await foreignKeyCheck.ExecuteReaderAsync(ct);
        if (await violations.ReadAsync(ct))
            throw new InvalidOperationException("Databaze baliku obsahuje porusene vazby.");
    }

    private static async Task<int> CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        if (!Directory.Exists(source))
            return 0;

        var copied = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, sourceFile);
            var targetFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await CopyFileAsync(sourceFile, targetFile, ct);
            copied++;
        }
        return copied;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var sourceStream = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous);
        await using var destinationStream = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);
        await sourceStream.CopyToAsync(destinationStream, CopyBufferSize, ct);
        await destinationStream.FlushAsync(ct);
    }

    private static async Task CreateDatabaseSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(ct);
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);
        ct.ThrowIfCancellationRequested();
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Docasny soubor {Path} se nepodarilo odstranit", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Docasny adresar {Path} se nepodarilo odstranit", path);
        }
    }

    public async Task<IReadOnlyList<ExportFilePlan>> BuildNamingPlanAsync(
        string targetRoot,
        IReadOnlyCollection<int> mediaFileIds,
        CancellationToken ct = default)
    {
        var selectedIds = mediaFileIds.Distinct().ToArray();
        var media = await db.MediaFiles
            .AsNoTracking()
            .Where(x => selectedIds.Contains(x.Id))
            .Include(x => x.Movie)
            .Include(x => x.Episode).ThenInclude(x => x!.Show)
            .OrderBy(x => x.Path)
            .ToListAsync(ct);
        return BuildNamingPlan(media, targetRoot);
    }

    public static IReadOnlyList<ExportFilePlan> BuildNamingPlan(
        IEnumerable<MediaFile> mediaFiles,
        string targetRoot,
        IReadOnlyCollection<int> mediaFileIds)
    {
        var selectedIds = mediaFileIds.ToHashSet();
        return BuildNamingPlan(mediaFiles.Where(x => selectedIds.Contains(x.Id)), targetRoot);
    }

    public static IReadOnlyList<ExportFilePlan> BuildNamingPlan(
        IEnumerable<MediaFile> mediaFiles,
        string targetRoot)
    {
        var root = NormalizeRoot(targetRoot);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExportFilePlan>();

        foreach (var media in mediaFiles.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var (relativeDirectory, baseName, matched) = GetTargetName(media);
            var targetDirectory = Path.Combine(root, relativeDirectory);
            var extension = string.IsNullOrWhiteSpace(media.Extension)
                ? Path.GetExtension(media.Path)
                : media.Extension;
            if (!extension.StartsWith('.'))
                extension = "." + extension;

            var targetPath = AllocateTargetPath(
                targetDirectory,
                baseName,
                extension,
                media.Path,
                reserved);
            var sidecars = BuildSidecarPlan(media.Path, targetPath, reserved);
            result.Add(new ExportFilePlan(
                media.Id,
                media.Path,
                targetPath,
                media.SizeBytes,
                matched,
                sidecars));
        }

        return result;
    }

    public static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static (string Directory, string BaseName, bool Matched) GetTargetName(MediaFile media)
    {
        if (media.Movie is { TmdbId: not null } movie)
        {
            var title = SanitizeFileName(movie.DisplayTitle ?? movie.Title);
            var name = movie.Year is { } year ? $"{title} ({year})" : title;
            return (Path.Combine("Library", "Movies", name), name, true);
        }

        if (media.Episode is { Show.TmdbId: not null } episode)
        {
            var show = SanitizeFileName(episode.Show.DisplayTitle ?? episode.Show.Title);
            var episodeTitle = string.IsNullOrWhiteSpace(episode.Title)
                ? null
                : SanitizeFileName(episode.Title);
            var name = $"{show} - S{episode.Season:00}E{episode.Number:00}"
                       + (episodeTitle is null ? "" : $" - {episodeTitle}");
            return (Path.Combine("Library", "Shows", show, $"Season {episode.Season:00}"), name, true);
        }

        return (
            Path.Combine("Library", "_Nezarazeno"),
            SanitizeFileName(Path.GetFileNameWithoutExtension(media.Path)),
            false);
    }

    private static string AllocateTargetPath(
        string directory,
        string baseName,
        string extension,
        string sourcePath,
        ISet<string> reserved)
    {
        for (var suffix = 1; ; suffix++)
        {
            var candidateName = suffix == 1 ? baseName : $"{baseName} ({suffix})";
            var candidate = Path.Combine(directory, candidateName + extension);
            var collidesWithPlan = reserved.Contains(candidate);
            var collidesWithDifferentFile = File.Exists(candidate)
                                            && !FilesHaveSameContent(sourcePath, candidate);
            if (collidesWithPlan || collidesWithDifferentFile)
                continue;

            reserved.Add(candidate);
            return candidate;
        }
    }

    private static IReadOnlyList<ExportSidecarPlan> BuildSidecarPlan(
        string sourceVideo,
        string targetVideo,
        ISet<string> reserved)
    {
        var sourceBase = Path.GetFileNameWithoutExtension(sourceVideo);
        var targetBase = Path.GetFileNameWithoutExtension(targetVideo);
        var targetDirectory = Path.GetDirectoryName(targetVideo)!;
        var result = new List<ExportSidecarPlan>();

        foreach (var sidecar in FindSidecarFiles(sourceVideo))
        {
            var sidecarBase = Path.GetFileNameWithoutExtension(sidecar);
            var suffix = sidecarBase[sourceBase.Length..];
            var candidate = Path.Combine(targetDirectory, targetBase + suffix + Path.GetExtension(sidecar));
            if (reserved.Add(candidate))
                result.Add(new ExportSidecarPlan(sidecar, candidate));
        }

        return result;
    }

    public static IReadOnlyList<string> FindSidecarFiles(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase)
                           || Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetFileNameWithoutExtension(path)
                .StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task CreateRewrittenSnapshotAsync(
        string snapshotPath,
        string targetRoot,
        IReadOnlyList<ExportFilePlan> plan,
        bool includePosters,
        CancellationToken ct)
    {
        db.Database.OpenConnection();
        var liveConnection = (SqliteConnection)db.Database.GetDbConnection();
        await using (var command = liveConnection.CreateCommand())
        {
            command.CommandText = "VACUUM INTO @path;";
            command.Parameters.AddWithValue("@path", snapshotPath);
            await command.ExecuteNonQueryAsync(ct);
        }

        var connectionString = new SqliteConnectionStringBuilder { DataSource = snapshotPath }.ToString();
        await using var snapshot = new SqliteConnection(connectionString);
        await snapshot.OpenAsync(ct);
        await using (var pragma = snapshot.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }
        await using var transaction = await snapshot.BeginTransactionAsync(ct);
        var sqliteTransaction = (SqliteTransaction)transaction;

        await CreateSelectionTablesAsync(snapshot, plan, sqliteTransaction, ct);

        foreach (var item in plan)
        {
            await ExecuteAsync(
                snapshot,
                "UPDATE MediaFiles SET Path = @new, FileName = @fileName WHERE Path = @old;",
                [
                    new SqliteParameter("@new", item.TargetPath),
                    new SqliteParameter("@fileName", Path.GetFileName(item.TargetPath)),
                    new SqliteParameter("@old", item.SourcePath),
                ],
                sqliteTransaction,
                ct);

            foreach (var (table, column) in new[]
                     {
                         ("PlaybackProgress", "Path"),
                         ("ParseCaches", "Path"),
                         ("ManualMatches", "Key"),
                     })
            {
                await ExecuteAsync(
                    snapshot,
                    $"UPDATE {table} SET {column} = @new WHERE {column} = @old;",
                    [new SqliteParameter("@new", item.TargetPath), new SqliteParameter("@old", item.SourcePath)],
                    sqliteTransaction,
                    ct);
            }
        }

        await PruneUnselectedRowsAsync(snapshot, sqliteTransaction, ct);

        await ExecuteAsync(snapshot, "DELETE FROM LibraryFolders;", [], sqliteTransaction, ct);
        await ExecuteAsync(
            snapshot,
            "INSERT INTO LibraryFolders (Path, AddedAt) VALUES (@path, @addedAt);",
            [
                new SqliteParameter("@path", Path.Combine(targetRoot, "Library")),
                new SqliteParameter("@addedAt", DateTimeOffset.UtcNow),
            ],
            sqliteTransaction,
            ct);

        RewritePosterColumns(snapshot, Path.Combine(targetRoot, "data", "posters"), includePosters, sqliteTransaction);
        PortablePathRewriter.SetSetting(snapshot, PortablePathRewriter.LastRootSetting, targetRoot, sqliteTransaction);
        await transaction.CommitAsync(ct);
    }

    private async Task MergeSnapshotAsync(string snapshotPath, string packageDbPath, CancellationToken ct)
    {
        SqliteConnection.ClearAllPools();
        await using var incoming = CreateContext(snapshotPath);
        await using var package = CreateContext(packageDbPath);

        var packageShows = await package.Shows
            .Include(x => x.Episodes)
            .ThenInclude(x => x.MediaFile)
            .ToDictionaryAsync(x => x.Title, StringComparer.OrdinalIgnoreCase, ct);
        var incomingShows = await incoming.Shows.AsNoTracking().ToListAsync(ct);
        foreach (var source in incomingShows)
        {
            if (!packageShows.TryGetValue(source.Title, out var target))
            {
                target = CloneShow(source);
                package.Shows.Add(target);
                packageShows[source.Title] = target;
            }
            else
            {
                ApplyShowMetadata(target, source);
            }
        }

        var existingMedia = await package.MediaFiles
            .Include(x => x.Movie)
            .Include(x => x.Episode)
            .ToDictionaryAsync(x => x.Path, StringComparer.OrdinalIgnoreCase, ct);
        var incomingMedia = await incoming.MediaFiles
            .AsNoTracking()
            .Include(x => x.Movie)
            .Include(x => x.Episode).ThenInclude(x => x!.Show)
            .ToListAsync(ct);

        foreach (var source in incomingMedia)
        {
            if (existingMedia.TryGetValue(source.Path, out var existing))
            {
                ApplyMediaMetadata(existing, source);
                continue;
            }

            var target = CloneMediaFile(source);
            if (source.Movie is not null)
                target.Movie = CloneMovie(source.Movie);
            if (source.Episode is not null)
            {
                var targetShow = packageShows[source.Episode.Show.Title];
                target.Episode = new Episode
                {
                    Show = targetShow,
                    Season = source.Episode.Season,
                    Number = source.Episode.Number,
                    Title = source.Episode.Title,
                };
            }
            package.MediaFiles.Add(target);
            existingMedia[target.Path] = target;
        }

        await MergeProgressAsync(incoming, package, ct);
        await MergeSimpleCachesAsync(incoming, package, ct);
        await package.SaveChangesAsync(ct);
    }

    private static async Task MergeProgressAsync(
        LibraryDbContext incoming,
        LibraryDbContext package,
        CancellationToken ct)
    {
        var target = await package.PlaybackProgress.ToDictionaryAsync(x => x.Path, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var source in await incoming.PlaybackProgress.AsNoTracking().ToListAsync(ct))
        {
            if (!target.TryGetValue(source.Path, out var existing))
            {
                package.PlaybackProgress.Add(new PlaybackProgress
                {
                    Path = source.Path,
                    PositionSeconds = source.PositionSeconds,
                    DurationSeconds = source.DurationSeconds,
                    Finished = source.Finished,
                    UpdatedAt = source.UpdatedAt,
                });
            }
            else if (source.UpdatedAt > existing.UpdatedAt)
            {
                existing.PositionSeconds = source.PositionSeconds;
                existing.DurationSeconds = source.DurationSeconds;
                existing.Finished = source.Finished;
                existing.UpdatedAt = source.UpdatedAt;
            }
        }
    }

    private static async Task MergeSimpleCachesAsync(
        LibraryDbContext incoming,
        LibraryDbContext package,
        CancellationToken ct)
    {
        var parsePaths = await package.ParseCaches.ToDictionaryAsync(x => x.Path, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var x in await incoming.ParseCaches.AsNoTracking().ToListAsync(ct))
        {
            if (!parsePaths.TryGetValue(x.Path, out var existing))
                package.ParseCaches.Add(CloneParseCache(x));
            else if (x.UpdatedAt >= existing.UpdatedAt)
                ApplyParseCache(existing, x);
        }

        var manualKeys = await package.ManualMatches.ToDictionaryAsync(x => x.Key, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var x in await incoming.ManualMatches.AsNoTracking().ToListAsync(ct))
        {
            if (!manualKeys.TryGetValue(x.Key, out var existing))
                package.ManualMatches.Add(CloneManualMatch(x));
            else if (x.CreatedAt >= existing.CreatedAt)
                ApplyManualMatch(existing, x);
        }

        var aliasKeys = await package.MatchAliases.ToDictionaryAsync(x => x.Key, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var x in await incoming.MatchAliases.AsNoTracking().ToListAsync(ct))
        {
            if (!aliasKeys.TryGetValue(x.Key, out var existing))
                package.MatchAliases.Add(CloneMatchAlias(x));
            else if (x.CreatedAt >= existing.CreatedAt)
                ApplyMatchAlias(existing, x);
        }

        var tmdbKeys = await package.TmdbCaches.ToDictionaryAsync(x => x.QueryKey, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var x in await incoming.TmdbCaches.AsNoTracking().ToListAsync(ct))
        {
            if (!tmdbKeys.TryGetValue(x.QueryKey, out var existing))
                package.TmdbCaches.Add(CloneTmdbCache(x));
            else if (x.FetchedAt >= existing.FetchedAt)
                ApplyTmdbCache(existing, x);
        }

        var seasons = await package.TmdbSeasonCaches
            .ToDictionaryAsync(x => (x.TmdbId, x.Season), ct);
        foreach (var x in await incoming.TmdbSeasonCaches.AsNoTracking().ToListAsync(ct))
        {
            if (!seasons.TryGetValue((x.TmdbId, x.Season), out var existing))
            {
                package.TmdbSeasonCaches.Add(new TmdbSeasonCache
                {
                    TmdbId = x.TmdbId,
                    Season = x.Season,
                    Data = x.Data,
                    FetchedAt = x.FetchedAt,
                });
            }
            else if (x.FetchedAt >= existing.FetchedAt)
            {
                existing.Data = x.Data;
                existing.FetchedAt = x.FetchedAt;
            }
        }
    }

    private async Task<ExportReport> CopyMediaAsync(
        ExportRequest request,
        string targetRoot,
        IReadOnlyList<ExportFilePlan> plan,
        bool extended,
        IProgress<ExportProgress> progress,
        long totalBytes,
        CancellationToken ct)
    {
        var newFiles = 0;
        var existingFiles = 0;
        var skippedFiles = 0;
        long bytesDone = 0;
        var unmatched = plan.Where(x => !x.IsMatched)
            .Select(x => Path.GetRelativePath(targetRoot, x.TargetPath))
            .ToList();
        var failures = new List<string>();

        for (var index = 0; index < plan.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = plan[index];
            progress.Report(new ExportProgress(
                "Videa",
                index,
                plan.Count,
                bytesDone,
                totalBytes,
                Path.GetFileName(item.SourcePath)));

            try
            {
                if (!File.Exists(item.SourcePath))
                {
                    skippedFiles++;
                    failures.Add($"Zdroj neexistuje: {item.SourcePath}");
                    bytesDone += item.SizeBytes;
                    continue;
                }

                if (File.Exists(item.TargetPath)
                    && FilesHaveSameContent(item.SourcePath, item.TargetPath))
                {
                    existingFiles++;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    await CopyFileWithProgressAsync(
                        item.SourcePath,
                        item.TargetPath,
                        copied => progress.Report(new ExportProgress(
                            "Videa",
                            index,
                            plan.Count,
                            bytesDone + copied,
                            totalBytes,
                            Path.GetFileName(item.SourcePath))),
                        ct);
                    VerifySameLength(item.SourcePath, item.TargetPath);
                    newFiles++;
                }

                foreach (var sidecar in item.Sidecars)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(sidecar.TargetPath)!);
                    if (!File.Exists(sidecar.TargetPath)
                        || new FileInfo(sidecar.TargetPath).Length != new FileInfo(sidecar.SourcePath).Length)
                    {
                        await CopyFileAtomicallyAsync(sidecar.SourcePath, sidecar.TargetPath, ct);
                    }
                    VerifySameLength(sidecar.SourcePath, sidecar.TargetPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skippedFiles++;
                failures.Add($"{item.SourcePath}: {ex.Message}");
                log.LogError(ex, "Export souboru {Path} selhal", item.SourcePath);
            }

            bytesDone += item.SizeBytes;
            progress.Report(new ExportProgress(
                "Videa",
                index + 1,
                plan.Count,
                bytesDone,
                totalBytes,
                Path.GetFileName(item.SourcePath)));
        }

        return new ExportReport(extended, newFiles, existingFiles, skippedFiles, unmatched, failures);
    }

    private async Task CopyFileWithProgressAsync(
        string sourcePath,
        string targetPath,
        Action<long> report,
        CancellationToken ct)
    {
        var temporaryTarget = targetPath + $".partial-{Guid.NewGuid():N}";
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(
                             temporaryTarget,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    long copied = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        copied += read;
                        report(copied);
                    }
                    await target.FlushAsync(ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            File.Move(temporaryTarget, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryTarget);
        }
    }

    private Task CopyFileAtomicallyAsync(string sourcePath, string targetPath, CancellationToken ct) =>
        CopyFileWithProgressAsync(sourcePath, targetPath, _ => { }, ct);

    private void PublishPackageDatabase(string stagingPath, string packageDbPath)
    {
        SqliteConnection.ClearAllPools();
        if (!File.Exists(packageDbPath))
        {
            File.Move(stagingPath, packageDbPath);
            return;
        }

        var backup = packageDbPath + $".replace-{Guid.NewGuid():N}.bak";
        File.Replace(stagingPath, packageDbPath, backup, ignoreMetadataErrors: true);
        TryDeleteFile(backup);
    }

    private IReadOnlyList<string> DeleteMovedSources(IReadOnlyList<ExportFilePlan> plan)
    {
        var failures = new List<string>();
        foreach (var item in plan)
        {
            foreach (var path in item.Sidecars
                         .Where(x => !PathsEqual(x.SourcePath, x.TargetPath))
                         .Select(x => x.SourcePath)
                         .Append(item.SourcePath))
            {
                if (PathsEqual(path, item.TargetPath)) continue;
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    failures.Add($"Zdroj se nepodarilo smazat: {path}: {ex.Message}");
                    log.LogWarning(ex, "Zdroj {Path} se po publikaci exportu nepodarilo smazat", path);
                }
            }
        }
        return failures;
    }

    private void CopyApplication(string targetRoot)
    {
        var sourceRoot = NormalizeRoot(AppContext.BaseDirectory);
        var targetApp = Path.Combine(targetRoot, "app");
        if (IsDescendant(sourceRoot, targetApp))
            throw new InvalidOperationException("Cil exportu nesmi lezet uvnitr adresare bezici aplikace.");

        CopyDirectory(sourceRoot, targetApp, overwrite: true);
        File.WriteAllText(Path.Combine(targetApp, "portable.txt"), "LSP portable\n");

        // ponytail: .bat místo .lnk — zástupce nese absolutní cestu a po změně písmene disku umře,
        // %~dp0 přežije cokoliv.
        File.WriteAllText(
            Path.Combine(targetRoot, "Spustit LSP.bat"),
            "@echo off\r\nstart \"\" \"%~dp0app\\LSP.exe\"\r\n");

        var ffmpegDir = Path.Combine(targetApp, "ffmpeg", "win-x64");
        CopyExternalTool(ffmpeg.FfmpegPath, ffmpegDir);
        CopyExternalTool(ffmpeg.FfprobePath, ffmpegDir);
    }

    private static void CopyExternalTool(string toolPath, string destinationDirectory)
    {
        if (!Path.IsPathFullyQualified(toolPath) || !File.Exists(toolPath))
            return;
        if (IsDescendant(NormalizeRoot(AppContext.BaseDirectory), toolPath))
            return;

        Directory.CreateDirectory(destinationDirectory);
        File.Copy(toolPath, Path.Combine(destinationDirectory, Path.GetFileName(toolPath)), overwrite: true);
    }

    private static async Task CreateSelectionTablesAsync(
        SqliteConnection connection,
        IReadOnlyList<ExportFilePlan> plan,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE TEMP TABLE SelectedMedia (
                Id INTEGER PRIMARY KEY,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL
            );
            CREATE TEMP TABLE SelectedShowTitles (Title TEXT PRIMARY KEY);
            CREATE TEMP TABLE SelectedTmdbIds (TmdbId INTEGER PRIMARY KEY);
            """,
            [],
            transaction,
            ct);

        foreach (var item in plan)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO SelectedMedia (Id, SourcePath, TargetPath) VALUES (@id, @source, @target);",
                [
                    new SqliteParameter("@id", item.MediaFileId),
                    new SqliteParameter("@source", item.SourcePath),
                    new SqliteParameter("@target", item.TargetPath),
                ],
                transaction,
                ct);
        }

        await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO SelectedShowTitles (Title)
            SELECT s.Title
            FROM Shows s
            JOIN Episodes e ON e.ShowId = s.Id
            JOIN SelectedMedia selected ON selected.Id = e.MediaFileId;

            INSERT OR IGNORE INTO SelectedTmdbIds (TmdbId)
            SELECT m.TmdbId
            FROM Movies m
            JOIN SelectedMedia selected ON selected.Id = m.MediaFileId
            WHERE m.TmdbId IS NOT NULL;

            INSERT OR IGNORE INTO SelectedTmdbIds (TmdbId)
            SELECT COALESCE(s.TmdbId, s.SoftTmdbId)
            FROM Shows s
            JOIN Episodes e ON e.ShowId = s.Id
            JOIN SelectedMedia selected ON selected.Id = e.MediaFileId
            WHERE COALESCE(s.TmdbId, s.SoftTmdbId) IS NOT NULL;
            """,
            [],
            transaction,
            ct);
    }

    private static async Task PruneUnselectedRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            DELETE FROM PlaybackProgress
            WHERE Path NOT IN (SELECT TargetPath FROM SelectedMedia);

            DELETE FROM ParseCaches
            WHERE Path NOT IN (SELECT TargetPath FROM SelectedMedia);

            DELETE FROM ManualMatches
            WHERE Key NOT IN (SELECT TargetPath FROM SelectedMedia)
              AND Key NOT IN (SELECT 'show:' || Title FROM SelectedShowTitles);

            DELETE FROM MatchAliases
            WHERE TmdbId NOT IN (SELECT TmdbId FROM SelectedTmdbIds);

            DELETE FROM TmdbSeasonCaches
            WHERE TmdbId NOT IN (SELECT TmdbId FROM SelectedTmdbIds);

            DELETE FROM TmdbCaches
            WHERE TmdbId IS NULL
               OR TmdbId NOT IN (SELECT TmdbId FROM SelectedTmdbIds);

            DELETE FROM MediaFiles
            WHERE Id NOT IN (SELECT Id FROM SelectedMedia);

            DELETE FROM Shows
            WHERE NOT EXISTS (SELECT 1 FROM Episodes e WHERE e.ShowId = Shows.Id);
            """,
            [],
            transaction,
            ct);
    }

    private static int CopySelectedPosters(string snapshotPath, string destination, bool overwrite)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = snapshotPath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT PosterFile FROM Movies WHERE PosterFile IS NOT NULL
                UNION ALL SELECT PosterFile FROM Shows WHERE PosterFile IS NOT NULL
                UNION ALL SELECT PosterFile FROM TmdbCaches WHERE PosterFile IS NOT NULL
                UNION ALL SELECT BackdropFile FROM TmdbCaches WHERE BackdropFile IS NOT NULL;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                names.Add(Path.GetFileName(reader.GetString(0)));
        }

        var copied = 0;
        foreach (var name in names)
        {
            var source = Path.Combine(AppPaths.PosterDir, name);
            if (!File.Exists(source))
                continue;

            var target = Path.Combine(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!overwrite && File.Exists(target))
                continue;
            File.Copy(source, target, overwrite);
            copied++;
        }
        return copied;
    }

    private static int CopyDirectory(string source, string destination, bool overwrite)
    {
        if (!Directory.Exists(source))
            return 0;

        var copied = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceFile);
            var targetFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (!overwrite && File.Exists(targetFile))
                continue;
            File.Copy(sourceFile, targetFile, overwrite);
            copied++;
        }
        return copied;
    }

    private static void RewritePosterColumns(
        SqliteConnection connection,
        string posterDirectory,
        bool includePosters,
        SqliteTransaction transaction)
    {
        foreach (var (table, column) in new[]
                 {
                     ("Movies", "PosterFile"),
                     ("Shows", "PosterFile"),
                     ("TmdbCaches", "PosterFile"),
                     ("TmdbCaches", "BackdropFile"),
                 })
        {
            var values = new List<(long Id, string Path)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"SELECT Id, {column} FROM {table} WHERE {column} IS NOT NULL;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    values.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            foreach (var (id, oldPath) in values)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET {column} = @path WHERE Id = @id;";
                update.Parameters.AddWithValue("@path", includePosters
                    ? Path.Combine(posterDirectory, Path.GetFileName(oldPath))
                    : DBNull.Value);
                update.Parameters.AddWithValue("@id", id);
                update.ExecuteNonQuery();
            }
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static LibraryDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;
        return new LibraryDbContext(options);
    }

    private static Show CloneShow(Show x) => new()
    {
        Title = x.Title,
        ImdbId = x.ImdbId,
        DisplayTitle = x.DisplayTitle,
        TmdbId = x.TmdbId,
        PosterFile = x.PosterFile,
        Overview = x.Overview,
        Rating = x.Rating,
        Genres = x.Genres,
        Cast = x.Cast,
        SoftTmdbId = x.SoftTmdbId,
        IsManual = x.IsManual,
    };

    private static void ApplyShowMetadata(Show target, Show source)
    {
        target.ImdbId = source.ImdbId;
        target.DisplayTitle = source.DisplayTitle;
        target.TmdbId = source.TmdbId;
        target.PosterFile = source.PosterFile;
        target.Overview = source.Overview;
        target.Rating = source.Rating;
        target.Genres = source.Genres;
        target.Cast = source.Cast;
        target.SoftTmdbId = source.SoftTmdbId;
        target.IsManual = source.IsManual;
    }

    private static void ApplyMediaMetadata(MediaFile target, MediaFile source)
    {
        target.FileName = source.FileName;
        target.Extension = source.Extension;
        target.SizeBytes = source.SizeBytes;
        target.Kind = source.Kind;
        target.AddedAt = source.AddedAt;
        target.Container = source.Container;
        target.VideoCodec = source.VideoCodec;
        target.AudioCodec = source.AudioCodec;
        target.DurationSeconds = source.DurationSeconds;
        target.Width = source.Width;
        target.Height = source.Height;

        if (target.Movie is not null && source.Movie is not null)
            ApplyMovieMetadata(target.Movie, source.Movie);
        if (target.Episode is not null && source.Episode is not null)
        {
            target.Episode.Season = source.Episode.Season;
            target.Episode.Number = source.Episode.Number;
            target.Episode.Title = source.Episode.Title;
        }
    }

    private static void ApplyMovieMetadata(Movie target, Movie source)
    {
        target.Title = source.Title;
        target.Year = source.Year;
        target.ImdbId = source.ImdbId;
        target.DisplayTitle = source.DisplayTitle;
        target.TmdbId = source.TmdbId;
        target.PosterFile = source.PosterFile;
        target.Overview = source.Overview;
        target.Rating = source.Rating;
        target.Genres = source.Genres;
        target.Cast = source.Cast;
        target.IsManual = source.IsManual;
    }

    private static void ApplyParseCache(ParseCache target, ParseCache source)
    {
        target.Kind = source.Kind;
        target.Title = source.Title;
        target.Year = source.Year;
        target.Season = source.Season;
        target.Number = source.Number;
        target.EpisodeTitle = source.EpisodeTitle;
        target.ImdbId = source.ImdbId;
        target.Source = source.Source;
        target.Confidence = source.Confidence;
        target.NormalizedQuery = source.NormalizedQuery;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void ApplyManualMatch(ManualMatch target, ManualMatch source)
    {
        target.TargetKind = source.TargetKind;
        target.TmdbId = source.TmdbId;
        target.MediaType = source.MediaType;
        target.Season = source.Season;
        target.Episode = source.Episode;
        target.ShowTitle = source.ShowTitle;
        target.CreatedAt = source.CreatedAt;
    }

    private static void ApplyMatchAlias(MatchAlias target, MatchAlias source)
    {
        target.TmdbId = source.TmdbId;
        target.MediaType = source.MediaType;
        target.CreatedAt = source.CreatedAt;
    }

    private static void ApplyTmdbCache(TmdbCache target, TmdbCache source)
    {
        target.TmdbId = source.TmdbId;
        target.MediaType = source.MediaType;
        target.Title = source.Title;
        target.Overview = source.Overview;
        target.PosterFile = source.PosterFile;
        target.BackdropFile = source.BackdropFile;
        target.Genres = source.Genres;
        target.Cast = source.Cast;
        target.Rating = source.Rating;
        target.ReleaseYear = source.ReleaseYear;
        target.Score = source.Score;
        target.FetchedAt = source.FetchedAt;
    }

    private static MediaFile CloneMediaFile(MediaFile x) => new()
    {
        Path = x.Path,
        FileName = x.FileName,
        Extension = x.Extension,
        SizeBytes = x.SizeBytes,
        Kind = x.Kind,
        AddedAt = x.AddedAt,
        Container = x.Container,
        VideoCodec = x.VideoCodec,
        AudioCodec = x.AudioCodec,
        DurationSeconds = x.DurationSeconds,
        Width = x.Width,
        Height = x.Height,
    };

    private static Movie CloneMovie(Movie x) => new()
    {
        Title = x.Title,
        Year = x.Year,
        ImdbId = x.ImdbId,
        DisplayTitle = x.DisplayTitle,
        TmdbId = x.TmdbId,
        PosterFile = x.PosterFile,
        Overview = x.Overview,
        Rating = x.Rating,
        Genres = x.Genres,
        Cast = x.Cast,
        IsManual = x.IsManual,
    };

    private static ParseCache CloneParseCache(ParseCache x) => new()
    {
        Path = x.Path,
        Kind = x.Kind,
        Title = x.Title,
        Year = x.Year,
        Season = x.Season,
        Number = x.Number,
        EpisodeTitle = x.EpisodeTitle,
        ImdbId = x.ImdbId,
        Source = x.Source,
        Confidence = x.Confidence,
        NormalizedQuery = x.NormalizedQuery,
        UpdatedAt = x.UpdatedAt,
    };

    private static ManualMatch CloneManualMatch(ManualMatch x) => new()
    {
        Key = x.Key,
        TargetKind = x.TargetKind,
        TmdbId = x.TmdbId,
        MediaType = x.MediaType,
        Season = x.Season,
        Episode = x.Episode,
        ShowTitle = x.ShowTitle,
        CreatedAt = x.CreatedAt,
    };

    private static MatchAlias CloneMatchAlias(MatchAlias x) => new()
    {
        Key = x.Key,
        TmdbId = x.TmdbId,
        MediaType = x.MediaType,
        CreatedAt = x.CreatedAt,
    };

    private static TmdbCache CloneTmdbCache(TmdbCache x) => new()
    {
        QueryKey = x.QueryKey,
        TmdbId = x.TmdbId,
        MediaType = x.MediaType,
        Title = x.Title,
        Overview = x.Overview,
        PosterFile = x.PosterFile,
        BackdropFile = x.BackdropFile,
        Genres = x.Genres,
        Cast = x.Cast,
        Rating = x.Rating,
        ReleaseYear = x.ReleaseYear,
        Score = x.Score,
        FetchedAt = x.FetchedAt,
    };

    private static void VerifySameLength(string source, string target)
    {
        if (new FileInfo(source).Length != new FileInfo(target).Length)
            throw new IOException($"Overeni delky selhalo: {target}");
    }

    private static bool FilesHaveSameContent(string left, string right)
    {
        if (PathsEqual(left, right)) return true;
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
                var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
                if (leftRead != rightRead) return false;
                if (leftRead == 0) return true;
                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                    return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    private static void DeleteSqliteSidecar(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void ValidateExportTarget(string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
            throw new ArgumentException("Cilova slozka je prazdna.", nameof(targetRoot));
        if (Path.GetPathRoot(targetRoot)?.Equals(targetRoot, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Vyberte slozku na disku, ne primo koren disku.");
    }

    private static void EnsureFreshTargetDoesNotContainDatabase(string targetRoot)
    {
        var dbPath = Path.Combine(targetRoot, "data", "library.db");
        if (File.Exists(dbPath))
            throw new InvalidOperationException("Cil obsahuje databazi, ale neni platnym LSP balikem.");
    }

    private static bool IsDescendant(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathFullyQualified(relative);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
