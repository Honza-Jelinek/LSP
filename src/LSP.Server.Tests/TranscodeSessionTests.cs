using System.Diagnostics;
using System.Globalization;
using LSP.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class TranscodeSessionTests
{
    private static readonly string? ToolDirectory = FindTools();

    [Fact]
    public async Task Copy_video_uses_true_keyframe_intervals_and_preserves_frames()
    {
        if (ToolDirectory is null) return;
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "input.mkv");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=320x180:rate=24:duration=13", "-f", "lavfi", "-i",
                "sine=frequency=440:sample_rate=48000:duration=13", "-c:v", "libopenh264",
                "-b:v", "2M", "-g", "300", "-force_key_frames", "0,1.3,5.2,9.7",
                "-c:a", "aac", "-b:a", "128k", "-y", input);
            var probe = await Probe(input);
            using var session = new TranscodeSession(1, input,
                new PlaybackPlan(PlaybackMode.Hls, true, true), 13,
                Path.Combine(dir, "segments"), Tool("ffmpeg"), "h264_mf", NullLogger.Instance,
                probe, Tool("ffprobe"));
            var playlist = await session.GetPlaylistAsync();
            Assert.Contains("#EXTINF:5.208000", playlist);
            Assert.Contains("#EXTINF:4.500000", playlist);
            Assert.Equal(3, session.TotalSegments);
            var sourceHashes = await FrameHashes(input);
            // Request out of order, including a gap between cached intervals.
            var files = new string?[session.TotalSegments];
            foreach (var i in new[] { 2, 0, 1 })
            {
                files[i] = await session.GetSegmentAsync(i);
                Assert.NotNull(files[i]);
            }
            var segmentHashes = new List<string>();
            foreach (var file in files) segmentHashes.AddRange(await FrameHashes(file!));
            Assert.Equal(sourceHashes, segmentHashes);
            await AssertContinuousAudio(files!);
            Assert.NotNull(await session.GetSegmentAsync(2));
            Assert.NotNull(await session.GetSegmentAsync(0));
            Assert.Null(await session.GetSegmentAsync(3));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Silent_video_and_software_fallback_produce_segments_on_demand()
    {
        if (ToolDirectory is null) return;
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "silent.mkv");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=320x180:rate=24:duration=5", "-c:v", "mpeg4", "-q:v", "5", "-an", "-y", input);
            var probe = await Probe(input);
            Assert.Empty(probe.AudioTracks);
            using var session = new TranscodeSession(2, input,
                new PlaybackPlan(PlaybackMode.Hls, false, false), 5,
                Path.Combine(dir, "segments"), Tool("ffmpeg"), "unavailable_encoder", NullLogger.Instance,
                probe, Tool("ffprobe"));
            var playlist = await session.GetPlaylistAsync();
            Assert.Contains("#EXTINF:4.000000", playlist);
            Assert.Contains("#EXTINF:1.000000", playlist);
            session.PauseEncoding(true);
            Assert.Empty(Directory.EnumerateFiles(session.WorkingDirectory));
            var file = await session.GetSegmentAsync(1);
            Assert.NotNull(file);
            Assert.True(new FileInfo(file).Length > 0);
            Assert.Single(Directory.EnumerateFiles(session.WorkingDirectory, "seg*.ts"));
            Assert.DoesNotContain("audio", await RunCapture("ffprobe", "-v", "error", "-show_entries",
                "stream=codec_type", "-of", "default=nw=1", file));
            session.Dispose();
            session.Dispose();
        }
        finally { TryDelete(dir); }
    }

    [Theory]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(3840, 2160, 0.5)]
    public async Task Media_foundation_hardware_profile_has_a_working_software_fallback(int width, int height, double duration)
    {
        if (ToolDirectory is null) return;
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "motion.mkv");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                $"testsrc2=size={width}x{height}:rate=24:duration={duration.ToString(CultureInfo.InvariantCulture)}",
                "-c:v", "mpeg4", "-q:v", "3", "-an", "-y", input);
            var probe = await Probe(input);
            using var session = new TranscodeSession(3, input,
                new PlaybackPlan(PlaybackMode.Hls, false, false), duration,
                Path.Combine(dir, "segments"), Tool("ffmpeg"), "h264_mf", NullLogger.Instance,
                probe, Tool("ffprobe"));
            var file = await session.GetSegmentAsync(0);
            Assert.NotNull(file);
            var result = await Probe(file);
            Assert.Equal("h264", result.VideoCodec);
            Assert.Empty(result.AudioTracks);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Dispose_waits_for_active_encoder_and_cancels_pending_segment()
    {
        if (ToolDirectory is null) return;
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "long-motion.mkv");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=3840x2160:rate=24:duration=4", "-c:v", "mpeg4", "-q:v", "5", "-an", "-y", input);
            var session = new TranscodeSession(4, input,
                new PlaybackPlan(PlaybackMode.Hls, false, false), 4,
                Path.Combine(dir, "segments"), Tool("ffmpeg"), "libopenh264", NullLogger.Instance,
                await Probe(input), Tool("ffprobe"));
            var pending = session.GetSegmentAsync(0);
            var field = typeof(TranscodeSession).GetField("_activeProcess",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Process? active = null;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && active is null)
            {
                active = (Process?)field.GetValue(session);
                if (active is null) await Task.Delay(5);
            }
            Assert.NotNull(active);
            var pid = active.Id;
            session.Dispose();
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            Assert.Null(await pending);
            Assert.False(Directory.Exists(session.WorkingDirectory));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Canceling_one_of_two_waiters_keeps_shared_segment_generation()
    {
        if (ToolDirectory is null) return;
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "shared.mkv");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=1920x1080:rate=24:duration=4", "-c:v", "mpeg4", "-q:v", "5", "-an", "-y", input);
            using var session = new TranscodeSession(5, input,
                new PlaybackPlan(PlaybackMode.Hls, false, false), 4,
                Path.Combine(dir, "segments"), Tool("ffmpeg"), "libopenh264", NullLogger.Instance,
                await Probe(input), Tool("ffprobe"));
            using var canceled = new CancellationTokenSource();
            var first = session.GetSegmentAsync(0, canceled.Token);
            var second = session.GetSegmentAsync(0);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var file = await second;
            Assert.NotNull(file);
            Assert.True(new FileInfo(file).Length > 0);
            Assert.Single(Directory.EnumerateFiles(session.WorkingDirectory, "seg*.ts"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Transcoded_video_and_audio_have_continuous_timestamps_across_segments()
    {
        if (ToolDirectory is null) return;
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "source.mkv");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=320x180:rate=24:duration=8", "-f", "lavfi", "-i",
                "sine=frequency=440:sample_rate=48000:duration=8", "-c:v", "mpeg4", "-q:v", "4",
                "-c:a", "aac", "-b:a", "128k", "-y", input);
            using var session = new TranscodeSession(6, input,
                new PlaybackPlan(PlaybackMode.Hls, false, true), 8,
                Path.Combine(dir, "segments"), Tool("ffmpeg"), "libopenh264", NullLogger.Instance,
                await Probe(input), Tool("ffprobe"));
            var files = new[] { await session.GetSegmentAsync(0), await session.GetSegmentAsync(1) };
            Assert.All(files, file => Assert.NotNull(file));
            await AssertContinuousAudio(files!);
            var video0 = await PacketTimes(files[0]!, "v:0");
            var video1 = await PacketTimes(files[1]!, "v:0");
            Assert.InRange(video1[0] - video0[^1], 0, 0.1);
        }
        finally { TryDelete(dir); }
    }

    private static async Task<MediaProbe> Probe(string path)
    {
        var json = await RunCapture("ffprobe", "-v", "quiet", "-print_format", "json",
            "-show_format", "-show_streams", path);
        return FfprobeService.ParseProbeJson(json);
    }

    private static async Task<List<string>> FrameHashes(string path)
    {
        var output = await RunCapture("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", path,
            "-map", "0:v:0", "-f", "framemd5", "-");
        return output.Split('\n').Where(line => !line.StartsWith('#') && line.Contains(','))
            .Select(line => line.Split(',')[^1].Trim()).ToList();
    }

    private static async Task AssertContinuousAudio(string?[] files)
    {
        double? previousEnd = null;
        foreach (var file in files)
        {
            var times = await PacketTimes(file!, "a:0");
            Assert.NotEmpty(times);
            if (previousEnd is { } end) Assert.InRange(times[0] - end, -0.002, 0.05);
            previousEnd = times[^1] + 1024.0 / 48000;
        }
    }

    private static async Task<List<double>> PacketTimes(string path, string stream)
    {
        var output = await RunCapture("ffprobe", "-v", "error", "-select_streams", stream,
            "-show_packets", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path);
        return output.Split('\n').Select(line => line.Split(',')[0].Trim())
            .Where(line => double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(line => double.Parse(line, CultureInfo.InvariantCulture)).ToList();
    }

    private static string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "lsp-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static void TryDelete(string path) { try { Directory.Delete(path, true); } catch (IOException) { } }
    private static string Tool(string name) => Path.Combine(ToolDirectory!, OperatingSystem.IsWindows() ? name + ".exe" : name);
    private static Task Run(string name, params string[] args) => RunCapture(name, args);
    private static async Task<string> RunCapture(string name, params string[] args)
    {
        var psi = new ProcessStartInfo(Tool(name)) { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
    private static string? FindTools()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "ffmpeg", rid);
            if (File.Exists(Path.Combine(path, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))) return path;
        }
        return null;
    }
}
