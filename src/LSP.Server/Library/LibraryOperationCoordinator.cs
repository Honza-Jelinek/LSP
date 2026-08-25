namespace LSP.Server.Library;

/// <summary>Serializuje operace, ktere meni nebo nahrazuji knihovnu a jeji soubory.</summary>
public sealed class LibraryOperationCoordinator
{
    private readonly Lock _gate = new();
    private Operation? _active;

    public bool IsScanRunning
    {
        get { lock (_gate) return _active is Operation.Scan; }
    }

    public bool IsImportRunning
    {
        get { lock (_gate) return _active is Operation.Import; }
    }

    public bool TryBeginScan(out IDisposable? lease) => TryBegin(Operation.Scan, out lease);

    public bool TryBeginEnrichment(out IDisposable? lease) => TryBegin(Operation.Enrichment, out lease);

    public bool TryBeginExport(out IDisposable? lease) => TryBegin(Operation.Export, out lease);

    public bool TryBeginImport(out IDisposable? lease) => TryBegin(Operation.Import, out lease);

    public bool TryBeginMaintenance(out IDisposable? lease) => TryBegin(Operation.Maintenance, out lease);

    private bool TryBegin(Operation operation, out IDisposable? lease)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                lease = null;
                return false;
            }
            _active = operation;
            lease = new Lease(this, operation);
            return true;
        }
    }

    private void End(Operation operation)
    {
        lock (_gate)
        {
            if (_active == operation)
                _active = null;
        }
    }

    private enum Operation { Scan, Enrichment, Export, Import, Maintenance }

    private sealed class Lease(LibraryOperationCoordinator owner, Operation operation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.End(operation);
        }
    }
}
