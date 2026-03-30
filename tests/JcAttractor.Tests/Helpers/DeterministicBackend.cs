using System.Text.Json;
using System.Text.RegularExpressions;
using JcAttractor.Attractor;

namespace JcAttractor.Tests;

internal sealed class DeterministicBackend : ICodergenBackend
{
    private static readonly Regex NodeIdPattern = new(@"executing node ""(?<id>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, Queue<Func<DeterministicInvocation, CodergenResult>>> _plans =
        new(StringComparer.Ordinal);

    public List<DeterministicInvocation> Invocations { get; } = new();

    public DeterministicBackend On(string nodeId, Func<DeterministicInvocation, CodergenResult> handler)
    {
        if (!_plans.TryGetValue(nodeId, out var queue))
        {
            queue = new Queue<Func<DeterministicInvocation, CodergenResult>>();
            _plans[nodeId] = queue;
        }

        queue.Enqueue(handler);
        return this;
    }

    public DeterministicBackend Queue(string nodeId, params CodergenResult[] results)
    {
        foreach (var result in results)
        {
            On(nodeId, _ => result);
        }

        return this;
    }

    public Task<CodergenResult> RunAsync(
        string prompt,
        string? model = null,
        string? provider = null,
        string? reasoningEffort = null,
        CancellationToken ct = default)
    {
        var nodeId = ExtractNodeId(prompt);
        var invocation = new DeterministicInvocation(
            Index: Invocations.Count,
            NodeId: nodeId,
            Prompt: prompt,
            Model: model,
            Provider: provider,
            ReasoningEffort: reasoningEffort);
        Invocations.Add(invocation);

        if (_plans.TryGetValue(nodeId, out var queue) && queue.Count > 0)
        {
            var handler = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Task.FromResult(handler(invocation));
        }

        return Task.FromResult(Result(notes: $"deterministic success for {nodeId}"));
    }

    public static CodergenResult Result(
        OutcomeStatus status = OutcomeStatus.Success,
        string? preferredNextLabel = null,
        IEnumerable<string>? suggestedNextIds = null,
        IDictionary<string, string>? contextUpdates = null,
        string? notes = null,
        string? failureReason = null)
    {
        var contract = new StageStatusContract(
            Status: status,
            PreferredNextLabel: preferredNextLabel ?? string.Empty,
            SuggestedNextIds: suggestedNextIds?.ToList() ?? new List<string>(),
            ContextUpdates: contextUpdates is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(contextUpdates, StringComparer.Ordinal),
            Notes: notes ?? $"deterministic {StageStatusContract.ToStatusString(status)}",
            FailureReason: failureReason);

        var response = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = StageStatusContract.ToStatusString(contract.Status),
            ["preferred_next_label"] = contract.PreferredNextLabel,
            ["suggested_next_ids"] = contract.SuggestedNextIds,
            ["context_updates"] = contract.ContextUpdates,
            ["notes"] = contract.Notes,
            ["failure_reason"] = contract.FailureReason
        });

        return new CodergenResult(
            Response: response,
            Status: contract.Status,
            ContextUpdates: contract.ContextUpdates,
            PreferredLabel: contract.PreferredNextLabel,
            SuggestedNextIds: contract.SuggestedNextIds,
            StageStatus: contract,
            RawAssistantResponse: response);
    }

    private static string ExtractNodeId(string prompt)
    {
        var match = NodeIdPattern.Match(prompt ?? string.Empty);
        return match.Success ? match.Groups["id"].Value : $"unknown_{Guid.NewGuid():N}";
    }
}

internal sealed record DeterministicInvocation(
    int Index,
    string NodeId,
    string Prompt,
    string? Model,
    string? Provider,
    string? ReasoningEffort);
