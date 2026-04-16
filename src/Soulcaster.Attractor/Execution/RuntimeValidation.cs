namespace Soulcaster.Attractor.Execution;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class RuntimeValidationModes
{
    public const string None = "none";
    public const string Advisory = "advisory";
    public const string Required = "required";

    public static string Normalize(string? value, string fallback = None)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Advisory => Advisory,
            Required => Required,
            None => None,
            _ => fallback
        };
    }
}

public static class RuntimeValidationFailActions
{
    public const string Fail = "fail";
    public const string Retry = "retry";

    public static string Normalize(string? value, string fallback = Fail)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Retry => Retry,
            Fail => Fail,
            _ => fallback
        };
    }
}

public static class RuntimeValidationCheckKinds
{
    public const string Command = "command";
    public const string FileExists = "file_exists";
    public const string FileContent = "file_content";
    public const string Diff = "diff";
    public const string Artifact = "artifact";
    public const string Schema = "schema";

    public static string Normalize(string? value, string fallback = Command)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            FileExists => FileExists,
            FileContent => FileContent,
            Diff => Diff,
            Artifact => Artifact,
            Schema => Schema,
            Command => Command,
            _ => fallback
        };
    }
}

public static class RuntimeValidationCheckSources
{
    public const string NodePolicy = "node_policy";
    public const string GraphDefault = "graph_default";
    public const string RuntimeDefault = "runtime_default";
    public const string ModelRequested = "model_requested";
}

public static class RuntimeValidationStates
{
    public const string NotRequired = "not_required";
    public const string NotRun = "not_run";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
    public const string Missing = "missing";
    public const string Misconfigured = "misconfigured";
    public const string Skipped = "skipped";
}

public sealed record RuntimeValidationPolicy(
    string Mode,
    string? Profile = null,
    IReadOnlyList<RuntimeValidationCheckRegistration>? Checks = null,
    int? TimeoutMs = null,
    string FailAction = RuntimeValidationFailActions.Fail)
{
    public static RuntimeValidationPolicy None { get; } = new(RuntimeValidationModes.None);

    public IReadOnlyList<RuntimeValidationCheckRegistration> EffectiveChecks => Checks ?? Array.Empty<RuntimeValidationCheckRegistration>();

    public bool IsRequired => string.Equals(Mode, RuntimeValidationModes.Required, StringComparison.Ordinal);
}

public sealed record RuntimeValidationCheckRegistration(
    string Kind,
    string Name,
    string? Command = null,
    string? Path = null,
    IReadOnlyList<string>? Paths = null,
    string? Workdir = null,
    string? ContainsText = null,
    string? MatchesRegex = null,
    string? JsonPath = null,
    string? ExpectedValueJson = null,
    string? ExpectedSchemaJson = null,
    bool RequireAnyChange = false,
    int? TimeoutMs = null,
    bool Required = true,
    string Source = RuntimeValidationCheckSources.NodePolicy,
    string? SourceReference = null);

public sealed record RuntimeValidationManifest(
    string NodeId,
    string Mode,
    string? Profile,
    IReadOnlyList<RuntimeValidationManifestCheck> Checks,
    DateTimeOffset CreatedAtUtc);

public sealed record RuntimeValidationManifestCheck(
    string Id,
    string Kind,
    string Name,
    string? Command,
    string? Path,
    IReadOnlyList<string>? Paths,
    string? Workdir,
    string? ContainsText,
    string? MatchesRegex,
    string? JsonPath,
    string? ExpectedValueJson,
    string? ExpectedSchemaJson,
    bool RequireAnyChange,
    int? TimeoutMs,
    bool Required,
    string Source,
    string? SourceReference = null);

public sealed record RuntimeValidationEvidence(
    IReadOnlyList<string> TouchedPaths)
{
    public static RuntimeValidationEvidence Empty { get; } = new(Array.Empty<string>());
}

