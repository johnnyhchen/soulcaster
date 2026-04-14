namespace JcAttractor.Attractor;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public interface ICodergenBackend
{
    Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, string? reasoningEffort = null, CancellationToken ct = default);
}

public record CodergenResult(
    string Response,
    OutcomeStatus Status,
    Dictionary<string, string>? ContextUpdates = null,
    string? PreferredLabel = null,
    List<string>? SuggestedNextIds = null,
    StageStatusContract? StageStatus = null,
    string? RawAssistantResponse = null,
    Dictionary<string, object?>? Telemetry = null,
    Dictionary<string, string>? Artifacts = null);

public class CodergenHandler : INodeHandler
{
    private const int MaxContractAttempts = 3;

    private readonly ICodergenBackend _backend;

    public CodergenHandler(ICodergenBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Expand variables before adding runtime contract instructions.
        var expandedPrompt = ExpandVariables(node.Prompt, context, graph);
        var gatesRoot = Path.Combine(Path.GetDirectoryName(logsRoot) ?? logsRoot, "gates");
        expandedPrompt = RewriteArtifactPaths(expandedPrompt, logsRoot, gatesRoot);

        // Create stage directory and write prompt.
        var stageDir = RuntimeStageResolver.ResolveStageDir(logsRoot, context, node.Id);
        Directory.CreateDirectory(stageDir);

        var outgoingLabels = graph.OutgoingEdges(node.Id)
            .Where(e => !string.IsNullOrWhiteSpace(e.Label))
            .Select(e => e.Label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        expandedPrompt =
            BuildPreamble(node, graph, logsRoot, context, outgoingLabels) +
            BuildStageContractInstructions(outgoingLabels) +
            expandedPrompt;

        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), expandedPrompt, ct);

        var statusPath = Path.Combine(stageDir, "status.json");
        ArchivePreviousStatus(statusPath);

        var model = !string.IsNullOrEmpty(node.LlmModel) ? node.LlmModel : null;
        var provider = !string.IsNullOrEmpty(node.LlmProvider) ? node.LlmProvider : null;
        var reasoningEffort = !string.IsNullOrEmpty(node.ReasoningEffort) ? node.ReasoningEffort : null;

        CodergenResult result = new(
            Response: "No backend result produced.",
            Status: OutcomeStatus.Fail);

        StageStatusContract? stageStatus = null;
        var contractError = string.Empty;
        var attemptsUsed = 0;

        var attemptPrompt = expandedPrompt;
        for (var attempt = 1; attempt <= MaxContractAttempts; attempt++)
        {
            attemptsUsed = attempt;
            try
            {
                result = await _backend.RunAsync(attemptPrompt, model, provider, reasoningEffort, ct);
            }
            catch (Exception ex)
            {
                result = new CodergenResult(
                    Response: $"Error: {ex.Message}",
                    Status: OutcomeStatus.Fail,
                    StageStatus: new StageStatusContract(
                        Status: OutcomeStatus.Fail,
                        PreferredNextLabel: string.Empty,
                        SuggestedNextIds: new List<string>(),
                        ContextUpdates: new Dictionary<string, string>(),
                        Notes: "Codergen backend threw an exception.",
                        FailureReason: ex.Message));
            }

            await PersistAttemptArtifactsAsync(stageDir, attempt, result, ct);

            stageStatus = result.StageStatus;
            if (stageStatus is null)
            {
                stageStatus = await TryReadStageStatusArtifactAsync(statusPath, ct);
            }

            if (stageStatus is null)
            {
                var raw = result.RawAssistantResponse ?? result.Response;
                if (!StageStatusContract.TryParseAssistantResponse(raw, out stageStatus, out contractError))
                    stageStatus = null;
            }

            if (stageStatus is not null &&
                !TryValidatePreferredLabel(stageStatus.PreferredNextLabel, outgoingLabels, out contractError))
            {
                stageStatus = null;
            }

            if (stageStatus is not null)
                break;

            // Preserve legacy retry/fail semantics for backends that haven't
            // adopted the structured contract yet.
            if (result.Status != OutcomeStatus.Success)
                break;

            if (attempt < MaxContractAttempts)
            {
                var reminder = BuildReminder(contractError, outgoingLabels);
                await File.WriteAllTextAsync(Path.Combine(stageDir, $"reminder-{attempt}.md"), reminder, ct);
                attemptPrompt = expandedPrompt + "\n\n[STAGE CONTRACT REMINDER]\n" + reminder;
            }
        }

