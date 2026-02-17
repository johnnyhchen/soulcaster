namespace JcAttractor.Attractor;

public class HandlerRegistry
{
    private readonly Dictionary<string, INodeHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public HandlerRegistry(ICodergenBackend? backend = null, IInterviewer? interviewer = null)
    {
        // Register default handlers
        _handlers["Mdiamond"] = new StartHandler();
        _handlers["Msquare"] = new ExitHandler();
        _handlers["box"] = new CodergenHandler(backend ?? new NullCodergenBackend());
        _handlers["hexagon"] = new WaitHumanHandler(interviewer ?? new AutoApproveInterviewer());
        _handlers["diamond"] = new ConditionalHandler();
        _handlers["component"] = new ParallelHandler(this);
        _handlers["tripleoctagon"] = new FanInHandler();
        _handlers["parallelogram"] = new ToolHandler();
    }

    public void Register(string shape, INodeHandler handler)
    {
        _handlers[shape] = handler;
    }

    public INodeHandler? GetHandler(string shape)
    {
        return _handlers.GetValueOrDefault(shape);
    }

    public INodeHandler GetHandlerOrThrow(string shape)
    {
        return _handlers.TryGetValue(shape, out var handler)
            ? handler
            : throw new InvalidOperationException($"No handler registered for shape '{shape}'.");
    }

    /// <summary>
    /// Null backend that returns fail for any codergen call when no backend is configured.
    /// </summary>
    private class NullCodergenBackend : ICodergenBackend
    {
        public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
        {
            return Task.FromResult(new CodergenResult(
                Response: "No codergen backend configured.",
                Status: OutcomeStatus.Fail
            ));
        }
    }
}
