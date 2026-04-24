namespace Soulcaster.CodingAgent;

public class Subagent
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public Session Session { get; }
    public int Depth { get; }

    public SubagentState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public Exception? LastError
    {
        get
        {
            lock (_gate)
                return _lastError;
        }
    }

    public string? LastOutput
    {
        get
        {
            lock (_gate)
                return _lastOutput;
        }
    }

    public int PendingInputCount
    {
        get
        {
            lock (_gate)
                return _pendingInputs.Count;
        }
    }

    public const int DefaultMaxDepth = 3;

    private readonly object _gate = new();
    private readonly Queue<string> _pendingInputs = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _workerTask;
    private CancellationTokenSource? _activeInputCts;
    private TaskCompletionSource<string> _completion = CreateCompletedCompletion("[Agent has no output yet]");
    private SubagentState _state = SubagentState.Spawned;
    private Exception? _lastError;
    private string? _lastOutput;

    public Subagent(Session session, int depth)
    {
        Session = session;
        Depth = depth;
    }

    /// <summary>
    /// Queues input for background processing and returns immediately.
    /// The queued work is observed via WaitForCompletionAsync.
    /// </summary>
    public ValueTask EnqueueInputAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message is required.", nameof(message));

        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfClosed();

            _pendingInputs.Enqueue(message);

            if (_state is SubagentState.Spawned or SubagentState.Completed or SubagentState.Failed or SubagentState.Canceled)
                _state = SubagentState.Queued;

            if (_workerTask is null || _workerTask.IsCompleted)
            {
                _completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _workerTask = Task.Run(ProcessQueueAsync);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Queues input and waits for the subagent to finish all currently queued work.
    /// </summary>
    public async Task<string> SendInputAsync(string message, CancellationToken ct = default)
    {
        await EnqueueInputAsync(message, ct);
        return await WaitForCompletionAsync(ct);
    }

    /// <summary>
    /// Waits for the subagent to finish the current batch of queued work.
    /// </summary>
    public Task<string> WaitForCompletionAsync(CancellationToken ct = default)
    {
        Task<string> completionTask;
        lock (_gate)
        {
            if (_state == SubagentState.Spawned)
                return Task.FromResult(BuildCompletionMessageUnsafe());

            completionTask = _completion.Task;
        }

        return completionTask.WaitAsync(ct);
    }

    /// <summary>
    /// Closes the subagent, cancelling any running work.
    /// </summary>
    public void Close()
    {
        CancellationTokenSource? activeInputCts;
        TaskCompletionSource<string> completion;

        lock (_gate)
        {
            if (_state == SubagentState.Closed)
                return;

            _pendingInputs.Clear();
            _state = SubagentState.Closed;
            activeInputCts = _activeInputCts;
            _activeInputCts = null;
            completion = _completion;
        }

        activeInputCts?.Cancel();
        _lifetimeCts.Cancel();
        Session.Close();
        completion.TrySetResult("[Agent closed]");
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                string message;
                CancellationToken token;

                lock (_gate)
                {
                    if (_state == SubagentState.Closed)
                    {
                        _completion.TrySetResult(BuildCompletionMessageUnsafe());
                        return;
                    }

                    if (_pendingInputs.Count == 0)
                    {
                        _state = _lastError is null ? SubagentState.Completed : SubagentState.Failed;
                        _completion.TrySetResult(BuildCompletionMessageUnsafe());
                        return;
                    }

                    message = _pendingInputs.Dequeue();
                    _activeInputCts?.Dispose();
                    _activeInputCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                    token = _activeInputCts.Token;
                    _state = SubagentState.Running;
                }

                try
                {
                    var result = await Session.ProcessInputAsync(message, token);
                    lock (_gate)
                    {
                        _lastOutput = result.Content ?? "[No response from subagent]";
                        _lastError = null;
                    }
                }
                catch (OperationCanceledException ex) when (_lifetimeCts.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (_state != SubagentState.Closed)
                            _state = SubagentState.Canceled;

                        _lastError = ex;
                        _completion.TrySetResult(BuildCompletionMessageUnsafe());
                    }

                    return;
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _lastError = ex;
                        if (_state != SubagentState.Closed)
                            _state = SubagentState.Failed;
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        _activeInputCts?.Dispose();
                        _activeInputCts = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _lastError = ex;
                if (_state != SubagentState.Closed)
                    _state = SubagentState.Failed;

                _completion.TrySetResult(BuildCompletionMessageUnsafe());
            }
        }
    }

    private string BuildCompletionMessageUnsafe() => _state switch
    {
        SubagentState.Completed => _lastOutput ?? "[No response from subagent]",
        SubagentState.Failed => $"[Agent failed: {_lastError?.Message ?? "unknown error"}]",
        SubagentState.Canceled => "[Agent canceled]",
        SubagentState.Closed => "[Agent closed]",
        _ => "[Agent has no output yet]"
    };

    private void ThrowIfClosed()
    {
        if (_state == SubagentState.Closed)
            throw new InvalidOperationException("Agent is closed.");
    }

    private static TaskCompletionSource<string> CreateCompletedCompletion(string result)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetResult(result);
        return completion;
    }
}
