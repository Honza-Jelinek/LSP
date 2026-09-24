using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Media;

/// <summary>On-demand VOD segments. Each request addresses a real time interval; no process runs beyond it.</summary>
public sealed class TranscodeSession : IDisposable
{
    public const double SegmentSeconds = 4;
    private const int MaxCachedSegments = 32;
    private const long MaxCachedBytes = 512L * 1024 * 1024;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public int MediaFileId { get; }
    public double TotalDuration { get; }
    public string WorkingDirectory { get; }
    public int TotalSegments => Math.Max(1, (_boundaries?.Length ?? (int)Math.Ceiling(TotalDuration / SegmentSeconds) + 1) - 1);
    public DateTimeOffset LastAccess { get; private set; } = DateTimeOffset.UtcNow;

    private readonly string _sourcePath;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _preferredEncoder;
    private readonly PlaybackPlan _plan;
    private readonly MediaProbe? _probe;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _encoderGate = new(1, 1);
    private readonly Dictionary<int, PendingWork> _pending = new();
    private readonly Dictionary<int, DateTimeOffset> _cached = new();
    private CancellationTokenSource _generationCancellation = new();
    private Process? _activeProcess;
    private Task<double[]>? _boundaryTask;
    private double[]? _boundaries;
    private double _sourceFirstKeyframe;
    private int _audioOrdinal;
    private int _generation;
    private bool _disposed;
    private bool _paused;
    private TaskCompletionSource _resumeSignal = CompletedSignal();
    private string? _selectedEncoder;

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed class PendingWork(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<string?> Task { get; set; } = null!;
        public int Waiters { get; set; }
    }

    public TranscodeSession(int mediaFileId, string sourcePath, PlaybackPlan plan, double totalDuration,
        string workingDirectory, string ffmpegPath, string videoEncoder, ILogger log,
        MediaProbe? probe = null, string? ffprobePath = null)
    {
        MediaFileId = mediaFileId;
        _sourcePath = sourcePath;
        _plan = plan;
        TotalDuration = totalDuration;
        WorkingDirectory = workingDirectory;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath ?? Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        _preferredEncoder = videoEncoder;
        _log = log;
        _probe = probe;
        Directory.CreateDirectory(workingDirectory);
    }

    public void Touch() => LastAccess = DateTimeOffset.UtcNow;

