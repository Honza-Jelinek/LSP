using System.Diagnostics;
using LSP.ProcessFixture;
using LSP.Server.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LSP.Server.Tests;

public sealed class FfprobeCancellationTests
{
    [Fact]
    public async Task Cancelled_probe_terminates_its_child_process()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ffmpeg:Directory"] = FixtureDirectory() }).Build();
        var locator = new FfmpegLocator(configuration);
        Assert.True(File.Exists(locator.FfprobePath));
        var service = new FfprobeService(locator, NullLogger<FfprobeService>.Instance);
        var marker = Path.Combine(Path.GetTempPath(), $"lsp-probe-{Guid.NewGuid():N}.pid");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var operation = service.ProbeAsync(marker, cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline)
                await Task.Delay(30);
            Assert.True(File.Exists(marker), "Fixture did not reach its running state.");
            var pid = int.Parse(await File.ReadAllTextAsync(marker));

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsRunning(pid) && DateTime.UtcNow < deadline)
                await Task.Delay(30);
            Assert.False(IsRunning(pid));
        }
        finally
        {
            cts.Cancel();
            try { await operation; }
            catch (OperationCanceledException) { }
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string FixtureDirectory()
    {
        _ = typeof(Marker); // The project reference ensures the executable is built.
        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "LSP.ProcessFixture", "bin", configuration, "net10.0"));
    }
}
