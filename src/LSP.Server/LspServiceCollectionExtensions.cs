using LSP.Server.Data;
using LSP.Server.External;
using LSP.Server.Library;
using LSP.Server.Library.Parsing;
using LSP.Server.Library.Parsing.Parsers;
using LSP.Server.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LSP.Server;

public static class LspServiceCollectionExtensions
{
    /// <summary>Zaregistruje DB, parsery a služby knihovny.</summary>
    public static IServiceCollection AddLspServices(this IServiceCollection services, IConfiguration config)
    {
        // Kořen knihovny: konfigurace → env → default.
        services.Configure<MediaOptions>(opts =>
        {
            opts.Root = config[$"{MediaOptions.SectionName}:Root"]
                        ?? Environment.GetEnvironmentVariable("LSP_MEDIA_ROOT")
                        ?? @"E:\_Filmy";
        });

        // SQLite v AppPaths.DataDir: LOCALAPPDATA pro instalaci, data/ vedle app v portable baliku.
        Directory.CreateDirectory(AppPaths.DataDir);
        var dbPath = Path.Combine(AppPaths.DataDir, "library.db");

        services.AddDbContext<LibraryDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<SettingsService>();

        // Parsery (stateless → singleton). Nový parser = jeden řádek navíc.
        services.AddSingleton<IMediaParser, SeasonEpisodeParser>();
        services.AddSingleton<IMediaParser, ThreeDigitEpisodeParser>();
        services.AddSingleton<IMediaParser, MovieParser>();
        services.AddSingleton<MediaParserChain>();

        services.AddScoped<LibraryScanner>();
        services.AddScoped<SeasonEpisodeCache>();
        services.AddScoped<EnrichmentService>();
        services.AddScoped<ManualMatchService>();
        services.AddScoped<ExportService>();

        // Enrichment jako background job (singleton) + sledování aktivity klientů pro lingering shutdown.
        services.AddSingleton<IEnrichmentRunner, ScopedEnrichmentRunner>();
        services.AddSingleton<EnrichmentJobService>();
        services.AddSingleton<LibraryOperationCoordinator>();
        services.AddSingleton<ExportJobService>();
        services.AddSingleton<ClientActivityTracker>();

        // External (free/subscription seam).
        services.AddScoped<ICredentialProvider, SettingsCredentialProvider>();
        services.AddHttpClient<IMetadataProvider, LocalTmdbMetadataProvider>();
        services.AddHttpClient<ILlmClient, OpenRouterLlmClient>();

        // Media / streaming.
        services.AddSingleton<FfmpegLocator>();
        services.AddScoped<FfprobeService>();
        services.AddScoped<SubtitleService>();
        services.AddSingleton<TranscodeSessionManager>();

        return services;
    }
}
