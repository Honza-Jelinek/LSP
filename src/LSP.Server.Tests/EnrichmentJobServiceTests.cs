using LSP.Server.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public class EnrichmentJobServiceTests
{
    private sealed class FakeRunner : IEnrichmentRunner
    {
        public int CallCount;
        public readonly TaskCompletionSource<EnrichmentSummary> Gate = new();
        public Exception? ThrowOnRun;
        public IProgress<EnrichmentProgress>? LastProgress;

        public async Task<EnrichmentSummary> RunAsync(bool force, IProgress<EnrichmentProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            LastProgress = progress;
            if (ThrowOnRun is not null) throw ThrowOnRun;

            // Registrace na už zrušeném tokenu se spustí synchronně hned — Cancel() volaný
            // před tímhle řádkem tedy funguje stejně spolehlivě jako po něm.
            await using var reg = ct.Register(() => Gate.TrySetCanceled(ct));
            return await Gate.Task;
        }
    }

    private static EnrichmentJobService CreateService(FakeRunner runner) =>
        new(runner, new LibraryOperationCoordinator(), NullLogger<EnrichmentJobService>.Instance);

    private static EnrichmentSummary DummySummary() => new(1, 2, 3, 4, 5, 6, 7, 8, 9);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Podmínka se nesplnila včas.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void TryStart_FromIdle_StartsRunningJob()
    {
        var svc = CreateService(new FakeRunner());

        var started = svc.TryStart(force: true, out var status);

        Assert.True(started);
        Assert.Equal(EnrichmentJobState.Running, status.State);
        Assert.True(status.Force);
        Assert.NotNull(status.StartedAt);
        Assert.True(svc.IsRunning);
    }

    [Fact]
    public async Task TryStart_WhileRunning_ReturnsFalse_AndDoesNotInvokeRunnerTwice()
    {
        var runner = new FakeRunner();
        var svc = CreateService(runner);

        Assert.True(svc.TryStart(false, out _));
        var second = svc.TryStart(true, out var status2);

        Assert.False(second);
        Assert.Equal(EnrichmentJobState.Running, status2.State);

        runner.Gate.SetResult(DummySummary());
        await svc.WaitForCompletionAsync();

        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task Completion_SetsCompletedStateWithSummary_AndAllowsRestart()
    {
        var runner = new FakeRunner();
        var svc = CreateService(runner);

        Assert.True(svc.TryStart(true, out _));

        var summary = DummySummary();
        runner.Gate.SetResult(summary);
        await svc.WaitForCompletionAsync();

        var final = svc.GetStatus();
        Assert.Equal(EnrichmentJobState.Completed, final.State);
        Assert.Equal(summary, final.Summary);
        Assert.NotNull(final.FinishedAt);
        Assert.False(svc.IsRunning);

        var restarted = svc.TryStart(false, out var status2);
        Assert.True(restarted);
        Assert.Equal(EnrichmentJobState.Running, status2.State);
    }

    [Fact]
    public async Task Exception_SetsFailedStateWithError()
    {
        var runner = new FakeRunner { ThrowOnRun = new InvalidOperationException("boom") };
        var svc = CreateService(runner);

        svc.TryStart(false, out _);
        await svc.WaitForCompletionAsync();

        var status = svc.GetStatus();
        Assert.Equal(EnrichmentJobState.Failed, status.State);
        Assert.NotNull(status.Summary?.Error);
        Assert.Contains("boom", status.Summary!.Error);
    }

    [Fact]
    public async Task Cancel_WhileRunning_CausesCancelledState()
    {
        var runner = new FakeRunner();
        var svc = CreateService(runner);

        svc.TryStart(false, out _);
        Assert.True(svc.Cancel());

        await svc.WaitForCompletionAsync();

        Assert.Equal(EnrichmentJobState.Cancelled, svc.GetStatus().State);
    }

    [Fact]
    public void Cancel_WhenIdle_ReturnsFalse()
    {
        var svc = CreateService(new FakeRunner());
        Assert.False(svc.Cancel());
    }

    [Fact]
    public async Task WaitForCompletionAsync_ReturnsImmediately_WhenIdle()
    {
        var svc = CreateService(new FakeRunner());
        var task = svc.WaitForCompletionAsync();
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task ProgressReports_UpdateStatus()
    {
        var runner = new FakeRunner();
        var svc = CreateService(runner);

        svc.TryStart(false, out _);
        await WaitUntilAsync(() => runner.LastProgress is not null);

        runner.LastProgress!.Report(new EnrichmentProgress(1, "Filmy", 3, 10));

        var status = svc.GetStatus();
        Assert.NotNull(status.Progress);
        Assert.Equal("Filmy", status.Progress!.PhaseName);
        Assert.Equal(3, status.Progress.Processed);
        Assert.Equal(10, status.Progress.Total);

        runner.Gate.SetResult(DummySummary());
        await svc.WaitForCompletionAsync();
    }
}
