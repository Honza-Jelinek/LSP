namespace LSP.Server.Library.Parsing.Parsers;

/// <summary>
/// Rozpozná epizody ve formátu <c>S01E02</c> i <c>01x02</c> (nejběžnější).
/// Název seriálu bere přednostně z názvu top-level složky.
/// </summary>
public sealed class SeasonEpisodeParser : IMediaParser
{
    public int Order => 10;

    public MediaParseResult? TryParse(MediaParseContext context)
    {
        var name = context.FileNameWithoutExtension;

        // Extrahuj IMDB ID z názvu (tt1234567) a vyčisti
        string? imdbId;
        (name, imdbId) = NameCleaner.ExtractImdbId(name);

        var token = EpisodePatterns.MatchEpisodeToken(name);
        if (token is null)
            return null;

        var (season, episode, index, length) = token.Value;

        // Název seriálu: nejspolehlivěji z kontextové složky (s odseknutým sezónním ocasem);
        // pokud selže (prázdný výsledek nebo bez složky), z části názvu souboru před tokenem.
        var folderTitle = context.ContentFolderName is { } folder
            ? NameCleaner.StripTrailingSeasonMarker(NameCleaner.CleanTitle(folder), season)
            : null;

        var showTitle = !string.IsNullOrWhiteSpace(folderTitle)
            ? folderTitle
            : NameCleaner.CleanTitle(name[..index]);

        if (string.IsNullOrWhiteSpace(showTitle))
            showTitle = context.ContentFolderName ?? context.TopFolderName ?? name;

        // Název epizody: text za tokenem (pokud nějaký je).
        var after = name[(index + length)..];
        var episodeTitle = NameCleaner.CleanEpisodeTitle(after);

        var hasFolder = context.ContentFolderName is not null;
        var confidence = hasFolder ? 0.95 : 0.85;
        var source = "regex-sxxexx";

        return new MediaParseResult(
            MediaKind.Episode,
            showTitle,
            Season: season,
            Episode: episode,
            EpisodeTitle: string.IsNullOrWhiteSpace(episodeTitle) ? null : episodeTitle,
            ImdbId: imdbId,
            Confidence: confidence,
            Source: source);
    }
}
