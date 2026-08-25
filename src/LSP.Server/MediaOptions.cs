namespace LSP.Server;

/// <summary>Konfigurace knihovny – kde na disku leží média.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Kořenová složka s filmy a seriály.</summary>
    public string Root { get; set; } = "";
}
