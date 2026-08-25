namespace LSP.Server.Library.Parsing.Parsers;

/// <summary>
/// Fallback parser: cokoliv, co nebylo rozpoznáno jako epizoda, považuje za film.
/// Běží jako poslední, takže vždy vrací výsledek (řetězec nikdy neskončí bez určení).
/// </summary>
public sealed class MovieParser : IMediaParser
{
    public int Order => int.MaxValue;

    public MediaParseResult TryParse(MediaParseContext context)
    {
        var name = context.FileNameWithoutExtension;
        string? imdbId;
        (name, imdbId) = NameCleaner.ExtractImdbId(name);

        var year = NameCleaner.ExtractYear(name)
                   ?? (context.ContentFolderName is { } f ? NameCleaner.ExtractYear(f) : null);

        var title = NameCleaner.CleanTitle(name);
        if (string.IsNullOrWhiteSpace(title) && context.ContentFolderName is { } folder)
            title = NameCleaner.CleanTitle(folder);
        if (string.IsNullOrWhiteSpace(title))
            title = name;

        double confidence = year is not null ? 0.75 : 0.50;
        if (context.ContentFolderName is not null && year is null)
            confidence = 0.40;
        if (title == name)
            confidence = 0.30;

        return new MediaParseResult(
            MediaKind.Movie, title, Year: year, ImdbId: imdbId,
            Confidence: confidence, Source: "regex-movie");
    }

    // Explicitní implementace rozhraní – fallback nikdy nevrací null.
    MediaParseResult? IMediaParser.TryParse(MediaParseContext context) => TryParse(context);
}
