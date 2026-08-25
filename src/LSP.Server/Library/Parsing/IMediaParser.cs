namespace LSP.Server.Library.Parsing;

/// <summary>
/// Jeden krok v řetězci rozpoznávání. Vrátí výsledek, pokud souboru rozumí, jinak null
/// (řetězec zkusí další parser). Sem půjde v budoucnu přidat LLM parser i vlastní parsery.
/// </summary>
public interface IMediaParser
{
    /// <summary>Pořadí v řetězci – nižší číslo = dřív. Specifické parsery mají běžet před fallbackem.</summary>
    int Order { get; }

    MediaParseResult? TryParse(MediaParseContext context);
}