public sealed record RuntimeValidationResults(
    string NodeId,
    string Mode,
    string? Profile,
    string OverallState,
    string? FailureKind,
    int RequiredChecksTotal,
    int RequiredChecksPassed,
    int OptionalChecksFailed,
    bool HasAuthoritativeEvidence,
    IReadOnlyList<RuntimeValidationCheckResult> Checks,
    DateTimeOffset CompletedAtUtc)
{
    public RuntimeValidationSummary ToSummary() =>
        new(
            NodeId,
            Mode,
            Profile,
            OverallState,
            FailureKind,
            RequiredChecksTotal,
            RequiredChecksPassed,
            OptionalChecksFailed,
            HasAuthoritativeEvidence,
            Checks.Count);

    public static RuntimeValidationResults Empty(string nodeId, string mode, string? profile) =>
        new(
            NodeId: nodeId,
            Mode: mode,
            Profile: profile,
            OverallState: mode == RuntimeValidationModes.Required
                ? RuntimeValidationStates.Missing
                : mode == RuntimeValidationModes.None
                    ? RuntimeValidationStates.NotRequired
                    : RuntimeValidationStates.NotRun,
            FailureKind: mode == RuntimeValidationModes.Required ? "validation_missing" : null,
            RequiredChecksTotal: 0,
            RequiredChecksPassed: 0,
            OptionalChecksFailed: 0,
            HasAuthoritativeEvidence: false,
            Checks: Array.Empty<RuntimeValidationCheckResult>(),
            CompletedAtUtc: DateTimeOffset.UtcNow);

    public static RuntimeValidationResults Skipped(RuntimeValidationManifest manifest) =>
        new(
            NodeId: manifest.NodeId,
            Mode: manifest.Mode,
            Profile: manifest.Profile,
            OverallState: RuntimeValidationStates.Skipped,
            FailureKind: null,
            RequiredChecksTotal: manifest.Checks.Count(check => check.Required),
            RequiredChecksPassed: 0,
            OptionalChecksFailed: 0,
            HasAuthoritativeEvidence: false,
            Checks: manifest.Checks.Select(check => new RuntimeValidationCheckResult(
                Id: check.Id,
                Kind: check.Kind,
                Name: check.Name,
                State: RuntimeValidationStates.Skipped,
                Required: check.Required,
                FailureKind: null,
                ExitCode: null,
                DurationMs: 0,
                StdoutPath: null,
                StderrPath: null,
                Message: "Validation was skipped because the work segment did not finish in a success state.")).ToList(),
            CompletedAtUtc: DateTimeOffset.UtcNow);
}

public sealed record RuntimeValidationCheckResult(
    string Id,
    string Kind,
    string Name,
    string State,
    bool Required,
    string? FailureKind,
    int? ExitCode,
    long DurationMs,
    string? StdoutPath,
    string? StderrPath,
    string? Message = null);

public sealed record RuntimeValidationSummary(
    string NodeId,
    string Mode,
    string? Profile,
    string OverallState,
    string? FailureKind,
    int RequiredChecksTotal,
    int RequiredChecksPassed,
    int OptionalChecksFailed,
    bool HasAuthoritativeEvidence,
    int TotalChecks);

public sealed class RuntimeValidationExecutor
{
    public async Task<RuntimeValidationResults> ExecuteAsync(
        RuntimeValidationManifest manifest,
        string stageDir,
        RuntimeValidationEvidence? evidence = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageDir);

        evidence ??= RuntimeValidationEvidence.Empty;
        var logsDir = Path.Combine(stageDir, "validation-logs");
        Directory.CreateDirectory(logsDir);

        if (manifest.Checks.Count == 0)
            return RuntimeValidationResults.Empty(manifest.NodeId, manifest.Mode, manifest.Profile);

        var requiredChecks = manifest.Checks.Where(check => check.Required).ToList();
        var optionalChecks = manifest.Checks.Where(check => !check.Required).ToList();
        var results = new List<RuntimeValidationCheckResult>(manifest.Checks.Count);
        var stopReason = string.Empty;

        foreach (var check in requiredChecks)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteCheckAsync(check, stageDir, logsDir, evidence, ct);
            results.Add(result);

