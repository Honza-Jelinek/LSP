using System.Text.Json;

namespace LSP.Server.Library;

/// <summary>Sdílené parsování TMDB genres JSON (`[{"id":28,"name":"Akční"}]`) do zobrazitelného "Akční, Horor".</summary>
public static class GenreFormat
{
    public static string? ExtractNames(string? genresJson)
    {
        if (string.IsNullOrWhiteSpace(genresJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(genresJson);
            var names = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    names.Add(n.GetString()!);
            }
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pro data z DB, kde Genres může být buď už hotový text, nebo starý surový TMDB JSON.</summary>
    public static string? NormalizeStored(string? genres) =>
        genres is { Length: > 0 } && genres[0] == '[' ? ExtractNames(genres) : genres;
}
