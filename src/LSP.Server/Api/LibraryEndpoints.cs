using LSP.Server.Data;
using LSP.Server.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Api;

// DTO posílané do UI. MediaFileId = klíč pro /api/stream. PosterUrl = null → frontend placeholder.
public sealed record MovieSourceDto(int MediaFileId, string FileName);
public sealed record MovieDto(int Id, int MediaFileId, string Title, int? Year, string FileName, string? PosterUrl, string? Genres, IReadOnlyList<MovieSourceDto> Sources);
public sealed record EpisodeDto(int Id, int MediaFileId, int Season, int Number, string? Title, string FileName);
public sealed record SeasonDto(int Season, IReadOnlyList<EpisodeDto> Episodes);
public sealed record ShowDto(int Id, string Title, string? PosterUrl, string? Genres, IReadOnlyList<SeasonDto> Seasons);
public sealed record LibraryDto(IReadOnlyList<MovieDto> Movies, IReadOnlyList<ShowDto> Shows);

public sealed record NextEpisodeDto(int MediaFileId, string ShowTitle, int Season, int Number, string? Title);

// Jeden řádek na home page (Netflix-styl). Nový typ kontejneru = nová metoda vracející HomeRowDto, beze změny frontendu.
public sealed record HomeItemDto(
    string Kind, int? MediaFileId, int? ShowId, string Title, string? Subtitle,
    string? PosterUrl, string? Badge, int? Progress);
public sealed record HomeRowDto(string Key, string Title, IReadOnlyList<HomeItemDto> Items);

public static class LibraryEndpoints
{
    private const int HomeRowLimit = 12;

    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/library");

        group.MapGet("/", GetLibrary);

