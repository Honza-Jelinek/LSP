using System.Reflection;
using LSP.Server.Api;
using LSP.Server.Data;
using LSP.Server.Library.Parsing;
using LSP.Server.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Tests;

public sealed class PlaybackProgressTests
{
    [Fact]
    public async Task LatestCompletionWinsOverOlderPartialEpisode()
    {
        await using var connection = await OpenAsync();
        await using var db = Context(connection);
        var show = AddShow(db);
        db.PlaybackProgress.AddRange(
            Progress("S01E01.mkv", 20, false, 1),
            Progress("S02E08.mkv", 2500, false, 2),
            Progress("S03E03.mkv", 3500, true, 3));
        await db.SaveChangesAsync();
        var next = Assert.Single(await ProgressEndpoints.BuildContinueItemsAsync(db, default));
        Assert.Equal("S03E04", next.Subtitle);
        Assert.Equal(0, next.PositionSeconds);
        Assert.Equal(show.Id, next.ShowId);
    }

    [Theory]
    [InlineData(100, false, "S01E01", 100)]
    [InlineData(3500, true, "S01E02", 0)]
    public async Task IntentionalRewatchFollowsLatestActivity(double position, bool finished, string expected, double expectedPosition)
    {
        await using var connection = await OpenAsync();
        await using var db = Context(connection);
        AddShow(db);
        db.PlaybackProgress.AddRange(Progress("S03E03.mkv", 3500, true, 1), Progress("S01E01.mkv", position, finished, 2));
        await db.SaveChangesAsync();
        var next = Assert.Single(await ProgressEndpoints.BuildContinueItemsAsync(db, default));
        Assert.Equal(expected, next.Subtitle);
        Assert.Equal(expectedPosition, next.PositionSeconds);
    }

    [Fact]
    public async Task IncidentalOpeningDoesNotReplaceLatestMeaningfulActivity()
    {
        await using var connection = await OpenAsync();
        await using var db = Context(connection);
        AddShow(db);
        db.PlaybackProgress.AddRange(Progress("S03E03.mkv", 3500, true, 1), Progress("S01E01.mkv", 2, false, 2));
        await db.SaveChangesAsync();
        Assert.Equal("S03E04", Assert.Single(await ProgressEndpoints.BuildContinueItemsAsync(db, default)).Subtitle);
    }

    [Fact]
    public async Task LateWritesCannotOverwriteNewSessionButIntentionalRewindCan()
    {
        await using var connection = await OpenAsync();
        int fileId;
        await using (var db = Context(connection))
        {
            var file = new MediaFile { Path = "movie.mkv", FileName = "movie.mkv", Extension = ".mkv", Kind = MediaKind.Movie };
            db.MediaFiles.Add(file);
            db.PlaybackProgress.Add(Progress(file.Path, 1500, false, 1));
            await db.SaveChangesAsync();
            fileId = file.Id;
        }
        var writers = new ProgressWriteCoordinator();
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        Assert.Equal(200, await Read(connection, writers, fileId, oldSession));
        Assert.Equal(204, await Save(connection, writers, new(fileId, 1510, 3600, oldSession, 2)));
        Assert.Equal(409, await Save(connection, writers, new(fileId, 6, 3600, oldSession, 1)));
        Assert.Equal(200, await Read(connection, writers, fileId, newSession));
        Assert.Equal(409, await Read(connection, writers, fileId, oldSession));
        Assert.Equal(409, await Save(connection, writers, new(fileId, 6, 3600, oldSession, 3)));
        Assert.Equal(409, await Save(connection, writers, new(fileId, 6, 3600))); // legacy client must not override active lease
        Assert.Equal(204, await Save(connection, writers, new(fileId, 30, 3600, newSession, 1)));
        Assert.Equal(409, await Save(connection, writers, new(fileId, 3000, 3600, newSession, 1)));
        await using var check = Context(connection);
        Assert.Equal(30, (await check.PlaybackProgress.SingleAsync()).PositionSeconds);
    }

