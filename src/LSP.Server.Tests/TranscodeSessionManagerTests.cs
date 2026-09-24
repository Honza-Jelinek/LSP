using LSP.Server.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class TranscodeSessionManagerTests
{
    [Fact]
    public void Release_is_idempotent_and_does_not_affect_another_player()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "session-tests", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Media:TranscodeRoot"] = root }).Build();
        using var manager = new TranscodeSessionManager(
            new FfmpegLocator(config), config, NullLogger<TranscodeSessionManager>.Instance);
        var plan = new PlaybackPlan(PlaybackMode.Hls, CopyVideo: false, CopyAudio: false);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var firstSession = manager.GetOrCreate(42, "unused.mkv", plan, 20, first);
        var secondSession = manager.GetOrCreate(42, "unused.mkv", plan, 20, second);
        Assert.NotNull(firstSession);
        Assert.NotNull(secondSession);
        Assert.NotSame(firstSession, secondSession);
        Assert.NotEqual(firstSession.WorkingDirectory, secondSession.WorkingDirectory);

        manager.Release(42, first);
        manager.Release(42, first);
        Assert.Null(manager.TryGet(42, first));
        Assert.Null(manager.GetOrCreate(42, "unused.mkv", plan, 20, first));
        Assert.Same(secondSession, manager.TryGet(42, second));
        Assert.True(manager.Touch(42, second, paused: true));
        Assert.False(manager.Touch(42, first, paused: false));
    }

    [Fact]
    public void Purge_all_tombstones_active_tokens()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "session-tests", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Media:TranscodeRoot"] = root }).Build();
        using var manager = new TranscodeSessionManager(
            new FfmpegLocator(config), config, NullLogger<TranscodeSessionManager>.Instance);
        var token = Guid.NewGuid();
        var plan = new PlaybackPlan(PlaybackMode.Hls, CopyVideo: false, CopyAudio: false);
        Assert.NotNull(manager.GetOrCreate(7, "unused.mkv", plan, 20, token));

        manager.PurgeSegments(7);

        Assert.Null(manager.GetOrCreate(7, "unused.mkv", plan, 20, token));
        Assert.NotNull(manager.GetOrCreate(7, "unused.mkv", plan, 20, Guid.NewGuid()));
    }
}
