namespace Soulcaster.CodingAgent;

public sealed class SessionPool : IDisposable
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _disposed;

    public Session GetOrCreate(string threadId, Func<Session> factory)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            throw new ArgumentException("threadId is required", nameof(threadId));
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        lock (_lock)
        {
            ThrowIfDisposed();

            if (_sessions.TryGetValue(threadId, out var existing) && existing.State != SessionState.Closed)
                return existing;

            var created = factory();
            _sessions[threadId] = created;
            return created;
        }
    }

    public bool TryGet(string threadId, out Session? session)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            if (_sessions.TryGetValue(threadId, out var existing) && existing.State != SessionState.Closed)
            {
                session = existing;
                return true;
            }

            session = null;
            return false;
        }
    }

    public void Release(string threadId)
    {
        // No-op in this implementation. Sessions remain pooled until discarded/closed.
        _ = threadId;
    }

    public bool Discard(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();

            if (!_sessions.Remove(threadId, out var session))
                return false;

            session.Close();
            return true;
        }
    }

    public void CloseAll()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            foreach (var session in _sessions.Values)
                session.Close();

            _sessions.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            CloseAll();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionPool));
    }
}
