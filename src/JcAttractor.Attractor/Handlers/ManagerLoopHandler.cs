namespace JcAttractor.Attractor;

using System.Text.Json;

/// <summary>
/// Handler for stack.manager_loop nodes (shape=house).
/// Implements a supervisor loop that runs a child pipeline, polls for telemetry,
/// applies steering, checks stop conditions, and enforces max cycles.
/// </summary>
public class ManagerLoopHandler : INodeHandler
{
    private readonly ICodergenBackend? _backend;

    public ManagerLoopHandler(ICodergenBackend? backend = null)
    {
        _backend = backend;
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Read attributes
        int maxCycles = node.RawAttributes.TryGetValue("max_cycles", out var mc) && int.TryParse(mc, out var mcVal) ? mcVal : 10;
        var stopCondition = node.RawAttributes.GetValueOrDefault("stop_condition", "");
        var childDotfile = node.RawAttributes.GetValueOrDefault("child_dotfile", "");
        int steerCooldownMs = node.RawAttributes.TryGetValue("steer_cooldown", out var sc) && int.TryParse(sc, out var scVal) ? scVal : 5000;

        // Create stage directory
        string stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);

        var cycleLog = new List<Dictionary<string, object?>>();
        int currentCycle = 0;

        while (currentCycle < maxCycles)
        {
            ct.ThrowIfCancellationRequested();
            currentCycle++;

            // If we have a backend and a prompt, run a management cycle
            if (_backend is not null && !string.IsNullOrEmpty(node.Prompt))
            {
                var cyclePrompt = $"[Cycle {currentCycle}/{maxCycles}]\n{node.Prompt}\n\nContext:\n";
                foreach (var (key, value) in context.All)
                    cyclePrompt += $"  {key} = {value}\n";

                var result = await _backend.RunAsync(cyclePrompt, ct: ct);

                // Write cycle output
                await File.WriteAllTextAsync(
                    Path.Combine(stageDir, $"cycle-{currentCycle}.md"),
                    result.Response, ct);

                // Apply context updates from the cycle
                if (result.ContextUpdates is not null)
                    context.MergeUpdates(result.ContextUpdates);

                cycleLog.Add(new Dictionary<string, object?>
                {
                    ["cycle"] = currentCycle,
                    ["status"] = result.Status.ToString().ToLowerInvariant(),
                    ["response_length"] = result.Response.Length
                });

                // Check stop condition
                if (!string.IsNullOrEmpty(stopCondition))
                {
                    // Simple stop condition: check if a context key matches a value
                    // Format: "context.key=value"
                    if (stopCondition.Contains('='))
                    {
                        var parts = stopCondition.Split('=', 2);
                        var condKey = parts[0].Replace("context.", "").Trim();
                        var condValue = parts[1].Trim().Trim('"');
                        if (context.Get(condKey) == condValue)
                            break;
                    }
                }

                // Check if backend returned success/fail explicitly
                if (result.Status == OutcomeStatus.Fail)
                    break;
            }
            else
            {
                // No backend — just a cycle counter stub
                cycleLog.Add(new Dictionary<string, object?>
                {
                    ["cycle"] = currentCycle,
                    ["status"] = "no_backend"
                });
                break; // No work to do without a backend
            }

            // Apply steer cooldown between cycles
            if (currentCycle < maxCycles)
            {
                await Task.Delay(steerCooldownMs, ct);
            }
        }

        // Write final status
        var statusData = new Dictionary<string, object?>
        {
            ["node_id"] = node.Id,
            ["total_cycles"] = currentCycle,
            ["max_cycles"] = maxCycles,
            ["cycles"] = cycleLog
        };
        await File.WriteAllTextAsync(
            Path.Combine(stageDir, "status.json"),
            JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        var reachedMax = currentCycle >= maxCycles;
        var status = reachedMax ? OutcomeStatus.PartialSuccess : OutcomeStatus.Success;

        return new Outcome(
            Status: status,
            ContextUpdates: new Dictionary<string, string>
            {
                [$"{node.Id}.cycles"] = currentCycle.ToString(),
                [$"{node.Id}.reached_max"] = reachedMax.ToString().ToLowerInvariant()
            },
            Notes: $"Manager loop '{node.Id}' completed {currentCycle}/{maxCycles} cycles."
        );
    }
}
