using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Library;

public enum EnrichmentJobState { Idle, Running, Completed, Failed, Cancelled }

public sealed record EnrichmentJobStatus(
    EnrichmentJobState State, bool Force,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    EnrichmentProgress? Progress, EnrichmentSummary? Summary);

/// <summary>Seam pro spuštění enrichmentu — umožňuje testovat job service dvojníkem bez skutečné DB/TMDB/LLM.</summary>
public interface IEnrichmentRunner
{
    Task<EnrichmentSummary> RunAsync(bool force, IProgress<EnrichmentProgress> progress, CancellationToken ct);
}

/// <summary>Výchozí runner: EnrichmentService je scoped (drží DbContext) → vlastní DI scope na job.</summary>
public sealed class ScopedEnrichmentRunner(IServiceScopeFactory scopeFactory) : IEnrichmentRunner
{
    public async Task<EnrichmentSummary> RunAsync(bool force, IProgress<EnrichmentProgress> progress, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<EnrichmentService>();
        return await svc.EnrichAsync(force, progress, ct);
    }
}

/// <summary>
/// Drží stav jednoho běžícího/posledního enrichment jobu (singleton). Umožňuje spustit enrichment
/// jako fire-and-forget background task a pollovat jeho průběh z UI, místo aby HTTP request na
/// /api/enrich čekal (v praxi i desítky minut kvůli LLM rate limitu) na celý výsledek.
/// </summary>
public sealed class EnrichmentJobService(
    IEnrichmentRunner runner,
    LibraryOperationCoordinator operations,
    ILogger<EnrichmentJobService> log) : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TaskCompletionSource _idleTcs = CompletedTcs();
    private EnrichmentJobStatus _status = new(EnrichmentJobState.Idle, false, null, null, null, null);

    public EnrichmentJobStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _status.State == EnrichmentJobState.Running; }
    }

    /// <summary>Spustí nový job, pokud žádný neběží. Vrací false (a aktuální stav), když už jeden běží.</summary>
    public bool TryStart(bool force, out EnrichmentJobStatus status)
    {
        CancellationTokenSource cts;
        IDisposable? operationLease;
        lock (_gate)
        {
            if (_status.State == EnrichmentJobState.Running
                || !operations.TryBeginEnrichment(out operationLease))
            {
                status = _status;
                return false;
            }

            cts = _cts = new CancellationTokenSource();
            _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _status = new EnrichmentJobStatus(EnrichmentJobState.Running, force, DateTimeOffset.UtcNow, null, null, null);
            status = _status;
        }

        _runTask = Task.Run(() => RunJobAsync(force, operationLease!, cts.Token));
        return true;
    }

    /// <summary>Zruší běžící job (no-op, pokud žádný neběží). Vrací true, pokud byl nějaký job zrušen.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_status.State != EnrichmentJobState.Running || _cts is null) return false;
            _cts.Cancel();
            return true;
        }
    }

    /// <summary>Hotovo hned, pokud job zrovna neběží; jinak čeká na dokončení/selhání/zrušení.</summary>
    public Task WaitForCompletionAsync()
    {
        lock (_gate) return _idleTcs.Task;
    }

    private async Task RunJobAsync(bool force, IDisposable operationLease, CancellationToken ct)
    {
        using (operationLease)
        {
            var reporter = new ActionProgress<EnrichmentProgress>(p =>
            {
                lock (_gate) _status = _status with { Progress = p };
            });

            try
            {
                var summary = await runner.RunAsync(force, reporter, ct);
                lock (_gate)
                    _status = _status with { State = EnrichmentJobState.Completed, FinishedAt = DateTimeOffset.UtcNow, Summary = summary };
            }
            catch (OperationCanceledException)
            {
                log.LogInformation("Enrichment job zrušen.");
                lock (_gate)
                    _status = _status with { State = EnrichmentJobState.Cancelled, FinishedAt = DateTimeOffset.UtcNow };
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Enrichment job selhal");
                var summary = new EnrichmentSummary(0, 0, 0, 0, 0, 0, 0, 0, 0,
                    Error: $"Enrichment selhal: {ex.InnerException?.Message ?? ex.Message}");
                lock (_gate)
                    _status = _status with { State = EnrichmentJobState.Failed, FinishedAt = DateTimeOffset.UtcNow, Summary = summary };
            }
            finally
            {
                TaskCompletionSource tcs;
                lock (_gate) tcs = _idleTcs;
                tcs.TrySetResult();
            }
        }
    }

    /// <summary>Best-effort zrušení + krátké počkání — volá se při vypínání serveru.</summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? runTask;
        lock (_gate) { cts = _cts; runTask = _runTask; }

        cts?.Cancel();
        if (runTask is not null)
        {
            try { runTask.Wait(TimeSpan.FromSeconds(5)); }
            catch { /* best effort, server se stejně vypíná */ }
        }
        cts?.Dispose();
    }

    private static TaskCompletionSource CompletedTcs()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary>Přímé volání callbacku — Progress&lt;T&gt; by potřeboval SynchronizationContext, který tu není.</summary>
    private sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
