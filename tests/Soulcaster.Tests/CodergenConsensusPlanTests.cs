using System.Text.Json;
using Soulcaster.Attractor;

namespace Soulcaster.Tests;

public class CodergenConsensusPlanTests
{
    [Fact]
    public async Task PipelineEngine_RetryWithNoRetryBudget_TerminatesWithFail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_retrybudget_{Guid.NewGuid():N}");

        try
        {
            var engine = new PipelineEngine(new PipelineConfig(
                LogsRoot: tempDir,
                Backend: new AlwaysRetryBackend()));

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode { Id = "work", Shape = "box", Prompt = "do work" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Fail, result.Status);
            Assert.Equal(OutcomeStatus.Fail, result.NodeOutcomes["work"].Status);

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, "work", "status.json")));
            Assert.Equal("fail", statusDocument.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void StageStatusContract_ToStatusJson_FallbackSetsContractValidatedFalse()
    {
        var contract = new StageStatusContract(
            Status: OutcomeStatus.Success,
            PreferredNextLabel: string.Empty,
            SuggestedNextIds: new List<string>(),
            ContextUpdates: new Dictionary<string, string>(),
            Notes: "ok");

        var statusJson = contract.ToStatusJson(
            nodeId: "coder",
            model: "gpt-5.4",
            provider: "openai",
            usedFallback: true,
            validationError: "Missing structured stage status output.");

        Assert.False((bool)statusJson["contract_validated"]!);
        Assert.True((bool)statusJson["used_fallback"]!);
    }

    [Fact]
    public void StageStatusContract_ResolveContractState_UsesProviderErrorForUpstreamFailures()
    {
        var contractState = StageStatusContract.ResolveContractState(
            usedFallback: true,
            reason: "Invalid JSON: upstream provider returned plain text.",
            providerState: "rate_limited");

        Assert.Equal("provider_error", contractState);
    }

    [Fact]
    public void RuntimeDurationParser_SupportsMillisecondsAndSuffixFormats()
    {
        Assert.True(RuntimeDurationParser.TryParseMilliseconds("1500", out var rawMilliseconds));
        Assert.Equal(1500, rawMilliseconds);

        Assert.True(RuntimeDurationParser.TryParseMilliseconds("600s", out var secondsMilliseconds));
        Assert.Equal(600_000, secondsMilliseconds);

        Assert.True(RuntimeDurationParser.TryParseTimeout("10m", out var timeout));
        Assert.Equal(TimeSpan.FromMinutes(10), timeout);
    }

    [Fact]
    public async Task CodergenHandler_NodeTimeoutPolicy_PropagatesToBackendOptions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_timeoutpolicy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend();
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "timeout-policy" };
            var node = new GraphNode
            {
                Id = "coder",
                Shape = "box",
                Prompt = "Return stage status JSON.",
                Timeout = "600s"
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Single(backend.Invocations);
            Assert.Equal(600_000, backend.Invocations[0].Options?.MaxProviderResponseMs);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_PromotesPreferredLabelToOutcomeContext_WhenRoutingUsesOutcomeConditions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_routeoutcome_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend()
                .On("router", _ => DeterministicBackend.Result(preferredNextLabel: "needs_dod", notes: "route to DoD"));
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "route-outcome" };
            var node = new GraphNode
            {
                Id = "router",
                Shape = "box",
                Prompt = "Decide the next route."
            };
            graph.Nodes[node.Id] = node;
            graph.Nodes["dod"] = new GraphNode { Id = "dod", Shape = "box", Prompt = "define dod" };
            graph.Nodes["retry"] = new GraphNode { Id = "retry", Shape = "box", Prompt = "retry" };
            graph.Edges.Add(new GraphEdge { FromNode = "router", ToNode = "dod", Condition = "outcome=needs_dod" });
            graph.Edges.Add(new GraphEdge { FromNode = "router", ToNode = "retry", Condition = "outcome=retry" });

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Equal("needs_dod", outcome.PreferredLabel);
            Assert.Equal("needs_dod", outcome.ContextUpdates!["outcome"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_ImplementationNode_ZeroTouchedFiles_DowngradesToFail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_implguard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend();
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "edit-guard" };
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                Prompt = "Write the code",
                RawAttributes = new Dictionary<string, string>
                {
                    ["node_kind"] = "implementation"
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Fail, outcome.Status);

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "status.json")));
            Assert.Equal("none", statusDocument.RootElement.GetProperty("edit_state").GetString());
            Assert.False(statusDocument.RootElement.GetProperty("advance_allowed").GetBoolean());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_ExplicitRequireEdits_ZeroTouchedFiles_DowngradesToFail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_requiredits_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend();
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "require-edits" };
            var node = new GraphNode
            {
                Id = "review",
                Shape = "box",
                Prompt = "Check the implementation and report status.",
                RawAttributes = new Dictionary<string, string>
                {
                    ["require_edits"] = "true"
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Fail, outcome.Status);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_ProgressArtifactReference_DoesNotInferRequireEdits()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_progressartifact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend();
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "progress-artifact-reference" };
            var node = new GraphNode
            {
                Id = "orient",
                Shape = "box",
                Label = "Orient: Python environment",
                Prompt = """
                    Check for prior run artifacts: logs/orient/ORIENT-*.md, logs/implement/PROGRESS-*.md, logs/validate/VALIDATION-RUN-*.md.

                    Write to logs/orient/ORIENT-{N}.md.
                    """
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "status.json")));
            Assert.Equal("none", statusDocument.RootElement.GetProperty("edit_state").GetString());
            Assert.True(statusDocument.RootElement.GetProperty("advance_allowed").GetBoolean());
            Assert.False(statusDocument.RootElement.GetProperty("effective_policy").GetProperty("require_edits").GetBoolean());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_WritesRuntimeOwnedStatusArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_runtimeartifacts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend()
                .Queue("implement", DeterministicBackend.Result(
                    telemetry: new Dictionary<string, object?>
                    {
                        ["provider_state"] = "completed",
                        ["verification_state"] = "passed",
                        ["verification_commands"] = new List<object?> { "dotnet test tests/Soulcaster.Tests/Soulcaster.Tests.csproj" },
                        ["verification_errors"] = 0,
                        ["touched_files"] = new List<object?> { "src/app.cs" },
                        ["touched_files_count"] = 1
                    }));

            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "runtime-artifacts" };
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                LlmProvider = "openai",
                LlmModel = "gpt-5.4",
                Prompt = "Write the implementation",
                RawAttributes = new Dictionary<string, string>
                {
                    ["node_kind"] = "implementation",
                    ["require_verification"] = "true",
                    ["codergen_version"] = "v1"
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.True(File.Exists(Path.Combine(tempDir, node.Id, "runtime-status.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, node.Id, "provider-events.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, node.Id, "diff-summary.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, node.Id, "verification.json")));

            using var runtimeDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "runtime-status.json")));
            Assert.Equal("openai", runtimeDocument.RootElement.GetProperty("provider").GetString());
            Assert.Equal("gpt-5.4", runtimeDocument.RootElement.GetProperty("model").GetString());
            Assert.True(runtimeDocument.RootElement.GetProperty("advance_allowed").GetBoolean());
            Assert.Equal("modified", runtimeDocument.RootElement.GetProperty("edit_state").GetString());

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "status.json")));
            Assert.True(statusDocument.RootElement.GetProperty("verification_passed").GetBoolean());
            Assert.EndsWith("runtime-status.json", statusDocument.RootElement.GetProperty("runtime_status_path").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("provider-events.json", statusDocument.RootElement.GetProperty("provider_events_path").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("diff-summary.json", statusDocument.RootElement.GetProperty("diff_summary_path").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("verification.json", statusDocument.RootElement.GetProperty("verification_path").GetString(), StringComparison.Ordinal);

            using var verificationDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "verification.json")));
            Assert.Equal("passed", verificationDocument.RootElement.GetProperty("verification_state").GetString());
            Assert.True(verificationDocument.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(1, verificationDocument.RootElement.GetProperty("command_count").GetInt32());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_V2ImplementationNode_RequiresAuthoritativeValidation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_validationgate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend()
                .Queue("implement", DeterministicBackend.Result(
                    telemetry: new Dictionary<string, object?>
                    {
                        ["provider_state"] = "completed",
                        ["verification_state"] = "passed",
                        ["verification_commands"] = new List<object?> { "uv run pytest tests/unit -q" },
                        ["verification_errors"] = 0,
                        ["touched_files"] = new List<object?> { "src/app.cs" },
                        ["touched_files_count"] = 1
                    }));

            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "validation-gate" };
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                Prompt = "Write the implementation.",
                RawAttributes = new Dictionary<string, string>
                {
                    ["node_kind"] = "implementation",
                    ["codergen_version"] = "v2",
                    ["require_verification"] = "true"
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Fail, outcome.Status);

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "status.json")));
            Assert.Equal("passed", statusDocument.RootElement.GetProperty("verification_state").GetString());
            Assert.Equal("passed", statusDocument.RootElement.GetProperty("observed_verification_state").GetString());
            Assert.Equal("missing", statusDocument.RootElement.GetProperty("authoritative_validation_state").GetString());
            Assert.Equal("validation_missing", statusDocument.RootElement.GetProperty("failure_kind").GetString());
            Assert.False(statusDocument.RootElement.GetProperty("advance_allowed").GetBoolean());

            using var runtimeDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "runtime-status.json")));
            Assert.Equal("success", runtimeDocument.RootElement.GetProperty("work_segment_status").GetString());
            Assert.Equal("missing", runtimeDocument.RootElement.GetProperty("authoritative_validation_state").GetString());

            using var validationDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "validation-results.json")));
            Assert.Equal("missing", validationDocument.RootElement.GetProperty("overall_state").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_StatusJson_PreservesProviderFailureInsteadOfInvalidJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_providerstatus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            const string providerMessage = "Gemini upstream 503: This model is currently experiencing high demand.";
            var backend = new FixedCodergenBackend(new CodergenResult(
                Response: $"[Error: {providerMessage}]",
                Status: OutcomeStatus.Retry,
                RawAssistantResponse: $"[Error: {providerMessage}]",
                Telemetry: new Dictionary<string, object?>
                {
                    ["provider_state"] = "rate_limited",
                    ["failure_kind"] = "provider_rate_limit",
                    ["provider_status_code"] = 503,
                    ["provider_retryable"] = true,
                    ["provider_error_message"] = providerMessage
                }));

            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "provider-status" };
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                Prompt = "Write the implementation."
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Retry, outcome.Status);

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "status.json")));
            Assert.Equal("rate_limited", statusDocument.RootElement.GetProperty("provider_state").GetString());
            Assert.Equal("provider_error", statusDocument.RootElement.GetProperty("contract_state").GetString());
            Assert.Equal(providerMessage, statusDocument.RootElement.GetProperty("failure_reason").GetString());
            Assert.Equal(providerMessage, statusDocument.RootElement.GetProperty("provider_error_message").GetString());
            Assert.Equal(JsonValueKind.Null, statusDocument.RootElement.GetProperty("validation_error").ValueKind);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_ExplicitValidationCommands_RunInAuthoritativeValidationSegment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_validationcommand_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new DeterministicBackend()
                .Queue("implement", DeterministicBackend.Result(
                    telemetry: new Dictionary<string, object?>
                    {
                        ["provider_state"] = "completed",
                        ["touched_files"] = new List<object?> { "src/app.cs" },
                        ["touched_files_count"] = 1
                    }));

            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "validation-command" };
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                Prompt = "Write the implementation.",
                RawAttributes = new Dictionary<string, string>
                {
                    ["node_kind"] = "implementation",
                    ["codergen_version"] = "v2",
                    ["validation_mode"] = "required",
                    ["validation_commands"] = "printf 'validation ok\\n'"
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);

            using var statusDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "status.json")));
            Assert.Equal("passed", statusDocument.RootElement.GetProperty("authoritative_validation_state").GetString());
            Assert.True(statusDocument.RootElement.GetProperty("advance_allowed").GetBoolean());

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "validation-manifest.json")));
            var manifestChecks = manifestDocument.RootElement.GetProperty("checks").EnumerateArray().ToList();
            Assert.Single(manifestChecks);

            using var resultsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "validation-results.json")));
            Assert.Equal("passed", resultsDocument.RootElement.GetProperty("overall_state").GetString());
            var check = resultsDocument.RootElement.GetProperty("checks").EnumerateArray().Single();
            Assert.Equal("passed", check.GetProperty("state").GetString());
            var stdoutPath = check.GetProperty("stdout_path").GetString();
            Assert.False(string.IsNullOrWhiteSpace(stdoutPath));
            Assert.True(File.Exists(Path.Combine(tempDir, node.Id, stdoutPath!.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_StructuredValidationChecks_SupportRemainingCheckKinds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_validationstructured_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = Path.Combine(tempDir, "src", "app.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "// changed");

            var generatedPath = Path.Combine(tempDir, "out", "generated.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
            File.WriteAllText(generatedPath, "generated output");

            var jsonPath = Path.Combine(tempDir, "out", "result.json");
            File.WriteAllText(jsonPath, """{"status":"ok","items":[{"name":"first"}]}""");

            var stageDir = Path.Combine(tempDir, "implement");
            Directory.CreateDirectory(stageDir);
            File.WriteAllText(Path.Combine(stageDir, "artifact.json"), """{"built":true}""");

            var validationChecks = JsonSerializer.Serialize(new object[]
            {
                new Dictionary<string, object?> { ["kind"] = "diff", ["name"] = "source-diff", ["path"] = sourcePath },
                new Dictionary<string, object?> { ["kind"] = "file_exists", ["name"] = "generated-file", ["path"] = generatedPath },
                new Dictionary<string, object?> { ["kind"] = "file_content", ["name"] = "json-status", ["path"] = jsonPath, ["json_path"] = "status", ["expected_value_json"] = "\"ok\"" },
                new Dictionary<string, object?> { ["kind"] = "artifact", ["name"] = "stage-artifact", ["path"] = "artifact.json" },
                new Dictionary<string, object?>
                {
                    ["kind"] = "schema",
                    ["name"] = "json-schema",
                    ["path"] = jsonPath,
                    ["expected_schema_json"] = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "status", "items" },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["status"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "ok" } },
                            ["items"] = new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "object",
                                    ["required"] = new[] { "name" },
                                    ["properties"] = new Dictionary<string, object?>
                                    {
                                        ["name"] = new Dictionary<string, object?> { ["type"] = "string" }
                                    }
                                }
                            }
                        }
                    })
                }
            });

            var backend = new DeterministicBackend()
                .Queue("implement", DeterministicBackend.Result(
                    telemetry: new Dictionary<string, object?>
                    {
                        ["provider_state"] = "completed",
                        ["touched_files"] = new List<object?> { sourcePath },
                        ["touched_files_count"] = 1
                    }));

            var handler = new CodergenHandler(backend);
            var graph = new Graph { Name = "validation-structured" };
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                Prompt = "Write the implementation.",
                RawAttributes = new Dictionary<string, string>
                {
                    ["node_kind"] = "implementation",
                    ["codergen_version"] = "v2",
                    ["validation_mode"] = "required",
                    ["validation_checks"] = validationChecks
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "validation-manifest.json")));
            var manifestKinds = manifestDocument.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("kind").GetString())
                .ToList();
            Assert.Equal(new[] { "diff", "file_exists", "file_content", "artifact", "schema" }, manifestKinds);

            using var resultsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "validation-results.json")));
            Assert.Equal("passed", resultsDocument.RootElement.GetProperty("overall_state").GetString());
            var resultChecks = resultsDocument.RootElement.GetProperty("checks").EnumerateArray().ToList();
            Assert.Equal(5, resultChecks.Count);
            Assert.All(resultChecks, check => Assert.Equal("passed", check.GetProperty("state").GetString()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_GraphDefaultStructuredValidationChecks_ApplyWhenNodeOmitsChecks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_validationgraphdefault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "out", "graph-default.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "ok");

            var backend = new DeterministicBackend()
                .Queue("implement", DeterministicBackend.Result(
                    telemetry: new Dictionary<string, object?>
                    {
                        ["provider_state"] = "completed",
                        ["touched_files"] = new List<object?> { filePath },
                        ["touched_files_count"] = 1
                    }));

            var graph = new Graph { Name = "validation-graph-default" };
            graph.Attributes["validation_mode"] = "required";
            graph.Attributes["default_validation_checks"] = JsonSerializer.Serialize(new object[]
            {
                new Dictionary<string, object?> { ["kind"] = "file_exists", ["name"] = "graph-default-check", ["path"] = filePath }
            });
            var node = new GraphNode
            {
                Id = "implement",
                Shape = "box",
                Prompt = "Write the implementation.",
                RawAttributes = new Dictionary<string, string>
                {
                    ["node_kind"] = "implementation"
                }
            };
            graph.Nodes[node.Id] = node;

            var outcome = await new CodergenHandler(backend).ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempDir, node.Id, "validation-manifest.json")));
            var check = manifestDocument.RootElement.GetProperty("checks").EnumerateArray().Single();
            Assert.Equal("graph_default", check.GetProperty("source").GetString());
            Assert.Equal("file_exists", check.GetProperty("kind").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

internal sealed class FixedCodergenBackend : ICodergenBackend
{
    private readonly CodergenResult _result;

    public FixedCodergenBackend(CodergenResult result)
    {
        _result = result;
    }

    public Task<CodergenResult> RunAsync(
        string prompt,
        string? model = null,
        string? provider = null,
        string? reasoningEffort = null,
        CancellationToken ct = default,
        CodergenExecutionOptions? options = null)
    {
        return Task.FromResult(_result);
    }
}
