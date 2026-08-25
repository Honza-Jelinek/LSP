using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LSP.Server.Library;

public enum ExportJobState { Idle, Running, Completed, Failed, Cancelled }

public sealed record ExportJobStatus(
    ExportJobState State,
    ExportRequest? Request,
    bool Extended,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ExportProgress? Progress,
    ExportReport? Report,
    string? Error);

/// <summary>Singleton stav jednoho exportu; kazdy beh si vytvori vlastni DI scope s DbContextem.</summary>
public sealed class ExportJobService(
    IServiceScopeFactory scopeFactory,
    LibraryOperationCoordinator operations,
    ILogger<ExportJobService> log) : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private ExportJobStatus _status = new(ExportJobState.Idle, null, false, null, null, null, null, null);

    public ExportJobStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _status.State == ExportJobState.Running; }
    }

    public bool TryStart(ExportRequest request, out ExportJobStatus status)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource cts;
        IDisposable? operationLease;

        lock (_gate)
        {
            if (_status.State == ExportJobState.Running || !operations.TryBeginExport(out operationLease))
            {
                status = _status;
                return false;
            }

            cts = _cts = new CancellationTokenSource();
            _status = new ExportJobStatus(
                ExportJobState.Running,
                request,
                ExportService.IsExistingPackage(request.TargetRoot),
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null);
            status = _status;
        }

        _runTask = Task.Run(() => RunJobAsync(request, operationLease!, cts.Token));
        return true;
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_status.State != ExportJobState.Running || _cts is null)
                return false;
            _cts.Cancel();
            return true;
        }
    }

    private async Task RunJobAsync(ExportRequest request, IDisposable operationLease, CancellationToken ct)
    {
        using (operationLease)
        using (var scope = scopeFactory.CreateScope())
        {
            var reporter = new CallbackProgress<ExportProgress>(value =>
            {
                lock (_gate) _status = _status with { Progress = value };
            });

            try
            {
                var service = scope.ServiceProvider.GetRequiredService<ExportService>();
                var report = await service.ExportAsync(request, reporter, ct);
                lock (_gate)
                {
                    _status = _status with
                    {
                        State = ExportJobState.Completed,
                        Extended = report.Extended,
                        FinishedAt = DateTimeOffset.UtcNow,
                        Report = report,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                log.LogInformation("Export byl zrusen mezi soubory.");
                lock (_gate)
                    _status = _status with { State = ExportJobState.Cancelled, FinishedAt = DateTimeOffset.UtcNow };
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Export selhal");
                lock (_gate)
                {
                    _status = _status with
                    {
                        State = ExportJobState.Failed,
                        FinishedAt = DateTimeOffset.UtcNow,
                        Error = ex.InnerException?.Message ?? ex.Message,
                    };
                }
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;
        cts?.Cancel();
        cts?.Dispose();
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
