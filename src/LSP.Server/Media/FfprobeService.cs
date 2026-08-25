using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Media;

/// <summary>Výsledek sondy souboru – co potřebujeme pro rozhodnutí direct/remux/transkód.</summary>
public sealed record AudioTrackInfo(
    int Ordinal,
    int StreamIndex,
    string? Codec,
    string? Language,
    string? NormalizedLanguage,
    string Label,
    bool IsDefault);

public sealed record SubtitleTrackInfo(
    string Id,
    string Source,
    int? Ordinal,
    int? StreamIndex,
    string? Codec,
    string? Language,
    string? NormalizedLanguage,
    string Label,
    bool IsDefault,
    bool IsForced,
    bool IsPlayable,
    string? FilePath);

public sealed record MediaProbe(
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    double? DurationSeconds,
    int? Width,
    int? Height,
    IReadOnlyList<AudioTrackInfo> AudioTracks,
    IReadOnlyList<SubtitleTrackInfo> SubtitleTracks);

/// <summary>Spustí ffprobe a vyparsuje kodeky/kontejner/rozlišení z JSON výstupu.</summary>
public sealed class FfprobeService(FfmpegLocator locator, ILogger<FfprobeService> log)
{
    public async Task<MediaProbe?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = locator.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var proc = Process.Start(psi)!;
            var json = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return null;

            return ParseProbeJson(json);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ffprobe selhal pro {Path}", filePath);
            return null;
        }
    }

    public static MediaProbe ParseProbeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? container = null;
        double? duration = null;
        if (root.TryGetProperty("format", out var format))
        {
            container = format.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
            if (format.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var dur))
                duration = dur;
        }

        string? video = null, audio = null;
        int? width = null, height = null;
        var audioTracks = new List<AudioTrackInfo>();
        var audioOrdinal = 0;
        var subtitleTracks = new List<SubtitleTrackInfo>();
        var subtitleOrdinal = 0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                var streamIndex = s.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var idx) ? idx : -1;

                if (type == "video" && video is null)
                {
                    video = codec;
                    if (s.TryGetProperty("width", out var w)) width = w.GetInt32();
                    if (s.TryGetProperty("height", out var h)) height = h.GetInt32();
                }
                else if (type == "audio" && audio is null)
                {
                    audio = codec;
                }

                if (type == "audio")
                {
                    var language = ReadTag(s, "language");
                    var title = ReadTag(s, "title");
                    var normalized = MediaLanguageNormalizer.Normalize(language ?? title);
                    var isDefault = IsDispositionSet(s, "default");
                    var label = BuildAudioLabel(audioOrdinal, language, title, codec, isDefault);

                    audioTracks.Add(new AudioTrackInfo(
                        audioOrdinal, streamIndex, codec, language, normalized, label, isDefault));
                    audioOrdinal++;
                }
                else if (type == "subtitle")
                {
                    var language = ReadTag(s, "language");
                    var title = ReadTag(s, "title");
                    var normalized = MediaLanguageNormalizer.Normalize(language ?? title);
                    var isDefault = IsDispositionSet(s, "default");
                    var isForced = IsDispositionSet(s, "forced");
                    var isPlayable = IsPlayableSubtitleCodec(codec);
                    var label = BuildSubtitleLabel(subtitleOrdinal, "Interní", language, title, codec, isDefault, isForced);

                    subtitleTracks.Add(new SubtitleTrackInfo(
                        $"internal-{subtitleOrdinal}",
                        "internal",
                        subtitleOrdinal,
                        streamIndex,
                        codec,
                        language,
                        normalized,
                        label,
                        isDefault,
                        isForced,
                        isPlayable,
                        null));
                    subtitleOrdinal++;
                }
            }
        }

        return new MediaProbe(container, video, audio, duration, width, height, audioTracks, subtitleTracks);
    }

    private static string? ReadTag(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return null;

        return tags.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static bool IsDispositionSet(JsonElement stream, string name)
    {
        return stream.TryGetProperty("disposition", out var disposition)
               && disposition.ValueKind == JsonValueKind.Object
               && disposition.TryGetProperty(name, out var value)
               && value.TryGetInt32(out var flag)
               && flag == 1;
    }

    private static string BuildAudioLabel(
        int ordinal, string? language, string? title, string? codec, bool isDefault)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());
        if (!string.IsNullOrWhiteSpace(language)) parts.Add(language.Trim());
        if (!string.IsNullOrWhiteSpace(codec)) parts.Add(codec.Trim().ToUpperInvariant());
        if (isDefault) parts.Add("default");

        return parts.Count > 0 ? string.Join(" · ", parts) : $"Audio {ordinal + 1}";
    }

    private static bool IsPlayableSubtitleCodec(string? codec) =>
        codec is not null && codec.ToLowerInvariant() is
            "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text";

    internal static string BuildSubtitleLabel(
        int ordinal, string source, string? language, string? title, string? codec, bool isDefault, bool isForced)
    {
        var parts = new List<string> { $"{source} {ordinal + 1}" };
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());
        if (!string.IsNullOrWhiteSpace(language)) parts.Add(language.Trim());
        if (!string.IsNullOrWhiteSpace(codec)) parts.Add(codec.Trim().ToUpperInvariant());
        if (isForced) parts.Add("forced");
        if (isDefault) parts.Add("default");

        return string.Join(" · ", parts);
    }
}
