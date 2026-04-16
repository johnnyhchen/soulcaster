using System.Text.Json;
using Soulcaster.Runner;

namespace Soulcaster.Tests;

[Trait("Harness", "Scenario")]
public class ProcessRunScenarioTests
{
    [Fact]
    public async Task ProcessRun_ContextReset_ResetsOnlySelectedEdgeSession()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("context_reset");
        var runDir = workspace.WorkingDir("context-reset");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "context-reset-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["seed"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "seed-memory-token")
                        }
                    ],
                    ["carry"] =
                    [
                        new ScriptedResponsePlan
                        {
                            MustContain = new List<string> { "seed-memory-token" },
                            AssistantText = StatusJson(
                                contextUpdates: new Dictionary<string, string> { ["context_reset.carry"] = "carried" },
                                notes: "carry-forward stage observed prior thread state")
                        }
                    ],
                    ["fresh"] =
                    [
                        new ScriptedResponsePlan
                        {
                            MustNotContain = new List<string> { "seed-memory-token" },
                            AssistantText = StatusJson(
                                contextUpdates: new Dictionary<string, string> { ["context_reset.fresh"] = "reset" },
                                notes: "fresh stage did not observe prior thread state")
                        }
                    ],
                    ["verify"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "verified context reset")
                        }
                    ]
                }
            });

        var result = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-context-reset.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var carryStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(result.LogsDir, "carry", "status.json"));
        var freshStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(result.LogsDir, "fresh", "status.json"));
        Assert.Equal("carried", ReadNestedString(carryStatus, "context_updates", "context_reset.carry"));
        Assert.Equal("reset", ReadNestedString(freshStatus, "context_updates", "context_reset.fresh"));

        var manifest = ProcessRunHarness.ReadJsonObject(result.ManifestPath);
        Assert.False(string.IsNullOrWhiteSpace(ReadString(manifest, "run_id")));
    }

    [Fact]
    public async Task ProcessRun_AutoAnswer_UsesHelperSessionAndAvoidsHumanGate()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("autoanswer");
        var runDir = workspace.WorkingDir("autoanswer");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "autoanswer-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["asker"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = QuestionStatusJson("What filename should I use?")
                        },
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(
                                contextUpdates: new Dictionary<string, string> { ["autoanswer.used"] = "true" },
                                notes: "completed after helper answer")
                        }
                    ],
                    ["verify"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "verify complete")
                        }
                    ]
                },
                Helpers =
                [
                    new ScriptedHelperPlan
                    {
                        MatchContains = "What filename should I use?",
                        AssistantText = "Use logs/autoanswer/AUTOANSWER.md"
                    }
                ]
            });

        var result = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-autoanswer.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var askerDir = Path.Combine(result.LogsDir, "asker");
        Assert.True(File.Exists(Path.Combine(askerDir, "primary-question-1.md")));
        Assert.True(File.Exists(Path.Combine(askerDir, "helper-answer-1.md")));
        Assert.Contains("What filename should I use?", File.ReadAllText(Path.Combine(askerDir, "primary-question-1.md")));
        Assert.Contains("logs/autoanswer/AUTOANSWER.md", File.ReadAllText(Path.Combine(askerDir, "helper-answer-1.md")));

        var status = ProcessRunHarness.ReadJsonObject(Path.Combine(askerDir, "status.json"));
        Assert.Equal("true", ReadNestedString(status, "context_updates", "autoanswer.used"));
        Assert.Equal("1", ReadNestedString(status, "telemetry", "helper_session_count"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(runDir, "gates")));
    }

    [Fact]
    public async Task ProcessRun_AutoresumeAlways_RespawnsAfterCrashAndCompletes()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("autoresume");
        var runDir = workspace.WorkingDir("autoresume");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "autoresume-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["stage_a"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "stage a done") }],
                    ["crash_point"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "crash point done") }],
                    ["verify"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "verify done") }]
                }
            });

        var result = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-autoresume-crash.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--autoresume-policy", "always",
                "--crash-after-stage", "crash_point"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(20));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var manifest = ProcessRunHarness.ReadJsonObject(result.ManifestPath);
        Assert.Equal("completed", ReadString(manifest, "status"));
        Assert.True(long.Parse(ReadString(manifest, "respawn_count")!) >= 1);
    }

    [Fact]
    public async Task ProcessRun_SupervisorWorker_ProgressesAfterSteering()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("supervisor_worker");
        var runDir = workspace.WorkingDir("supervisor-worker");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "supervisor-worker-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["manager"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "Proceed now.")
                        }
                    ]
                }
            });

        var result = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-supervisor-worker.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(20));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var workerRunDir = Path.Combine(runDir, "workers", "qa-supervisor-worker-child");
        Assert.True(File.Exists(Path.Combine(workerRunDir, "logs", "control", "steer_applied.txt")));
        Assert.True(File.Exists(Path.Combine(workerRunDir, "logs", "worker", "WORKER-DONE.md")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(result.LogsDir, "manager"), "cycle-*.md").Any());
    }

    [Fact]
    public async Task ProcessRun_SupervisorEscalation_ReturnsRetryAndLeavesArtifacts()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("supervisor_escalation");
        var runDir = workspace.WorkingDir("supervisor-escalation");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "supervisor-escalation-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["manager"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "Still waiting.")
                        }
                    ]
                }
            });

        var result = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-supervisor-escalation.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(20));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var managerStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(result.LogsDir, "manager", "status.json"));
        Assert.Equal("retry", ReadString(managerStatus, "status"));
        Assert.Equal("true", ReadString(managerStatus, "escalated")?.ToLowerInvariant());
        Assert.True(File.Exists(Path.Combine(runDir, "workers", "qa-supervisor-stalled-child", "logs", "control", "waiting.txt")));
    }

    [Fact]
    public async Task ProcessRun_InteractiveEditor_CanAuthorSaveAndRunWorkflow()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("interactive");
        var dotFilePath = Path.Combine(workspace.Root, "interactive-generated.dot");
        var workingDir = Path.Combine(workspace.Root, "output", "interactive-generated");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "interactive-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["write"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "interactive write done") }]
                }
            });

        var commands = string.Join(
            Environment.NewLine,
            [
                "goal Exercise interactive mode",
                "stage write shape=box label=\"Write Artifact\"",
                "prompt write",
                "Return success after writing the interactive artifact.",
                ".",
                "edge start write",
                "edge write done",
                $"run --backend scripted --backend-script {planPath}",
                "quit",
                string.Empty
            ]);

        var result = await ProcessRunHarness.RunInteractiveAsync(dotFilePath, commands);
        await ProcessRunHarness.WaitForLogicalCompletionAsync(Path.Combine(workingDir, "logs", "result.json"), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(dotFilePath));
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(Path.Combine(workingDir, "logs", "result.json")), "status"));
    }

    private static string StatusJson(
        string status = "success",
        IDictionary<string, string>? contextUpdates = null,
        string notes = "ok")
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = status,
            ["preferred_next_label"] = "",
            ["suggested_next_ids"] = Array.Empty<string>(),
            ["context_updates"] = contextUpdates ?? new Dictionary<string, string>(),
            ["notes"] = notes
        });
    }

    private static string QuestionStatusJson(string question)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "retry",
            ["preferred_next_label"] = "",
            ["suggested_next_ids"] = Array.Empty<string>(),
            ["context_updates"] = new Dictionary<string, string>(),
            ["notes"] = "Need helper guidance.",
            ["blocking_question"] = new Dictionary<string, object?>
            {
                ["text"] = question
            }
        });
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static string? ReadNestedString(
        IReadOnlyDictionary<string, object?> payload,
        string outerKey,
        string innerKey)
    {
        if (!payload.TryGetValue(outerKey, out var outer) || outer is not Dictionary<string, object?> nested)
            return null;

        return nested.TryGetValue(innerKey, out var value) ? value?.ToString() : null;
    }
}
