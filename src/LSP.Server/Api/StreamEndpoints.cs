using LSP.Server.Data;
using LSP.Server.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Api;

public sealed record StreamInfoDto(
    int MediaFileId,
    string Mode,            // "direct" | "hls"
    string Url,             // co má frontend načíst
    double? DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    IReadOnlyList<AudioTrackDto> AudioTracks,
    int? SelectedAudioOrdinal,
    string? SelectedAudioLanguage,
    IReadOnlyList<SubtitleTrackDto> SubtitleTracks);

public sealed record AudioTrackDto(
    int Ordinal,
    int StreamIndex,
    string? Codec,
    string? Language,
    string? NormalizedLanguage,
    string Label,
    bool IsDefault);

public sealed record SubtitleTrackDto(
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
    string? Url);

public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stream");

        group.MapGet("/{id:int}/info", GetInfo);
        group.MapGet("/{id:int}/direct", GetDirect);
        group.MapGet("/{id:int}/hls/index.m3u8", GetPlaylist);
        group.MapGet("/{id:int}/hls/{segment}", GetSegment);
        group.MapGet("/{id:int}/subtitles/{trackId}.vtt", GetSubtitle);
        group.MapDelete("/{id:int}/segments", PurgeSegments);
    }

    /// <summary>Zahodí transkód relaci a smaže segmenty daného souboru (volá se při přepnutí na jinou epizodu).</summary>
    private static IResult PurgeSegments(int id, TranscodeSessionManager sessions)
    {
        sessions.PurgeSegments(id);
        return Results.NoContent();
    }

    private static async Task<IResult> GetInfo(
        int id, int? audio, LibraryDbContext db, FfprobeService ffprobe, SettingsService settings, SubtitleService subtitles, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return Results.NotFound();

        await EnsureProbedAsync(db, ffprobe, file, ct);

        var liveProbe = File.Exists(file.Path) ? await ffprobe.ProbeAsync(file.Path, ct) : null;
        var probe = liveProbe ?? new MediaProbe(
            file.Container, file.VideoCodec, file.AudioCodec, file.DurationSeconds, file.Width, file.Height, [], []);
        var subtitleTracks = subtitles.BuildTracks(file.Path, probe);

        var preferredLanguage = await settings.GetAsync(SettingsService.PlayerAudioLanguage, ct);
        var selectedAudio = AudioTrackSelector.Select(probe.AudioTracks, audio, preferredLanguage);
        var plan = StreamingPlanner.Plan(file.Extension, probe);
        if (selectedAudio?.Ordinal > 0 && plan.Mode == PlaybackMode.Direct)
            plan = plan with { Mode = PlaybackMode.Hls };

        var url = plan.Mode == PlaybackMode.Direct
            ? $"/api/stream/{id}/direct"
            : $"/api/stream/{id}/hls/index.m3u8?audio={selectedAudio?.Ordinal ?? 0}";

        return Results.Ok(new StreamInfoDto(
            id, plan.Mode == PlaybackMode.Direct ? "direct" : "hls", url,
            probe.DurationSeconds, probe.VideoCodec, probe.AudioCodec,
            probe.AudioTracks.Select(ToDto).ToList(),
            selectedAudio?.Ordinal,
            selectedAudio?.NormalizedLanguage,
            subtitleTracks.Select(t => ToDto(id, t)).ToList()));
    }

    private static async Task<IResult> GetDirect(int id, LibraryDbContext db, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || !File.Exists(file.Path)) return Results.NotFound();

        return Results.File(file.Path, ContentType(file.Extension), enableRangeProcessing: true);
    }

    private static async Task<IResult> GetPlaylist(
        int id, int? audio, LibraryDbContext db, FfprobeService ffprobe, TranscodeSessionManager sessions, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || !File.Exists(file.Path)) return Results.NotFound();

        await EnsureProbedAsync(db, ffprobe, file, ct);
        if (file.DurationSeconds is not > 0)
            return Results.Problem("Neznámá délka videa – nelze sestavit HLS playlist.");

        var probe = new MediaProbe(
            file.Container, file.VideoCodec, file.AudioCodec, file.DurationSeconds, file.Width, file.Height, [], []);
        var plan = StreamingPlanner.Plan(file.Extension, probe);

        // Založ relaci (ffmpeg se rozjede až na první žádost o segment) a vrať VOD playlist z délky.
        var session = sessions.GetOrCreate(id, file.Path, plan, file.DurationSeconds.Value);
        session.SetAudio(Math.Max(0, audio ?? 0));
        return Results.Text(BuildVodPlaylist(file.DurationSeconds.Value), "application/vnd.apple.mpegurl");
    }

    private static async Task<IResult> GetSegment(
        int id, string segment, TranscodeSessionManager sessions, CancellationToken ct)
    {
        var session = sessions.TryGet(id);
        if (session is null) return Results.NotFound();

        var name = Path.GetFileNameWithoutExtension(segment); // "seg00012"
        if (!name.StartsWith("seg", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(3), out var index))
            return Results.NotFound();

        var path = await session.GetSegmentAsync(index, ct);
        if (path is null) return Results.NotFound();

        return Results.File(path, "video/mp2t");
    }

    private static async Task<IResult> GetSubtitle(
        int id, string trackId, LibraryDbContext db, FfprobeService ffprobe, SubtitleService subtitles, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || !File.Exists(file.Path)) return Results.NotFound();

        var probe = await ffprobe.ProbeAsync(file.Path, ct)
                    ?? new MediaProbe(file.Container, file.VideoCodec, file.AudioCodec, file.DurationSeconds, file.Width, file.Height, [], []);
        var decodedTrackId = Uri.UnescapeDataString(trackId);
        var track = subtitles.BuildTracks(file.Path, probe)
            .FirstOrDefault(t => string.Equals(t.Id, decodedTrackId, StringComparison.Ordinal));
        if (track is null || !track.IsPlayable) return Results.NotFound();

        var vttPath = await subtitles.GetOrCreateVttAsync(file.Path, track, ct);
        return vttPath is not null && File.Exists(vttPath)
            ? Results.File(vttPath, "text/vtt")
            : Results.NotFound();
    }

    /// <summary>Sestaví statický VOD playlist z celkové délky – přehrávač zná celý čas a může seekovat.</summary>
    private static string BuildVodPlaylist(double durationSeconds)
    {
        const double seg = TranscodeSession.SegmentSeconds;
        var count = Math.Max(1, (int)Math.Ceiling(durationSeconds / seg));

        var targetDuration = (int)Math.Ceiling(seg) + 1;
        var sb = new System.Text.StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append($"#EXT-X-TARGETDURATION:{targetDuration}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-INDEPENDENT-SEGMENTS\n");

        for (var i = 0; i < count; i++)
        {
            var segDur = i < count - 1 ? seg : Math.Max(0.1, durationSeconds - (count - 1) * seg);
            sb.Append("#EXTINF:")
              .Append(segDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\n")
              .Append($"seg{i:D5}.ts\n");
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    private static async Task EnsureProbedAsync(
        LibraryDbContext db, FfprobeService ffprobe, MediaFile file, CancellationToken ct)
    {
        if (file.VideoCodec is not null || file.DurationSeconds is not null)
            return; // už máme

        var probe = await ffprobe.ProbeAsync(file.Path, ct);
        if (probe is null) return;

        file.Container = probe.Container;
        file.VideoCodec = probe.VideoCodec;
        file.AudioCodec = probe.AudioCodec;
        file.DurationSeconds = probe.DurationSeconds;
        file.Width = probe.Width;
        file.Height = probe.Height;
        await db.SaveChangesAsync(ct);
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream",
    };

    private static AudioTrackDto ToDto(AudioTrackInfo track) => new(
        track.Ordinal,
        track.StreamIndex,
        track.Codec,
        track.Language,
        track.NormalizedLanguage,
        track.Label,
        track.IsDefault);

    private static SubtitleTrackDto ToDto(int mediaFileId, SubtitleTrackInfo track) => new(
        track.Id,
        track.Source,
        track.Ordinal,
        track.StreamIndex,
        track.Codec,
        track.Language,
        track.NormalizedLanguage,
        track.Label,
        track.IsDefault,
        track.IsForced,
        track.IsPlayable,
        track.IsPlayable ? $"/api/stream/{mediaFileId}/subtitles/{Uri.EscapeDataString(track.Id)}.vtt" : null);
}
