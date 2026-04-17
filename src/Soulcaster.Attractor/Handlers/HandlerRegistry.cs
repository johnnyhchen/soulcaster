namespace Soulcaster.Attractor.Handlers;

public class HandlerRegistry
{
    private readonly Dictionary<string, INodeHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public HandlerRegistry(ICodergenBackend? backend = null, IInterviewer? interviewer = null, ISupervisorController? supervisorController = null)
    {
        // Register default handlers
        _handlers["Mdiamond"] = new StartHandler();
        _handlers["Msquare"] = new ExitHandler();
        _handlers["box"] = new CodergenHandler(backend ?? new NullCodergenBackend());
        _handlers["hexagon"] = new WaitHumanHandler(interviewer ?? new AutoApproveInterviewer());
        _handlers["diamond"] = new ConditionalHandler();
        _handlers["component"] = new ParallelHandler(this, backend);
        _handlers["tripleoctagon"] = new FanInHandler(backend);
        _handlers["parallelogram"] = new ToolHandler();
        _handlers["house"] = new ManagerLoopHandler(backend, supervisorController);
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

    public IReadOnlyList<string> GetRegisteredShapes()
    {
        return _handlers.Keys
            .OrderBy(shape => shape, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Null backend that returns fail for any codergen call when no backend is configured.
    /// </summary>
    private class NullCodergenBackend : ICodergenBackend
    {
        public Task<CodergenResult> RunAsync(
            string prompt,
            string? model = null,
            string? provider = null,
            string? reasoningEffort = null,
            CancellationToken ct = default,
            CodergenExecutionOptions? options = null)
        {
            return Task.FromResult(new CodergenResult(
                Response: "[Simulated] Response for codergen node. No backend configured.",
                Status: OutcomeStatus.Success
            ));
        }
    }

}
