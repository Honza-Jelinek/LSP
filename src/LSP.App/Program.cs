using System.Net.Http;
using System.Threading;
using LSP.Server;
using LSP.Server.Library;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace LSP.App;

internal static class Program
{
    /// <summary>Jak dlouho po zavření okna vlastník čeká na ticho (žádný /api/* request), než ukončí proces.</summary>
    private static readonly TimeSpan QuietShutdownWindow = TimeSpan.FromSeconds(30);

    [STAThread]
    private static void Main(string[] args)
    {
        // Chromium ve WebView2 renderuje zvuk v odděleném utility procesu, takže WASAPI session
        // patří msedgewebview2.exe, ne nám. Discord (Go Live) hákuje audio per-proces podle PID
        // sdílené aplikace → obraz jede, zvuk je ticho. Tímhle audio zůstane in-process.
        // ponytail: env var místo Photino API – WebView2 ji čte sám; kdyby to přestalo stačit,
        // upgradovat na CoreWebView2EnvironmentOptions.AdditionalBrowserArguments.
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-features=AudioServiceOutOfProcess");

        // wwwroot leží vedle exe (zkopírováno z build outputu); v dev běhu je v project dir.
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        // Řízení přes CLI args (spolehlivě přes `dotnet run -- ...`), env jen jako fallback.
        var serverOnly = args.Contains("--server-only")
                         || Environment.GetEnvironmentVariable("LSP_SERVER_ONLY") == "1";
        var listenUrl = GetArgValue(args, "--listen")
                        ?? Environment.GetEnvironmentVariable("LSP_LISTEN_URL")
                        ?? "http://127.0.0.1:0";

        // Server-only režim (smoke test / dev): žádný owner/attach tanec, přesně jako dřív.
        if (serverOnly)
        {
            var devServer = LspServer.Create(webRoot, listenUrl);
            devServer.Start();
            Console.WriteLine($"LSP server běží na {ResolveBaseUrl(devServer)}");
            devServer.WaitForShutdown();
            return;
        }

        // Jediný "vlastník" serveru napříč instancemi aplikace — arbitráž přes named mutex.
        // Poražený se buď připojí k běžícímu serveru (attach), nebo (výjimečně) běží degradovaně.
        using var ownerMutex = new Mutex(initiallyOwned: false, @"Local\LSP.ServerOwner");
        var isOwner = ownerMutex.WaitOne(TimeSpan.Zero);

        if (!isOwner)
        {
            var attachedUrl = TryAttachToRunningServer();
            if (attachedUrl is not null)
            {
                CreateWindow(attachedUrl).WaitForClose();
                return; // serveru se vůbec nedotýkáme — patří jiné instanci
            }
            // Vlastník neodpovídá (právě startuje, nebo spadl bez úklidu) → běž degradovaně:
            // vlastní server, ale bez port file (nechceme přepsat soubor skutečného vlastníka).
        }

        var server = LspServer.Create(webRoot, listenUrl);
        server.Start();
        var baseUrl = ResolveBaseUrl(server);

        if (isOwner)
        {
            // Soubor tu může ležet po procesu, co spadl bez úklidu — mutex je teď náš, takže je jistě zastaralý.
            ServerPortFile.DeleteStale();
            ServerPortFile.Write(new Uri(baseUrl).Port);
        }

        try
        {
            CreateWindow(baseUrl).WaitForClose();

            if (isOwner)
                LingerIfEnrichmentRunning(server);
        }
        finally
        {
            if (isOwner) ServerPortFile.DeleteIfOwn();

            server.StopAsync().GetAwaiter().GetResult();
            // DisposeAsync uvolní DI kontejner → TranscodeSessionManager.Dispose() zabije ffmpeg procesy.
            (server as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();

            if (isOwner) ownerMutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Po zavření okna: pokud běží enrichment, proces zůstane na pozadí, dokud job neskončí A
    /// zároveň nikdo (reattached okno, streamující klient) posledních 30 s nevolal žádné API —
    /// obě podmínky společně kryjí "job doběhl, nikdo se nevrátil" i "job doběhl, uživatel je zpátky".
    /// </summary>
    private static void LingerIfEnrichmentRunning(IHost server)
    {
        var jobs = server.Services.GetRequiredService<EnrichmentJobService>();
        if (!jobs.IsRunning) return;

        var activity = server.Services.GetRequiredService<ClientActivityTracker>();
        var log = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LSP.App");
        log.LogInformation("Okno zavřeno, enrichment běží — proces zůstává na pozadí.");

        while (true)
        {
            jobs.WaitForCompletionAsync().GetAwaiter().GetResult();

            while (activity.ActiveWithin(QuietShutdownWindow))
            {
                if (jobs.IsRunning) break; // reattached klient mezitím spustil nový job
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }

            if (!jobs.IsRunning) break; // ticho i nečinnost → konec
        }

        log.LogInformation("Enrichment dokončen a klienti odpojeni — ukončuji proces.");
    }

    /// <summary>Zkusí najít a napingovat server z předchozí instance. Vrátí jeho baseUrl, nebo null.</summary>
    private static string? TryAttachToRunningServer()
    {
        var port = ServerPortFile.TryReadPort();
        if (port is null) return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var resp = http.GetAsync($"http://127.0.0.1:{port}/api/ping").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                    return $"http://localhost:{port}";
            }
            catch
            {
                // Server ještě nenaběhl (owner právě startuje) nebo už neběží — zkus znovu / vzdej se.
            }

            Thread.Sleep(500);
        }

        return null;
    }

    private static PhotinoWindow CreateWindow(string baseUrl)
    {
        var isFullScreen = false;

        return new PhotinoWindow()
            .SetTitle("Local Stream Player")
            .SetUseOsDefaultSize(false)
            .SetSize(1280, 800)
            .Center()
            .SetResizable(true)
            // Web Fullscreen API (requestFullscreen) nemá ve WebView2 vliv na nativní okno –
            // přehrávač proto fullscreen vyžaduje zprávou (window.external.sendMessage) a my
            // přepneme reálné okno a potvrdíme stav zpět do JS. Photino SetFullScreen/SetMaximized
            // má mouchy (DPI, maximalizovaná okna, restore) — děláme to přímo přes Win32.
            .RegisterWebMessageReceivedHandler((sender, message) =>
            {
                if (message is not ("fullscreen:toggle" or "fullscreen:on" or "fullscreen:off"))
                    return;
                var win = (PhotinoWindow)sender;
                var target = message switch
                {
                    "fullscreen:on" => true,
                    "fullscreen:off" => false,
                    _ => !isFullScreen,
                };
                if (target != isFullScreen)
                {
                    isFullScreen = target;
                    if (isFullScreen) NativeFullscreen.Enter(win.WindowHandle);
                    else NativeFullscreen.Exit(win.WindowHandle);
                }
                // Vždy potvrď stav zpět do JS (idempotentní protokol).
                win.SendWebMessage(isFullScreen ? "fullscreen:on" : "fullscreen:off");
            })
            .Load(new Uri(baseUrl));
    }

    /// <summary>Vrátí hodnotu argumentu ve tvaru "--klic hodnota" nebo "--klic=hodnota".</summary>
    private static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == key && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal))
                return args[i][(key.Length + 1)..];
        }
        return null;
    }

    /// <summary>Zjistí skutečnou adresu, na kterou Kestrel po startu nabindoval.</summary>
    private static string ResolveBaseUrl(IHost host)
    {
        var serverAddresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        var address = serverAddresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel nevrátil žádnou adresu.");

        return address.Replace("127.0.0.1", "localhost");
    }
}
