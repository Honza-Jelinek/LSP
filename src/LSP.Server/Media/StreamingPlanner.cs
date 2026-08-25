namespace LSP.Server.Media;

public enum PlaybackMode
{
    /// <summary>Soubor je rovnou přehratelný ve webview – servíruje se přímo s podporou range (plný seek).</summary>
    Direct,

    /// <summary>Potřeba remux/transkód – doručeno jako HLS.</summary>
    Hls,
}

/// <summary>Plán přehrávání: jak doručit + co (ne)transkódovat.</summary>
public sealed record PlaybackPlan(PlaybackMode Mode, bool CopyVideo, bool CopyAudio)
{
    public bool NeedsVideoTranscode => Mode == PlaybackMode.Hls && !CopyVideo;
    public bool NeedsAudioTranscode => Mode == PlaybackMode.Hls && !CopyAudio;
}

/// <summary>
/// Z výsledku sondy a přípony rozhodne, jak soubor přehrát. Cíl kompatibility: H.264 + AAC.
/// </summary>
public static class StreamingPlanner
{
    // Kodeky, které webview (Chromium/WebView2) zvládne.
    private static readonly HashSet<string> BrowserVideo = new(StringComparer.OrdinalIgnoreCase)
        { "h264", "avc1", "vp8", "vp9", "av1" };
    private static readonly HashSet<string> BrowserAudio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "mp3", "opus", "vorbis" };

    // Kontejnery, které lze servírovat přímo.
    private static readonly HashSet<string> DirectContainers = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".webm" };

    // Pro HLS copy stačí, aby byl kodek browser-friendly (mění se jen kontejner).
    private static readonly HashSet<string> HlsCopyableVideo = new(StringComparer.OrdinalIgnoreCase)
        { "h264", "avc1" };
    private static readonly HashSet<string> HlsCopyableAudio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "mp3" };

    public static PlaybackPlan Plan(string extension, MediaProbe? probe)
    {
        // Bez sondy se nepodaří rozhodnout bezpečně → transkóduj vše (nejjistější).
        if (probe is null)
            return new PlaybackPlan(PlaybackMode.Hls, CopyVideo: false, CopyAudio: false);

        var video = probe.VideoCodec ?? "";
        var audio = probe.AudioCodec ?? "";

        var directOk = DirectContainers.Contains(extension)
                       && BrowserVideo.Contains(video)
                       && (audio.Length == 0 || BrowserAudio.Contains(audio));

        if (directOk)
            return new PlaybackPlan(PlaybackMode.Direct, CopyVideo: true, CopyAudio: true);

        return new PlaybackPlan(
            PlaybackMode.Hls,
            CopyVideo: HlsCopyableVideo.Contains(video),
            CopyAudio: audio.Length == 0 || HlsCopyableAudio.Contains(audio));
    }
}
