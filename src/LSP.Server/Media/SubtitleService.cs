using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Media;

public sealed class SubtitleService(FfmpegLocator locator, ILogger<SubtitleService> log)
{
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".vtt" };

    private readonly string _cacheRoot = AppPaths.SubDir("subtitles");

    public IReadOnlyList<SubtitleTrackInfo> BuildTracks(string videoPath, MediaProbe probe)
    {
        var tracks = new List<SubtitleTrackInfo>(probe.SubtitleTracks);
        tracks.AddRange(FindSidecarTracks(videoPath));
        return tracks;
    }

    public async Task<string?> GetOrCreateVttAsync(string videoPath, SubtitleTrackInfo track, CancellationToken ct)
    {
        if (!track.IsPlayable)
            return null;

        if (track.Source == "sidecar")
            return await GetOrCreateSidecarVttAsync(videoPath, track, ct);

        if (track.Source == "internal" && track.Ordinal is { } ordinal)
            return await GetOrCreateInternalVttAsync(videoPath, track, ordinal, ct);

        return null;
    }

    private static IReadOnlyList<SubtitleTrackInfo> FindSidecarTracks(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var files = Directory.EnumerateFiles(directory)
            .Where(path =>
                SidecarExtensions.Contains(Path.GetExtension(path))
                && Path.GetFileNameWithoutExtension(path).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<SubtitleTrackInfo>();
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var suffix = Path.GetFileNameWithoutExtension(path)[baseName.Length..]
                .Trim(' ', '.', '-', '_');
            var language = ExtractLanguageFromSuffix(suffix);
            var normalized = MediaLanguageNormalizer.Normalize(language ?? suffix);
            var isForced = suffix.Contains("forced", StringComparison.OrdinalIgnoreCase);
            var label = FfprobeService.BuildSubtitleLabel(
                i, "Externí", language, string.IsNullOrWhiteSpace(suffix) ? null : suffix, extension, false, isForced);

            result.Add(new SubtitleTrackInfo(
                $"sidecar-{i}",
                "sidecar",
                i,
                null,
                extension,
                language,
                normalized,
                label,
                false,
                isForced,
                true,
                path));
        }

        return result;
    }

    private async Task<string?> GetOrCreateSidecarVttAsync(
        string videoPath, SubtitleTrackInfo track, CancellationToken ct)
    {
        if (track.FilePath is null || !File.Exists(track.FilePath))
            return null;

        if (Path.GetExtension(track.FilePath).Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            return track.FilePath;

        var output = CachePath(videoPath, track);
        if (File.Exists(output))
            return output;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var srt = await File.ReadAllTextAsync(track.FilePath, ct);
        await File.WriteAllTextAsync(output, ConvertSrtToWebVtt(srt), Encoding.UTF8, ct);
        return output;
    }

    private async Task<string?> GetOrCreateInternalVttAsync(
        string videoPath, SubtitleTrackInfo track, int ordinal, CancellationToken ct)
    {
        var output = CachePath(videoPath, track);
        if (File.Exists(output))
            return output;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var psi = new ProcessStartInfo
        {
            FileName = locator.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"0:s:{ordinal}");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("webvtt");
        psi.ArgumentList.Add(output);

        try
        {
            using var proc = Process.Start(psi)!;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode == 0 && File.Exists(output))
                return output;

            log.LogWarning("Extrakce titulků {Track} selhala: {Err}", track.Id, stderr);
            TryDelete(output);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Extrakce titulků {Track} selhala", track.Id);
            TryDelete(output);
            return null;
        }
    }

    public static string ConvertSrtToWebVtt(string srt)
    {
        var normalized = srt.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var sb = new StringBuilder("WEBVTT\n\n");

        foreach (var line in lines)
        {
            if (line.Contains(" --> ", StringComparison.Ordinal))
                sb.AppendLine(line.Replace(',', '.'));
            else
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private string CachePath(string videoPath, SubtitleTrackInfo track)
    {
        var hashInput = $"{videoPath}|{File.GetLastWriteTimeUtc(videoPath).Ticks}|{track.Id}|{track.FilePath}|{(track.FilePath is null ? 0 : File.GetLastWriteTimeUtc(track.FilePath).Ticks)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..24];
        return Path.Combine(_cacheRoot, hash, $"{track.Id}.vtt");
    }

    private static string? ExtractLanguageFromSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return null;

        return suffix.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => MediaLanguageNormalizer.Normalize(token) is not null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup of failed extraction output.
        }
    }
}