            if (IsFailureState(result.State))
            {
                stopReason = $"Skipped because required validation check '{check.Name}' failed.";
                results.AddRange(requiredChecks
                    .Skip(results.Count(resultItem => resultItem.Required))
                    .Select(remaining => BuildSkippedResult(remaining, stopReason)));
                results.AddRange(optionalChecks.Select(optional => BuildSkippedResult(optional, stopReason)));
                return Summarize(manifest, results);
            }
        }

        if (optionalChecks.Count > 0)
        {
            var optionalResults = await Task.WhenAll(optionalChecks
                .Select(check => ExecuteCheckAsync(check, stageDir, logsDir, evidence, ct)));
            results.AddRange(optionalResults);
        }

        return Summarize(manifest, results);
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        RuntimeValidationEvidence evidence,
        CancellationToken ct)
    {
        return RuntimeValidationCheckKinds.Normalize(check.Kind) switch
        {
            RuntimeValidationCheckKinds.Command => await ExecuteCommandCheckAsync(check, stageDir, logsDir, ct),
            RuntimeValidationCheckKinds.FileExists => await ExecuteFileExistsCheckAsync(check, stageDir, logsDir, ct),
            RuntimeValidationCheckKinds.FileContent => await ExecuteFileContentCheckAsync(check, stageDir, logsDir, ct),
            RuntimeValidationCheckKinds.Diff => await ExecuteDiffCheckAsync(check, stageDir, logsDir, evidence, ct),
            RuntimeValidationCheckKinds.Artifact => await ExecuteArtifactCheckAsync(check, stageDir, logsDir, ct),
            RuntimeValidationCheckKinds.Schema => await ExecuteSchemaCheckAsync(check, stageDir, logsDir, ct),
            _ => await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                $"Validation kind '{check.Kind}' is not supported yet.",
                ct)
        };
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteCommandCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(check.Command))
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                "Command validation checks require a non-empty command.",
                ct);
        }

        var workdir = ResolveWorkingDirectory(check.Workdir);
        if (!Directory.Exists(workdir))
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                $"Validation working directory does not exist: {workdir}",
                ct);
        }

        var (stdoutRelativePath, stdoutPath, stderrRelativePath, stderrPath) = BuildLogPaths(check.Id, stageDir);

        var duration = Stopwatch.StartNew();
        var stdout = string.Empty;
        var stderr = string.Empty;
        int? exitCode = null;
        var state = RuntimeValidationStates.Failed;
        string? message = null;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workdir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(check.Command);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = check.TimeoutMs is int timeoutMs && timeoutMs > 0
                ? new CancellationTokenSource(timeoutMs)
                : null;
            using var linkedCts = timeoutCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
            {
                state = RuntimeValidationStates.Timeout;
                message = $"Validation command timed out after {check.TimeoutMs}ms.";
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            stdout = await stdoutTask;
            stderr = await stderrTask;
            exitCode = process.HasExited ? process.ExitCode : null;

            if (state != RuntimeValidationStates.Timeout)
                state = exitCode == 0 ? RuntimeValidationStates.Passed : RuntimeValidationStates.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state = RuntimeValidationStates.Misconfigured;
            message = ex.Message;
            stderr = AppendMessage(stderr, ex.Message);
        }
        finally
        {
            duration.Stop();
            await File.WriteAllTextAsync(stdoutPath, stdout ?? string.Empty, ct);
            await File.WriteAllTextAsync(stderrPath, stderr ?? string.Empty, ct);
        }

        return new RuntimeValidationCheckResult(
            Id: check.Id,
            Kind: check.Kind,
            Name: check.Name,
            State: state,
            Required: check.Required,
            FailureKind: ResolveCheckFailureKind(check.Kind, state),
            ExitCode: exitCode,
            DurationMs: duration.ElapsedMilliseconds,
            StdoutPath: NormalizeRelativePath(stdoutRelativePath),
            StderrPath: NormalizeRelativePath(stderrRelativePath),
            Message: message);
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteFileExistsCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        CancellationToken ct)
    {
        var baseDir = ResolveWorkingDirectory(check.Workdir);
        var rawPaths = ExpandPaths(check.Path, check.Paths);
        if (rawPaths.Count == 0)
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                "File existence checks require 'path' or 'paths'.",
                ct);
        }

        var resolved = rawPaths.Select(path => ResolvePath(path, baseDir)).ToList();
        var missing = resolved.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToList();
        var stdout = new StringBuilder();
        stdout.AppendLine($"Base directory: {baseDir}");
        foreach (var path in resolved)
            stdout.AppendLine($"Checked: {path}");

        var stderr = missing.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, missing.Select(path => $"Missing: {path}"));

        return await WriteCheckResultAsync(
            check,
            stageDir,
            RuntimeValidationStatesForOutcome(missing.Count == 0),
            stdout.ToString(),
            stderr,
            missing.Count == 0 ? null : $"Expected path(s) were missing: {string.Join(", ", missing)}",
            ct);
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteFileContentCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        CancellationToken ct)
    {
        var rawPaths = ExpandPaths(check.Path, check.Paths);
        if (rawPaths.Count != 1)
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                "File content checks require exactly one 'path'.",
                ct);
        }

        if (string.IsNullOrWhiteSpace(check.ContainsText) &&
            string.IsNullOrWhiteSpace(check.MatchesRegex) &&
            string.IsNullOrWhiteSpace(check.JsonPath))
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                "File content checks require contains_text, matches_regex, or json_path.",
                ct);
        }

        var baseDir = ResolveWorkingDirectory(check.Workdir);
        var resolvedPath = ResolvePath(rawPaths[0], baseDir);
        if (!File.Exists(resolvedPath))
        {
            return await WriteCheckResultAsync(
                check,
                stageDir,
                RuntimeValidationStates.Failed,
                $"Checked: {resolvedPath}",
                $"Missing file: {resolvedPath}",
                $"File '{resolvedPath}' did not exist.",
                ct);
        }

        var content = await File.ReadAllTextAsync(resolvedPath, ct);
        var stdout = new StringBuilder();
        stdout.AppendLine($"Checked: {resolvedPath}");
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(check.ContainsText))
        {
            stdout.AppendLine($"contains_text: {check.ContainsText}");
            if (!content.Contains(check.ContainsText, StringComparison.Ordinal))
                failures.Add($"File did not contain expected text: {check.ContainsText}");
        }

        if (!string.IsNullOrWhiteSpace(check.MatchesRegex))
        {
            stdout.AppendLine($"matches_regex: {check.MatchesRegex}");
            if (!Regex.IsMatch(content, check.MatchesRegex, RegexOptions.CultureInvariant | RegexOptions.Multiline))
                failures.Add($"File did not match expected regex: {check.MatchesRegex}");
        }

        if (!string.IsNullOrWhiteSpace(check.JsonPath))
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (!TrySelectJsonPath(document.RootElement, check.JsonPath, out var selected))
                {
                    failures.Add($"JSON path was missing: {check.JsonPath}");
                }
                else if (!string.IsNullOrWhiteSpace(check.ExpectedValueJson))
                {
                    if (!JsonValuesEqual(selected, check.ExpectedValueJson))
                        failures.Add($"JSON path '{check.JsonPath}' did not match expected value.");
                }
            }
            catch (JsonException ex)
            {
                failures.Add($"File content was not valid JSON: {ex.Message}");
            }
        }

        return await WriteCheckResultAsync(
            check,
            stageDir,
            RuntimeValidationStatesForOutcome(failures.Count == 0),
            stdout.ToString(),
            string.Join(Environment.NewLine, failures),
            failures.Count == 0 ? null : string.Join(" ", failures),
            ct);
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteDiffCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        RuntimeValidationEvidence evidence,
        CancellationToken ct)
    {
        var touchedPaths = NormalizeTouchedPaths(evidence.TouchedPaths);
        var expectedPaths = ExpandPaths(check.Path, check.Paths);
        var stdout = new StringBuilder();
        stdout.AppendLine($"Touched paths count: {touchedPaths.Count}");
        foreach (var path in touchedPaths)
            stdout.AppendLine($"Touched: {path}");

        bool passed;
        string? message = null;
        string stderr;

        if (expectedPaths.Count == 0)
        {
            passed = touchedPaths.Count > 0;
            stderr = passed ? string.Empty : "No touched paths were recorded.";
            if (!passed)
                message = "Expected at least one changed path.";
        }
        else
        {
            var normalizedExpected = expectedPaths
                .Select(path => NormalizeComparisonPath(path, ResolveWorkingDirectory(check.Workdir)))
                .ToList();
            stdout.AppendLine("Expected paths:");
            foreach (var path in normalizedExpected)
                stdout.AppendLine($"Expected: {path}");

            var matched = normalizedExpected.Where(expected => touchedPaths.Contains(expected)).ToList();
            var missing = normalizedExpected.Where(expected => !touchedPaths.Contains(expected)).ToList();
            passed = check.RequireAnyChange ? matched.Count > 0 : missing.Count == 0;
            stderr = passed
                ? string.Empty
                : check.RequireAnyChange
                    ? "None of the expected paths were changed."
                    : string.Join(Environment.NewLine, missing.Select(path => $"Missing changed path: {path}"));
            if (!passed)
                message = check.RequireAnyChange
                    ? "Expected at least one specified path to change."
                    : $"Expected changed paths were missing: {string.Join(", ", missing)}";
        }

        return await WriteCheckResultAsync(
            check,
            stageDir,
            RuntimeValidationStatesForOutcome(passed),
            stdout.ToString(),
            stderr,
            message,
            ct);
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteArtifactCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        CancellationToken ct)
    {
        var rawPaths = ExpandPaths(check.Path, check.Paths);
        if (rawPaths.Count == 0)
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                "Artifact checks require 'path' or 'paths'.",
                ct);
        }

        var baseDir = ResolveArtifactBaseDirectory(stageDir, check.Workdir);
        var resolved = rawPaths.Select(path => ResolvePath(path, baseDir)).ToList();
        var missing = resolved.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToList();
        var stdout = new StringBuilder();
        stdout.AppendLine($"Artifact base directory: {baseDir}");
        foreach (var path in resolved)
            stdout.AppendLine($"Checked artifact: {path}");

        return await WriteCheckResultAsync(
            check,
            stageDir,
            RuntimeValidationStatesForOutcome(missing.Count == 0),
            stdout.ToString(),
            missing.Count == 0 ? string.Empty : string.Join(Environment.NewLine, missing.Select(path => $"Missing artifact: {path}")),
            missing.Count == 0 ? null : $"Expected artifact(s) were missing: {string.Join(", ", missing)}",
            ct);
    }

    private static async Task<RuntimeValidationCheckResult> ExecuteSchemaCheckAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string logsDir,
        CancellationToken ct)
    {
        var rawPaths = ExpandPaths(check.Path, check.Paths);
        if (rawPaths.Count != 1 || string.IsNullOrWhiteSpace(check.ExpectedSchemaJson))
        {
            return await WriteMisconfiguredResultAsync(
                check,
                stageDir,
                "Schema checks require exactly one 'path' and expected_schema_json.",
                ct);
        }

        var baseDir = ResolveWorkingDirectory(check.Workdir);
        var resolvedPath = ResolvePath(rawPaths[0], baseDir);
        if (!File.Exists(resolvedPath))
        {
            return await WriteCheckResultAsync(
                check,
                stageDir,
                RuntimeValidationStates.Failed,
                $"Checked schema path: {resolvedPath}",
                $"Missing file: {resolvedPath}",
                $"Schema target '{resolvedPath}' did not exist.",
                ct);
        }

        try
        {
            using var targetDocument = JsonDocument.Parse(await File.ReadAllTextAsync(resolvedPath, ct));
            using var schemaDocument = JsonDocument.Parse(check.ExpectedSchemaJson);
            var errors = new List<string>();
            ValidateAgainstSchema(targetDocument.RootElement, schemaDocument.RootElement, "$", errors);

            return await WriteCheckResultAsync(
                check,
                stageDir,
                RuntimeValidationStatesForOutcome(errors.Count == 0),
                $"Validated schema target: {resolvedPath}",
                string.Join(Environment.NewLine, errors),
                errors.Count == 0 ? null : string.Join(" ", errors),
                ct);
        }
        catch (JsonException ex)
        {
            return await WriteCheckResultAsync(
                check,
                stageDir,
                RuntimeValidationStates.Failed,
                $"Checked schema path: {resolvedPath}",
                ex.Message,
                $"Schema validation failed because the target or schema JSON was invalid: {ex.Message}",
                ct);
        }
    }

    private static async Task<RuntimeValidationCheckResult> WriteMisconfiguredResultAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string message,
        CancellationToken ct)
    {
        return await WriteCheckResultAsync(
            check,
            stageDir,
            RuntimeValidationStates.Misconfigured,
            string.Empty,
            message,
            message,
            ct);
    }

    private static async Task<RuntimeValidationCheckResult> WriteCheckResultAsync(
        RuntimeValidationManifestCheck check,
        string stageDir,
        string state,
        string stdout,
        string stderr,
        string? message,
        CancellationToken ct,
        int? exitCode = null,
        long durationMs = 0)
    {
        var (stdoutRelativePath, stdoutPath, stderrRelativePath, stderrPath) = BuildLogPaths(check.Id, stageDir);
        await File.WriteAllTextAsync(stdoutPath, stdout ?? string.Empty, ct);
        await File.WriteAllTextAsync(stderrPath, stderr ?? string.Empty, ct);

        return new RuntimeValidationCheckResult(
            Id: check.Id,
            Kind: check.Kind,
            Name: check.Name,
            State: state,
            Required: check.Required,
            FailureKind: ResolveCheckFailureKind(check.Kind, state),
            ExitCode: exitCode,
            DurationMs: durationMs,
            StdoutPath: NormalizeRelativePath(stdoutRelativePath),
            StderrPath: NormalizeRelativePath(stderrRelativePath),
            Message: message);
    }

    private static RuntimeValidationCheckResult BuildSkippedResult(RuntimeValidationManifestCheck check, string message)
    {
        return new RuntimeValidationCheckResult(
            Id: check.Id,
            Kind: check.Kind,
            Name: check.Name,
            State: RuntimeValidationStates.Skipped,
            Required: check.Required,
            FailureKind: null,
            ExitCode: null,
            DurationMs: 0,
            StdoutPath: null,
            StderrPath: null,
            Message: message);
    }

    private static RuntimeValidationResults Summarize(
        RuntimeValidationManifest manifest,
        IReadOnlyList<RuntimeValidationCheckResult> checks)
    {
        var requiredChecks = checks.Where(check => check.Required).ToList();
        var optionalChecks = checks.Where(check => !check.Required).ToList();
        var overallState = ResolveOverallState(manifest.Mode, requiredChecks, optionalChecks);
        var failureKind = ResolveOverallFailureKind(overallState, requiredChecks, optionalChecks);

        return new RuntimeValidationResults(
            NodeId: manifest.NodeId,
            Mode: manifest.Mode,
            Profile: manifest.Profile,
            OverallState: overallState,
            FailureKind: failureKind,
            RequiredChecksTotal: requiredChecks.Count,
            RequiredChecksPassed: requiredChecks.Count(check => check.State == RuntimeValidationStates.Passed),
            OptionalChecksFailed: optionalChecks.Count(check => IsFailureState(check.State)),
            HasAuthoritativeEvidence: checks.Any(check => check.State != RuntimeValidationStates.Skipped),
            Checks: checks,
            CompletedAtUtc: DateTimeOffset.UtcNow);
    }

    private static string ResolveOverallState(
        string mode,
        IReadOnlyList<RuntimeValidationCheckResult> requiredChecks,
        IReadOnlyList<RuntimeValidationCheckResult> optionalChecks)
    {
        if (requiredChecks.Count == 0 && optionalChecks.Count == 0)
        {
            return mode switch
            {
                RuntimeValidationModes.Required => RuntimeValidationStates.Missing,
                RuntimeValidationModes.None => RuntimeValidationStates.NotRequired,
                _ => RuntimeValidationStates.NotRun
            };
        }

        if (mode == RuntimeValidationModes.Required && requiredChecks.Count == 0)
            return RuntimeValidationStates.Missing;

        if (requiredChecks.Any(check => check.State == RuntimeValidationStates.Timeout))
            return RuntimeValidationStates.Timeout;

        if (requiredChecks.Any(check => check.State == RuntimeValidationStates.Misconfigured))
            return RuntimeValidationStates.Misconfigured;

        if (requiredChecks.Any(check => check.State == RuntimeValidationStates.Failed))
            return RuntimeValidationStates.Failed;

        if (requiredChecks.Count > 0)
            return requiredChecks.All(check => check.State == RuntimeValidationStates.Passed)
                ? RuntimeValidationStates.Passed
                : RuntimeValidationStates.Missing;

        if (optionalChecks.Any(check => check.State == RuntimeValidationStates.Timeout))
            return RuntimeValidationStates.Timeout;

        if (optionalChecks.Any(check => check.State == RuntimeValidationStates.Misconfigured))
            return RuntimeValidationStates.Misconfigured;

        if (optionalChecks.Any(check => check.State == RuntimeValidationStates.Failed))
            return RuntimeValidationStates.Failed;

        return optionalChecks.All(check => check.State == RuntimeValidationStates.Skipped)
            ? RuntimeValidationStates.NotRun
            : RuntimeValidationStates.Passed;
    }

    private static string? ResolveOverallFailureKind(
        string overallState,
        IReadOnlyList<RuntimeValidationCheckResult> requiredChecks,
        IReadOnlyList<RuntimeValidationCheckResult> optionalChecks)
    {
        if (overallState == RuntimeValidationStates.Missing)
            return "validation_missing";

        var failure = requiredChecks.Concat(optionalChecks).FirstOrDefault(check => IsFailureState(check.State));
        return failure?.FailureKind ?? overallState switch
        {
            RuntimeValidationStates.Timeout => "validation_timeout",
            RuntimeValidationStates.Misconfigured => "validation_misconfigured",
            RuntimeValidationStates.Failed => "validation_failed",
            _ => null
        };
    }

    private static string ResolveCheckFailureKind(string kind, string state)
    {
        return state switch
        {
            RuntimeValidationStates.Timeout => "validation_timeout",
            RuntimeValidationStates.Misconfigured => "validation_misconfigured",
            RuntimeValidationStates.Failed => RuntimeValidationCheckKinds.Normalize(kind) switch
            {
                RuntimeValidationCheckKinds.Artifact => "validation_artifact_missing",
                RuntimeValidationCheckKinds.Schema => "validation_schema_failed",
                _ => "validation_failed"
            },
            _ => null!
        };
    }

    private static bool IsFailureState(string state)
    {
        return state is RuntimeValidationStates.Failed or RuntimeValidationStates.Timeout or RuntimeValidationStates.Misconfigured;
    }

    private static string RuntimeValidationStatesForOutcome(bool passed) =>
        passed ? RuntimeValidationStates.Passed : RuntimeValidationStates.Failed;

    private static (string StdoutRelativePath, string StdoutPath, string StderrRelativePath, string StderrPath) BuildLogPaths(string checkId, string stageDir)
    {
        var stdoutRelativePath = Path.Combine("validation-logs", $"{checkId}.stdout.log");
        var stderrRelativePath = Path.Combine("validation-logs", $"{checkId}.stderr.log");
        return (
            stdoutRelativePath,
            Path.Combine(stageDir, stdoutRelativePath),
            stderrRelativePath,
            Path.Combine(stageDir, stderrRelativePath));
    }

    private static IReadOnlyList<string> ExpandPaths(string? singular, IReadOnlyList<string>? plural)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(singular))
            values.Add(singular.Trim());

        if (plural is not null)
        {
            values.AddRange(plural
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim()));
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveWorkingDirectory(string? workdir)
    {
        if (string.IsNullOrWhiteSpace(workdir))
            return Directory.GetCurrentDirectory();

        return Path.IsPathRooted(workdir)
            ? Path.GetFullPath(workdir)
            : Path.GetFullPath(workdir, Directory.GetCurrentDirectory());
    }

    private static string ResolveArtifactBaseDirectory(string stageDir, string? workdir)
    {
        if (string.IsNullOrWhiteSpace(workdir))
            return stageDir;

        return Path.IsPathRooted(workdir)
            ? Path.GetFullPath(workdir)
            : Path.GetFullPath(workdir, stageDir);
    }

    private static string ResolvePath(string path, string baseDir)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, baseDir);
    }

    private static HashSet<string> NormalizeTouchedPaths(IReadOnlyList<string> touchedPaths)
    {
        return touchedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeComparisonPath(path, Directory.GetCurrentDirectory()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeComparisonPath(string path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            var fullPath = ResolvePath(path, baseDir);
            var currentDir = Directory.GetCurrentDirectory();
            var relativePath = fullPath.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(currentDir, fullPath)
                : fullPath;

            return relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return path.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private static bool JsonValuesEqual(JsonElement actual, string expectedJson)
    {
        var actualNode = JsonNode.Parse(actual.GetRawText());
        var expectedNode = JsonNode.Parse(expectedJson);
        return JsonNode.DeepEquals(actualNode, expectedNode);
    }

    private static bool TrySelectJsonPath(JsonElement root, string path, out JsonElement selected)
    {
        selected = root;
        if (string.IsNullOrWhiteSpace(path))
            return true;

        foreach (var token in ParseJsonPath(path))
        {
            if (token.Kind == JsonPathTokenKind.Property)
            {
                if (selected.ValueKind != JsonValueKind.Object || !selected.TryGetProperty(token.Text!, out selected))
                    return false;
            }
            else
            {
                if (selected.ValueKind != JsonValueKind.Array || token.Index is null || selected.GetArrayLength() <= token.Index.Value)
                    return false;
                selected = selected[token.Index.Value];
            }
        }

        return true;
    }

    private static IReadOnlyList<JsonPathToken> ParseJsonPath(string path)
    {
        var tokens = new List<JsonPathToken>();
        var current = new StringBuilder();

        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            switch (ch)
            {
                case '.':
                    if (current.Length > 0)
                    {
                        tokens.Add(JsonPathToken.Property(current.ToString()));
                        current.Clear();
                    }
                    break;
                case '[':
                    if (current.Length > 0)
                    {
                        tokens.Add(JsonPathToken.Property(current.ToString()));
                        current.Clear();
                    }

                    var end = path.IndexOf(']', i + 1);
                    if (end <= i + 1 || !int.TryParse(path[(i + 1)..end], out var index))
                        throw new FormatException($"Invalid JSON path segment in '{path}'.");
                    tokens.Add(JsonPathToken.Indexed(index));
                    i = end;
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
            tokens.Add(JsonPathToken.Property(current.ToString()));

        return tokens;
    }

    private static void ValidateAgainstSchema(JsonElement value, JsonElement schema, string path, ICollection<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Schema at {path} must be an object.");
            return;
        }

        if (schema.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            !MatchesSchemaType(value, typeElement.GetString()!))
        {
            errors.Add($"{path} expected type '{typeElement.GetString()}', found '{value.ValueKind.ToString().ToLowerInvariant()}'.");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumElement) &&
            enumElement.ValueKind == JsonValueKind.Array)
        {
            var matched = enumElement.EnumerateArray().Any(candidate => JsonValuesEqual(value, candidate.GetRawText()));
            if (!matched)
                errors.Add($"{path} was not one of the allowed enum values.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var requiredElement) &&
                requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var requiredProperty in requiredElement.EnumerateArray()
                             .Where(item => item.ValueKind == JsonValueKind.String)
                             .Select(item => item.GetString())
                             .Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    if (!value.TryGetProperty(requiredProperty!, out _))
                        errors.Add($"{path} is missing required property '{requiredProperty}'.");
                }
            }

            if (schema.TryGetProperty("properties", out var propertiesElement) &&
                propertiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertySchema in propertiesElement.EnumerateObject())
                {
                    if (value.TryGetProperty(propertySchema.Name, out var propertyValue))
                        ValidateAgainstSchema(propertyValue, propertySchema.Value, $"{path}.{propertySchema.Name}", errors);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array &&
                 schema.TryGetProperty("items", out var itemsSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateAgainstSchema(item, itemsSchema, $"{path}[{index}]", errors);
                index++;
            }
        }
    }

    private static bool MatchesSchemaType(JsonElement value, string expectedType)
    {
        return expectedType.Trim().ToLowerInvariant() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort kill.
        }
    }

    private static string AppendMessage(string current, string message)
    {
        if (string.IsNullOrWhiteSpace(current))
            return message;

        var sb = new StringBuilder(current.TrimEnd());
        sb.AppendLine();
        sb.Append(message);
        return sb.ToString();
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private enum JsonPathTokenKind
    {
        Property,
        Index
    }

    private sealed record JsonPathToken(JsonPathTokenKind Kind, string? Text = null, int? Index = null)
    {
        public static JsonPathToken Property(string text) => new(JsonPathTokenKind.Property, Text: text);
        public static JsonPathToken Indexed(int index) => new(JsonPathTokenKind.Index, Index: index);
    }
}
