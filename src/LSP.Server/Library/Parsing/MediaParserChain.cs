namespace LSP.Server.Library.Parsing;

/// <summary>
/// Řetězec parserů (chain of responsibility). Spouští je v pořadí dle <see cref="IMediaParser.Order"/>;
/// vrací výsledek prvního, který souboru rozumí. Nové parsery (LLM, vlastní) stačí zaregistrovat do DI.
/// </summary>
public sealed class MediaParserChain
{
    private readonly IReadOnlyList<IMediaParser> _parsers;

    public MediaParserChain(IEnumerable<IMediaParser> parsers)
    {
        _parsers = parsers.OrderBy(p => p.Order).ToArray();
    }

    public MediaParseResult Parse(MediaParseContext context)
    {
        foreach (var parser in _parsers)
        {
            if (parser.TryParse(context) is { } result)
                return result;
        }

        // Pojistka – v praxi MovieParser vždy něco vrátí.
        return new MediaParseResult(MediaKind.Unknown, context.FileNameWithoutExtension);
    }
}
