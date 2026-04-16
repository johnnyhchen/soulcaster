namespace Soulcaster.CodingAgent;

public class Subagent
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public Session Session { get; }
    public int Depth { get; }
    private CancellationTokenSource? _cts;
    private Task<AssistantTurn>? _runningTask;

    public const int DefaultMaxDepth = 3;

    public Subagent(Session session, int depth)
    {
        Session = session;
        Depth = depth;
    }

    /// <summary>
    /// Sends input to the subagent and returns when it produces a final response.
    /// </summary>
    public async Task<string> SendInputAsync(string message, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningTask = Session.ProcessInputAsync(message, _cts.Token);
        var result = await _runningTask;
        return result.Content ?? "[No response from subagent]";
    }

    /// <summary>
    /// Closes the subagent, cancelling any running work.
    /// </summary>
    public void Close()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
