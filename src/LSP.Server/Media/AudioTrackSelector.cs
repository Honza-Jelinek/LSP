namespace LSP.Server.Media;

public static class AudioTrackSelector
{
    public static AudioTrackInfo? Select(
        IReadOnlyList<AudioTrackInfo> tracks, int? requestedOrdinal, string? preferredLanguage)
    {
        if (tracks.Count == 0)
            return null;

        if (requestedOrdinal is { } ordinal)
        {
            var requested = tracks.FirstOrDefault(t => t.Ordinal == ordinal);
            if (requested is not null)
                return requested;
        }

        var normalizedPreference = MediaLanguageNormalizer.Normalize(preferredLanguage);
        if (!string.IsNullOrWhiteSpace(normalizedPreference))
        {
            var languageMatches = tracks
                .Where(t => string.Equals(t.NormalizedLanguage, normalizedPreference, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (languageMatches.Count > 0)
                return languageMatches.FirstOrDefault(t => t.IsDefault) ?? languageMatches[0];
        }

        return tracks[0];
    }
}
