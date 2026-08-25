using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Media;

/// <summary>
/// Perzistentní HLS transkódovací relace. Jeden plynulý ffmpeg průchod s vynucenými klíčovými snímky
/// po 6 s → čisté, souvislé segmenty (žádné koktání). Při seeku mimo aktuální rozsah se ffmpeg restartuje
/// od daného segmentu; <c>-output_ts_offset</c> zajistí správný absolutní čas (klíčová oprava rozbitého PTS/délky).
/// HLS cesta vždy transkóduje (i H.264), aby hranice segmentů přesně seděly s VOD playlistem.
/// </summary>
public sealed class TranscodeSession : IDisposable
{
    // Kratší segmenty = nižší latence startu i seeku (víc segmentů je zanedbatelná režie).
    public const double SegmentSeconds = 4;

    // Kolik segmentů dopředu (oproti produkci) tolerujeme jako prefetch, než restartujeme ffmpeg.
    private const int RestartAheadSegments = 12;

    public int MediaFileId { get; }
    public double TotalDuration { get; }
    public string WorkingDirectory { get; }
    public int TotalSegments => Math.Max(1, (int)Math.Ceiling(TotalDuration / SegmentSeconds));
    public DateTimeOffset LastAccess { get; private set; } = DateTimeOffset.UtcNow;

    private readonly string _sourcePath;
    private readonly string _ffmpegPath;
    private readonly string _videoEncoder;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private int _startSegment;
    private int _audioOrdinal;

    public TranscodeSession(
        int mediaFileId, string sourcePath, PlaybackPlan plan, double totalDuration,
        string workingDirectory, string ffmpegPath, string videoEncoder, ILogger log)
    {
        MediaFileId = mediaFileId;
        _sourcePath = sourcePath;
        TotalDuration = totalDuration;
        WorkingDirectory = workingDirectory;
        _ffmpegPath = ffmpegPath;
        _videoEncoder = videoEncoder;
        _log = log;
        _ = plan; // HLS cesta vždy transkóduje
    }

    public void Touch() => LastAccess = DateTimeOffset.UtcNow;

    private string SegPath(int index) => Path.Combine(WorkingDirectory, $"seg{index:D5}.ts");

    public void SetAudio(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        _gate.Wait();
        try
        {
            Touch();
            if (ordinal == _audioOrdinal)
                return;

            KillProcess();
            ClearGeneratedFiles();
            _audioOrdinal = ordinal;
            _startSegment = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetSegmentAsync(int index, CancellationToken ct = default)
    {
        Touch();

        await _gate.WaitAsync(ct);
        try
        {
            EnsureCovers(index);
        }
        finally
        {
            _gate.Release();
        }

        // Díky -hls_flags +temp_file je seg{index}.ts na disku až jako KOMPLETNÍ (atomické přejmenování),
        // takže ho stačí servírovat hned jak existuje.
        var path = SegPath(index);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(path))
                return path;
            if ((_process?.HasExited ?? true) && !File.Exists(path))
                return null; // ffmpeg skončil/spadl a segment nevznikl

            await Task.Delay(100, ct);
        }
        return File.Exists(path) ? path : null;
    }

    private void EnsureCovers(int index)
    {
        var running = _process is { HasExited: false };
        var producedMax = HighestSegment();

        if (!running)
        {
            if (!File.Exists(SegPath(index)))
                StartFrom(index);
        }
        else if (index < _startSegment && !File.Exists(SegPath(index)))
        {
            StartFrom(index); // seek dozadu před aktuální běh
        }
        else if (index > producedMax + RestartAheadSegments)
        {
            StartFrom(index); // seek daleko dopředu
        }
        // jinak ffmpeg segment brzy doprodukuje → jen počkáme
    }

    private int HighestSegment()
    {
        var max = -1;
        if (!Directory.Exists(WorkingDirectory)) return max;
        foreach (var f in Directory.EnumerateFiles(WorkingDirectory, "seg*.ts"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(3), out var n) && n > max)
                max = n;
        }
        return max;
    }

    private void StartFrom(int segment)
    {
        KillProcess();
        _startSegment = segment;

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory,
        };
        foreach (var arg in BuildArgs(segment))
            psi.ArgumentList.Add(arg);

        _log.LogInformation("Transkód #{Id}: start od segmentu {Seg} (t={T}s)", MediaFileId, segment, segment * SegmentSeconds);

        _process = Process.Start(psi)!;
        _ = DrainAsync(_process);
    }

    private IEnumerable<string> BuildArgs(int startSegment)
    {
        var seg = SegmentSeconds.ToString(CultureInfo.InvariantCulture);
        var offset = startSegment * SegmentSeconds;

        yield return "-hide_banner";
        yield return "-loglevel"; yield return "warning";
        yield return "-hwaccel"; yield return "auto";

        if (startSegment > 0)
        {
            yield return "-ss";
            yield return offset.ToString(CultureInfo.InvariantCulture);
        }

        yield return "-i"; yield return _sourcePath;

        // Zachovej reálné časové značky zdroje → segmenty mají správný absolutní čas i po seeku
        // (HLS muxer ignoruje -output_ts_offset; bez -copyts se PTS po seeku resetuje a hls.js zmatkuje).
        yield return "-copyts";

        yield return "-map"; yield return "0:v:0";
        yield return "-map"; yield return $"0:a:{_audioOrdinal}";

        yield return "-c:v"; yield return _videoEncoder;
        yield return "-b:v"; yield return "8M";
        // Vynucený klíčový snímek na hranicích segmentů. S -copyts je t reálný čas, takže
        // do exprese zahrnujeme i offset (startSegment), jinak by se forcovalo každý snímek.
        yield return "-force_key_frames";
        yield return $"expr:gte(t,({startSegment}+n_forced)*{seg})";

        yield return "-c:a"; yield return "aac";
        yield return "-b:a"; yield return "192k";
        yield return "-ac"; yield return "2";

        yield return "-f"; yield return "hls";
        yield return "-hls_time"; yield return seg;
        yield return "-hls_list_size"; yield return "0";
        yield return "-hls_flags"; yield return "independent_segments+temp_file";
        yield return "-hls_segment_type"; yield return "mpegts";
        yield return "-start_number"; yield return startSegment.ToString();
        yield return "-hls_segment_filename"; yield return Path.Combine(WorkingDirectory, "seg%05d.ts");
        yield return Path.Combine(WorkingDirectory, "internal.m3u8");
    }

    private async Task DrainAsync(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                _log.LogWarning("Transkód #{Id} kód {Code}: {Err}",
                    MediaFileId, process.ExitCode, stderr.Length > 600 ? stderr[^600..] : stderr);
        }
        catch { /* killnuto při restartu/dispose */ }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch { /* best-effort */ }
        _process = null;
    }

    private void ClearGeneratedFiles()
    {
        try
        {
            Directory.CreateDirectory(WorkingDirectory);
            foreach (var file in Directory.EnumerateFiles(WorkingDirectory))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NepodaĹ™ilo se vyÄŤistit segmenty pro transkĂłd #{Id}", MediaFileId);
        }
    }

    public void Dispose()
    {
        KillProcess();
        _gate.Dispose();
        try
        {
            if (Directory.Exists(WorkingDirectory))
                Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch { /* best-effort */ }
    }
}
