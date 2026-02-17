namespace JcAttractor.CodingAgent;

public class EventEmitter
{
    private readonly List<Func<SessionEvent, Task>> _handlers = new();
    public string SessionId { get; set; } = "";

    public void Subscribe(Func<SessionEvent, Task> handler) => _handlers.Add(handler);

    public async Task EmitAsync(EventKind kind, Dictionary<string, object?>? data = null)
    {
        var evt = new SessionEvent(kind, DateTimeOffset.UtcNow, SessionId, data ?? new());
        foreach (var h in _handlers)
            await h(evt);
    }
}
