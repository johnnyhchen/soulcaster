using System.Text.Json;

namespace Soulcaster.Tests;

public class FileInterviewerTests
{
    [Fact]
    public async Task AskAsync_ReusesPendingGateForSameNode_AndConsumesExistingAnswer()
    {
        var gatesDir = Path.Combine(Path.GetTempPath(), $"jc_file_interviewer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(gatesDir);

        try
        {
            var gateId = "gate-20260101-010101001-abc123";
            var gateDir = Path.Combine(gatesDir, gateId);
            Directory.CreateDirectory(gateDir);

            var question = BuildQuestion("gate_1");
            await WriteQuestionAsync(Path.Combine(gateDir, "question.json"), gateId, question);
            await File.WriteAllTextAsync(Path.Combine(gatesDir, "pending"), gateId);
            await WriteAnswerAsync(Path.Combine(gateDir, "answer.json"), "approve");

            var interviewer = new FileInterviewer(gatesDir, pollInterval: TimeSpan.FromMilliseconds(10));
            var answer = await interviewer.AskAsync(question);

            Assert.Equal("approve", answer.Text);
            Assert.Equal(AnswerStatus.Answered, answer.Status);
            Assert.False(File.Exists(Path.Combine(gatesDir, "pending")));

            var gateDirs = Directory.GetDirectories(gatesDir)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith("gate-", StringComparison.Ordinal))
                .ToList();

            Assert.Single(gateDirs);
            Assert.Equal(gateId, gateDirs[0]);
        }
        finally
        {
            if (Directory.Exists(gatesDir))
                Directory.Delete(gatesDir, recursive: true);
        }
    }

    [Fact]
    public async Task AskAsync_CreatesNewGate_WhenPendingGateBelongsToDifferentNode()
    {
        var gatesDir = Path.Combine(Path.GetTempPath(), $"jc_file_interviewer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(gatesDir);

        try
        {
            var oldGateId = "gate-20260101-010101001-old111";
            var oldGateDir = Path.Combine(gatesDir, oldGateId);
            Directory.CreateDirectory(oldGateDir);

            var previousQuestion = BuildQuestion("gate_old");
            await WriteQuestionAsync(Path.Combine(oldGateDir, "question.json"), oldGateId, previousQuestion);
            await File.WriteAllTextAsync(Path.Combine(gatesDir, "pending"), oldGateId);

            var interviewer = new FileInterviewer(gatesDir, pollInterval: TimeSpan.FromMilliseconds(10));
            var question = BuildQuestion("gate_new");
            var askTask = interviewer.AskAsync(question);

            var newGateId = await WaitForPendingGateIdAsync(gatesDir, oldGateId, TimeSpan.FromSeconds(2));
            Assert.NotEqual(oldGateId, newGateId);

            var newAnswerPath = Path.Combine(gatesDir, newGateId, "answer.json");
            await WriteAnswerAsync(newAnswerPath, "approve");

            var answer = await askTask;
            Assert.Equal("approve", answer.Text);
            Assert.Equal(AnswerStatus.Answered, answer.Status);
        }
        finally
        {
            if (Directory.Exists(gatesDir))
                Directory.Delete(gatesDir, recursive: true);
        }
    }

    private static InterviewQuestion BuildQuestion(string nodeId)
    {
        var question = new InterviewQuestion(
            "Gate 1: series foundation",
            QuestionType.SingleSelect,
            ["approve", "revise story", "stop"]);

        return question with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["node_id"] = nodeId,
                ["shape"] = "hexagon",
                ["graph_name"] = "ai_manga_pipeline"
            }
        };
    }

    private static async Task WriteQuestionAsync(string path, string gateId, InterviewQuestion question)
    {
        var payload = new
        {
            text = question.Text,
            type = question.Type.ToString(),
            options = question.Options,
            metadata = question.Metadata,
            gate_id = gateId,
            status = "pending",
            timestamp = DateTime.UtcNow.ToString("o"),
            created_at = DateTime.UtcNow.ToString("o")
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WriteAnswerAsync(string path, string text)
    {
        var payload = new
        {
            text,
            selected_options = new[] { text },
            status = "answered",
            actor = "scenario-tester",
            source = "test",
            answered_at = DateTime.UtcNow.ToString("o")
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> WaitForPendingGateIdAsync(string gatesDir, string excludeGateId, TimeSpan timeout)
    {
        var pendingPath = Path.Combine(gatesDir, "pending");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pendingPath))
            {
                var gateId = (await File.ReadAllTextAsync(pendingPath)).Trim();
                if (!string.IsNullOrWhiteSpace(gateId) && !string.Equals(gateId, excludeGateId, StringComparison.Ordinal))
                    return gateId;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for a new pending gate in '{gatesDir}'.");
    }
}
