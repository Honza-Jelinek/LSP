using LSP.Server.Library;

namespace LSP.Server.Tests;

public sealed class LibraryOperationCoordinatorTests
{
    [Fact]
    public void Operations_AreMutuallyExclusive_AndLeaseReleasesCoordinator()
    {
        var coordinator = new LibraryOperationCoordinator();

        Assert.True(coordinator.TryBeginScan(out var scan));
        Assert.True(coordinator.IsScanRunning);
        Assert.False(coordinator.TryBeginEnrichment(out _));
        Assert.False(coordinator.TryBeginExport(out _));
        Assert.False(coordinator.TryBeginImport(out _));
        Assert.False(coordinator.TryBeginMaintenance(out _));

        scan!.Dispose();

        Assert.False(coordinator.IsScanRunning);
        Assert.True(coordinator.TryBeginEnrichment(out var enrichment));
        enrichment!.Dispose();
        Assert.True(coordinator.TryBeginMaintenance(out var maintenance));
        maintenance!.Dispose();
    }
}
