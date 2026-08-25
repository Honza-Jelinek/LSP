namespace LSP.Server;

/// <summary>Centralizuje zapisovatelne cesty aplikace a rozlisuje instalovany a portable rezim.</summary>
public static class AppPaths
{
    private static readonly string ExecutableDir = Path.GetFullPath(AppContext.BaseDirectory);

    public static bool IsPortable { get; } = File.Exists(Path.Combine(ExecutableDir, "portable.txt"));

    /// <summary>
    /// Koren baliku. V portable rezimu je aplikace v podslozce app, proto je korenem jeji rodic.
    /// </summary>
    public static string AppRoot { get; } = IsPortable
        ? Directory.GetParent(ExecutableDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName
            ?? ExecutableDir
        : ExecutableDir;

    public static string DataDir { get; } = IsPortable
        ? Path.Combine(AppRoot, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSP");

    public static string PosterDir => SubDir("posters");

    public static string SubDir(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Path.Combine(DataDir, name);
    }
}
