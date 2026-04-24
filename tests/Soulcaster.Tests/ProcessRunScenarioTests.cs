using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task ProcessRun_ReliabilityGapClosure_ProjectsStoreReplayAndAuditedGateFlow()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("reliability_gap_closure");
        var runDir = workspace.WorkingDir("reliability-gap-closure");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "reliability-gap-closure-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize complete") }]
                }
            });

        var runTask = ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-reliability-gap-closure.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--autoresume-policy", "always",
                "--crash-after-stage", "draft"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(25));

        await ProcessRunHarness.WaitForPendingGateAsync(runDir, TimeSpan.FromSeconds(20), CancellationToken.None);

        var answerResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "gate",
                "answer",
                "--dir", runDir,
                "approve",
                "--actor", "scenario-tester",
                "--reason", "Approved after resume validation.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, answerResult.ExitCode);

        var result = await runTask;
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var storeDir = Path.Combine(runDir, "store");
        Assert.True(File.Exists(Path.Combine(storeDir, "runs.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "stage_attempts.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "run_events.jsonl")));
        Assert.True(File.Exists(Path.Combine(storeDir, "gate_answers.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "provider_invocations.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "artifact_versions.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "artifact_lineage.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "leases.json")));
        Assert.True(File.Exists(Path.Combine(storeDir, "replay.json")));

        using (var gateAnswers = JsonDocument.Parse(File.ReadAllText(Path.Combine(storeDir, "gate_answers.json"))))
        {
            var gateAnswer = Assert.Single(gateAnswers.RootElement.EnumerateArray());
            Assert.Equal("scenario-tester", gateAnswer.GetProperty("actor").GetString());
            Assert.Equal("Approved after resume validation.", gateAnswer.GetProperty("rationale").GetString());
            Assert.Equal("scenario-test", gateAnswer.GetProperty("source").GetString());
        }

        using (var stageAttempts = JsonDocument.Parse(File.ReadAllText(Path.Combine(storeDir, "stage_attempts.json"))))
        {
            var attempts = stageAttempts.RootElement.EnumerateArray().ToList();
            Assert.Contains(attempts, attempt => attempt.GetProperty("node_id").GetString() == "draft");
            Assert.Contains(attempts, attempt => attempt.GetProperty("node_id").GetString() == "finalize");
        }

        using (var providerInvocations = JsonDocument.Parse(File.ReadAllText(Path.Combine(storeDir, "provider_invocations.json"))))
        {
            var invocations = providerInvocations.RootElement.EnumerateArray().ToList();
            Assert.Contains(invocations, invocation =>
                invocation.GetProperty("provider").GetString() == "openai" &&
                invocation.GetProperty("model").GetString() == "gpt-5.4");
        }

        using (var leases = JsonDocument.Parse(File.ReadAllText(Path.Combine(storeDir, "leases.json"))))
        {
            var leaseEntries = leases.RootElement.EnumerateArray().ToList();
            Assert.Contains(leaseEntries, lease => lease.GetProperty("state").GetString() == "released");
        }

        using (var replay = JsonDocument.Parse(File.ReadAllText(Path.Combine(storeDir, "replay.json"))))
        {
            var events = replay.RootElement.GetProperty("events").EnumerateArray().ToList();
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "lease_acquired");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "run_crashed");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "gate_created");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "gate_answered");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "run_finished");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "lease_released");
        }

        var replayResult = await ProcessRunHarness.RunRunnerAsync(
            ["replay", "--dir", runDir],
            workingDirectory: runDir);

        Assert.Equal(0, replayResult.ExitCode);
        Assert.Contains("run_crashed", replayResult.Stdout);
        Assert.Contains("gate_answered", replayResult.Stdout);
        Assert.Contains("run_finished", replayResult.Stdout);

        var overviewQuery = await ProcessRunHarness.RunRunnerAsync(
            ["query", "overview", "--dir", runDir, "--json"],
            workingDirectory: runDir);

        Assert.Equal(0, overviewQuery.ExitCode);
        using (var overviewDocument = JsonDocument.Parse(overviewQuery.Stdout))
        {
            var row = Assert.Single(overviewDocument.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal("completed", row.GetProperty("status").GetString());
            Assert.Equal("success", row.GetProperty("outcome_status").GetString());
            Assert.True(row.GetProperty("operator_activity_count").GetInt64() >= 1);
            Assert.Equal(0, row.GetProperty("pending_gate_count").GetInt64());
        }

        var operatorQuery = await ProcessRunHarness.RunRunnerAsync(
            ["query", "operators", "--dir", runDir, "--event-type", "gate_answered", "--json"],
            workingDirectory: runDir);

        Assert.Equal(0, operatorQuery.ExitCode);
        using (var operatorDocument = JsonDocument.Parse(operatorQuery.Stdout))
        {
            var rows = operatorDocument.RootElement.GetProperty("rows").EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(rows, row => row.GetProperty("activity_type").GetString() == "gate_answered");
            Assert.Contains(rows, row => row.GetProperty("actor").GetString() == "scenario-tester");
        }
    }

    [Fact]
    public async Task ProcessRun_ControlCancel_CancelsActiveGateAndProjectsSqlite()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("control_cancel");
        var runDir = workspace.WorkingDir("control-cancel");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "control-cancel-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize complete") }]
                }
            });

        var runTask = ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-reliability-gap-closure.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(20));

        await ProcessRunHarness.WaitForPendingGateAsync(runDir, TimeSpan.FromSeconds(20), CancellationToken.None);

        var cancelResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "cancel",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Cancel during gate wait.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, cancelResult.ExitCode);

        var result = await runTask;
        Assert.Equal(130, result.ExitCode);
        Assert.Equal("cancelled", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));
        Assert.Equal("cancelled", ReadString(ProcessRunHarness.ReadJsonObject(result.ManifestPath), "status"));

        var events = ProcessRunHarness.ReadEvents(result.EventsPath);
        Assert.Contains(events, evt => ReadString(evt, "event_type") == "run_cancel_requested");
        Assert.Contains(events, evt => ReadString(evt, "event_type") == "run_cancelled");

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.True(File.Exists(dbPath));
        Assert.Equal("cancelled", await WaitForSqliteStringAsync(
            dbPath,
            "SELECT status FROM runs LIMIT 1;",
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM replay_events WHERE event_type = 'run_cancelled';",
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM leases WHERE state = 'released';",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_ControlRetryStageAndResume_RecoversFailedRunUsingPersistedBackendPath()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("retry_resume");
        var runDir = workspace.WorkingDir("retry-resume");
        var dotPath = WriteDot(
            workspace,
            "retry-resume.dot",
            """
            digraph G {
                goal = "Exercise retry-stage and resume."

                start [shape=Mdiamond]
                draft [shape=box, label="Draft", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                finalize [shape=box, label="Finalize", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the final stage completed successfully."]
                done [shape=Msquare]

                start -> draft
                draft -> finalize
                finalize -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "retry-resume-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(status: "fail", notes: "finalize failed") }]
                }
            });

        var initialResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(1, initialResult.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(initialResult.ResultPath), "status"));

        var initialManifest = ProcessRunHarness.ReadJsonObject(initialResult.ManifestPath);
        Assert.Equal(planPath, ReadString(initialManifest, "backend_script_path"));

        ProcessRunHarness.WritePlan(
            workspace,
            "retry-resume-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize recovered") }]
                }
            });

        var retryResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "retry-stage",
                "--dir", runDir,
                "--node", "finalize",
                "--actor", "scenario-tester",
                "--reason", "Retry finalize after correcting the scripted backend plan.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, retryResult.ExitCode);

        var resumeResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "resume",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Resume after retry-stage preparation.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, resumeResult.ExitCode);

        await ProcessRunHarness.WaitForResultStatusAsync(
            initialResult.ResultPath,
            "success",
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.Equal("completed", ReadString(ProcessRunHarness.ReadJsonObject(initialResult.ManifestPath), "status"));

        using (var stageAttempts = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "store", "stage_attempts.json"))))
        {
            var attempts = stageAttempts.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Equal(1, attempts.Count(item => item.GetProperty("node_id").GetString() == "draft"));
            Assert.Equal(2, attempts.Count(item => item.GetProperty("node_id").GetString() == "finalize"));
            Assert.Contains(attempts, item =>
                item.GetProperty("node_id").GetString() == "finalize" &&
                item.GetProperty("attempt_number").GetInt32() == 2 &&
                item.GetProperty("is_current").GetBoolean());
        }

        using (var replay = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "store", "replay.json"))))
        {
            var events = replay.RootElement.GetProperty("events").EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "operator_retry_stage_requested");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "run_resume_requested");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "run_finished");
        }

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal(2, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM stage_attempts WHERE node_id = 'finalize';",
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM replay_events WHERE event_type = 'operator_retry_stage_requested';",
            TimeSpan.FromSeconds(10)));

        var mutationsResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "query",
                "mutations",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--json"
            ],
            workingDirectory: runDir);
        Assert.Equal(0, mutationsResult.ExitCode);
        using (var mutations = JsonDocument.Parse(mutationsResult.Stdout))
        {
            var rows = mutations.RootElement.GetProperty("rows").EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(rows, row => row.GetProperty("mutation_type").GetString() == "retry_stage");
            Assert.Contains(rows, row => row.GetProperty("mutation_type").GetString() == "resume_run");
        }

        var hotspotsResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "query",
                "hotspots",
                "--dir", runDir,
                "--json"
            ],
            workingDirectory: runDir);
        Assert.Equal(0, hotspotsResult.ExitCode);
        using (var hotspots = JsonDocument.Parse(hotspotsResult.Stdout))
        {
            var rows = hotspots.RootElement.GetProperty("rows").EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(rows, row =>
                row.GetProperty("node_id").GetString() == "finalize" &&
                row.GetProperty("attempt_count").GetInt64() == 2);
        }

        var leasesResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "query",
                "leases",
                "--dir", runDir,
                "--status", "released",
                "--json"
            ],
            workingDirectory: runDir);
        Assert.Equal(0, leasesResult.ExitCode);
        using (var leases = JsonDocument.Parse(leasesResult.Stdout))
        {
            var rows = leases.RootElement.GetProperty("rows").EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(rows, row => row.GetProperty("state").GetString() == "released");
        }
    }

    [Fact]
    public async Task ProcessRun_ControlExpectedVersion_RejectsStaleMutationAndAllowsFreshResume()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("control_expected_version");
        var runDir = workspace.WorkingDir("control-expected-version");
        var dotPath = WriteDot(
            workspace,
            "control-expected-version.dot",
            """
            digraph G {
                goal = "Exercise expected-version guards on control-plane mutations."

                start [shape=Mdiamond]
                draft [shape=box, label="Draft", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                finalize [shape=box, label="Finalize", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the final stage completed successfully."]
                done [shape=Msquare]

                start -> draft
                draft -> finalize
                finalize -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "control-expected-version-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(status: "fail", notes: "finalize failed") }]
                }
            });

        var initialResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(1, initialResult.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(initialResult.ResultPath), "status"));

        var initialManifest = ProcessRunHarness.ReadJsonObject(initialResult.ManifestPath);
        var initialVersion = long.Parse(ReadString(initialManifest, "state_version")!);

        ProcessRunHarness.WritePlan(
            workspace,
            "control-expected-version-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize recovered") }]
                }
            });

        var retryResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "retry-stage",
                "--dir", runDir,
                "--node", "finalize",
                "--actor", "scenario-tester",
                "--reason", "Retry finalize using a guarded expected version.",
                "--source", "scenario-test",
                "--expected-version", initialVersion.ToString()
            ],
            workingDirectory: runDir);

        Assert.Equal(0, retryResult.ExitCode);

        var retryManifest = ProcessRunHarness.ReadJsonObject(initialResult.ManifestPath);
        var retryVersion = long.Parse(ReadString(retryManifest, "state_version")!);
        Assert.True(retryVersion > initialVersion);

        var staleRetryResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "retry-stage",
                "--dir", runDir,
                "--node", "finalize",
                "--actor", "scenario-tester",
                "--reason", "This retry should be rejected because the version is stale.",
                "--source", "scenario-test",
                "--expected-version", initialVersion.ToString()
            ],
            workingDirectory: runDir);

        Assert.Equal(2, staleRetryResult.ExitCode);
        Assert.Contains($"Expected run '", staleRetryResult.Stderr);
        Assert.Contains($"current version is {retryVersion}", staleRetryResult.Stderr, StringComparison.OrdinalIgnoreCase);

        var resumeResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "resume",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Resume after the guarded retry succeeded.",
                "--source", "scenario-test",
                "--expected-version", retryVersion.ToString()
            ],
            workingDirectory: runDir);

        Assert.Equal(0, resumeResult.ExitCode);

        await ProcessRunHarness.WaitForResultStatusAsync(
            initialResult.ResultPath,
            "success",
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        var completedManifest = ProcessRunHarness.ReadJsonObject(initialResult.ManifestPath);
        Assert.Equal("completed", ReadString(completedManifest, "status"));
        Assert.True(long.Parse(ReadString(completedManifest, "state_version")!) > retryVersion);

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal(long.Parse(ReadString(completedManifest, "state_version")!), await WaitForSqliteInt64Async(
            dbPath,
            "SELECT state_version FROM runs LIMIT 1;",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_RoutingAndScorecards_ProjectEffectiveModelsAcrossJsonAndSqlite()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("routing_scorecards");
        var runDir = workspace.WorkingDir("routing-scorecards");
        var dotPath = WriteDot(
            workspace,
            "routing-scorecards.dot",
            """
            digraph G {
                goal = "Exercise model routing and scorecard projections."

                start [shape=Mdiamond]
                draft [shape=box, label="Draft", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                finalize [shape=box, label="Finalize", provider="openai",
                    preferred_model="gpt-5.4", fallback_models="gpt-5.2", max_expected_latency_ms="500",
                    prompt="Return valid stage status JSON indicating the final stage completed successfully."]
                done [shape=Msquare]

                start -> draft
                draft -> finalize
                finalize -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "routing-scorecards-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete"), DelayMs = 40 }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize complete"), DelayMs = 10 }]
                }
            });

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var draftStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(runDir, "logs", "draft", "status.json"));
        var finalizeStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(runDir, "logs", "finalize", "status.json"));
        Assert.Equal("gpt-5.4", ReadString(draftStatus, "model"));
        Assert.Equal("gpt-5.2", ReadString(finalizeStatus, "model"));

        var scorecardsPath = Path.Combine(runDir, "store", "model_scorecards.json");
        Assert.True(File.Exists(scorecardsPath));
        using (var scorecards = JsonDocument.Parse(File.ReadAllText(scorecardsPath)))
        {
            var entries = scorecards.RootElement.EnumerateArray().ToList();
            Assert.Contains(entries, entry =>
                entry.GetProperty("provider").GetString() == "openai" &&
                entry.GetProperty("model").GetString() == "gpt-5.4" &&
                entry.GetProperty("invocation_count").GetInt32() == 1);
            Assert.Contains(entries, entry =>
                entry.GetProperty("provider").GetString() == "openai" &&
                entry.GetProperty("model").GetString() == "gpt-5.2" &&
                entry.GetProperty("invocation_count").GetInt32() == 1);
        }

        var scorecardResult = await ProcessRunHarness.RunRunnerAsync(
            ["scorecard", "--dir", runDir, "--json"],
            workingDirectory: runDir);

        Assert.Equal(0, scorecardResult.ExitCode);
        Assert.Contains("gpt-5.2", scorecardResult.Stdout);
        Assert.Contains("gpt-5.4", scorecardResult.Stdout);

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal("2", await WaitForSqliteStringAsync(
            dbPath,
            "SELECT CAST(COUNT(*) AS TEXT) FROM model_scorecards WHERE provider = 'openai';",
            TimeSpan.FromSeconds(10)));
        Assert.Equal("gpt-5.2", await WaitForSqliteStringAsync(
            dbPath,
            "SELECT model FROM provider_invocations WHERE node_id = 'finalize' LIMIT 1;",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_ArtifactLineage_TracksPromptInputsAndSupersededVersionsAcrossRetry()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("artifact_lineage");
        var runDir = workspace.WorkingDir("artifact-lineage");
        var dotPath = WriteDot(
            workspace,
            "artifact-lineage.dot",
            """
            digraph G {
                goal = "Exercise artifact lineage and superseded versions."

                start [shape=Mdiamond]
                draft [shape=box, label="Draft", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                finalize [shape=box, label="Finalize", provider="openai", model="gpt-5.4",
                    prompt="Read logs/draft/status.json before returning valid stage status JSON indicating the final stage completed successfully."]
                done [shape=Msquare]

                start -> draft
                draft -> finalize
                finalize -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "artifact-lineage-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(status: "fail", notes: "finalize failed") }]
                }
            });

        var initialResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(1, initialResult.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(initialResult.ResultPath), "status"));

        ProcessRunHarness.WritePlan(
            workspace,
            "artifact-lineage-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize recovered") }]
                }
            });

        var retryResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "retry-stage",
                "--dir", runDir,
                "--node", "finalize",
                "--actor", "scenario-tester",
                "--reason", "Retry finalize so lineage captures the superseded artifact version.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, retryResult.ExitCode);

        var resumeResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "resume",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Resume finalize after retry staging.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, resumeResult.ExitCode);

        await ProcessRunHarness.WaitForResultStatusAsync(
            initialResult.ResultPath,
            "success",
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        using var versionsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "store", "artifact_versions.json")));
        var versions = versionsDocument.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();

        var draftStatusVersion = versions.Single(item =>
            item.GetProperty("logical_path").GetString() == "logs/draft/status.json" &&
            item.GetProperty("is_default").GetBoolean());
        var finalizeStatusVersions = versions
            .Where(item => item.GetProperty("logical_path").GetString() == "logs/finalize/status.json")
            .ToList();

        Assert.Equal(2, finalizeStatusVersions.Count);

        var currentFinalizeVersion = finalizeStatusVersions.Single(item => item.GetProperty("is_default").GetBoolean());
        var previousFinalizeVersion = finalizeStatusVersions.Single(item => !item.GetProperty("is_default").GetBoolean());

        Assert.Equal("openai", currentFinalizeVersion.GetProperty("producer_provider").GetString());
        Assert.Equal("gpt-5.4", currentFinalizeVersion.GetProperty("producer_model").GetString());
        Assert.Equal("logs/finalize/prompt.md", currentFinalizeVersion.GetProperty("prompt_path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(currentFinalizeVersion.GetProperty("prompt_sha256").GetString()));
        Assert.Equal(
            previousFinalizeVersion.GetProperty("artifact_version_id").GetString(),
            currentFinalizeVersion.GetProperty("supersedes_artifact_version_id").GetString());
        Assert.Contains(
            draftStatusVersion.GetProperty("artifact_version_id").GetString(),
            currentFinalizeVersion.GetProperty("input_artifact_version_ids").EnumerateArray().Select(item => item.GetString()));

        using var lineageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "store", "artifact_lineage.json")));
        var lineage = lineageDocument.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
        var currentFinalizeVersionId = currentFinalizeVersion.GetProperty("artifact_version_id").GetString();
        var draftStatusVersionId = draftStatusVersion.GetProperty("artifact_version_id").GetString();
        var previousFinalizeVersionId = previousFinalizeVersion.GetProperty("artifact_version_id").GetString();

        Assert.Contains(lineage, item =>
            item.GetProperty("artifact_version_id").GetString() == currentFinalizeVersionId &&
            item.GetProperty("relation_type").GetString() == "input" &&
            item.GetProperty("related_artifact_version_id").GetString() == draftStatusVersionId);
        Assert.Contains(lineage, item =>
            item.GetProperty("artifact_version_id").GetString() == currentFinalizeVersionId &&
            item.GetProperty("relation_type").GetString() == "supersedes" &&
            item.GetProperty("related_artifact_version_id").GetString() == previousFinalizeVersionId);

        var lineageResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "lineage",
                "--dir", runDir,
                "logs/finalize/status.json",
                "--json"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, lineageResult.ExitCode);
        using (var lineageResultDocument = JsonDocument.Parse(lineageResult.Stdout))
        {
            var lineageEntries = lineageResultDocument.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(lineageEntries, item =>
                item.GetProperty("relation_type").GetString() == "input" &&
                item.GetProperty("related_logical_path").GetString() == "logs/draft/status.json");
            Assert.Contains(lineageEntries, item =>
                item.GetProperty("relation_type").GetString() == "supersedes" &&
                item.GetProperty("related_artifact_version_id").GetString() == previousFinalizeVersionId);
        }

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            $"SELECT COUNT(*) FROM artifact_lineage WHERE artifact_version_id = '{currentFinalizeVersionId}' AND relation_type = 'input' AND related_logical_path = 'logs/draft/status.json';",
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            $"SELECT COUNT(*) FROM artifact_lineage WHERE artifact_version_id = '{currentFinalizeVersionId}' AND relation_type = 'supersedes' AND related_artifact_version_id = '{previousFinalizeVersionId}';",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_ControlForceAdvanceAndResume_SkipsBlockedGate()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("force_advance");
        var runDir = workspace.WorkingDir("force-advance");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "force-advance-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize should be skipped") }]
                }
            });

        var runTask = ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                Path.Combine(ProcessRunHarness.DotfilesRoot, "qa-reliability-gap-closure.dot"),
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(20));

        await ProcessRunHarness.WaitForPendingGateAsync(runDir, TimeSpan.FromSeconds(20), CancellationToken.None);

        var cancelResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "cancel",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Cancel before force-advance.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, cancelResult.ExitCode);

        var cancelledRun = await runTask;
        Assert.Equal(130, cancelledRun.ExitCode);
        Assert.Equal("cancelled", ReadString(ProcessRunHarness.ReadJsonObject(cancelledRun.ResultPath), "status"));

        var forceAdvanceResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "force-advance",
                "--dir", runDir,
                "--to-node", "done",
                "--actor", "scenario-tester",
                "--reason", "Skip the blocked review gate and finish the run.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, forceAdvanceResult.ExitCode);

        var resumeResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "resume",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Resume from the force-advanced checkpoint.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, resumeResult.ExitCode);

        await ProcessRunHarness.WaitForResultStatusAsync(
            cancelledRun.ResultPath,
            "success",
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(runDir, "logs", "finalize", "status.json")));

        using (var replay = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "store", "replay.json"))))
        {
            var events = replay.RootElement.GetProperty("events").EnumerateArray().Select(item => item.Clone()).ToList();
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "operator_force_advanced");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "run_resume_requested");
            Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "run_finished");
        }

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM replay_events WHERE event_type = 'operator_force_advanced';",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_ArtifactPromoteAndRollback_SwitchesDefaultVersionAcrossProjectionStores()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("artifact_promotion");
        var runDir = workspace.WorkingDir("artifact-promotion");
        var dotPath = WriteDot(
            workspace,
            "artifact-promotion.dot",
            """
            digraph G {
                goal = "Exercise artifact promotion and rollback."

                start [shape=Mdiamond]
                draft [shape=box, label="Draft", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                finalize [shape=box, label="Finalize", provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the final stage completed successfully."]
                done [shape=Msquare]

                start -> draft
                draft -> finalize
                finalize -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "artifact-promotion-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(status: "fail", notes: "finalize failed") }]
                }
            });

        var initialRunResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(1, initialRunResult.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(initialRunResult.ResultPath), "status"));

        ProcessRunHarness.WritePlan(
            workspace,
            "artifact-promotion-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "finalize recovered") }]
                }
            });

        var retryResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "retry-stage",
                "--dir", runDir,
                "--node", "finalize",
                "--actor", "scenario-tester",
                "--reason", "Retry finalize before artifact promotion review.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, retryResult.ExitCode);

        var resumeResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "resume",
                "--dir", runDir,
                "--actor", "scenario-tester",
                "--reason", "Resume finalized run for artifact promotion review.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, resumeResult.ExitCode);

        await ProcessRunHarness.WaitForResultStatusAsync(
            initialRunResult.ResultPath,
            "success",
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        var listResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "list",
                "--dir", runDir,
                "--json"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, listResult.ExitCode);

        var initialListing = ReadArtifactListing(listResult.Stdout);
        var statusArtifact = initialListing.Artifacts.Single(item =>
            item.GetProperty("logical_path").GetString() == "logs/finalize/status.json");
        var artifactId = statusArtifact.GetProperty("artifact_id").GetString()!;
        var currentVersionId = statusArtifact.GetProperty("current_version_id").GetString()!;
        var statusVersions = initialListing.Versions
            .Where(item => item.GetProperty("artifact_id").GetString() == artifactId)
            .OrderBy(item => item.GetProperty("relative_path").GetString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(2, statusVersions.Count);
        Assert.Contains(statusVersions, item =>
            item.GetProperty("relative_path").GetString() == "logs/finalize/status.json" &&
            item.GetProperty("is_default").GetBoolean());
        Assert.Contains(statusVersions, item =>
            item.GetProperty("relative_path").GetString()!.StartsWith("logs/finalize/status.previous.", StringComparison.Ordinal) &&
            !item.GetProperty("is_default").GetBoolean());

        var priorVersionId = statusVersions
            .Select(item => item.GetProperty("artifact_version_id").GetString())
            .First(versionId => !string.Equals(versionId, currentVersionId, StringComparison.Ordinal))!;

        var promoteResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "promote",
                "--dir", runDir,
                "logs/finalize/status.json",
                priorVersionId,
                "--actor", "scenario-tester",
                "--reason", "Promote the first status artifact revision.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, promoteResult.ExitCode);

        var promotedListingResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "list",
                "--dir", runDir,
                "--json"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, promotedListingResult.ExitCode);

        var promotedListing = ReadArtifactListing(promotedListingResult.Stdout);
        var promotedArtifact = promotedListing.Artifacts.Single(item =>
            item.GetProperty("artifact_id").GetString() == artifactId);
        Assert.Equal(priorVersionId, promotedArtifact.GetProperty("current_version_id").GetString());
        Assert.Equal("scenario-tester", promotedArtifact.GetProperty("actor").GetString());
        Assert.Equal("Promote the first status artifact revision.", promotedArtifact.GetProperty("rationale").GetString());
        Assert.Equal("scenario-test", promotedArtifact.GetProperty("source").GetString());

        var rollbackResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "rollback",
                "--dir", runDir,
                "logs/finalize/status.json",
                "--actor", "scenario-tester",
                "--reason", "Rollback to the latest status artifact revision.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, rollbackResult.ExitCode);

        var rolledBackListingResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "list",
                "--dir", runDir,
                "--json"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, rolledBackListingResult.ExitCode);

        var rolledBackListing = ReadArtifactListing(rolledBackListingResult.Stdout);
        var rolledBackArtifact = rolledBackListing.Artifacts.Single(item =>
            item.GetProperty("artifact_id").GetString() == artifactId);
        Assert.Equal(currentVersionId, rolledBackArtifact.GetProperty("current_version_id").GetString());

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal(2, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM artifact_promotions;",
            TimeSpan.FromSeconds(10)));
        Assert.Equal("rollback", await WaitForSqliteStringAsync(
            dbPath,
            "SELECT action FROM artifact_promotions ORDER BY timestamp_utc DESC LIMIT 1;",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_DryRunPreview_ReportsControlPolicyAndNodeModalities_WithoutExecuting()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("dry_run_preview");
        var runDir = workspace.WorkingDir("dry-run-preview");
        var dotPath = WriteDot(
            workspace,
            "dry-run-preview.dot",
            """
            digraph G {
                goal = "Preview the workflow without executing providers."
                allow_force_advance = "false"
                operator_retry_budget = "2"

                start [shape=Mdiamond]
                draft [shape=box, provider="openai", model="gpt-5.4",
                    execution_lane="multimodal_leaf",
                    output_modalities="text,image",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                done [shape=Msquare]

                start -> draft
                draft -> done
            }
            """);

        var dryRunResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--resume-from", runDir,
                "--dry-run",
                "--json"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, dryRunResult.ExitCode);

        using var preview = JsonDocument.Parse(dryRunResult.Stdout);
        Assert.False(preview.RootElement.GetProperty("control_policy").GetProperty("allow_force_advance").GetBoolean());
        Assert.Equal(2, preview.RootElement.GetProperty("control_policy").GetProperty("operator_retry_budget").GetInt32());

        var node = preview.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "draft");
        Assert.Equal("multimodal_leaf", node.GetProperty("execution_lane").GetString());
        Assert.Contains(
            node.GetProperty("output_modalities").EnumerateArray().Select(item => item.GetString()),
            value => value == "image");

        Assert.False(File.Exists(Path.Combine(runDir, "run-manifest.json")));
        Assert.False(File.Exists(Path.Combine(runDir, "logs", "result.json")));
    }

    [Fact]
    public async Task ProcessRun_ControlPolicy_DeniesRetryAndForceAdvance_WhenWorkflowDisallowsThem()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("control_policy_denials");
        var runDir = workspace.WorkingDir("control-policy-denials");
        var dotPath = WriteDot(
            workspace,
            "control-policy-denials.dot",
            """
            digraph G {
                goal = "Exercise policy-denied operator mutations."
                allow_force_advance = "false"

                start [shape=Mdiamond]
                draft [shape=box, provider="openai", model="gpt-5.4",
                    prompt="Return valid stage status JSON indicating the draft stage completed successfully."]
                finalize [shape=box, provider="openai", model="gpt-5.4",
                    allow_operator_retry="false",
                    prompt="Return valid stage status JSON indicating the final stage failed."]
                done [shape=Msquare]

                start -> draft
                draft -> finalize
                finalize -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "control-policy-denials-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["draft"] = [new ScriptedResponsePlan { AssistantText = StatusJson(notes: "draft complete") }],
                    ["finalize"] = [new ScriptedResponsePlan { AssistantText = StatusJson(status: "fail", notes: "finalize failed") }]
                }
            });

        var initialRun = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(1, initialRun.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(initialRun.ResultPath), "status"));

        var retryResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "retry-stage",
                "--dir", runDir,
                "--node", "finalize",
                "--actor", "scenario-tester",
                "--reason", "Retry should be denied by workflow policy.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.NotEqual(0, retryResult.ExitCode);
        Assert.Contains("not eligible for operator-triggered retry", retryResult.Stderr, StringComparison.OrdinalIgnoreCase);

        var forceAdvanceResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "control",
                "force-advance",
                "--dir", runDir,
                "--to-node", "done",
                "--actor", "scenario-tester",
                "--reason", "Force advance should be denied by workflow policy.",
                "--source", "scenario-test"
            ],
            workingDirectory: runDir);

        Assert.NotEqual(0, forceAdvanceResult.ExitCode);
        Assert.Contains("Force-advance is disabled", forceAdvanceResult.Stderr, StringComparison.OrdinalIgnoreCase);

        var events = File.ReadAllLines(Path.Combine(runDir, "logs", "events.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
        Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "operator_retry_stage_denied");
        Assert.Contains(events, evt => evt.GetProperty("event_type").GetString() == "operator_force_advance_denied");

        var dbPath = Path.Combine(runDir, "store", "workflow.sqlite");
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM run_events WHERE event_type = 'operator_retry_stage_denied';",
            TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await WaitForSqliteInt64Async(
            dbPath,
            "SELECT COUNT(*) FROM run_events WHERE event_type = 'operator_force_advance_denied';",
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessRun_MultimodalLeafLane_PersistsGeneratedImages_AndPreservesImageContinuity()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("multimodal_leaf");
        var runDir = workspace.WorkingDir("multimodal-leaf");
        var referenceImagePath = Path.Combine(workspace.Root, "reference.png");
        File.WriteAllBytes(referenceImagePath, Convert.FromBase64String(UnifiedLlmTestAssets.TestImageBase64));
        var dotPath = WriteDot(
            workspace,
            "multimodal-leaf.dot",
            """
            digraph G {
                goal = "Exercise multimodal leaf continuity over a shared thread."

                start [shape=Mdiamond]
                generate [shape=box, provider="openai", model="gpt-5.4",
                    execution_lane="multimodal_leaf", output_modalities="text,image",
                    input_images="$reference_image",
                    thread_id="visual-thread", fidelity="full",
                    prompt="Return valid stage status JSON indicating the visual draft completed successfully."]
                refine [shape=box, provider="openai", model="gpt-5.4",
                    execution_lane="multimodal_leaf",
                    thread_id="visual-thread", fidelity="full",
                    prompt="Return valid stage status JSON indicating the visual refinement completed successfully."]
                done [shape=Msquare]

                start -> generate
                generate -> refine
                refine -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "multimodal-leaf-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["generate"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "visual draft complete"),
                            ExpectedOutputModalities = ["text", "image"],
                            ExpectedRequestImageCount = 1,
                            ImageBase64 = UnifiedLlmTestAssets.TestImageBase64,
                            ImageMediaType = "image/png",
                            ImageProviderToken = "visual-token"
                        }
                    ],
                    ["refine"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "visual refinement complete"),
                            ExpectedRequestImageCount = 2,
                            ExpectedImageProviderToken = "visual-token"
                        }
                    ]
                }
            });

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--var", $"reference_image={referenceImagePath}"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var generatedImagePath = Path.Combine(runDir, "logs", "generate", "generated", "image-1.png");
        Assert.True(File.Exists(generatedImagePath));

        var generateStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(runDir, "logs", "generate", "status.json"));
        var refineStatus = ProcessRunHarness.ReadJsonObject(Path.Combine(runDir, "logs", "refine", "status.json"));
        Assert.Equal("1", ReadNestedString(generateStatus, "telemetry", "assistant_image_count"));
        Assert.Equal("success", ReadString(refineStatus, "status"));

        var artifactListResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "artifact",
                "list",
                "--dir", runDir,
                "--json"
            ],
            workingDirectory: runDir);

        Assert.Equal(0, artifactListResult.ExitCode);
        var listing = ReadArtifactListing(artifactListResult.Stdout);
        Assert.Contains(listing.Artifacts, item =>
            item.GetProperty("logical_path").GetString() == "logs/generate/generated/image-1.png");
    }

    [Fact]
    public async Task ProcessRun_AutoresumeAlways_RepeatedCrashInjection_CompletesAfterMultipleRespawns()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("autoresume_repeat");
        var runDir = workspace.WorkingDir("autoresume-repeat");
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "autoresume-repeat-plan.json",
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
                "--crash-after-stage", "crash_point",
                "--crash-after-stage-count", "2"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(result.ResultPath), "status"));

        var manifest = ProcessRunHarness.ReadJsonObject(result.ManifestPath);
        Assert.Equal("completed", ReadString(manifest, "status"));
        Assert.True(long.Parse(ReadString(manifest, "respawn_count")!) >= 2);
        Assert.Equal("0", ReadString(manifest, "crash_injections_remaining"));

        var events = ProcessRunHarness.ReadEvents(Path.Combine(runDir, "logs", "events.jsonl"));
        Assert.Equal(2, events.Count(evt => ReadString(evt, "event_type") == "run_crashed"));
    }

    [Fact]
    public async Task ProcessRun_LeafLane_AttachesDocumentAndAudioInputs()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("leaf_attachments");
        var runDir = workspace.WorkingDir("leaf-attachments");
        var documentPath = Path.Combine(workspace.Root, "brief.md");
        var audioPath = Path.Combine(workspace.Root, "notes.mp3");
        File.WriteAllText(documentPath, "# Brief\n\nReview the proposed launch plan.");
        File.WriteAllBytes(audioPath, [0x49, 0x44, 0x33, 0x03]);

        var dotPath = WriteDot(
            workspace,
            "leaf-attachments.dot",
            """
            digraph G {
                goal = "Exercise leaf-mode document and audio attachments."

                start [shape=Mdiamond]
                review [shape=box, provider="gemini", model="gemini-2.5-pro",
                    execution_lane="leaf",
                    input_documents="$reference_document",
                    input_audio="$reference_audio",
                    prompt="Return valid stage status JSON indicating the review completed successfully."]
                done [shape=Msquare]

                start -> review
                review -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "leaf-attachments-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["review"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "attachment review complete"),
                            ExpectedRequestDocumentCount = 1,
                            ExpectedRequestAudioCount = 1
                        }
                    ]
                }
            });

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--var", $"reference_document={documentPath}",
                "--var", $"reference_audio={audioPath}"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var reviewStatusPath = Path.Combine(runDir, "logs", "review", "status.json");
        using var reviewStatus = JsonDocument.Parse(File.ReadAllText(reviewStatusPath));
        Assert.Equal(1, reviewStatus.RootElement.GetProperty("telemetry").GetProperty("attached_document_count").GetInt32());
        Assert.Equal(1, reviewStatus.RootElement.GetProperty("telemetry").GetProperty("attached_audio_count").GetInt32());

        var effectivePolicy = reviewStatus.RootElement.GetProperty("effective_policy");
        Assert.Equal(documentPath, effectivePolicy.GetProperty("input_document_paths")[0].GetString());
        Assert.Equal(audioPath, effectivePolicy.GetProperty("input_audio_paths")[0].GetString());
    }

    [Fact]
    public async Task ProcessRun_LeafLane_OpenAIDocumentInput_SucceedsOnDocumentCapableModel()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("openai_leaf_document");
        var runDir = workspace.WorkingDir("openai-leaf-document");
        var documentPath = Path.Combine(workspace.Root, "brief.pdf");
        File.WriteAllBytes(documentPath, [0x25, 0x50, 0x44, 0x46]);

        var dotPath = WriteDot(
            workspace,
            "openai-leaf-document.dot",
            """
            digraph G {
                goal = "Exercise OpenAI document-capable leaf execution."

                start [shape=Mdiamond]
                review [shape=box, provider="openai", model="gpt-5.4",
                    execution_lane="leaf",
                    input_documents="$reference_document",
                    prompt="Return valid stage status JSON indicating the review completed successfully."]
                done [shape=Msquare]

                start -> review
                review -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "openai-leaf-document-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["review"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "openai document review complete"),
                            ExpectedRequestDocumentCount = 1,
                            ExpectedRequestAudioCount = 0
                        }
                    ]
                }
            });

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--var", $"reference_document={documentPath}"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var reviewStatusPath = Path.Combine(runDir, "logs", "review", "status.json");
        using var reviewStatus = JsonDocument.Parse(File.ReadAllText(reviewStatusPath));
        Assert.Equal("openai", reviewStatus.RootElement.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.4", reviewStatus.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, reviewStatus.RootElement.GetProperty("telemetry").GetProperty("attached_document_count").GetInt32());
    }

    [Fact]
    public async Task ProcessRun_LeafLane_RoutesOpenAIAudioInput_ToGptAudio()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("openai_leaf_audio");
        var runDir = workspace.WorkingDir("openai-leaf-audio");
        var audioPath = Path.Combine(workspace.Root, "notes.mp3");
        File.WriteAllBytes(audioPath, [0x49, 0x44, 0x33, 0x03]);

        var dotPath = WriteDot(
            workspace,
            "openai-leaf-audio.dot",
            """
            digraph G {
                goal = "Exercise OpenAI audio routing in leaf execution."

                start [shape=Mdiamond]
                review [shape=box, provider="openai",
                    execution_lane="leaf",
                    input_audio="$reference_audio",
                    prompt="Return valid stage status JSON indicating the review completed successfully."]
                done [shape=Msquare]

                start -> review
                review -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "openai-leaf-audio-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["review"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "openai audio review complete"),
                            ExpectedRequestDocumentCount = 0,
                            ExpectedRequestAudioCount = 1
                        }
                    ]
                }
            });

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--var", $"reference_audio={audioPath}"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var reviewStatusPath = Path.Combine(runDir, "logs", "review", "status.json");
        using var reviewStatus = JsonDocument.Parse(File.ReadAllText(reviewStatusPath));
        Assert.Equal("openai", reviewStatus.RootElement.GetProperty("provider").GetString());
        Assert.Equal("gpt-audio", reviewStatus.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, reviewStatus.RootElement.GetProperty("telemetry").GetProperty("attached_audio_count").GetInt32());
    }

    [Fact]
    public async Task ProcessRun_LeafLane_AnthropicDocumentInput_SucceedsOnDocumentCapableModel()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("anthropic_leaf_document");
        var runDir = workspace.WorkingDir("anthropic-leaf-document");
        var documentPath = Path.Combine(workspace.Root, "brief.pdf");
        File.WriteAllBytes(documentPath, [0x25, 0x50, 0x44, 0x46]);

        var dotPath = WriteDot(
            workspace,
            "anthropic-leaf-document.dot",
            """
            digraph G {
                goal = "Exercise Anthropic document-capable leaf execution."

                start [shape=Mdiamond]
                review [shape=box, provider="anthropic", model="claude-opus-4-6",
                    execution_lane="leaf",
                    input_documents="$reference_document",
                    prompt="Return valid stage status JSON indicating the review completed successfully."]
                done [shape=Msquare]

                start -> review
                review -> done
            }
            """);
        var planPath = ProcessRunHarness.WritePlan(
            workspace,
            "anthropic-leaf-document-plan.json",
            new ScriptedBackendPlan
            {
                Nodes = new Dictionary<string, List<ScriptedResponsePlan>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["review"] =
                    [
                        new ScriptedResponsePlan
                        {
                            AssistantText = StatusJson(notes: "anthropic document review complete"),
                            ExpectedRequestDocumentCount = 1,
                            ExpectedRequestAudioCount = 0
                        }
                    ]
                }
            });

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--backend", "scripted",
                "--backend-script", planPath,
                "--resume-from", runDir,
                "--var", $"reference_document={documentPath}"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal("success", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var reviewStatusPath = Path.Combine(runDir, "logs", "review", "status.json");
        using var reviewStatus = JsonDocument.Parse(File.ReadAllText(reviewStatusPath));
        Assert.Equal("anthropic", reviewStatus.RootElement.GetProperty("provider").GetString());
        Assert.Equal("claude-opus-4-6", reviewStatus.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, reviewStatus.RootElement.GetProperty("telemetry").GetProperty("attached_document_count").GetInt32());
    }

    [Fact]
    public async Task ProcessRun_LeafLane_AnthropicAudioInput_FailsClosedBeforeProviderExecution()
    {
        using var workspace = ProcessRunHarness.CreateWorkspace("anthropic_leaf_audio_invalid");
        var runDir = workspace.WorkingDir("anthropic-leaf-audio-invalid");
        var audioPath = Path.Combine(workspace.Root, "notes.mp3");
        File.WriteAllBytes(audioPath, [0x49, 0x44, 0x33, 0x03]);

        var dotPath = WriteDot(
            workspace,
            "anthropic-leaf-audio-invalid.dot",
            """
            digraph G {
                goal = "Exercise Anthropic audio fail-closed validation."

                start [shape=Mdiamond]
                review [shape=box, provider="anthropic", model="claude-opus-4-6",
                    execution_lane="leaf",
                    input_audio="$reference_audio",
                    prompt="Return valid stage status JSON indicating the review completed successfully."]
                done [shape=Msquare]

                start -> review
                review -> done
            }
            """);

        var runResult = await ProcessRunHarness.RunRunnerAsync(
            [
                "run",
                dotPath,
                "--resume-from", runDir,
                "--var", $"reference_audio={audioPath}"
            ],
            workingDirectory: runDir,
            completionTimeout: TimeSpan.FromSeconds(15));

        Assert.Equal(1, runResult.ExitCode);
        Assert.Equal("fail", ReadString(ProcessRunHarness.ReadJsonObject(runResult.ResultPath), "status"));

        var reviewStatusPath = Path.Combine(runDir, "logs", "review", "status.json");
        using var reviewStatus = JsonDocument.Parse(File.ReadAllText(reviewStatusPath));
        Assert.Equal("anthropic", reviewStatus.RootElement.GetProperty("provider").GetString());
        Assert.Equal("claude-opus-4-6", reviewStatus.RootElement.GetProperty("model").GetString());
        Assert.Equal("capability_validation", reviewStatus.RootElement.GetProperty("failure_kind").GetString());
        Assert.Equal("invalid_capability", reviewStatus.RootElement.GetProperty("provider_state").GetString());
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

    private static string WriteDot(ProcessRunWorkspace workspace, string fileName, string content)
    {
        var path = Path.Combine(workspace.Root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<string?> WaitForSqliteStringAsync(string dbPath, string sql, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(dbPath))
            {
                try
                {
                    using var connection = new SqliteConnection($"Data Source={dbPath}");
                    await connection.OpenAsync();
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    var value = await command.ExecuteScalarAsync();
                    if (value is not null && value is not DBNull)
                        return value.ToString();
                }
                catch
                {
                    // Retry until the database is stable.
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for SQLite scalar from '{dbPath}'.");
    }

    private static async Task<long> WaitForSqliteInt64Async(string dbPath, string sql, TimeSpan timeout)
    {
        var value = await WaitForSqliteStringAsync(dbPath, sql, timeout);
        return long.Parse(value!);
    }

    private static (List<JsonElement> Artifacts, List<JsonElement> Versions) ReadArtifactListing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var artifacts = document.RootElement.GetProperty("artifacts").EnumerateArray().Select(item => item.Clone()).ToList();
        var versions = document.RootElement.GetProperty("versions").EnumerateArray().Select(item => item.Clone()).ToList();
        return (artifacts, versions);
    }
}