        var usedFallback = false;
        if (stageStatus is null)
        {
            usedFallback = true;
            stageStatus = StageStatusContract.FromLegacy(
                result,
                fallbackReason: string.IsNullOrWhiteSpace(contractError)
                    ? "Missing structured stage status output."
                    : contractError);
        }

        if (string.IsNullOrWhiteSpace(stageStatus.Notes))
        {
            stageStatus = stageStatus with
            {
                Notes = $"Codergen node '{node.Id}' completed with status {stageStatus.Status}."
            };
        }

        var outcome = stageStatus.ToOutcome();
        var telemetry = BuildStageTelemetry(result, attemptsUsed, usedFallback);
        if (telemetry.Count > 0)
            outcome = outcome with { Telemetry = telemetry };

        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), result.Response ?? string.Empty, ct);

        var rawAssistant = result.RawAssistantResponse;
        if (!string.IsNullOrWhiteSpace(rawAssistant) && !string.Equals(rawAssistant, result.Response, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(stageDir, "raw_assistant_response.md"), rawAssistant, ct);
        }

        var statusData = stageStatus.ToStatusJson(
            nodeId: node.Id,
            model: model,
            provider: provider,
            usedFallback: usedFallback,
            validationError: string.IsNullOrWhiteSpace(contractError) ? null : contractError);
        if (telemetry.Count > 0)
            statusData["telemetry"] = telemetry;

        await File.WriteAllTextAsync(
            statusPath,
            JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return outcome;
    }

    private static Dictionary<string, object?> BuildStageTelemetry(CodergenResult result, int attemptsUsed, bool usedFallback)
    {
        var telemetry = result.Telemetry is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(result.Telemetry, StringComparer.Ordinal);

        telemetry["contract_attempts"] = attemptsUsed;
        telemetry["contract_used_fallback"] = usedFallback;
        return telemetry;
    }

    private static void ArchivePreviousStatus(string statusPath)
    {
        if (!File.Exists(statusPath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(statusPath)!;
            var archived = Path.Combine(dir, $"status.previous.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(statusPath, archived, overwrite: true);
        }
        catch
        {
            // Non-critical. If archive fails, status.json will be overwritten later.
        }
    }

    private static async Task PersistAttemptArtifactsAsync(string stageDir, int attempt, CodergenResult result, CancellationToken ct)
    {
        var suffix = attempt == 1 ? string.Empty : $"-attempt-{attempt}";
        await File.WriteAllTextAsync(
            Path.Combine(stageDir, $"response{suffix}.md"),
            result.Response ?? string.Empty,
            ct);

        if (!string.IsNullOrWhiteSpace(result.RawAssistantResponse) &&
            !string.Equals(result.RawAssistantResponse, result.Response, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(
                Path.Combine(stageDir, $"raw-response{suffix}.md"),
                result.RawAssistantResponse,
                ct);
        }

        await PersistSupplementalArtifactsAsync(stageDir, result.Artifacts, ct);
    }

    private static async Task<StageStatusContract?> TryReadStageStatusArtifactAsync(string statusPath, CancellationToken ct)
    {
        if (!File.Exists(statusPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(statusPath, ct);
            if (StageStatusContract.TryParseJson(json, out var parsed, out _))
                return parsed;
        }
        catch
        {
            // Treat as absent; caller will retry/remind.
        }

        return null;
    }

    private static bool TryValidatePreferredLabel(
        string preferredLabel,
        IReadOnlyCollection<string> outgoingLabels,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(preferredLabel) || outgoingLabels.Count == 0)
            return true;

        var normalizedPreferred = EdgeSelector.NormalizeLabel(preferredLabel);
        var hasMatch = outgoingLabels
            .Select(EdgeSelector.NormalizeLabel)
            .Any(label => label == normalizedPreferred);

        if (hasMatch)
            return true;

        error = $"preferred_next_label '{preferredLabel}' does not match outgoing labels: {string.Join(", ", outgoingLabels)}";
        return false;
    }

    private static string RewriteArtifactPaths(string prompt, string logsRoot, string gatesRoot)
    {
        var rewritten = RewriteArtifactPrefix(prompt, "logs/", logsRoot);
        rewritten = RewriteArtifactPrefix(rewritten, "gates/", gatesRoot);
        return rewritten;
    }

    private static string RewriteArtifactPrefix(string prompt, string prefix, string absoluteRoot)
    {
        var normalizedRoot = absoluteRoot.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/') + "/";
        var pattern = $@"(?<![A-Za-z0-9_/\.-]){Regex.Escape(prefix)}(?<rest>[\w./-]+)";
        return Regex.Replace(
            prompt,
            pattern,
            match => normalizedRoot + match.Groups["rest"].Value,
            RegexOptions.CultureInvariant);
    }

    private static string BuildStageContractInstructions(IReadOnlyList<string> outgoingLabels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[STAGE STATUS CONTRACT]");
        sb.AppendLine("Before finishing this stage, output a valid JSON object (optionally in ```json fences) with these fields:");
        sb.AppendLine("  status: success | retry | fail | partial_success");
        sb.AppendLine("  preferred_next_label: string (optional)");
        sb.AppendLine("  suggested_next_ids: string[] (optional)");
        sb.AppendLine("  context_updates: object<string,string> (optional)");
        sb.AppendLine("  notes: string (optional)");
        sb.AppendLine("  failure_reason: string (optional)");
        sb.AppendLine("  blocking_question: string | object (optional when you need Soulcaster to auto-answer a blocking question)");
        if (outgoingLabels.Count > 0)
            sb.AppendLine($"If preferred_next_label is set, it MUST be one of: {string.Join(", ", outgoingLabels)}");
        sb.AppendLine("[/STAGE STATUS CONTRACT]");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildReminder(string reason, IReadOnlyList<string> outgoingLabels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Your previous output did not satisfy the stage status contract.");
        if (!string.IsNullOrWhiteSpace(reason))
            sb.AppendLine($"Reason: {reason}");
        sb.AppendLine("Return ONLY a valid JSON object with keys: status, preferred_next_label, suggested_next_ids, context_updates, notes, failure_reason.");
        if (outgoingLabels.Count > 0)
            sb.AppendLine($"Allowed preferred_next_label values: {string.Join(", ", outgoingLabels)}");
        return sb.ToString();
    }

    private static string BuildPreamble(
        GraphNode node,
        Graph graph,
        string logsRoot,
        PipelineContext context,
        IReadOnlyList<string> outgoingLabels)
    {
        // logsRoot is the absolute path to the logs/ directory.
        // outputRoot = logs parent = the run output directory (e.g. dotfiles/output/run-name).
        var outputRoot = Path.GetFullPath(Path.Combine(logsRoot, ".."));

        // Project root: dotfile lives at {project}/dotfiles/{name}.dot,
        // so output is at {project}/dotfiles/output/{run}. Going up three
        // levels from logsRoot (logs → output/run → output → dotfiles → project).
        var projectRoot = Path.GetFullPath(Path.Combine(logsRoot, "..", "..", "..", ".."));

        var fidelity = string.IsNullOrWhiteSpace(node.Fidelity) ? "full" : node.Fidelity;
        var thread = string.IsNullOrWhiteSpace(node.ThreadId) ? node.Id : node.ThreadId;
        var stageId = RuntimeStageResolver.ResolveStageId(context, node.Id);
        var resumeMode = context.Get("pipeline.resume_mode");
        var helperProvider = ResolveHelperAttribute(node, graph, "provider");
        var helperModel = ResolveHelperAttribute(node, graph, "model");
        var helperReasoning = ResolveHelperAttribute(node, graph, "reasoning_effort");
        if (string.IsNullOrWhiteSpace(resumeMode))
            resumeMode = "fresh";

        var sb = new StringBuilder();
        sb.AppendLine("[PIPELINE CONTEXT]");
        sb.AppendLine($"You are executing node \"{node.Id}\" in pipeline \"{graph.Name}\".");
        sb.AppendLine();
        sb.AppendLine("Your working directory (CWD) is the project root:");
        sb.AppendLine($"  {projectRoot}");
        sb.AppendLine();
        sb.AppendLine("Pipeline artifacts are stored at:");
        sb.AppendLine($"  {logsRoot}/");
        sb.AppendLine();
        sb.AppendLine("Runtime settings:");
        sb.AppendLine($"  Runtime fidelity: {fidelity}");
        sb.AppendLine($"  Runtime thread: {thread}");
        if (!string.Equals(stageId, node.Id, StringComparison.Ordinal))
            sb.AppendLine($"  Runtime stage: {stageId}");
        sb.AppendLine($"  Resume mode: {resumeMode}");
        if (!string.IsNullOrWhiteSpace(helperProvider))
            sb.AppendLine($"  Helper provider: {helperProvider}");
        if (!string.IsNullOrWhiteSpace(helperModel))
            sb.AppendLine($"  Helper model: {helperModel}");
        if (!string.IsNullOrWhiteSpace(helperReasoning))
            sb.AppendLine($"  Helper reasoning effort: {helperReasoning}");
        if (outgoingLabels.Count > 0)
            sb.AppendLine($"  Outgoing labels: {string.Join(", ", outgoingLabels)}");
        sb.AppendLine();
        sb.AppendLine("Artifact directory layout:");

        // List each node that has a logs subdirectory, marking which exist.
        foreach (var n in graph.Nodes.Values.OrderBy(n => n.Id))
        {
            if (n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase) ||
                n.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase) ||
                n.Shape.Equals("hexagon", StringComparison.OrdinalIgnoreCase))
                continue;

            var nodeDir = Path.Combine(logsRoot, n.Id);
            var exists = Directory.Exists(nodeDir);
            var marker = exists ? "(exists)" : "(not yet created)";
            sb.AppendLine($"  {logsRoot}/{n.Id,-25} {marker}");
        }

        sb.AppendLine();
        sb.AppendLine($"Current stage directory: {Path.Combine(logsRoot, stageId)}");
        sb.AppendLine($"Run output root: {outputRoot}");
        sb.AppendLine("Use ABSOLUTE paths when reading/writing pipeline artifacts.");
        sb.AppendLine("Source code files can use paths relative to the project root (your CWD).");
        sb.AppendLine("[/PIPELINE CONTEXT]");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string ExpandVariables(string prompt, PipelineContext context, Graph graph)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        var expanded = prompt.Replace("$goal", graph.Goal);

        foreach (var (key, value) in context.All)
        {
            expanded = expanded.Replace($"${{context.{key}}}", value);
        }

        return expanded;
    }

    private static string ResolveHelperAttribute(GraphNode node, Graph graph, string suffix)
    {
        var nodeKey = $"helper_{suffix}";
        if (node.RawAttributes.TryGetValue(nodeKey, out var nodeValue) && !string.IsNullOrWhiteSpace(nodeValue))
            return nodeValue;

        return graph.Attributes.TryGetValue(nodeKey, out var graphValue) ? graphValue : string.Empty;
    }

    private static async Task PersistSupplementalArtifactsAsync(
        string stageDir,
        IReadOnlyDictionary<string, string>? artifacts,
        CancellationToken ct)
    {
        if (artifacts is null || artifacts.Count == 0)
            return;

        foreach (var (relativePath, content) in artifacts)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var safePath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(stageDir, safePath));
            if (!fullPath.StartsWith(stageDir, StringComparison.Ordinal))
                continue;

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(fullPath, content ?? string.Empty, ct);
        }
    }
}
