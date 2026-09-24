using System.Collections.Concurrent;

namespace LSP.Server.Media;

/// <summary>Serializes progress reads/writes per path and rejects superseded playback sessions.</summary>
public sealed class ProgressWriteCoordinator
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> WithLockAsync<T>(string path, Func<WriterState, Task<T>> operation, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(path, _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await operation(entry.State);
        }
        finally { entry.Gate.Release(); }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public WriterState State { get; } = new();
    }

    public sealed class WriterState
    {
        private Guid? _session;
        private long _sequence;
        private bool _revoked;
        private readonly HashSet<Guid> _retired = [];

        public bool Begin(Guid session)
        {
            if (session == Guid.Empty || _retired.Contains(session)) return false;
            if (_session == session) return !_revoked;
            if (_session is { } previous) _retired.Add(previous);
            _session = session;
            _sequence = 0;
            _revoked = false;
            return true;
        }

        public bool CanWrite(Guid? session, long? sequence) => !_revoked &&
            (session is null
                ? _session is null && sequence is null
                : session == _session && sequence is > 0 && sequence > _sequence);

        public void Commit(long? sequence)
        {
            if (sequence is { } value) _sequence = value;
        }

        public void Revoke()
        {
            if (_session is { } previous) _retired.Add(previous);
            _session = null;
            _revoked = true;
        }
    }
}
