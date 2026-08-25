using LSP.Server.Api;
using LSP.Server.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LSP.Server;

/// <summary>
/// Sestaví a nakonfiguruje in-process ASP.NET Core (Kestrel) server,
/// který servíruje built React SPA a LSP API. Spouští ho host LSP.App.
/// </summary>
public static class LspServer
{
    /// <summary>
    /// Vytvoří nakonfigurovanou <see cref="WebApplication"/>. Server se nespouští –
    /// volající ho nastartuje (StartAsync) a předá Photino oknu URL.
    /// </summary>
    /// <param name="webRootPath">Absolutní cesta ke složce s build outputem React SPA.</param>
    /// <param name="listenUrl">URL, na kterém má Kestrel poslouchat (např. http://127.0.0.1:0 pro dynamický port).</param>
    public static WebApplication Create(string webRootPath, string listenUrl)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = webRootPath,
        });

        builder.WebHost.UseUrls(listenUrl);
        // Ztiš framework spam (EF DbCommand, HttpClient handler) → v logu zůstanou jen LSP hlášky.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddProvider(new FileLoggerProvider());
        builder.Services.AddLspServices(builder.Configuration);

        var app = builder.Build();

        // Build stamp na první řádek logu — okamžitě odhalí běžící starý build (klasika:
        // běžící instance drží zamčený exe → build selže/přeskočí se → spustí se starý výstup).
        var assemblyPath = typeof(LspServer).Assembly.Location;
        var buildStamp = File.GetLastWriteTime(assemblyPath);
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LSP.Server")
            .LogInformation("LSP.Server start — build z {BuildStamp:dd.MM.yyyy HH:mm:ss} ({Path})",
                buildStamp, assemblyPath);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.Database.Migrate();
            PortablePathRewriter.InitializePortableDatabase(db);
        }

        // Každý API request obnoví "poslední aktivitu" — drží server naživu, dokud je někdo
        // připojený (i jen pollingem statusu nebo streamem videa), po zavření okna vlastníka.
        var activity = app.Services.GetRequiredService<ClientActivityTracker>();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
                activity.Touch();
            await next();
        });

        app.UseDefaultFiles();
        // Hashované assety (index-XXXX.js/css) se můžou cachovat; .html NIKDY —
        // jinak WebView2 drží starý index.html ukazující na staré assety (peklo při vývoji).
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            },
        });

        app.MapGet("/api/ping", () => Results.Ok(new
        {
            service = "LSP.Server",
            status = "ok",
            time = DateTimeOffset.Now,
            build = buildStamp,
        }));

        app.MapLibraryEndpoints();
        app.MapStreamEndpoints();
        app.MapProgressEndpoints();
        app.MapSettingsEndpoints();
        app.MapEnrichmentEndpoints();
        app.MapExportEndpoints();

        // SPA fallback → index.html VŽDY bez cache (aby WebView2 nedržel starou verzi).
        app.MapFallback(async ctx =>
        {
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.ContentType = "text/html";
            await ctx.Response.SendFileAsync(Path.Combine(webRootPath, "index.html"));
        });

        return app;
    }
}
