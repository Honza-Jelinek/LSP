using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace LSP.Server.Media;

/// <summary>
/// Najde spustitelné ffmpeg/ffprobe. Pořadí: konfigurace → přibalené v tools/ffmpeg →
/// vedle exe (publish) → PATH. Volají se jako sidecar (externí proces).
/// </summary>
public sealed class FfmpegLocator
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public FfmpegLocator(IConfiguration config)
    {
        var configured = config["Ffmpeg:Directory"];
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx-x64"
            : "linux-x64";

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);

        // Publish: vedle exe (…/ffmpeg/<rid>) i přímo vedle exe.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", rid));
        candidates.Add(AppContext.BaseDirectory);

        // Dev: vyšplhej nahoru a najdi repo tools/ffmpeg/<rid>.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "tools", "ffmpeg", rid));
        }

        FfmpegPath = Resolve(candidates, "ffmpeg") ?? "ffmpeg";   // fallback na PATH
        FfprobePath = Resolve(candidates, "ffprobe") ?? "ffprobe";
    }

    private static string? Resolve(IEnumerable<string> dirs, string tool)
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? tool + ".exe" : tool;
        foreach (var dir in dirs)
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full))
                return full;
        }
        return null;
    }
}