        app.MapGet("/api/next-episode/{mediaFileId:int}", GetNextEpisode);
        app.MapGet("/api/previous-episode/{mediaFileId:int}", GetPreviousEpisode);
        app.MapGet("/api/home", GetHome);
    }

    /// <summary>Vrátí následující epizodu (v rámci seriálu, řazeno dle sezóny a čísla), nebo 204.</summary>
    private static async Task<IResult> GetNextEpisode(int mediaFileId, LibraryDbContext db, CancellationToken ct)
    {
        var current = await db.Episodes
            .Include(e => e.Show)
            .FirstOrDefaultAsync(e => e.MediaFileId == mediaFileId, ct);
        if (current is null) return Results.NoContent(); // není to epizoda

        var next = await db.Episodes
            .Where(e => e.ShowId == current.ShowId
                        && (e.Season > current.Season
                            || (e.Season == current.Season && e.Number > current.Number)))
            .OrderBy(e => e.Season).ThenBy(e => e.Number)
            .FirstOrDefaultAsync(ct);
        if (next is null) return Results.NoContent(); // poslední epizoda

        return Results.Ok(new NextEpisodeDto(
            next.MediaFileId, current.Show.DisplayTitle ?? current.Show.Title, next.Season, next.Number, next.Title));
    }

    /// <summary>Vrátí předchozí epizodu (v rámci seriálu, řazeno dle sezóny a čísla), nebo 204.</summary>
    private static async Task<IResult> GetPreviousEpisode(int mediaFileId, LibraryDbContext db, CancellationToken ct)
    {
        var current = await db.Episodes
            .Include(e => e.Show)
            .FirstOrDefaultAsync(e => e.MediaFileId == mediaFileId, ct);
        if (current is null) return Results.NoContent(); // není to epizoda

        var previous = await db.Episodes
            .Where(e => e.ShowId == current.ShowId
                        && (e.Season < current.Season
                            || (e.Season == current.Season && e.Number < current.Number)))
            .OrderByDescending(e => e.Season).ThenByDescending(e => e.Number)
            .FirstOrDefaultAsync(ct);
        if (previous is null) return Results.NoContent(); // první epizoda

        return Results.Ok(new NextEpisodeDto(
            previous.MediaFileId, current.Show.DisplayTitle ?? current.Show.Title, previous.Season, previous.Number, previous.Title));
    }

    private static async Task<IResult> GetLibrary(LibraryDbContext db, CancellationToken ct)
    {
        var rawMovies = await db.Movies
            .OrderBy(m => m.Title)
            .Select(m => new
            {
                m.Id, m.MediaFileId, m.TmdbId,
                Title = m.DisplayTitle ?? m.Title, m.Year, FileName = m.MediaFile.FileName,
                HasPoster = m.PosterFile != null, m.Genres,
            })
            .ToListAsync(ct);

        // Seskup duplicity podle TmdbId (víc souborů = jeden film, výběr zdroje v UI). Null TmdbId neseskupovat.
        var movies = rawMovies
            .GroupBy(m => m.TmdbId is int id ? $"tmdb:{id}" : $"file:{m.Id}")
            .Select(g =>
            {
                var primary = g.OrderBy(m => m.Id).First();
                var sources = g.OrderBy(m => m.Id)
                    .Select(m => new MovieSourceDto(m.MediaFileId, m.FileName)).ToList();
                return new MovieDto(primary.Id, primary.MediaFileId, primary.Title, primary.Year, primary.FileName,
                    primary.HasPoster ? $"/api/poster/movie/{primary.Id}?v={primary.TmdbId}" : null,
                    GenreFormat.NormalizeStored(primary.Genres), sources);
            })
            .OrderBy(m => m.Title)
            .ToList();

        var shows = await db.Shows
            .OrderBy(s => s.Title)
            .Select(s => new
            {
                s.Id,
                Title = s.DisplayTitle ?? s.Title,
                HasPoster = s.PosterFile != null,
                s.TmdbId,
                s.Genres,
                Episodes = s.Episodes
                    .OrderBy(e => e.Season).ThenBy(e => e.Number)
                    .Select(e => new EpisodeDto(e.Id, e.MediaFileId, e.Season, e.Number, e.Title, e.MediaFile.FileName))
                    .ToList(),
            })
            .ToListAsync(ct);

        var showDtos = shows.Select(s => new ShowDto(
            s.Id,
            s.Title,
            s.HasPoster ? $"/api/poster/show/{s.Id}?v={s.TmdbId}" : null,
            GenreFormat.NormalizeStored(s.Genres),
            s.Episodes
                .GroupBy(e => e.Season)
                .OrderBy(g => g.Key)
                .Select(g => new SeasonDto(g.Key, g.ToList()))
                .ToList())).ToList();

        return Results.Ok(new LibraryDto(movies, showDtos));
    }

    /// <summary>Netflix-styl řádky pro home page. Nový kontejner (např. "podle kategorie") = přidej metodu, co vrátí HomeRowDto, a připoj ji do seznamu níž.</summary>
    private static async Task<IResult> GetHome(LibraryDbContext db, CancellationToken ct)
    {
        var rows = new List<HomeRowDto>();

        var continueItems = await ProgressEndpoints.BuildContinueItemsAsync(db, ct);
        if (continueItems.Count > 0)
            rows.Add(new HomeRowDto("continue-watching", "Pokračovat v přehrávání", continueItems.Select(c =>
                new HomeItemDto(c.Kind, c.MediaFileId, c.ShowId, c.Title, c.Subtitle, c.PosterUrl, null, c.Percent)).ToList()));

        var shows = await db.Shows
            .OrderBy(s => s.Title)
            .Take(HomeRowLimit)
            .Select(s => new
            {
                s.Id,
                Title = s.DisplayTitle ?? s.Title,
                HasPoster = s.PosterFile != null,
                s.TmdbId,
                EpisodeCount = s.Episodes.Count,
            })
            .ToListAsync(ct);
        if (shows.Count > 0)
            rows.Add(new HomeRowDto("shows", "Seriály", shows.Select(s =>
                new HomeItemDto("show", null, s.Id, s.Title, $"{s.EpisodeCount} epizod",
                    s.HasPoster ? $"/api/poster/show/{s.Id}?v={s.TmdbId}" : null, "SERIÁL", null)).ToList()));

        var allMovies = await db.Movies
            .OrderBy(m => m.Title)
            .Select(m => new
            {
                m.MediaFileId,
                Title = m.DisplayTitle ?? m.Title,
                m.Year,
                m.Id,
                m.TmdbId,
                HasPoster = m.PosterFile != null,
            })
            .ToListAsync(ct);
        // Dedupe duplicit podle TmdbId (null = ponech každý zvlášť), pak ořízni na řádek.
        var movies = allMovies
            .DistinctBy(m => m.TmdbId is int id ? $"tmdb:{id}" : $"file:{m.Id}")
            .Take(HomeRowLimit)
            .ToList();
        if (movies.Count > 0)
            rows.Add(new HomeRowDto("movies", "Filmy", movies.Select(m =>
                new HomeItemDto("movie", m.MediaFileId, null, m.Title, m.Year != null ? m.Year.ToString() : null,
                    m.HasPoster ? $"/api/poster/movie/{m.Id}?v={m.TmdbId}" : null, null, null)).ToList()));

        return Results.Ok(rows);
    }
}
