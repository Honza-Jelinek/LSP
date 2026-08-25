using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Media;

/// <summary>
/// Spravuje VOD HLS transkódovací relace (jedna na soubor). Singleton. Uklízí nečinné relace.
/// </summary>
public sealed class TranscodeSessionManager : IDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<TranscodeSessionManager> _log;
    private readonly string _rootDir;
    private readonly string _videoEncoder;

    private readonly ConcurrentDictionary<int, TranscodeSession> _sessions = new();
    private readonly object _createLock = new();
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    public TranscodeSessionManager(FfmpegLocator locator, IConfiguration config, ILogger<TranscodeSessionManager> log)
    {
        _locator = locator;
        _log = log;
        _videoEncoder = config["Media:VideoEncoder"] ?? "h264_mf"; // HW přes Media Foundation; fallback libopenh264
        _rootDir = config["Media:TranscodeRoot"] ?? AppPaths.SubDir("transcode");
        Directory.CreateDirectory(_rootDir);

        // Při startu je dict prázdný → všechny podsložky jsou sirotci z minulého běhu (pád/taskkill).
        foreach (var dir in Directory.EnumerateDirectories(_rootDir))
            TryDeleteDir(dir);

        _cleanupTimer = new Timer(_ => CleanupIdle(), null, IdleTimeout, IdleTimeout);
    }

    public TranscodeSession? TryGet(int mediaFileId)
    {
        if (_sessions.TryGetValue(mediaFileId, out var s)) { s.Touch(); return s; }
        return null;
    }

    /// <summary>Vrátí (a případně založí) relaci pro soubor. Nečeká na ffmpeg – segmenty si počká endpoint.</summary>
    public TranscodeSession GetOrCreate(int mediaFileId, string sourcePath, PlaybackPlan plan, double duration)
    {
        if (_sessions.TryGetValue(mediaFileId, out var existing))
        {
            existing.Touch();
            return existing;
        }

        lock (_createLock)
        {
            if (_sessions.TryGetValue(mediaFileId, out existing))
            {
                existing.Touch();
                return existing;
            }

            var dir = Path.Combine(_rootDir, mediaFileId.ToString());
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            var session = new TranscodeSession(
                mediaFileId, sourcePath, plan, duration, dir, _locator.FfmpegPath, _videoEncoder, _log);
            _sessions[mediaFileId] = session;
            return session;
        }
    }

    /// <summary>Zabije relaci daného souboru a smaže jeho segmenty z disku (i osiřelé, bez živé relace).</summary>
    public void PurgeSegments(int mediaFileId)
    {
        if (_sessions.TryRemove(mediaFileId, out var s))
            s.Dispose(); // zabije ffmpeg + smaže WorkingDirectory
        else
            TryDeleteDir(Path.Combine(_rootDir, mediaFileId.ToString()));
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private void CleanupIdle()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastAccess < cutoff && _sessions.TryRemove(id, out var removed))
            {
                _log.LogInformation("Uklízím nečinný transkód #{Id}", id);
                removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }
}
