using JcAttractor.Attractor;
using JcAttractor.CodingAgent;
using JcAttractor.UnifiedLlm;

// ── Configuration ────────────────────────────────────────────────────
var dotFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "betrayal.dot");
if (args.Length > 0)
    dotFilePath = args[0];

dotFilePath = Path.GetFullPath(dotFilePath);
if (!File.Exists(dotFilePath))
{
    Console.Error.WriteLine($"DOT file not found: {dotFilePath}");
    return 1;
}

var workingDir = Path.Combine(Path.GetDirectoryName(dotFilePath)!, "output");
Directory.CreateDirectory(workingDir);

var logsDir = Path.Combine(workingDir, "logs");
Directory.CreateDirectory(logsDir);

Console.WriteLine($"Pipeline:    {dotFilePath}");
Console.WriteLine($"Working dir: {workingDir}");
Console.WriteLine($"Logs dir:    {logsDir}");
Console.WriteLine();

// ── Parse the DOT file ──────────────────────────────────────────────
var dotSource = await File.ReadAllTextAsync(dotFilePath);
var graph = DotParser.Parse(dotSource);

Console.WriteLine($"Graph: {graph.Name}");
Console.WriteLine($"Goal:  {graph.Goal?[..Math.Min(graph.Goal.Length, 80)]}...");
Console.WriteLine($"Nodes: {graph.Nodes.Count}");
Console.WriteLine($"Edges: {graph.Edges.Count}");
Console.WriteLine();

// ── Build the codergen backend ──────────────────────────────────────
var backend = new AgentCodergenBackend(workingDir);

// ── Configure and run the pipeline ──────────────────────────────────
var config = new PipelineConfig(
    LogsRoot: logsDir,
    Interviewer: new AutoApproveInterviewer(),
    Backend: backend,
    Transforms: new List<IGraphTransform>
    {
        new StylesheetTransform(),
        new VariableExpansionTransform()
    }
);

var engine = new PipelineEngine(config);

Console.WriteLine("Starting pipeline...");
Console.WriteLine(new string('─', 60));

var result = await engine.RunAsync(graph);

Console.WriteLine(new string('─', 60));
Console.WriteLine($"Pipeline finished: {result.Status}");
Console.WriteLine($"Completed nodes:  {string.Join(", ", result.CompletedNodes)}");

foreach (var (nodeId, outcome) in result.NodeOutcomes)
{
    Console.WriteLine($"  {nodeId}: {outcome.Status} - {outcome.Notes}");
}

return result.Status == OutcomeStatus.Success ? 0 : 1;

// ═════════════════════════════════════════════════════════════════════
// AgentCodergenBackend - bridges the Attractor pipeline to the
// CodingAgent agentic loop, which actually writes code via tools
// ═════════════════════════════════════════════════════════════════════
class AgentCodergenBackend : ICodergenBackend
{
    private readonly string _workingDir;

    public AgentCodergenBackend(string workingDir)
    {
        _workingDir = workingDir;
    }

    public async Task<CodergenResult> RunAsync(
        string prompt, string? model = null, string? provider = null, string? reasoningEffort = null, CancellationToken ct = default)
    {
        // Resolve the LLM adapter
        IProviderAdapter adapter;
        IProviderProfile profile;

        var resolvedProvider = provider ?? InferProvider(model);

        switch (resolvedProvider?.ToLowerInvariant())
        {
            case "openai":
                var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
                adapter = new OpenAiAdapter(openAiKey);
                profile = new OpenAiProfile();
                break;

            case "gemini":
                var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                    ?? throw new InvalidOperationException("GEMINI_API_KEY not set.");
                adapter = new GeminiAdapter(geminiKey);
                profile = new GeminiProfile();
                break;

            default: // "anthropic" or unspecified
                var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
                adapter = new AnthropicAdapter(anthropicKey);
                profile = new AnthropicProfile();
                break;
        }

        // Override the model if specified by the pipeline node
        if (!string.IsNullOrEmpty(model))
        {
            SetProfileModel(profile, model);
        }

        // Create execution environment and session
        var env = new LocalExecutionEnvironment(_workingDir);
        var sessionConfig = new SessionConfig(
            MaxTurns: 200,
            MaxToolRoundsPerInput: 100,
            DefaultCommandTimeoutMs: 30_000,
            MaxCommandTimeoutMs: 600_000,
            ReasoningEffort: reasoningEffort
        );

        var session = new Session(adapter, profile, env, sessionConfig);

        // Subscribe to events for logging
        session.EventEmitter.Subscribe(evt =>
        {
            switch (evt.Kind)
            {
                case EventKind.ToolCallStart:
                    var toolName = evt.Data.GetValueOrDefault("toolName");
                    Console.WriteLine($"  [tool] {toolName}");
                    break;
                case EventKind.AssistantTextDelta:
                    var text = evt.Data.GetValueOrDefault("text") as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var preview = text.Length > 200 ? text[..200] + "..." : text;
                        Console.Write(preview);
                    }
                    break;
            }
            return Task.CompletedTask;
        });

        // Run the agentic loop
        Console.WriteLine($"  [codergen] Starting agent session (model={model ?? "default"})");

        try
        {
            var turn = await session.ProcessInputAsync(prompt, ct);
            var response = turn.Content ?? "[no response]";

            Console.WriteLine($"  [codergen] Session complete ({turn.ToolCalls.Count} tool calls made)");

            // Detect API errors that were caught internally by the Session
            var status = OutcomeStatus.Success;
            if (response.StartsWith("[Error:") || response.StartsWith("[Turn limit reached]"))
            {
                Console.Error.WriteLine($"  [codergen] Agent returned error: {response}");
                status = OutcomeStatus.Retry;
            }

            return new CodergenResult(
                Response: response,
                Status: status
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [codergen] Error: {ex.Message}");
            return new CodergenResult(
                Response: $"Agent error: {ex.Message}",
                Status: OutcomeStatus.Retry
            );
        }
    }

    private static string? InferProvider(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var lower = model.ToLowerInvariant();
        if (lower.StartsWith("claude")) return "anthropic";
        if (lower.StartsWith("gpt") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4")) return "openai";
        if (lower.StartsWith("gemini")) return "gemini";
        return null;
    }

    private static void SetProfileModel(IProviderProfile profile, string model)
    {
        switch (profile)
        {
            case AnthropicProfile ap:
                ap.Model = model;
                break;
            case OpenAiProfile op:
                op.Model = model;
                break;
            case GeminiProfile gp:
                gp.Model = model;
                break;
        }
    }
}
