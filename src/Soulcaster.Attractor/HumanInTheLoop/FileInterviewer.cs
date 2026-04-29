using System.Text.Json;
using Soulcaster.Attractor.Execution;

namespace Soulcaster.Attractor.HumanInTheLoop;

/// <summary>
/// Interviewer that communicates via the filesystem.
/// Writes a question JSON to {gateDir}/question.json and polls for {gateDir}/answer.json.
/// An external process (or human) reads the question, writes the answer, and the pipeline continues.
/// </summary>
public class FileInterviewer : IInterviewer
{
    private readonly string _gatesDir;
    private readonly Func<CancellationToken, Task>? _onMutation;
    private readonly TimeSpan _pollInterval;

    public FileInterviewer(
        string gatesDir,
        TimeSpan? pollInterval = null,
        Func<CancellationToken, Task>? onMutation = null)
    {
        _gatesDir = gatesDir;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _onMutation = onMutation;
        Directory.CreateDirectory(_gatesDir);
    }

    public async Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default)
    {
        var logsRoot = WorkflowEventLog.TryResolveLogsRootFromGatesDir(_gatesDir);

        // Also maintain a "pending" symlink/file so watchers can find the active gate easily
        var pendingFile = Path.Combine(_gatesDir, "pending");
        var reusedPendingGate = TryReusePendingGate(question, pendingFile, out var gateId, out var gateDir);

        if (!reusedPendingGate)
        {
            // Create a gate directory for this question using a timestamp to avoid collisions
            gateId = $"gate-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
            gateDir = Path.Combine(_gatesDir, gateId);
            Directory.CreateDirectory(gateDir);
        }

        var createdAt = DateTime.UtcNow.ToString("o");

        var questionPath = Path.Combine(gateDir, "question.json");
        var answerPath = Path.Combine(gateDir, "answer.json");

        if (!reusedPendingGate)
        {
            // Write the question
            var questionPayload = new
            {
                text = question.Text,
                type = question.Type.ToString(),
                options = question.Options,
                metadata = question.Metadata,
                gate_id = gateId,
                status = "pending",
                timestamp = createdAt,
                created_at = createdAt
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(questionPath, JsonSerializer.Serialize(questionPayload, jsonOptions), ct);
            await File.WriteAllTextAsync(pendingFile, gateId, ct);
            if (!string.IsNullOrWhiteSpace(logsRoot))
            {
                await WorkflowEventLog.AppendAsync(
                    logsRoot,
                    eventType: "gate_created",
                    nodeId: question.Metadata.GetValueOrDefault("node_id"),
                    data: new Dictionary<string, object?>
                    {
                        ["gate_id"] = gateId,
                        ["question"] = question.Text,
                        ["question_type"] = question.Type.ToString(),
                        ["options"] = question.Options,
                        ["default_choice"] = question.Metadata.GetValueOrDefault("default_choice"),
                        ["metadata"] = question.Metadata
                    },
                    ct: ct);
            }
            await NotifyMutationAsync(ct);
        }

        // Print to console so background watchers can see it
        Console.WriteLine();
        Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  HUMAN GATE: {question.Text}");
        Console.WriteLine($"║  Gate ID: {gateId}");
        if (question.Options.Count > 0)
        {
            for (int i = 0; i < question.Options.Count; i++)
            {
                Console.WriteLine($"║    [{i + 1}] {question.Options[i]}");
            }
        }
        Console.WriteLine($"║");
        Console.WriteLine($"║  Waiting for: {answerPath}");
        Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Poll for answer
        while (!ct.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(logsRoot) && RunControl.TryReadCancellation(logsRoot, out var cancellation))
                throw new RunCancelledException(cancellation!);

            if (File.Exists(answerPath))
            {
                try
                {
                    var answerJson = await File.ReadAllTextAsync(answerPath, ct);
                    var answerDoc = JsonDocument.Parse(answerJson);
                    var root = answerDoc.RootElement;

                    var text = root.GetProperty("text").GetString() ?? "";
                    var selectedOptions = new List<string>();
                    if (root.TryGetProperty("selected_options", out var optionsEl))
                    {
                        foreach (var opt in optionsEl.EnumerateArray())
                        {
                            selectedOptions.Add(opt.GetString() ?? "");
                        }
                    }
                    else
                    {
                        selectedOptions.Add(text);
                    }

                    var actor = root.TryGetProperty("actor", out var actorEl) ? actorEl.GetString() : null;
                    var rationale = root.TryGetProperty("rationale", out var rationaleEl) ? rationaleEl.GetString() : null;
                    var source = root.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null;
                    var answeredAt = root.TryGetProperty("answered_at", out var answeredAtEl)
                        ? answeredAtEl.GetString()
                        : null;
                    var status = root.TryGetProperty("status", out var statusEl)
                        ? statusEl.GetString()
                        : "answered";

                    // Clean up pending marker
                    if (File.Exists(pendingFile)) File.Delete(pendingFile);

                    if (!string.IsNullOrWhiteSpace(logsRoot))
                    {
                        await WorkflowEventLog.AppendAsync(
                            logsRoot,
                            eventType: "gate_answered",
                            nodeId: question.Metadata.GetValueOrDefault("node_id"),
                            data: new Dictionary<string, object?>
                            {
                                ["gate_id"] = gateId,
                                ["status"] = status,
                                ["text"] = text,
                                ["selected_options"] = selectedOptions,
                                ["actor"] = actor,
                                ["rationale"] = rationale,
                                ["source"] = source,
                                ["answered_at"] = answeredAt
                            },
                            ct: ct);
                    }
                    await NotifyMutationAsync(ct);

                    Console.WriteLine($"  [gate] Answer received: {text}");
                    return new InterviewAnswer(text, selectedOptions, ParseAnswerStatus(status));
                }
                catch (JsonException)
                {
                    // File may be partially written, wait and retry
                }
            }

            await Task.Delay(_pollInterval, ct);
        }

        throw new OperationCanceledException("FileInterviewer was cancelled while waiting for answer", ct);
    }

    private bool TryReusePendingGate(
        InterviewQuestion question,
        string pendingFile,
        out string gateId,
        out string gateDir)
    {
        gateId = string.Empty;
        gateDir = string.Empty;

        try
        {
            if (!File.Exists(pendingFile))
                return false;

            var candidateGateId = File.ReadAllText(pendingFile).Trim();
            if (string.IsNullOrWhiteSpace(candidateGateId))
                return false;

            var candidateGateDir = Path.Combine(_gatesDir, candidateGateId);
            var candidateQuestionPath = Path.Combine(candidateGateDir, "question.json");
            if (!File.Exists(candidateQuestionPath))
                return false;

            using var questionDoc = JsonDocument.Parse(File.ReadAllText(candidateQuestionPath));
            var root = questionDoc.RootElement;

            var existingQuestion = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
            if (!string.Equals(existingQuestion, question.Text, StringComparison.Ordinal))
                return false;

            var existingOptions = new List<string>();
            if (root.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionsEl.EnumerateArray())
                    existingOptions.Add(option.GetString() ?? string.Empty);
            }
            if (!existingOptions.SequenceEqual(question.Options))
                return false;

            string? existingNodeId = null;
            if (root.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind == JsonValueKind.Object)
            {
                if (metadataEl.TryGetProperty("node_id", out var nodeIdEl))
                    existingNodeId = nodeIdEl.GetString();
            }

            question.Metadata.TryGetValue("node_id", out var expectedNodeId);
            if (!string.IsNullOrWhiteSpace(expectedNodeId) &&
                !string.Equals(existingNodeId, expectedNodeId, StringComparison.Ordinal))
                return false;

            gateId = candidateGateId;
            gateDir = candidateGateDir;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AnswerStatus ParseAnswerStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "timeout" => AnswerStatus.Timeout,
            "skipped" => AnswerStatus.Skipped,
            _ => AnswerStatus.Answered
        };
    }

    private async Task NotifyMutationAsync(CancellationToken ct)
    {
        if (_onMutation is null)
            return;

        try
        {
            await _onMutation(ct);
        }
        catch
        {
            // Store sync is best-effort from the interviewer path.
        }
    }
}
