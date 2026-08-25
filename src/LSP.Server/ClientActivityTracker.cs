namespace LSP.Server;

/// <summary>
/// Sleduje čas poslední /api/* aktivity. Používá host proces (LSP.App) k rozhodnutí, jestli
/// smí po zavření okna ukončit server běžící na pozadí — dokud je nějaký klient aktivní
/// (polluje status, streamuje video…), server nesmí zmizet, i když enrichment job doběhl.
/// </summary>
public sealed class ClientActivityTracker
{
    private long _lastTicks = DateTimeOffset.UtcNow.UtcTicks;

    public void Touch() => Interlocked.Exchange(ref _lastTicks, DateTimeOffset.UtcNow.UtcTicks);

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastTicks), TimeSpan.Zero);

    public bool ActiveWithin(TimeSpan window) => DateTimeOffset.UtcNow - LastActivity < window;
}
