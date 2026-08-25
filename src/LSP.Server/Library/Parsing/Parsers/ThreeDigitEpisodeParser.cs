namespace LSP.Server.Library.Parsing.Parsers;

/// <summary>
/// Rozpozná epizody kódované 3místně jako <c>Breaking Bad 101</c> (1 = sezóna, 01 = díl).
/// Aplikuje se jen na soubory uvnitř složky seriálu a na samostatný (mezerami ohraničený)
/// token, aby nechytal kodeky (x265) ani rozlišení.
/// </summary>
public sealed class ThreeDigitEpisodeParser : IMediaParser
{
    public int Order => 20;

    public MediaParseResult? TryParse(MediaParseContext context)
    {
        if (context.Depth < 1)
            return null;

        var name = context.FileNameWithoutExtension;
        string? imdbId;
        (name, imdbId) = NameCleaner.ExtractImdbId(name);
        var match = EpisodePatterns.ThreeDigit().Match(name);
        if (!match.Success)
            return null;

        var season = int.Parse(match.Groups["s"].Value);
        var episode = int.Parse(match.Groups["e"].Value);

        var folder = context.ContentFolderName ?? context.TopFolderName ?? "";
        var showTitle = NameCleaner.StripTrailingSeasonMarker(NameCleaner.CleanTitle(folder), season);
        if (string.IsNullOrWhiteSpace(showTitle))
            showTitle = NameCleaner.CleanTitle(name[..match.Index]);
        if (string.IsNullOrWhiteSpace(showTitle))
            showTitle = folder;

        var after = name[(match.Index + match.Length)..];
        var episodeTitle = NameCleaner.CleanEpisodeTitle(after);

        return new MediaParseResult(
            MediaKind.Episode,
            showTitle,
            Season: season,
            Episode: episode,
            EpisodeTitle: string.IsNullOrWhiteSpace(episodeTitle) ? null : episodeTitle,
            ImdbId: imdbId,
            Confidence: 0.85,
            Source: "regex-3digit");
    }
}
