using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Media;

/// <summary>Owns independent playback sessions, including explicit release and idle expiry.</summary>
public sealed class TranscodeSessionManager : IDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<TranscodeSessionManager> _log;
    private readonly string _rootDir;
    private readonly string _videoEncoder;
    private readonly Dictionary<(int MediaId, Guid Token), TranscodeSession> _sessions = new();
    private readonly HashSet<(int MediaId, Guid Token)> _released = new();
    private readonly object _sync = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public TranscodeSessionManager(FfmpegLocator locator, IConfiguration config, ILogger<TranscodeSessionManager> log)
    {
        _locator = locator;
        _log = log;
        _videoEncoder = config["Media:VideoEncoder"] ?? "h264_mf";
        _rootDir = config["Media:TranscodeRoot"] ?? AppPaths.SubDir("transcode");
        Directory.CreateDirectory(_rootDir);
        foreach (var dir in Directory.EnumerateDirectories(_rootDir)) TryDeleteDir(dir);
        _cleanupTimer = new Timer(_ => CleanupIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public TranscodeSession? TryGet(int mediaFileId, Guid? token = null)
    {
        lock (_sync)
        {
            var key = (mediaFileId, token ?? Guid.Empty);
            if (_disposed || _released.Contains(key) || !_sessions.TryGetValue(key, out var session)) return null;
            session.Touch();
            return session;
        }
    }

    public TranscodeSession? GetOrCreate(int mediaFileId, string sourcePath, PlaybackPlan plan,
        double duration, Guid? token = null, MediaProbe? probe = null)
    {
        lock (_sync)
        {
            var key = (mediaFileId, token ?? Guid.Empty);
            if (_disposed || _released.Contains(key)) return null;
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.Touch();
                return existing;
            }
            var dir = Path.Combine(_rootDir, mediaFileId.ToString(), key.Item2.ToString("N"));
            Directory.CreateDirectory(dir);
            var session = new TranscodeSession(mediaFileId, sourcePath, plan, duration, dir,
                _locator.FfmpegPath, _videoEncoder, _log, probe, _locator.FfprobePath);
            _sessions.Add(key, session);
            return session;
        }
    }

    public bool Touch(int mediaFileId, Guid? token, bool paused)
    {
        lock (_sync)
        {
            var key = (mediaFileId, token ?? Guid.Empty);
            if (_disposed || _released.Contains(key) || !_sessions.TryGetValue(key, out var session)) return false;
            session.Touch();
            session.PauseEncoding(paused);
            return true;
        }
    }

    /// <summary>Release one generation. Its tombstone rejects late playlist and segment requests.</summary>
    public void Release(int mediaFileId, Guid token)
    {
        lock (_sync)
        {
            var key = (mediaFileId, token);
            _released.Add(key);
            if (_sessions.Remove(key, out var session)) session.Dispose();
            TryDeleteDir(Path.Combine(_rootDir, mediaFileId.ToString(), token.ToString("N")));
        }
    }

    /// <summary>Delete all sessions for an item, also used by progress deletion.</summary>
    public void PurgeSegments(int mediaFileId)
    {
        lock (_sync)
        {
            foreach (var key in _sessions.Keys.Where(k => k.MediaId == mediaFileId).ToArray())
            {
                if (_sessions.Remove(key, out var session)) session.Dispose();
                if (key.Token != Guid.Empty) _released.Add(key);
            }
            TryDeleteDir(Path.Combine(_rootDir, mediaFileId.ToString()));
        }
    }

    private void CleanupIdle()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _sessions.Where(p => p.Value.LastAccess < now - IdleTimeout).Select(p => p.Key).ToArray())
            {
                if (!_sessions.Remove(key, out var session)) continue;
                _log.LogInformation("Uklízím nečinný transkód #{Id}", key.MediaId);
                session.Dispose();
                if (key.Token != Guid.Empty) _released.Add(key);
            }
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
            _released.Clear();
        }
    }
}
