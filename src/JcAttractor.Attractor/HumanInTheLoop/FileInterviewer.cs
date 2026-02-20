using System.Text.Json;

namespace JcAttractor.Attractor;

/// <summary>
/// Interviewer that communicates via the filesystem.
/// Writes a question JSON to {gateDir}/question.json and polls for {gateDir}/answer.json.
/// An external process (or human) reads the question, writes the answer, and the pipeline continues.
/// </summary>
public class FileInterviewer : IInterviewer
{
    private readonly string _gatesDir;
    private readonly TimeSpan _pollInterval;

    public FileInterviewer(string gatesDir, TimeSpan? pollInterval = null)
    {
        _gatesDir = gatesDir;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        Directory.CreateDirectory(_gatesDir);
    }

    public async Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default)
    {
        // Create a gate directory for this question using a timestamp to avoid collisions
        var gateId = $"gate-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var gateDir = Path.Combine(_gatesDir, gateId);
        Directory.CreateDirectory(gateDir);

        // Also maintain a "pending" symlink/file so watchers can find the active gate easily
        var pendingFile = Path.Combine(_gatesDir, "pending");

        // Write the question
        var questionPayload = new
        {
            text = question.Text,
            type = question.Type.ToString(),
            options = question.Options,
            gate_id = gateId,
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var questionPath = Path.Combine(gateDir, "question.json");
        var answerPath = Path.Combine(gateDir, "answer.json");

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(questionPath, JsonSerializer.Serialize(questionPayload, jsonOptions), ct);
        await File.WriteAllTextAsync(pendingFile, gateId, ct);

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

                    // Clean up pending marker
                    if (File.Exists(pendingFile)) File.Delete(pendingFile);

                    Console.WriteLine($"  [gate] Answer received: {text}");
                    return new InterviewAnswer(text, selectedOptions);
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
}
