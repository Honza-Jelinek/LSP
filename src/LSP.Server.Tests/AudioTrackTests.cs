using LSP.Server.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class AudioTrackTests
{
    [Fact]
    public void PurgeSegments_deletes_orphan_dir_and_no_throw_when_missing()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "purge-tests", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Media:TranscodeRoot"] = root })
            .Build();

        // Sirotek z „minulého běhu" – smaže ho startup sweep v ctoru.
        var orphan = Path.Combine(root, "7");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "seg00000.ts"), "x");

        using var manager = new TranscodeSessionManager(
            new FfmpegLocator(new ConfigurationBuilder().Build()), config, NullLogger<TranscodeSessionManager>.Instance);

        Assert.False(Directory.Exists(orphan)); // smeteno při startu

        // Bez živé relace: PurgeSegments smaže složku dle id a nespadne u neexistující.
        var dir = Path.Combine(root, "42");
        Directory.CreateDirectory(dir);
        manager.PurgeSegments(42);
        Assert.False(Directory.Exists(dir));

        manager.PurgeSegments(999); // neexistuje → žádná výjimka
    }

    [Theory]
    [InlineData("cs", "cs")]
    [InlineData("cz", "cs")]
    [InlineData("ces", "cs")]
    [InlineData("cze", "cs")]
    [InlineData("cesky", "cs")]
    [InlineData("cestina", "cs")]
    [InlineData("česky", "cs")]
    [InlineData("čeština", "cs")]
    [InlineData("sk", "sk")]
    [InlineData("eng", "en")]
    [InlineData("deu", "de")]
    public void Normalize_maps_common_audio_language_aliases(string input, string expected)
    {
        Assert.Equal(expected, MediaLanguageNormalizer.Normalize(input));
    }

    [Fact]
    public void ParseProbeJson_keeps_absolute_stream_index_but_counts_audio_ordinals()
    {
        var probe = FfprobeService.ParseProbeJson("""
        {
          "format": { "format_name": "matroska,webm", "duration": "123.45" },
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 },
            {
              "index": 2,
              "codec_type": "audio",
              "codec_name": "aac",
              "tags": { "language": "eng", "title": "English" },
              "disposition": { "default": 0 }
            },
            { "index": 3, "codec_type": "subtitle", "codec_name": "subrip" },
            {
              "index": 5,
              "codec_type": "audio",
              "codec_name": "ac3",
              "tags": { "language": "cze", "title": "Čeština" },
              "disposition": { "default": 1 }
            }
          ]
        }
        """);

        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal(2, probe.AudioTracks.Count);

        Assert.Equal(0, probe.AudioTracks[0].Ordinal);
        Assert.Equal(2, probe.AudioTracks[0].StreamIndex);
        Assert.Equal("en", probe.AudioTracks[0].NormalizedLanguage);

        Assert.Equal(1, probe.AudioTracks[1].Ordinal);
        Assert.Equal(5, probe.AudioTracks[1].StreamIndex);
        Assert.Equal("cs", probe.AudioTracks[1].NormalizedLanguage);
        Assert.True(probe.AudioTracks[1].IsDefault);
    }

    [Fact]
    public void ParseProbeJson_marks_text_subtitles_playable_and_bitmap_subtitles_unplayable()
    {
        var probe = FfprobeService.ParseProbeJson("""
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264" },
            {
              "index": 4,
              "codec_type": "subtitle",
              "codec_name": "subrip",
              "tags": { "language": "cze", "title": "Čeština" },
              "disposition": { "default": 1, "forced": 0 }
            },
            {
              "index": 5,
              "codec_type": "subtitle",
              "codec_name": "hdmv_pgs_subtitle",
              "tags": { "language": "eng" },
              "disposition": { "default": 0, "forced": 1 }
            }
          ]
        }
        """);

        Assert.Equal(2, probe.SubtitleTracks.Count);
        Assert.Equal("internal-0", probe.SubtitleTracks[0].Id);
        Assert.Equal(0, probe.SubtitleTracks[0].Ordinal);
        Assert.Equal(4, probe.SubtitleTracks[0].StreamIndex);
        Assert.Equal("cs", probe.SubtitleTracks[0].NormalizedLanguage);
        Assert.True(probe.SubtitleTracks[0].IsPlayable);
        Assert.True(probe.SubtitleTracks[0].IsDefault);

        Assert.Equal("internal-1", probe.SubtitleTracks[1].Id);
        Assert.Equal(1, probe.SubtitleTracks[1].Ordinal);
        Assert.Equal(5, probe.SubtitleTracks[1].StreamIndex);
        Assert.False(probe.SubtitleTracks[1].IsPlayable);
        Assert.True(probe.SubtitleTracks[1].IsForced);
    }

    [Fact]
    public void Selector_prefers_language_then_default_then_first()
    {
        var tracks = new[]
        {
            new AudioTrackInfo(0, 1, "aac", "eng", "en", "English", false),
            new AudioTrackInfo(1, 2, "aac", "cze", "cs", "Czech commentary", false),
            new AudioTrackInfo(2, 3, "aac", "ces", "cs", "Czech default", true),
        };

        Assert.Equal(2, AudioTrackSelector.Select(tracks, null, "čeština")?.Ordinal);
        Assert.Equal(1, AudioTrackSelector.Select(tracks, 1, "en")?.Ordinal);
        Assert.Equal(0, AudioTrackSelector.Select(tracks, null, null)?.Ordinal);
    }

    [Fact]
    public void SetAudio_same_ordinal_keeps_segments_and_changed_ordinal_clears_them()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "audio-track-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var segment = Path.Combine(dir, "seg00000.ts");
        var playlist = Path.Combine(dir, "internal.m3u8");
        File.WriteAllText(segment, "old segment");
        File.WriteAllText(playlist, "old playlist");

        using var session = new TranscodeSession(
            42,
            "source.mkv",
            new PlaybackPlan(PlaybackMode.Hls, CopyVideo: false, CopyAudio: false),
            120,
            dir,
            "ffmpeg",
            "h264",
            NullLogger.Instance);

        session.SetAudio(0);

        Assert.True(File.Exists(segment));
        Assert.True(File.Exists(playlist));

        session.SetAudio(1);

        Assert.Empty(Directory.EnumerateFiles(dir));
    }

    [Fact]
    public void ConvertSrtToWebVtt_rewrites_header_and_timestamp_decimal_separator()
    {
        var vtt = SubtitleService.ConvertSrtToWebVtt("""
        1
        00:00:01,500 --> 00:00:02,750
        Ahoj
        """);

        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:02.750", vtt);
        Assert.Contains("Ahoj", vtt);
    }

    [Fact]
    public void BuildTracks_adds_matching_sidecar_subtitles()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "subtitle-track-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "Movie.mkv");
        var czech = Path.Combine(dir, "Movie.cs.srt");
        var english = Path.Combine(dir, "Movie.en.vtt");
        var unrelated = Path.Combine(dir, "Other.cs.srt");
        File.WriteAllText(video, "");
        File.WriteAllText(czech, "");
        File.WriteAllText(english, "");
        File.WriteAllText(unrelated, "");

        var service = new SubtitleService(
            new FfmpegLocator(new ConfigurationBuilder().Build()),
            NullLogger<SubtitleService>.Instance);
        var probe = new MediaProbe(null, null, null, null, null, null, [], []);

        var tracks = service.BuildTracks(video, probe);

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, track => Assert.Equal("sidecar", track.Source));
        Assert.Contains(tracks, track => track.NormalizedLanguage == "cs" && track.Codec == "srt");
        Assert.Contains(tracks, track => track.NormalizedLanguage == "en" && track.Codec == "vtt");
    }
}
