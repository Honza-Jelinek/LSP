using LSP.Server.Data;
using LSP.Server.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Api;

public sealed record StreamInfoDto(
    int MediaFileId,
    Guid? SessionId,
    string Mode,            // "direct" | "hls"
    string Url,             // co má frontend načíst
    double? DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    IReadOnlyList<AudioTrackDto> AudioTracks,
    int? SelectedAudioOrdinal,
    string? SelectedAudioLanguage,
    IReadOnlyList<SubtitleTrackDto> SubtitleTracks);

public sealed record StreamHeartbeatRequest(bool Paused, double PositionSeconds);

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
        group.MapPost("/{id:int}/heartbeat", Heartbeat);
    }

    /// <summary>Zahodí transkód relaci a smaže segmenty daného souboru (volá se při přepnutí na jinou epizodu).</summary>
    private static IResult PurgeSegments(int id, Guid? session, TranscodeSessionManager sessions)
    {
        if (session is { } token) sessions.Release(id, token);
        else sessions.PurgeSegments(id);
        return Results.NoContent();
    }

    private static IResult Heartbeat(int id, Guid? session, StreamHeartbeatRequest request, TranscodeSessionManager sessions)
    {
        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0)
            return Results.BadRequest();
        return sessions.Touch(id, session, request.Paused) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetInfo(
        int id, int? audio, Guid? session, LibraryDbContext db, FfprobeService ffprobe, SettingsService settings, SubtitleService subtitles, CancellationToken ct)
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
        var plan = StreamingPlanner.Plan(file.Extension, ForSelectedAudio(probe, selectedAudio));
        if (selectedAudio?.Ordinal > 0 && plan.Mode == PlaybackMode.Direct)
            plan = plan with { Mode = PlaybackMode.Hls };

        var url = plan.Mode == PlaybackMode.Direct
            ? $"/api/stream/{id}/direct"
            : $"/api/stream/{id}/hls/index.m3u8?audio={selectedAudio?.Ordinal ?? 0}{SessionQuery(session)}";

        return Results.Ok(new StreamInfoDto(
            id, session, plan.Mode == PlaybackMode.Direct ? "direct" : "hls", url,
            probe.DurationSeconds, probe.VideoCodec, selectedAudio?.Codec ?? probe.AudioCodec,
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
        int id, int? audio, Guid? session, LibraryDbContext db, FfprobeService ffprobe, TranscodeSessionManager sessions, CancellationToken ct)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || !File.Exists(file.Path)) return Results.NotFound();

        await EnsureProbedAsync(db, ffprobe, file, ct);
        var liveProbe = await ffprobe.ProbeAsync(file.Path, ct);
        var probe = liveProbe ?? new MediaProbe(
            file.Container, file.VideoCodec, file.AudioCodec, file.DurationSeconds, file.Width, file.Height, [], []);
        var duration = probe.DurationSeconds ?? file.DurationSeconds;
        if (duration is not > 0)
            return Results.Problem("Neznámá délka videa – nelze sestavit HLS playlist.");
        var selectedAudio = probe.AudioTracks.FirstOrDefault(t => t.Ordinal == (audio ?? 0));
        if (audio is < 0 || (audio is > 0 && selectedAudio is null)
            || (audio is not null && probe.AudioTracks.Count > 0 && selectedAudio is null))
            return Results.BadRequest("Neplatná zvuková stopa.");
        var plan = StreamingPlanner.Plan(file.Extension, ForSelectedAudio(probe, selectedAudio));

        // Založ relaci (ffmpeg se rozjede až na první žádost o segment) a vrať VOD playlist z délky.
        var transcode = sessions.GetOrCreate(id, file.Path, plan, duration.Value, session, probe);
        if (transcode is null) return Results.StatusCode(StatusCodes.Status410Gone);
        transcode.SetAudio(audio ?? 0);
        var playlist = await transcode.GetPlaylistAsync(ct);
        // Playlist segment URIs carry the same playback generation as the index URL.
        if (session is { } token)
            playlist = System.Text.RegularExpressions.Regex.Replace(playlist, @"(?m)^(seg\d+\.ts)$", $"$1?session={token:D}");
        return Results.Text(playlist, "application/vnd.apple.mpegurl");
    }

    private static async Task<IResult> GetSegment(
        int id, string segment, Guid? session, TranscodeSessionManager sessions, CancellationToken ct)
    {
        var transcode = sessions.TryGet(id, session);
        if (transcode is null) return Results.NotFound();

        var name = Path.GetFileNameWithoutExtension(segment); // "seg00012"
        if (!name.StartsWith("seg", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(3), out var index))
            return Results.NotFound();

        var path = await transcode.GetSegmentAsync(index, ct);
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

    private static MediaProbe ForSelectedAudio(MediaProbe probe, AudioTrackInfo? selected) =>
        selected is null ? probe : probe with { AudioCodec = selected.Codec };

    private static string SessionQuery(Guid? session) =>
        session is { } token ? $"&session={token:D}" : "";

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
