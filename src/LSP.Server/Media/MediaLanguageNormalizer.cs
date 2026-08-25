using System.Globalization;
using System.Text;

namespace LSP.Server.Media;

public static class MediaLanguageNormalizer
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "cs",
        ["cz"] = "cs",
        ["ces"] = "cs",
        ["cze"] = "cs",
        ["cesky"] = "cs",
        ["cestina"] = "cs",
        ["cech"] = "cs",

        ["sk"] = "sk",
        ["slo"] = "sk",
        ["slk"] = "sk",
        ["slovak"] = "sk",
        ["slovensky"] = "sk",
        ["slovencina"] = "sk",

        ["en"] = "en",
        ["eng"] = "en",
        ["english"] = "en",
        ["anglicky"] = "en",

        ["de"] = "de",
        ["deu"] = "de",
        ["ger"] = "de",
        ["german"] = "de",
        ["nemecky"] = "de",
        ["nemcina"] = "de",
    };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = RemoveDiacritics(value).Trim().ToLowerInvariant();
        var tokens = normalized
            .Split([' ', '-', '_', '.', ',', ';', ':', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens.Prepend(normalized))
        {
            if (Aliases.TryGetValue(token, out var mapped))
                return mapped;

            try
            {
                var culture = CultureInfo.GetCultureInfo(token);
                if (!string.IsNullOrWhiteSpace(culture.TwoLetterISOLanguageName))
                    return culture.TwoLetterISOLanguageName;
            }
            catch (CultureNotFoundException)
            {
                // Not a culture code; keep checking aliases/tokens.
            }
        }

        return normalized.Length is >= 2 and <= 3 ? normalized[..2] : null;
    }

    private static string RemoveDiacritics(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