    [Fact]
    public async Task RegisteringSessionDoesNotCreateOrResetProgress()
    {
        await using var connection = await OpenAsync();
        int id;
        await using (var db = Context(connection))
        {
            var file = new MediaFile { Path = "new.mkv", FileName = "new.mkv", Extension = ".mkv" };
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            id = file.Id;
        }
        var writers = new ProgressWriteCoordinator();
        var session = Guid.NewGuid();
        Assert.Equal(204, await Read(connection, writers, id, session));
        await using (var check = Context(connection)) Assert.Empty(await check.PlaybackProgress.ToListAsync());
        Assert.Equal(400, await Save(connection, writers, new(id, -1, 100, session, 1)));
        Assert.Equal(204, await Save(connection, writers, new(id, 25, 100, session, 2)));
        Assert.Equal(200, await Read(connection, writers, id, session));
        Assert.Equal(409, await Save(connection, writers, new(id, 1, 100, session, 1)));
        await using var final = Context(connection);
        Assert.Equal(25, (await final.PlaybackProgress.SingleAsync()).PositionSeconds);
    }

    [Fact]
    public async Task DelayedFirstReadOfOldPlayerCannotSupersedeNewPlayer()
    {
        await using var connection = await OpenAsync();
        int id;
        await using (var db = Context(connection))
        {
            var file = new MediaFile { Path = "race.mkv", FileName = "race.mkv", Extension = ".mkv" };
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            id = file.Id;
        }
        var writers = new ProgressWriteCoordinator();
        var current = Guid.NewGuid();
        Assert.Equal(204, await Read(connection, writers, id, current, 2000));
        Assert.Equal(409, await Read(connection, writers, id, Guid.NewGuid(), 1000));
        Assert.Equal(409, await Read(connection, writers, id, Guid.NewGuid()));
        Assert.Equal(204, await Save(connection, writers, new(id, 1500, 3600, current, 1)));
        Assert.Equal(200, await Read(connection, writers, id, current, 2000));
        Assert.Equal(409, await Save(connection, writers, new(id, 6, 3600, current, 1)));
    }

    private static async Task<int?> Read(SqliteConnection connection, ProgressWriteCoordinator writers, int id, Guid session, long? startedAt = null)
    {
        await using var db = Context(connection);
        return ((IStatusCodeHttpResult)await Invoke("GetProgress", id, (Guid?)session, startedAt!, db, writers, CancellationToken.None)).StatusCode;
    }

    private static async Task<int?> Save(SqliteConnection connection, ProgressWriteCoordinator writers, SaveProgressRequest request)
    {
        await using var db = Context(connection);
        return ((IStatusCodeHttpResult)await Invoke("SaveProgress", request, db, writers, CancellationToken.None)).StatusCode;
    }

    private static Task<IResult> Invoke(string name, params object[] args) =>
        (Task<IResult>)typeof(ProgressEndpoints).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static LibraryDbContext Context(SqliteConnection connection) => new(new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options);

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static PlaybackProgress Progress(string path, double position, bool finished, int day) => new()
    {
        Path = path, PositionSeconds = position, DurationSeconds = 3600, Finished = finished,
        UpdatedAt = new DateTime(2026, 9, day, 12, 0, 0, DateTimeKind.Utc),
    };

    private static Show AddShow(LibraryDbContext db)
    {
        var show = new Show { Title = "Mr. Robot test" };
        foreach (var (season, number) in new[] { (1, 1), (1, 2), (2, 8), (3, 3), (3, 4) })
        {
            var path = $"S{season:D2}E{number:D2}.mkv";
            show.Episodes.Add(new Episode { Show = show, Season = season, Number = number,
                MediaFile = new MediaFile { Path = path, FileName = path, Extension = ".mkv", Kind = MediaKind.Episode } });
        }
        db.Shows.Add(show);
        return show;
    }
}
