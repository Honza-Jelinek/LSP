using System.Diagnostics;
using LSP.Server.Data;
using LSP.Server.Library.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Library;

public sealed record ScanSummary(
    int FilesScanned,
    int Movies,
    int Shows,
    int Episodes,
    long ElapsedMs);

public sealed class LibraryScanner(
    LibraryDbContext db,
    MediaParserChain chain,
    ILogger<LibraryScanner> log)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm",
        ".ts", ".m2ts", ".mpg", ".mpeg", ".vob",
    };

    public async Task<ScanSummary> ScanAllAsync(CancellationToken ct = default)
    {
        var folders = await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct);
        return await ScanAsync(folders, ct);
    }

    public async Task<ScanSummary> ScanAsync(IReadOnlyList<string> roots, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Use transaction pro atomicitu — při selhání se vše vrátí
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var preserved = await LoadPreservedMetadataAsync(ct);

            await db.Episodes.ExecuteDeleteAsync(ct);
            await db.Movies.ExecuteDeleteAsync(ct);
            await db.Shows.ExecuteDeleteAsync(ct);
            await db.MediaFiles.ExecuteDeleteAsync(ct);

            // Pre-load ParseCaches do slovníku (eliminuje N+1)
            var allCaches = await db.ParseCaches.ToListAsync(ct);
            var cacheByPath = new Dictionary<string, ParseCache>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in allCaches)
                cacheByPath[c.Path] = c;

            var shows = new Dictionary<string, Show>(StringComparer.OrdinalIgnoreCase);
            var showsByTitle = new Dictionary<string, Show>(StringComparer.OrdinalIgnoreCase);
            int files = 0, movies = 0, episodes = 0;

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    log.LogWarning("Knihovní složka neexistuje: {Root}", root);
                    continue;
                }

                foreach (var path in EnumerateVideoFiles(root))
                {
                    ct.ThrowIfCancellationRequested();

                    var fileName = System.IO.Path.GetFileName(path);
                    var nameNoExt = System.IO.Path.GetFileNameWithoutExtension(path);
                    var ext = System.IO.Path.GetExtension(path);

                    var relative = System.IO.Path.GetRelativePath(root, path);
                    var segments = relative.Split(
                        [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries);

                    var context = new MediaParseContext
                    {
                        FullPath = path,
                        FileNameWithoutExtension = nameNoExt,
                        RootRelativeSegments = segments,
                    };

                    var result = chain.Parse(context);

                    long size = 0;
                    try { size = new FileInfo(path).Length; } catch { }

                    // Upsert ParseCache v paměti
                    if (cacheByPath.TryGetValue(path, out var existingCache))
                    {
                        existingCache.Kind = result.Kind;
                        existingCache.Title = result.Title;
                        existingCache.Year = result.Year;
                        existingCache.Season = result.Season;
                        existingCache.Number = result.Episode;
                        existingCache.EpisodeTitle = result.EpisodeTitle;
                        existingCache.ImdbId = result.ImdbId;
                        existingCache.Source = result.Source;
                        existingCache.Confidence = result.Confidence;
                        existingCache.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        existingCache = new ParseCache
                        {
                            Path = path,
                            Kind = result.Kind,
                            Title = result.Title,
                            Year = result.Year,
                            Season = result.Season,
                            Number = result.Episode,
                            EpisodeTitle = result.EpisodeTitle,
                            ImdbId = result.ImdbId,
                            Source = result.Source,
                            Confidence = result.Confidence,
                            UpdatedAt = DateTime.UtcNow,
                        };
                        db.ParseCaches.Add(existingCache);
                        cacheByPath[path] = existingCache;
                    }

                    var mediaFile = new MediaFile
                    {
                        Path = path,
                        FileName = fileName,
                        Extension = ext,
                        SizeBytes = size,
                        Kind = result.Kind,
                        AddedAt = DateTimeOffset.Now,
                    };
                    if (preserved.MediaFilesByPath.TryGetValue(path, out var mediaMetadata))
                        mediaMetadata.ApplyTo(mediaFile);

                    if (result.Kind == MediaKind.Episode && result is { Season: { } season, Episode: { } number })
                    {
                        var showKey = NormalizeFolderKey(context.ContentFolderName ?? context.TopFolderName, result.Title);
                        var show = GetOrCreateShow(
                            shows, showsByTitle, showKey, result.Title, result.ImdbId,
                            preserved.ShowsByMediaPath.GetValueOrDefault(path));
                        show.Episodes.Add(new Episode
                        {
                            Show = show,
                            Season = season,
                            Number = number,
                            Title = result.EpisodeTitle,
                            MediaFile = mediaFile,
                        });
                        episodes++;
                    }
                    else if (result.Kind == MediaKind.Movie || result.Kind == MediaKind.Unknown)
                    {
                        if (result.Kind == MediaKind.Movie || !string.IsNullOrWhiteSpace(result.Title))
                        {
                            mediaFile.Kind = MediaKind.Movie;
                            var movie = new Movie
                            {
                                Title = result.Title,
                                Year = result.Year,
                                ImdbId = result.ImdbId,
                                MediaFile = mediaFile,
                            };
                            if (preserved.MoviesByPath.TryGetValue(path, out var movieMetadata))
                                movieMetadata.ApplyTo(movie);
                            db.Movies.Add(movie);
                            movies++;
                        }
                    }

                    db.MediaFiles.Add(mediaFile);
                    files++;

                    // Periodické ukládání pro velké knihovny
                    if (files % 200 == 0)
                    {
                        await db.SaveChangesAsync(ct);
                        log.LogDebug("Průběžné uložení: {Files} souborů", files);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            sw.Stop();
            log.LogInformation(
                "Sken: {Files} souborů, {Movies} filmů, {Shows} seriálů, {Episodes} epizod ({Ms} ms)",
                files, movies, shows.Count, episodes, sw.ElapsedMilliseconds);

            return new ScanSummary(files, movies, shows.Count, episodes, sw.ElapsedMilliseconds);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string NormalizeFolderKey(string? contentFolder, string fallbackTitle)
    {
        var raw = contentFolder ?? fallbackTitle;
        return MatchScorer.Normalize(raw);
    }

    private Show GetOrCreateShow(
        Dictionary<string, Show> byKey,
        Dictionary<string, Show> byTitle,
        string key,
        string title,
        string? imdbId,
        ShowMetadata? metadata)
    {
        // Nejdřív podle klíče složky, pak podle titulu — dvě různé složky
        // se stejným parsovaným titulem musí sdílet jednu Show (unikátní index na Title)
        if (!byKey.TryGetValue(key, out var existing))
            byTitle.TryGetValue(title, out existing);

        if (existing is not null)
        {
            // Ulož IMDB ID pokud ho show zatím nemá (první soubor v sérii)
            if (imdbId is not null && existing.ImdbId is null)
                existing.ImdbId = imdbId;
            metadata?.ApplyTo(existing);
            byKey[key] = existing;
            return existing;
        }

        var show = new Show { Title = title, ImdbId = imdbId };
        metadata?.ApplyTo(show);
        byKey[key] = show;
        byTitle[title] = show;
        db.Shows.Add(show);
        return show;
    }

    private async Task<PreservedMetadata> LoadPreservedMetadataAsync(CancellationToken ct)
    {
        var mediaFiles = await db.MediaFiles
            .AsNoTracking()
            .Select(file => new
            {
                file.Path,
                Metadata = new MediaFileMetadata(
                    file.AddedAt, file.Container, file.VideoCodec, file.AudioCodec,
                    file.DurationSeconds, file.Width, file.Height),
            })
            .ToListAsync(ct);

        var movies = await db.Movies
            .AsNoTracking()
            .Select(movie => new
            {
                movie.MediaFile.Path,
                Metadata = new MovieMetadata(
                    movie.DisplayTitle, movie.TmdbId, movie.PosterFile, movie.Overview,
                    movie.Rating, movie.Genres, movie.Cast, movie.IsManual),
            })
            .ToListAsync(ct);

        var shows = await db.Episodes
            .AsNoTracking()
            .Select(episode => new
            {
                episode.MediaFile.Path,
                Metadata = new ShowMetadata(
                    episode.Show.DisplayTitle, episode.Show.TmdbId, episode.Show.PosterFile,
                    episode.Show.Overview, episode.Show.Rating, episode.Show.Genres,
                    episode.Show.Cast, episode.Show.SoftTmdbId, episode.Show.IsManual),
            })
            .ToListAsync(ct);

        return new PreservedMetadata(
            mediaFiles.ToDictionary(x => x.Path, x => x.Metadata, StringComparer.OrdinalIgnoreCase),
            movies.ToDictionary(x => x.Path, x => x.Metadata, StringComparer.OrdinalIgnoreCase),
            shows.ToDictionary(x => x.Path, x => x.Metadata, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record PreservedMetadata(
        IReadOnlyDictionary<string, MediaFileMetadata> MediaFilesByPath,
        IReadOnlyDictionary<string, MovieMetadata> MoviesByPath,
        IReadOnlyDictionary<string, ShowMetadata> ShowsByMediaPath);

    private sealed record MediaFileMetadata(
        DateTimeOffset AddedAt,
        string? Container,
        string? VideoCodec,
        string? AudioCodec,
        double? DurationSeconds,
        int? Width,
        int? Height)
    {
        public void ApplyTo(MediaFile file)
        {
            file.AddedAt = AddedAt;
            file.Container = Container;
            file.VideoCodec = VideoCodec;
            file.AudioCodec = AudioCodec;
            file.DurationSeconds = DurationSeconds;
            file.Width = Width;
            file.Height = Height;
        }
    }

    private sealed record MovieMetadata(
        string? DisplayTitle,
        int? TmdbId,
        string? PosterFile,
        string? Overview,
        double? Rating,
        string? Genres,
        string? Cast,
        bool IsManual)
    {
        public void ApplyTo(Movie movie)
        {
            movie.DisplayTitle = DisplayTitle;
            movie.TmdbId = TmdbId;
            movie.PosterFile = PosterFile;
            movie.Overview = Overview;
            movie.Rating = Rating;
            movie.Genres = Genres;
            movie.Cast = Cast;
            movie.IsManual = IsManual;
        }
    }

    private sealed record ShowMetadata(
        string? DisplayTitle,
        int? TmdbId,
        string? PosterFile,
        string? Overview,
        double? Rating,
        string? Genres,
        string? Cast,
        int? SoftTmdbId,
        bool IsManual)
    {
        public void ApplyTo(Show show)
        {
            show.DisplayTitle ??= DisplayTitle;
            show.TmdbId ??= TmdbId;
            show.PosterFile ??= PosterFile;
            show.Overview ??= Overview;
            show.Rating ??= Rating;
            show.Genres ??= Genres;
            show.Cast ??= Cast;
            show.SoftTmdbId ??= SoftTmdbId;
            show.IsManual |= IsManual;
        }
    }

    private IEnumerable<string> EnumerateVideoFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            var ext = System.IO.Path.GetExtension(path);
            if (!VideoExtensions.Contains(ext))
                continue;

            var name = System.IO.Path.GetFileName(path);
            if (name.StartsWith('~') || name.Contains("uTorrentPartFile", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return path;
        }
    }
}