    public void PauseEncoding(bool paused)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (paused && !_paused)
                _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else if (!paused && _paused)
                _resumeSignal.TrySetResult();
            _paused = paused;
            Touch();
        }
    }

    public void SetAudio(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        lock (_sync)
        {
            if (_disposed) return;
            if (_probe is not null && ordinal >= _probe.AudioTracks.Count && ordinal != 0)
                throw new ArgumentOutOfRangeException(nameof(ordinal));
            Touch();
            if (ordinal == _audioOrdinal) return;
            _audioOrdinal = ordinal;
            InvalidateGeneration(clearCache: true);
        }
    }

    public Task<string> GetPlaylistAsync(CancellationToken ct = default)
    {
        Task<double[]> task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Touch();
            task = _boundaryTask ??= LoadBoundariesAsync(_generationCancellation.Token);
        }
        return MakePlaylistAsync(task, ct);
    }

    private async Task<string> MakePlaylistAsync(Task<double[]> task, CancellationToken ct)
    {
        var boundaries = await task.WaitAsync(ct);
        var target = Math.Max(1, (int)Math.Ceiling(boundaries.Zip(boundaries.Skip(1), (a, b) => b - a).Max()));
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(target).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            sb.Append("#EXTINF:").Append((boundaries[i + 1] - boundaries[i]).ToString("F6", Culture))
                .Append(",\nseg").Append(i.ToString("D5", Culture)).Append(".ts\n");
        }
        return sb.Append("#EXT-X-ENDLIST\n").ToString();
    }

    public async Task<string?> GetSegmentAsync(int index, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundaries = await GetBoundariesAsync(ct);
        if (index < 0 || index >= boundaries.Length - 1) return null;
        PendingWork work;
        while (true)
        {
            Task? waitForResume = null;
            lock (_sync)
            {
                if (_disposed) return null;
                Touch();
                var path = SegPath(index);
                if (File.Exists(path))
                {
                    _cached[index] = DateTimeOffset.UtcNow;
                    return path;
                }
                if (_paused)
                    waitForResume = _resumeSignal.Task;
                else
                {
                    if (!_pending.TryGetValue(index, out work!))
                    {
                        var generation = _generation;
                        work = new PendingWork(CancellationTokenSource.CreateLinkedTokenSource(_generationCancellation.Token));
                        _pending[index] = work;
                        // Publish before execution so synchronous completion cannot remove an unstored item.
                        work.Task = Task.Run(() => ProduceAsync(index, boundaries[index], boundaries[index + 1],
                            generation, work, work.Cancellation.Token));
                    }
                    work.Waiters++;
                    break;
                }
            }
            await waitForResume!.WaitAsync(ct);
        }
        try { return await work.Task.WaitAsync(ct); }
        finally
        {
            lock (_sync)
            {
                work.Waiters--;
                if (work.Waiters == 0 && !work.Task.IsCompleted)
                {
                    if (_pending.TryGetValue(index, out var current) && ReferenceEquals(current, work))
                        _pending.Remove(index);
                    try { work.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { /* work finished concurrently */ }
                }
            }
        }
    }

    private async Task<double[]> GetBoundariesAsync(CancellationToken ct)
    {
        Task<double[]> task;
        lock (_sync)
        {
            if (_disposed) return [];
            task = _boundaryTask ??= LoadBoundariesAsync(_generationCancellation.Token);
        }
        return await task.WaitAsync(ct);
    }

    private async Task<double[]> LoadBoundariesAsync(CancellationToken token)
    {
        double[] result;
        if (_plan.CopyVideo)
        {
            var keyframes = await ReadKeyframesAsync(token);
            if (keyframes.Count == 0)
                throw new InvalidOperationException("H.264 stream has no accessible keyframe index; refusing an inaccurate HLS playlist.");
            var first = keyframes[0];
            _sourceFirstKeyframe = first;
            var points = new List<double> { 0 };
            foreach (var pts in keyframes.Skip(1))
            {
                var at = pts - first;
                if (at >= points[^1] + SegmentSeconds && at < TotalDuration - 0.05)
                    points.Add(at);
            }
            points.Add(TotalDuration);
            result = points.ToArray();
        }
        else
        {
            var count = Math.Max(1, (int)Math.Ceiling(TotalDuration / SegmentSeconds));
            result = Enumerable.Range(0, count + 1).Select(i => Math.Min(i * SegmentSeconds, TotalDuration)).ToArray();
        }
        lock (_sync) { if (!_disposed) _boundaries = result; }
        return result;
    }

    private async Task<List<double>> ReadKeyframesAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffprobePath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_packets",
                     "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", _sourcePath })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe could not start");
        using var registration = ct.Register(() => Kill(process));
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var keyframes = new List<double>();
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            var fields = line.Split(',', 3);
            if (fields.Length >= 2 && fields[1].Contains('K') &&
                double.TryParse(fields[0], NumberStyles.Float, Culture, out var pts) && double.IsFinite(pts))
                keyframes.Add(pts);
        }
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ffprobe keyframe scan failed: " + await stderr);
        keyframes.Sort();
        return keyframes.Distinct().ToList();
    }

    private async Task<string?> ProduceAsync(int index, double start, double end, int generation,
        PendingWork work, CancellationToken token)
    {
        try
        {
            await _encoderGate.WaitAsync(token);
            try
            {
                var profiles = _plan.CopyVideo ? new[] { "copy" } : EncoderProfiles();
                foreach (var profile in profiles)
                {
                    token.ThrowIfCancellationRequested();
                    var temp = Path.Combine(WorkingDirectory, $"g{generation}-s{index:D5}-{Guid.NewGuid():N}.tmp.ts");
                    try
                    {
                        var ok = await RunFfmpegAsync(BuildArgs(start, end, temp, profile), token);
                        if (!ok || !File.Exists(temp) || new FileInfo(temp).Length == 0)
                        {
                            _log.LogWarning("Encoder {Encoder} failed for media {Id}; trying fallback", profile, MediaFileId);
                            continue;
                        }
                        lock (_sync)
                        {
                            if (_disposed || generation != _generation) return null;
                            var path = SegPath(index);
                            File.Move(temp, path, overwrite: true);
                            _cached[index] = DateTimeOffset.UtcNow;
                            _selectedEncoder = profile;
                            TrimCache(index);
                            return path;
                        }
                    }
                    catch (OperationCanceledException) { return null; }
                    finally { TryDelete(temp); }
                }
                return null;
            }
            finally { _encoderGate.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(index, out var current) && ReferenceEquals(current, work))
                    _pending.Remove(index);
            }
            work.Cancellation.Dispose();
        }
    }

    private string[] EncoderProfiles()
    {
        // Hardware must be explicitly requested. A failed launch falls back to a bundled software encoder.
        if (_selectedEncoder is { } selected)
            return selected == "libopenh264" ? [selected] : [selected, "libopenh264"];
        return _preferredEncoder switch
        {
            "h264_mf" => ["h264_mf", "libopenh264"],
            "h264_nvenc" or "h264_amf" or "h264_qsv" => [_preferredEncoder, "libopenh264"],
            "libopenh264" => ["libopenh264"],
            _ => [_preferredEncoder, "libopenh264"]
        };
    }

    private IEnumerable<string> BuildArgs(double start, double end, string output, string encoder)
    {
        yield return "-hide_banner"; yield return "-loglevel"; yield return "warning";
        yield return "-ss"; yield return (start + (_plan.CopyVideo ? _sourceFirstKeyframe : 0)).ToString("R", Culture);
        yield return "-i"; yield return _sourcePath;
        // With -copyts FFmpeg interprets output -t against the absolute source PTS.
        // -to must therefore name the absolute boundary, including a nonzero first PTS.
        yield return "-to"; yield return (end + (_plan.CopyVideo ? _sourceFirstKeyframe : 0)).ToString("R", Culture);
        yield return "-map"; yield return "0:v:0";
        if (_probe?.AudioTracks.Count is not 0)
        {
            yield return "-map"; yield return _probe is null ? $"0:a:{_audioOrdinal}?" : $"0:a:{_audioOrdinal}";
            var selectedAudio = _probe?.AudioTracks.ElementAtOrDefault(_audioOrdinal);
            var copyAudio = _plan.CopyAudio && (selectedAudio is null || selectedAudio.Codec is "aac" or "mp3");
            yield return "-c:a"; yield return copyAudio ? "copy" : "aac";
            if (!copyAudio) { yield return "-b:a"; yield return "256k"; yield return "-ac"; yield return "2"; }
        }
        else yield return "-an";
        yield return "-c:v"; yield return encoder;
        if (encoder != "copy")
        {
            var pixels = (_probe?.Width ?? 1920L) * (_probe?.Height ?? 1080L);
            var bitrate = pixels >= 3840L * 2160 ? "40M" : pixels >= 1920L * 1080 ? "20M" : "10M";
            yield return "-b:v"; yield return bitrate;
            yield return "-pix_fmt"; yield return "yuv420p";
            if (encoder == "h264_mf")
            {
                yield return "-hw_encoding"; yield return "true";
                yield return "-rate_control"; yield return "quality";
                yield return "-quality"; yield return "85";
            }
            else if (encoder == "libopenh264")
            {
                yield return "-rc_mode"; yield return "quality";
                yield return "-profile:v"; yield return "high";
            }
            yield return "-force_key_frames"; yield return "0";
        }
        yield return "-copyts";
        yield return "-avoid_negative_ts"; yield return "disabled";
        yield return "-f"; yield return "mpegts";
        yield return "-y"; yield return output;
    }

    private async Task<bool> RunFfmpegAsync(IEnumerable<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not start");
        lock (_sync) { if (_disposed) { Kill(process); return false; } _activeProcess = process; }
        using var registration = token.Register(() => Kill(process));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(token);
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                _log.LogWarning("FFmpeg media {Id} exited {Code}: {Error}", MediaFileId, process.ExitCode,
                    error.Length > 1200 ? error[^1200..] : error);
            return process.ExitCode == 0;
        }
        finally { lock (_sync) { if (ReferenceEquals(_activeProcess, process)) _activeProcess = null; } }
    }

    private void TrimCache(int current)
    {
        long bytes = 0;
        foreach (var index in _cached.Keys.ToList())
        {
            var file = SegPath(index);
            if (!File.Exists(file)) _cached.Remove(index);
            else bytes += new FileInfo(file).Length;
        }
        foreach (var index in _cached.OrderBy(p => p.Value).Select(p => p.Key).ToList())
        {
            if (_cached.Count <= MaxCachedSegments && bytes <= MaxCachedBytes) break;
            if (index == current) continue;
            var file = SegPath(index);
            if (File.Exists(file)) { bytes -= new FileInfo(file).Length; TryDelete(file); }
            _cached.Remove(index);
        }
    }

    private void InvalidateGeneration(bool clearCache)
    {
        _generation++;
        _generationCancellation.Cancel();
        Kill(_activeProcess);
        _generationCancellation.Dispose();
        _generationCancellation = new CancellationTokenSource();
        _pending.Clear();
        if (_boundaryTask is { IsCompletedSuccessfully: false }) _boundaryTask = null;
        if (clearCache)
        {
            _cached.Clear();
            foreach (var file in Directory.EnumerateFiles(WorkingDirectory)) TryDelete(file);
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(WorkingDirectory, "g*.tmp.ts")) TryDelete(file);
        }
    }

    private string SegPath(int index) => Path.Combine(WorkingDirectory, $"seg{index:D5}.ts");
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Task<string?>[] pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _resumeSignal.TrySetResult();
            pending = _pending.Values.Select(p => p.Task).ToArray();
            InvalidateGeneration(clearCache: true);
        }
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        try { Directory.Delete(WorkingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
