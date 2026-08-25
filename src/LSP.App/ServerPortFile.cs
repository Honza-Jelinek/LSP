using System.Text.Json;

namespace LSP.App;

/// <summary>
/// Discovery soubor pro znovupřipojení k běžícímu serveru na pozadí (%LOCALAPPDATA%\LSP\server.json).
/// Vlastnictví serveru samo o sobě řeší named Mutex v Program.cs — tenhle soubor jen říká,
/// na kterém portu poslouchá, aby se nová instance nemusela dohadovat.
/// </summary>
internal static class ServerPortFile
{
    // ponytail: server.json zustava machine-local i v portable rezimu. Soubezne spustena
    // portable a instalovana instance proto sdileji discovery port a druha se pripoji k prvni.
    private sealed record ServerInfo(int Port, int Pid, DateTimeOffset StartedAt);

    public static string PathToFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSP", "server.json");

    public static void Write(int port)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathToFile)!);
            var info = new ServerInfo(port, Environment.ProcessId, DateTimeOffset.UtcNow);
            File.WriteAllText(PathToFile, JsonSerializer.Serialize(info));
        }
        catch { /* best effort — v nejhorším případě se příští spuštění chová jako první instance */ }
    }

    public static int? TryReadPort()
    {
        try
        {
            if (!File.Exists(PathToFile)) return null;
            var info = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(PathToFile));
            return info is { Port: > 0 } ? info.Port : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Smaže soubor jen pokud patří tomuto procesu (chrání před smazáním souboru jiné, právě startující instance).</summary>
    public static void DeleteIfOwn()
    {
        try
        {
            if (!File.Exists(PathToFile)) return;
            var info = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(PathToFile));
            if (info?.Pid == Environment.ProcessId)
                File.Delete(PathToFile);
        }
        catch { /* best effort */ }
    }

    /// <summary>Nepodmíněné smazání — volá se, když tento proces právě získal vlastnictví (mutex) a starý soubor je tedy jistě zbytek po mrtvém procesu.</summary>
    public static void DeleteStale()
    {
        try { File.Delete(PathToFile); }
        catch { /* best effort */ }
    }
}
