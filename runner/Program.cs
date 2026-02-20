using System.Text.Json;
using System.Text.RegularExpressions;
using JcAttractor.Attractor;
using JcAttractor.CodingAgent;
using JcAttractor.UnifiedLlm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

// ── Command dispatcher ──────────────────────────────────────────────
var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "run" => await RunPipeline(args[1..]),
    "gate" when args.Length > 1 && args[1] == "answer" => await GateAnswer(args[2..]),
    "gate" when args.Length > 1 && args[1] == "watch" => await GateWatch(args[2..]),
    "gate" => await GateShow(args[1..]),
    "status" => await ShowStatus(args[1..]),
    "logs" => await ShowLogs(args[1..]),
    "web" => await RunWeb(args[1..]),
    "help" or "--help" or "-h" => ShowHelp(),
    _ when command.EndsWith(".dot") => await RunPipeline(args), // backwards compat
    _ => ShowHelp()
};

// ═════════════════════════════════════════════════════════════════════
// Helpers
// ═════════════════════════════════════════════════════════════════════

static string? ResolveOutputDir(string[] args)
{
    // 1. Explicit --dir flag
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--dir")
            return Path.GetFullPath(args[i + 1]);
    }

    // 2. Look for output/ sibling to a .dot file in cwd
    var cwd = Directory.GetCurrentDirectory();
    var dotFiles = Directory.GetFiles(cwd, "*.dot");
    if (dotFiles.Length > 0)
    {
        var outputDir = Path.Combine(cwd, "output");
        if (Directory.Exists(outputDir))
        {
            // If there's exactly one subdirectory, use it; otherwise return the output dir
            var subdirs = Directory.GetDirectories(outputDir);
            if (subdirs.Length == 1)
                return subdirs[0];
            return outputDir;
        }
    }

    // 3. Look for dotfiles/output/ relative to cwd
    var dotfilesOutput = Path.Combine(cwd, "dotfiles", "output");
    if (Directory.Exists(dotfilesOutput))
        return dotfilesOutput;

    return null;
}

static string[] StripDirFlag(string[] args)
{
    var result = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--dir" && i + 1 < args.Length)
        {
            i++; // skip the value too
            continue;
        }
        result.Add(args[i]);
    }
    return result.ToArray();
}

static string? FindGatesDir(string outputDir)
{
    var gatesDir = Path.Combine(outputDir, "gates");
    if (Directory.Exists(gatesDir))
        return gatesDir;

    // Check subdirectories (outputDir might be the parent containing multiple pipeline outputs)
    foreach (var subdir in Directory.GetDirectories(outputDir))
    {
        var sub = Path.Combine(subdir, "gates");
        if (Directory.Exists(sub))
            return sub;
    }

    return null;
}

static string? FindLogsDir(string outputDir)
{
    var logsDir = Path.Combine(outputDir, "logs");
    if (Directory.Exists(logsDir))
        return logsDir;

    foreach (var subdir in Directory.GetDirectories(outputDir))
    {
        var sub = Path.Combine(subdir, "logs");
        if (Directory.Exists(sub))
            return sub;
    }

    return null;
}

static JsonSerializerOptions JsonOpts() => new() { WriteIndented = true };

// ═════════════════════════════════════════════════════════════════════
// Global run registry (~/.attractor/runs.json)
// ═════════════════════════════════════════════════════════════════════

static string RegistryPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".attractor", "runs.json");

static List<RunEntry> LoadRegistry()
{
    if (!File.Exists(RegistryPath())) return new();
    try
    {
        var json = File.ReadAllText(RegistryPath());
        return JsonSerializer.Deserialize<List<RunEntry>>(json) ?? new();
    }
    catch { return new(); }
}

static void SaveRegistry(List<RunEntry> entries)
{
    var dir = Path.GetDirectoryName(RegistryPath())!;
    Directory.CreateDirectory(dir);
    File.WriteAllText(RegistryPath(), JsonSerializer.Serialize(entries, JsonOpts()));
}

static void RegisterRun(string dotFile, string outputDir)
{
    var entries = LoadRegistry();
    // Remove stale entry for same output dir
    entries.RemoveAll(e => e.output_dir == outputDir);
    entries.Add(new RunEntry
    {
        dotfile = dotFile,
        output_dir = outputDir,
        name = Path.GetFileNameWithoutExtension(dotFile),
        started = DateTime.UtcNow.ToString("o")
    });
    SaveRegistry(entries);
}

/// <summary>Prune entries whose output_dir no longer exists on disk.</summary>
static List<RunEntry> PruneRegistry()
{
    var entries = LoadRegistry();
    var before = entries.Count;
    entries.RemoveAll(e => !Directory.Exists(e.output_dir));
    if (entries.Count != before) SaveRegistry(entries);
    return entries;
}

// ═════════════════════════════════════════════════════════════════════
// gate — show pending gate
// ═════════════════════════════════════════════════════════════════════

static async Task<int> GateShow(string[] args)
{
    var outputDir = ResolveOutputDir(args);
    if (outputDir == null)
    {
        Console.Error.WriteLine("Could not find output directory. Use --dir <path> or run from a directory containing a .dot file.");
        return 1;
    }

    var gatesDir = FindGatesDir(outputDir);
    if (gatesDir == null)
    {
        Console.Error.WriteLine($"No gates directory found under {outputDir}");
        return 1;
    }

    var pendingFile = Path.Combine(gatesDir, "pending");
    if (!File.Exists(pendingFile))
    {
        // No pending gate — show the most recent answered gate instead
        var gateDirs = Directory.GetDirectories(gatesDir)
            .OrderByDescending(d => Path.GetFileName(d))
            .ToList();

        if (gateDirs.Count == 0)
        {
            Console.WriteLine("No gates found.");
            return 0;
        }

        Console.WriteLine("No pending gate. Recent gates:");
        Console.WriteLine();

        foreach (var gateDir in gateDirs.Take(5))
        {
            var qFile = Path.Combine(gateDir, "question.json");
            if (!File.Exists(qFile)) continue;

            var question = JsonDocument.Parse(await File.ReadAllTextAsync(qFile));
            var root = question.RootElement;
            var gateId = root.GetProperty("gate_id").GetString();
            var text = root.GetProperty("text").GetString();
            var answered = File.Exists(Path.Combine(gateDir, "answer.json"));

            Console.WriteLine($"  {gateId}  {(answered ? "[answered]" : "[unanswered]")}");
            Console.WriteLine($"    {text}");

            if (answered)
            {
                var answerDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(gateDir, "answer.json")));
                var answerText = answerDoc.RootElement.GetProperty("text").GetString();
                Console.WriteLine($"    → {answerText}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    var gateId2 = (await File.ReadAllTextAsync(pendingFile)).Trim();
    var gateDir2 = Path.Combine(gatesDir, gateId2);
    var questionPath = Path.Combine(gateDir2, "question.json");

    if (!File.Exists(questionPath))
    {
        Console.Error.WriteLine($"Pending gate {gateId2} has no question.json");
        return 1;
    }

    var questionDoc = JsonDocument.Parse(await File.ReadAllTextAsync(questionPath));
    var qRoot = questionDoc.RootElement;
    var questionText = qRoot.GetProperty("text").GetString();

    Console.WriteLine($"Gate: {gateId2}");
    Console.WriteLine($"Question: {questionText}");

    if (qRoot.TryGetProperty("options", out var options))
    {
        int i = 1;
        foreach (var opt in options.EnumerateArray())
        {
            Console.WriteLine($"  [{i}] {opt.GetString()}");
            i++;
        }
    }

    // Show artifacts to review — find logs dir sibling to gates dir
    var logsDir = FindLogsDir(outputDir);
    if (logsDir != null)
    {
        var artifacts = new List<string>();
        foreach (var nodeDir in Directory.GetDirectories(logsDir).OrderBy(d => d))
        {
            foreach (var file in Directory.GetFiles(nodeDir))
            {
                var fileName = Path.GetFileName(file);
                // Match structured output files like PLAN-1.md, ORIENT-1.md, BREAKDOWN-1.md
                var stem = Path.GetFileNameWithoutExtension(fileName);
                if (fileName.EndsWith(".md") && stem.Contains('-') && stem == stem.ToUpperInvariant())
                    artifacts.Add(file);
            }
        }

        if (artifacts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Review artifacts:");
            foreach (var artifact in artifacts)
            {
                Console.WriteLine($"  {artifact}");
            }
        }
    }

    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// gate answer — answer a pending gate
// ═════════════════════════════════════════════════════════════════════

static async Task<int> GateAnswer(string[] args)
{
    var outputDir = ResolveOutputDir(args);
    if (outputDir == null)
    {
        Console.Error.WriteLine("Could not find output directory. Use --dir <path> or run from a directory containing a .dot file.");
        return 1;
    }

    var gatesDir = FindGatesDir(outputDir);
    if (gatesDir == null)
    {
        Console.Error.WriteLine($"No gates directory found under {outputDir}");
        return 1;
    }

    var pendingFile = Path.Combine(gatesDir, "pending");
    if (!File.Exists(pendingFile))
    {
        Console.Error.WriteLine("No pending gate to answer.");
        return 1;
    }

    var gateId = (await File.ReadAllTextAsync(pendingFile)).Trim();
    var gateDir = Path.Combine(gatesDir, gateId);
    var questionPath = Path.Combine(gateDir, "question.json");
    var answerPath = Path.Combine(gateDir, "answer.json");

    if (!File.Exists(questionPath))
    {
        Console.Error.WriteLine($"Pending gate {gateId} has no question.json");
        return 1;
    }

    var questionDoc = JsonDocument.Parse(await File.ReadAllTextAsync(questionPath));
    var qRoot = questionDoc.RootElement;
    var questionText = qRoot.GetProperty("text").GetString();
    var optionsList = new List<string>();

    if (qRoot.TryGetProperty("options", out var options))
    {
        foreach (var opt in options.EnumerateArray())
            optionsList.Add(opt.GetString() ?? "");
    }

    // Resolve the choice from remaining args (strip --dir)
    var remaining = StripDirFlag(args);
    string? choice = remaining.Length > 0 ? remaining[0] : null;

    if (choice == null)
    {
        // Interactive mode
        Console.WriteLine($"Gate: {gateId}");
        Console.WriteLine($"Question: {questionText}");
        for (int i = 0; i < optionsList.Count; i++)
            Console.WriteLine($"  [{i + 1}] {optionsList[i]}");
        Console.Write("> ");
        choice = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(choice))
        {
            Console.Error.WriteLine("No answer provided.");
            return 1;
        }
    }

    // Resolve choice: by number or by label (case-insensitive)
    string resolvedChoice;
    if (int.TryParse(choice, out int choiceNum) && choiceNum >= 1 && choiceNum <= optionsList.Count)
    {
        resolvedChoice = optionsList[choiceNum - 1];
    }
    else
    {
        // Try case-insensitive label match
        var match = optionsList.FirstOrDefault(o => o.Equals(choice, StringComparison.OrdinalIgnoreCase));
        resolvedChoice = match ?? choice;
    }

    var answer = new { text = resolvedChoice, selected_options = new[] { resolvedChoice } };
    await File.WriteAllTextAsync(answerPath, JsonSerializer.Serialize(answer, JsonOpts()));

    Console.WriteLine($"Answered gate {gateId}: {resolvedChoice}");
    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// gate watch — interactive watch mode
// ═════════════════════════════════════════════════════════════════════

static async Task<int> GateWatch(string[] args)
{
    var outputDir = ResolveOutputDir(args);
    if (outputDir == null)
    {
        Console.Error.WriteLine("Could not find output directory. Use --dir <path> or run from a directory containing a .dot file.");
        return 1;
    }

    var gatesDir = FindGatesDir(outputDir);
    if (gatesDir == null)
    {
        // Gates dir might not exist yet if pipeline hasn't started — create and watch
        gatesDir = Path.Combine(outputDir, "gates");
        Directory.CreateDirectory(gatesDir);
    }

    Console.WriteLine($"Watching for gates in: {gatesDir}");
    Console.WriteLine("Press Ctrl-C to stop.");
    Console.WriteLine();

    var lastGateId = "";

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var pendingFile = Path.Combine(gatesDir, "pending");

            if (File.Exists(pendingFile))
            {
                var gateId = (await File.ReadAllTextAsync(pendingFile, cts.Token)).Trim();

                if (gateId != lastGateId)
                {
                    lastGateId = gateId;
                    var gateDir = Path.Combine(gatesDir, gateId);
                    var questionPath = Path.Combine(gateDir, "question.json");

                    if (File.Exists(questionPath))
                    {
                        var questionDoc = JsonDocument.Parse(await File.ReadAllTextAsync(questionPath, cts.Token));
                        var qRoot = questionDoc.RootElement;
                        var questionText = qRoot.GetProperty("text").GetString();
                        var optionsList = new List<string>();

                        if (qRoot.TryGetProperty("options", out var options))
                        {
                            foreach (var opt in options.EnumerateArray())
                                optionsList.Add(opt.GetString() ?? "");
                        }

                        // Terminal bell to alert the user
                        Console.Write("\a");

                        Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
                        Console.WriteLine($"║  Gate: {gateId}");
                        Console.WriteLine($"║  Question: {questionText}");
                        for (int i = 0; i < optionsList.Count; i++)
                            Console.WriteLine($"║    [{i + 1}] {optionsList[i]}");
                        Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");

                        Console.Write("> ");
                        var input = Console.ReadLine()?.Trim();

                        if (!string.IsNullOrEmpty(input))
                        {
                            string resolvedChoice;
                            if (int.TryParse(input, out int choiceNum) && choiceNum >= 1 && choiceNum <= optionsList.Count)
                            {
                                resolvedChoice = optionsList[choiceNum - 1];
                            }
                            else
                            {
                                var match = optionsList.FirstOrDefault(o => o.Equals(input, StringComparison.OrdinalIgnoreCase));
                                resolvedChoice = match ?? input;
                            }

                            var answerPath = Path.Combine(gateDir, "answer.json");
                            var answer = new { text = resolvedChoice, selected_options = new[] { resolvedChoice } };
                            await File.WriteAllTextAsync(answerPath, JsonSerializer.Serialize(answer, JsonOpts()));

                            Console.WriteLine($"  → Answered: {resolvedChoice}");
                            Console.WriteLine();
                        }
                    }
                }
            }

            await Task.Delay(1000, cts.Token);
        }
    }
    catch (OperationCanceledException)
    {
        // Expected on Ctrl-C
    }

    Console.WriteLine();
    Console.WriteLine("Watch stopped.");
    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// status — pipeline progress
// ═════════════════════════════════════════════════════════════════════

static async Task<int> ShowStatus(string[] args)
{
    var outputDir = ResolveOutputDir(args);
    if (outputDir == null)
    {
        Console.Error.WriteLine("Could not find output directory. Use --dir <path> or run from a directory containing a .dot file.");
        return 1;
    }

    var logsDir = FindLogsDir(outputDir);
    if (logsDir == null)
    {
        Console.Error.WriteLine($"No logs directory found under {outputDir}");
        return 1;
    }

    var checkpointPath = Path.Combine(logsDir, "checkpoint.json");
    if (!File.Exists(checkpointPath))
    {
        Console.WriteLine("No checkpoint found. Pipeline may not have started.");
        return 0;
    }

    var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
    var root = checkpoint.RootElement;
    var currentNode = root.GetProperty("CurrentNodeId").GetString();
    var completedNodes = root.GetProperty("CompletedNodes").EnumerateArray()
        .Select(e => e.GetString() ?? "").ToList();

    Console.WriteLine("Pipeline Status");
    Console.WriteLine(new string('─', 50));
    Console.WriteLine();

    Console.WriteLine("Completed nodes:");
    foreach (var node in completedNodes.Distinct())
    {
        var statusFile = Path.Combine(logsDir, node, "status.json");
        string statusText = "done";
        if (File.Exists(statusFile))
        {
            var statusDoc = JsonDocument.Parse(await File.ReadAllTextAsync(statusFile));
            var sRoot = statusDoc.RootElement;
            statusText = sRoot.TryGetProperty("status", out var s) ? s.GetString() ?? "done" : "done";
            var notes = sRoot.TryGetProperty("notes", out var n) ? n.GetString() : null;
            var preferred = sRoot.TryGetProperty("preferred_label", out var p) ? p.GetString() : null;
            if (!string.IsNullOrEmpty(preferred))
                statusText += $" → {preferred}";
        }
        Console.WriteLine($"  ✓ {node,-25} {statusText}");
    }

    // Pipeline is done only if result.json exists (written when runner finishes)
    if (File.Exists(Path.Combine(logsDir, "result.json")))
    {
        Console.WriteLine();
        Console.WriteLine("Pipeline: completed");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine($"Current node: {currentNode}");

        // Check for pending gate
        var gatesDir = FindGatesDir(outputDir);
        if (gatesDir != null)
        {
            var pendingFile = Path.Combine(gatesDir, "pending");
            if (File.Exists(pendingFile))
            {
                var gateId = (await File.ReadAllTextAsync(pendingFile)).Trim();
                Console.WriteLine($"Waiting on gate: {gateId}");
            }
        }

        Console.WriteLine("Pipeline: in progress");
    }

    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// logs — view artifacts
// ═════════════════════════════════════════════════════════════════════

static async Task<int> ShowLogs(string[] args)
{
    var outputDir = ResolveOutputDir(args);
    if (outputDir == null)
    {
        Console.Error.WriteLine("Could not find output directory. Use --dir <path> or run from a directory containing a .dot file.");
        return 1;
    }

    var logsDir = FindLogsDir(outputDir);
    if (logsDir == null)
    {
        Console.Error.WriteLine($"No logs directory found under {outputDir}");
        return 1;
    }

    var remaining = StripDirFlag(args);
    var nodeName = remaining.Length > 0 ? remaining[0] : null;

    if (nodeName == null)
    {
        // List all nodes
        Console.WriteLine("Node artifacts:");
        Console.WriteLine();

        var nodeDirs = Directory.GetDirectories(logsDir).OrderBy(d => d).ToList();
        foreach (var nodeDir in nodeDirs)
        {
            var node = Path.GetFileName(nodeDir);
            var statusFile = Path.Combine(nodeDir, "status.json");
            string statusText = "unknown";

            if (File.Exists(statusFile))
            {
                var statusDoc = JsonDocument.Parse(await File.ReadAllTextAsync(statusFile));
                statusText = statusDoc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
            }

            var files = Directory.GetFiles(nodeDir).Select(Path.GetFileName).ToList();
            Console.WriteLine($"  {node,-25} [{statusText}]  {string.Join(", ", files)}");
        }
        return 0;
    }

    // Show specific node
    var specificDir = Path.Combine(logsDir, nodeName);
    if (!Directory.Exists(specificDir))
    {
        Console.Error.WriteLine($"Node '{nodeName}' not found in {logsDir}");
        return 1;
    }

    Console.WriteLine($"Artifacts for node: {nodeName}");
    Console.WriteLine(new string('─', 50));

    // Print files in a readable order: status.json, prompt.md, response.md, then others
    var allFiles = Directory.GetFiles(specificDir).OrderBy(f =>
    {
        var name = Path.GetFileName(f);
        if (name == "status.json") return 0;
        if (name == "prompt.md") return 1;
        if (name == "response.md") return 2;
        return 3;
    }).ToList();

    foreach (var file in allFiles)
    {
        var fileName = Path.GetFileName(file);
        Console.WriteLine();
        Console.WriteLine($"── {fileName} ──");
        var content = await File.ReadAllTextAsync(file);
        // Truncate very long files
        if (content.Length > 5000)
        {
            Console.WriteLine(content[..5000]);
            Console.WriteLine($"... ({content.Length} chars total, truncated)");
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// web — launch dashboard
// ═════════════════════════════════════════════════════════════════════

static async Task<int> RunWeb(string[] args)
{
    var outputDir = ResolveOutputDir(args);
    // If no --dir, use global registry
    bool globalMode = outputDir == null;

    if (globalMode)
    {
        var registry = PruneRegistry();
        if (registry.Count == 0)
        {
            Console.Error.WriteLine("No known pipeline runs. Run a pipeline first with 'attractor run <dotfile>', or use --dir <path>.");
            return 1;
        }
    }

    // Parse --port
    int port = 5099;
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
            port = p;
    }

    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://localhost:{port}");
    var app = builder.Build();

    // ── JSON API ────────────────────────────────────────────────────

    app.MapGet("/api/pipelines", () =>
    {
        var pipelines = globalMode ? DiscoverAllPipelines() : DiscoverPipelines(outputDir!);
        // Fall back to global registry if local dir has no pipelines yet
        if (pipelines.Count == 0 && !globalMode)
            pipelines = DiscoverAllPipelines();
        return Results.Json(pipelines);
    });

    app.MapGet("/api/queue", async () =>
    {
        var pipelines = globalMode ? DiscoverAllPipelines() : DiscoverPipelines(outputDir!);
        if (pipelines.Count == 0 && !globalMode)
            pipelines = DiscoverAllPipelines();
        var items = new List<object>();

        foreach (var pObj in pipelines)
        {
            // Extract id and name from the anonymous object via JSON round-trip
            var pJson = JsonSerializer.Serialize(pObj);
            var pDoc = JsonDocument.Parse(pJson);
            var pId = pDoc.RootElement.GetProperty("id").GetString() ?? "";
            var pName = pDoc.RootElement.GetProperty("name").GetString() ?? "";
            var pStatus = pDoc.RootElement.GetProperty("status").GetString() ?? "";

            var pipelineDir = globalMode ? DecodePipelineId(pId) : ResolvePipelineDir(outputDir!, pId);
            if (pipelineDir == null) continue;

            var logsDir = Path.Combine(pipelineDir, "logs");
            var gatesDir = Path.Combine(pipelineDir, "gates");

            // Check for pending gate
            var pendingFile = Path.Combine(gatesDir, "pending");
            if (File.Exists(pendingFile))
            {
                var gateId = (await File.ReadAllTextAsync(pendingFile)).Trim();
                var qFile = Path.Combine(gatesDir, gateId, "question.json");
                string? question = null;
                List<string> options = new();
                if (File.Exists(qFile))
                {
                    var q = JsonDocument.Parse(await File.ReadAllTextAsync(qFile));
                    question = q.RootElement.GetProperty("text").GetString();
                    if (q.RootElement.TryGetProperty("options", out var opts))
                        foreach (var o in opts.EnumerateArray())
                            options.Add(o.GetString() ?? "");
                }
                items.Add(new
                {
                    type = "gate",
                    pipeline_id = pId,
                    pipeline_name = pName,
                    gate_id = gateId,
                    question,
                    options
                });
            }

            // Check for failed/retry nodes
            if (Directory.Exists(logsDir))
            {
                foreach (var nodeDir in Directory.GetDirectories(logsDir))
                {
                    var statusFile = Path.Combine(nodeDir, "status.json");
                    if (!File.Exists(statusFile)) continue;
                    try
                    {
                        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(statusFile));
                        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                        if (status == "retry" || status == "fail")
                        {
                            var nodeId = Path.GetFileName(nodeDir);
                            var notes = doc.RootElement.TryGetProperty("notes", out var n) ? n.GetString() : null;
                            items.Add(new
                            {
                                type = "failed_node",
                                pipeline_id = pId,
                                pipeline_name = pName,
                                node_id = nodeId,
                                status,
                                notes
                            });
                        }
                    }
                    catch { /* skip corrupted status */ }
                }
            }

            // Add running pipeline with current node
            if (pStatus == "in_progress")
            {
                var checkpointPath = Path.Combine(logsDir, "checkpoint.json");
                if (File.Exists(checkpointPath))
                {
                    try
                    {
                        var cp = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
                        var currentNode = cp.RootElement.GetProperty("CurrentNodeId").GetString();
                        var hasPendingGate = File.Exists(pendingFile);
                        if (!hasPendingGate) // Don't duplicate if already shown as gate
                        {
                            items.Add(new
                            {
                                type = "running",
                                pipeline_id = pId,
                                pipeline_name = pName,
                                node_id = currentNode,
                                status = "running",
                                notes = $"Currently executing {currentNode}"
                            });
                        }
                    }
                    catch { }
                }
            }
        }

        return Results.Json(items);
    });

    app.MapGet("/api/pipeline/{id}/status", async (string id) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null || !Directory.Exists(pipelineDir)) return Results.NotFound();

        var logsDir = Path.Combine(pipelineDir, "logs");
        var checkpointPath = Path.Combine(logsDir, "checkpoint.json");
        if (!File.Exists(checkpointPath))
            return Results.Json(new { status = "not_started", nodes = Array.Empty<object>() });

        var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
        var root = checkpoint.RootElement;
        var currentNode = root.GetProperty("CurrentNodeId").GetString();

        // Raw list preserves duplicates for iteration tracking
        var rawCompleted = root.GetProperty("CompletedNodes").EnumerateArray()
            .Select(e => e.GetString() ?? "").ToList();
        var uniqueCompleted = rawCompleted.Distinct().ToList();

        // Compute iteration: find where a node first repeats — that starts a new iteration.
        // Walk the raw list and detect the last loop-back point.
        int iteration = 1;
        var currentIterationNodes = new List<string>();
        {
            var seen = new HashSet<string>();
            int lastLoopStart = 0;
            for (int idx = 0; idx < rawCompleted.Count; idx++)
            {
                if (!seen.Add(rawCompleted[idx]))
                {
                    // This node was seen before — a new iteration starts here
                    iteration++;
                    seen.Clear();
                    seen.Add(rawCompleted[idx]);
                    lastLoopStart = idx;
                }
            }
            // Everything from lastLoopStart onward is the current iteration
            for (int idx = lastLoopStart; idx < rawCompleted.Count; idx++)
            {
                if (!currentIterationNodes.Contains(rawCompleted[idx]))
                    currentIterationNodes.Add(rawCompleted[idx]);
            }
        }
        if (currentIterationNodes.Count == 0)
            currentIterationNodes = uniqueCompleted;

        var nodes = new List<object>();
        foreach (var node in uniqueCompleted)
        {
            var statusFile = Path.Combine(logsDir, node, "status.json");
            string status = "done";
            string? preferred = null;
            string? notes = null;
            if (File.Exists(statusFile))
            {
                var doc = JsonDocument.Parse(await File.ReadAllTextAsync(statusFile));
                var sr = doc.RootElement;
                status = sr.TryGetProperty("status", out var s) ? s.GetString() ?? "done" : "done";
                preferred = sr.TryGetProperty("preferred_label", out var p) ? p.GetString() : null;
                notes = sr.TryGetProperty("notes", out var n) ? n.GetString() : null;
            }
            var inCurrentIteration = currentIterationNodes.Contains(node);
            nodes.Add(new { id = node, status, preferred_label = preferred, notes, current_iteration = inCurrentIteration });
        }

        var pipelineStatus = File.Exists(Path.Combine(logsDir, "result.json")) ? "completed" : "in_progress";

        // Check for pending gate
        string? pendingGate = null;
        var gatesDir = Path.Combine(pipelineDir, "gates");
        var pendingFile = Path.Combine(gatesDir, "pending");
        if (File.Exists(pendingFile))
            pendingGate = (await File.ReadAllTextAsync(pendingFile)).Trim();

        return Results.Json(new
        {
            status = pipelineStatus,
            current_node = currentNode,
            pending_gate = pendingGate,
            iteration,
            nodes
        });
    });

    app.MapGet("/api/pipeline/{id}/gates", async (string id) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null) return Results.NotFound();

        var gatesDir = Path.Combine(pipelineDir, "gates");
        if (!Directory.Exists(gatesDir))
            return Results.Json(Array.Empty<object>());

        var pendingFile = Path.Combine(gatesDir, "pending");
        var pendingGateId = File.Exists(pendingFile) ? (await File.ReadAllTextAsync(pendingFile)).Trim() : null;

        // Build phase summaries — only artifacts created since the last answered gate
        var logsDir2 = Path.Combine(pipelineDir, "logs");
        var phaseSummaries = new List<object>();
        if (Directory.Exists(logsDir2))
        {
            // Find the most recent answered gate's timestamp as cutoff
            DateTime cutoff = DateTime.MinValue;
            foreach (var gDir in Directory.GetDirectories(gatesDir))
            {
                var aFile = Path.Combine(gDir, "answer.json");
                if (File.Exists(aFile))
                {
                    var mtime = File.GetLastWriteTimeUtc(aFile);
                    if (mtime > cutoff) cutoff = mtime;
                }
            }

            foreach (var nodeDir in Directory.GetDirectories(logsDir2).OrderBy(d => d))
            {
                var nodeName = Path.GetFileName(nodeDir);
                foreach (var file in Directory.GetFiles(nodeDir, "*.md").OrderByDescending(f => f))
                {
                    // Only include files newer than the last answered gate
                    if (cutoff > DateTime.MinValue && File.GetLastWriteTimeUtc(file) <= cutoff)
                        continue;

                    var fName = Path.GetFileName(file);
                    var stem = Path.GetFileNameWithoutExtension(fName);
                    if (fName.EndsWith(".md") && stem.Contains('-') && stem == stem.ToUpperInvariant())
                    {
                        var lines = (await File.ReadAllTextAsync(file)).Split('\n');
                        var preview = string.Join("\n", lines.Take(20));
                        if (lines.Length > 20) preview += "\n...";
                        phaseSummaries.Add(new { node = nodeName, file = fName, preview });
                    }
                }
            }
        }

        var gates = new List<object>();
        foreach (var gateDir in Directory.GetDirectories(gatesDir).OrderByDescending(d => Path.GetFileName(d)))
        {
            var qFile = Path.Combine(gateDir, "question.json");
            if (!File.Exists(qFile)) continue;

            var q = JsonDocument.Parse(await File.ReadAllTextAsync(qFile));
            var qr = q.RootElement;
            var gateId = qr.GetProperty("gate_id").GetString();
            var text = qr.GetProperty("text").GetString();
            var options = qr.TryGetProperty("options", out var opts)
                ? opts.EnumerateArray().Select(o => o.GetString()).ToList()
                : new List<string?>();

            var answerFile = Path.Combine(gateDir, "answer.json");
            string? answerText = null;
            string? answerChoice = null;
            if (File.Exists(answerFile))
            {
                var a = JsonDocument.Parse(await File.ReadAllTextAsync(answerFile));
                answerText = a.RootElement.GetProperty("text").GetString();
                if (a.RootElement.TryGetProperty("selected_options", out var selOpts))
                {
                    var first = selOpts.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                        answerChoice = first.GetString();
                }
                answerChoice ??= answerText;
            }

            gates.Add(new
            {
                gate_id = gateId,
                question = text,
                options,
                answer = answerText,
                answer_choice = answerChoice,
                is_pending = gateId == pendingGateId
            });
        }

        return Results.Json(new { gates, phase_summaries = phaseSummaries });
    });

    app.MapPost("/api/pipeline/{id}/gates/{gateId}/answer", async (string id, string gateId, HttpRequest request) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null) return Results.NotFound();

        var body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
        var choice = body.GetProperty("choice").GetString();
        if (string.IsNullOrEmpty(choice)) return Results.BadRequest("choice is required");

        // Optional freeform text (e.g. revision instructions)
        string? freeText = null;
        if (body.TryGetProperty("text", out var textEl))
            freeText = textEl.GetString();

        var gateDir = Path.Combine(pipelineDir, "gates", gateId);
        if (!Directory.Exists(gateDir)) return Results.NotFound();

        // Resolve by number or label
        var qFile = Path.Combine(gateDir, "question.json");
        var optionsList = new List<string>();
        if (File.Exists(qFile))
        {
            var q = JsonDocument.Parse(await File.ReadAllTextAsync(qFile));
            if (q.RootElement.TryGetProperty("options", out var opts))
            {
                foreach (var o in opts.EnumerateArray())
                    optionsList.Add(o.GetString() ?? "");
            }
        }

        string resolvedChoice;
        if (int.TryParse(choice, out int num) && num >= 1 && num <= optionsList.Count)
            resolvedChoice = optionsList[num - 1];
        else
        {
            var match = optionsList.FirstOrDefault(o => o.Equals(choice, StringComparison.OrdinalIgnoreCase));
            resolvedChoice = match ?? choice;
        }

        // Use freeform text if provided, otherwise use the choice label
        var answerText = !string.IsNullOrEmpty(freeText) ? freeText : resolvedChoice;

        var answerPath = Path.Combine(gateDir, "answer.json");
        var answer = new { text = answerText, selected_options = new[] { resolvedChoice } };
        await File.WriteAllTextAsync(answerPath, JsonSerializer.Serialize(answer, JsonOpts()));

        return Results.Json(new { answered = resolvedChoice, text = answerText });
    });

    app.MapGet("/api/pipeline/{id}/logs", async (string id) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null) return Results.NotFound();

        var logsDir = Path.Combine(pipelineDir, "logs");
        if (!Directory.Exists(logsDir))
            return Results.Json(Array.Empty<object>());

        var nodes = new List<object>();
        foreach (var nodeDir in Directory.GetDirectories(logsDir).OrderBy(d => d))
        {
            var node = Path.GetFileName(nodeDir);
            var statusFile = Path.Combine(nodeDir, "status.json");
            string status = "unknown";
            if (File.Exists(statusFile))
            {
                var doc = JsonDocument.Parse(await File.ReadAllTextAsync(statusFile));
                status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
            }
            var files = Directory.GetFiles(nodeDir).Select(Path.GetFileName).ToList();
            nodes.Add(new { id = node, status, files });
        }

        return Results.Json(nodes);
    });

    app.MapGet("/api/pipeline/{id}/logs/{node}/{file}", async (string id, string node, string file) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null) return Results.NotFound();

        var filePath = Path.Combine(pipelineDir, "logs", node, file);
        if (!File.Exists(filePath)) return Results.NotFound();

        var content = await File.ReadAllTextAsync(filePath);
        var contentType = file.EndsWith(".json") ? "application/json" : "text/plain";
        return Results.Text(content, contentType);
    });

    app.MapGet("/api/pipeline/{id}/summaries", async (string id) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null || !Directory.Exists(pipelineDir)) return Results.NotFound();

        var logsDir = Path.Combine(pipelineDir, "logs");
        if (!Directory.Exists(logsDir))
            return Results.Json(new { nodes = new Dictionary<string, object>() });

        var checkpointPath = Path.Combine(logsDir, "checkpoint.json");
        string? currentNodeId = null;
        if (File.Exists(checkpointPath))
        {
            try
            {
                var cp = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
                currentNodeId = cp.RootElement.GetProperty("CurrentNodeId").GetString();
            }
            catch { }
        }

        // Determine current task from running node's prompt.md
        string? currentTask = null;
        if (currentNodeId != null)
        {
            var promptPath = Path.Combine(logsDir, currentNodeId, "prompt.md");
            if (File.Exists(promptPath))
            {
                try
                {
                    var firstLine = (await File.ReadAllLinesAsync(promptPath)).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    currentTask = firstLine?.TrimStart('#', ' ');
                }
                catch { }
            }
        }

        var nodeSummaries = new Dictionary<string, object>();
        foreach (var nodeDir in Directory.GetDirectories(logsDir).OrderBy(d => d))
        {
            var nodeName = Path.GetFileName(nodeDir);
            var files = Directory.GetFiles(nodeDir);

            // Detect node type by looking at artifact filenames
            var fileNames = files.Select(Path.GetFileName).ToList();

            // Plan nodes: contain PLAN-*.md
            var planFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("PLAN-") && f.EndsWith(".md"));
            if (planFile != null)
            {
                var text = await File.ReadAllTextAsync(planFile);
                nodeSummaries[nodeName] = new
                {
                    summary = ExtractPlanSummary(text),
                    artifact = Path.GetFileName(planFile)
                };
                continue;
            }

            // Progress/implement nodes: contain PROGRESS-*.md
            var progressFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("PROGRESS-") && f.EndsWith(".md"));
            if (progressFile != null)
            {
                var text = await File.ReadAllTextAsync(progressFile);
                nodeSummaries[nodeName] = new
                {
                    summary = ExtractProgressSummary(text),
                    commits = ExtractProgressCommits(text),
                    artifact = Path.GetFileName(progressFile)
                };
                continue;
            }

            // Validation nodes: contain VALIDATION-*.md or VALIDATE-*.md
            var validationFile = files.FirstOrDefault(f =>
                (Path.GetFileName(f).StartsWith("VALIDATION-") || Path.GetFileName(f).StartsWith("VALIDATE-")) && f.EndsWith(".md"));
            if (validationFile != null)
            {
                var text = await File.ReadAllTextAsync(validationFile);
                nodeSummaries[nodeName] = new
                {
                    summary = ExtractValidationSummary(text),
                    checks = ExtractValidationChecks(text),
                    artifact = Path.GetFileName(validationFile)
                };
                continue;
            }

            // Critique nodes: contain CRITIQUE-*.md
            var critiqueFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("CRITIQUE-") && f.EndsWith(".md"));
            if (critiqueFile != null)
            {
                var text = await File.ReadAllTextAsync(critiqueFile);
                nodeSummaries[nodeName] = new
                {
                    summary = ExtractCritiqueSummary(text),
                    artifact = Path.GetFileName(critiqueFile)
                };
                continue;
            }

            // Fallback: try response.md
            var responseFile = files.FirstOrDefault(f => Path.GetFileName(f) == "response.md");
            if (responseFile != null)
            {
                var text = await File.ReadAllTextAsync(responseFile);
                var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var firstMeaningful = lines.FirstOrDefault(l => !l.StartsWith('#'))?.Trim();
                nodeSummaries[nodeName] = new
                {
                    summary = firstMeaningful != null && firstMeaningful.Length > 80
                        ? firstMeaningful[..80] + "..."
                        : firstMeaningful ?? "Completed"
                };
            }
        }

        return Results.Json(new
        {
            current_task = currentTask,
            current_node = currentNodeId,
            nodes = nodeSummaries
        });
    });

    // ── Dashboard HTML ──────────────────────────────────────────────

    app.MapGet("/", () => Results.Content(DashboardHtml(), "text/html"));

    Console.WriteLine($"Dashboard: http://localhost:{port}");
    if (globalMode)
        Console.WriteLine($"Mode:      global (all registered pipelines)");
    else
        Console.WriteLine($"Watching:  {outputDir}");
    Console.WriteLine("Press Ctrl-C to stop.");

    await app.RunAsync();
    return 0;
}

static List<object> DiscoverPipelines(string outputDir)
{
    var pipelines = new List<object>();

    void AddPipeline(string dir)
    {
        var name = Path.GetFileName(dir);
        string status;
        if (File.Exists(Path.Combine(dir, "logs", "result.json")))
            status = "completed";
        else if (File.Exists(Path.Combine(dir, "logs", "checkpoint.json")))
            status = "in_progress";
        else
            status = "unknown";
        var hasPendingGate = File.Exists(Path.Combine(dir, "gates", "pending"));
        pipelines.Add(new { id = name, name, status, has_pending_gate = hasPendingGate });
    }

    // Check if outputDir itself is a pipeline (has logs/)
    if (Directory.Exists(Path.Combine(outputDir, "logs")))
        AddPipeline(outputDir);

    // Check subdirectories for multiple pipelines
    foreach (var subdir in Directory.GetDirectories(outputDir))
    {
        if (Directory.Exists(Path.Combine(subdir, "logs")))
            AddPipeline(subdir);
    }

    return pipelines;
}

static string? ResolvePipelineDir(string outputDir, string name)
{
    // Try decoding as a pipeline ID first (base64url path)
    var decoded = DecodePipelineId(name);
    if (decoded != null && Directory.Exists(decoded))
        return decoded;

    // Direct match: outputDir is the pipeline itself
    if (Path.GetFileName(outputDir) == name && Directory.Exists(Path.Combine(outputDir, "logs")))
        return outputDir;

    // Subdirectory match
    var sub = Path.Combine(outputDir, name);
    if (Directory.Exists(sub) && Directory.Exists(Path.Combine(sub, "logs")))
        return sub;

    return null;
}

static string EncodePipelineId(string outputDir)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(outputDir);
    return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

static string? DecodePipelineId(string id)
{
    try
    {
        var padded = id.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
        var bytes = Convert.FromBase64String(padded);
        var path = System.Text.Encoding.UTF8.GetString(bytes);
        if (Directory.Exists(path) && Directory.Exists(Path.Combine(path, "logs")))
            return path;
        return null;
    }
    catch { return null; }
}

static List<object> DiscoverAllPipelines()
{
    var registry = PruneRegistry();
    var pipelines = new List<object>();

    foreach (var entry in registry)
    {
        var dir = entry.output_dir;
        if (!Directory.Exists(dir)) continue;

        string status;
        if (File.Exists(Path.Combine(dir, "logs", "result.json")))
            status = "completed";
        else if (File.Exists(Path.Combine(dir, "logs", "checkpoint.json")))
            status = "in_progress";
        else
            status = "unknown";

        var hasPendingGate = File.Exists(Path.Combine(dir, "gates", "pending"));
        var id = EncodePipelineId(dir);

        pipelines.Add(new
        {
            id,
            name = entry.name,
            dotfile = entry.dotfile,
            output_dir = dir,
            started = entry.started,
            status,
            has_pending_gate = hasPendingGate
        });
    }

    return pipelines;
}

// ── Summary extraction helpers ──────────────────────────────────────

static string ExtractPlanSummary(string text)
{
    try
    {
        var wiCount = Regex.Matches(text, @"^### (?:WI|MF)-", RegexOptions.Multiline).Count;
        var criticalCount = Regex.Matches(text, @"\*\*critical\*\*|priority:\s*critical", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
        if (wiCount == 0) return "Plan created";
        return criticalCount > 0
            ? $"{wiCount} work items ({criticalCount} critical)"
            : $"{wiCount} work items";
    }
    catch { return "Plan created"; }
}

static string ExtractProgressSummary(string text)
{
    try
    {
        var done = Regex.Matches(text, @"\bDONE\b", RegexOptions.Multiline).Count;
        var failed = Regex.Matches(text, @"\bFAILED\b", RegexOptions.Multiline).Count;
        var skipped = Regex.Matches(text, @"\bSKIPPED\b", RegexOptions.Multiline).Count;
        var total = done + failed + skipped;
        if (total == 0) return "In progress";
        var parts = new List<string> { $"{done}/{total} commits done" };
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        return string.Join(", ", parts);
    }
    catch { return "In progress"; }
}

static List<object> ExtractProgressCommits(string text)
{
    try
    {
        var commits = new List<object>();
        // Match lines like: | WI-1 | DONE | message | or commit-style rows
        foreach (Match m in Regex.Matches(text, @"(?:^|\|)\s*(WI-\d+|MF-\d+|[\w-]+)\s*\|\s*(DONE|FAILED|SKIPPED)\s*\|\s*([^|\n]*)", RegexOptions.Multiline))
        {
            commits.Add(new { id = m.Groups[1].Value.Trim(), status = m.Groups[2].Value.Trim(), message = m.Groups[3].Value.Trim() });
        }
        // Also match bullet-style: - WI-1: DONE - message
        foreach (Match m in Regex.Matches(text, @"^[-*]\s*(WI-\d+|MF-\d+):\s*(DONE|FAILED|SKIPPED)\s*[-–—]\s*(.*)", RegexOptions.Multiline))
        {
            if (!commits.Any(c => JsonSerializer.Serialize(c).Contains(m.Groups[1].Value)))
                commits.Add(new { id = m.Groups[1].Value.Trim(), status = m.Groups[2].Value.Trim(), message = m.Groups[3].Value.Trim() });
        }
        return commits;
    }
    catch { return new List<object>(); }
}

static string ExtractValidationSummary(string text)
{
    try
    {
        var pass = Regex.Matches(text, @"\bpass(?:ed)?\b", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
        var fail = Regex.Matches(text, @"\bfail(?:ed)?\b", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
        // Also check for checkmark/x patterns
        pass += Regex.Matches(text, @"✓|✅|\[x\]", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
        fail += Regex.Matches(text, @"✗|❌|\[ \]", RegexOptions.Multiline).Count;
        if (pass == 0 && fail == 0) return "Validation complete";
        return $"{pass} passed, {fail} failed";
    }
    catch { return "Validation complete"; }
}

static List<object> ExtractValidationChecks(string text)
{
    try
    {
        var checks = new List<object>();
        // Match table rows: | check name | pass/fail |
        foreach (Match m in Regex.Matches(text, @"\|\s*([^|]+?)\s*\|\s*(pass(?:ed)?|fail(?:ed)?|✓|✗|✅|❌)\s*\|", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value.Trim();
            var result = m.Groups[2].Value.Trim().ToLowerInvariant();
            if (name.Contains("---") || name.ToLowerInvariant() == "check" || name.ToLowerInvariant() == "name") continue;
            var passed = result.StartsWith("pass") || result == "✓" || result == "✅";
            checks.Add(new { name, passed });
        }
        // Match checklist items: - [x] name or - [ ] name
        foreach (Match m in Regex.Matches(text, @"^[-*]\s*\[([ x])\]\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var passed = m.Groups[1].Value.ToLowerInvariant() == "x";
            var name = m.Groups[2].Value.Trim();
            checks.Add(new { name, passed });
        }
        return checks;
    }
    catch { return new List<object>(); }
}

static string ExtractCritiqueSummary(string text)
{
    try
    {
        // Count items under MUST FIX heading
        var mustFixSection = Regex.Match(text, @"(?:^#+\s*MUST\s*FIX.*?\n)((?:[-*]\s+.*\n?)*)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var mustFix = mustFixSection.Success ? Regex.Matches(mustFixSection.Groups[1].Value, @"^[-*]\s+", RegexOptions.Multiline).Count : 0;

        // Count items under SHOULD FIX heading
        var shouldFixSection = Regex.Match(text, @"(?:^#+\s*SHOULD\s*FIX.*?\n)((?:[-*]\s+.*\n?)*)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var shouldFix = shouldFixSection.Success ? Regex.Matches(shouldFixSection.Groups[1].Value, @"^[-*]\s+", RegexOptions.Multiline).Count : 0;

        if (mustFix == 0 && shouldFix == 0)
        {
            // Fallback: count any bullet items
            var bullets = Regex.Matches(text, @"^[-*]\s+", RegexOptions.Multiline).Count;
            return bullets > 0 ? $"{bullets} items noted" : "Critique complete";
        }
        return $"{mustFix} must-fix, {shouldFix} should-fix";
    }
    catch { return "Critique complete"; }
}

static string DashboardHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Attractor Dashboard</title>
<style>
  :root {
    --bg: #0d1117; --surface: #161b22; --border: #30363d;
    --text: #e6edf3; --text2: #8b949e; --accent: #58a6ff;
    --green: #3fb950; --yellow: #d29922; --red: #f85149;
    --purple: #bc8cff;
  }
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
         background: var(--bg); color: var(--text); padding: 24px; }
  h1 { font-size: 20px; margin-bottom: 8px; }
  .subtitle { color: var(--text2); font-size: 13px; margin-bottom: 24px; }
  .pipeline-list { display: flex; flex-direction: column; gap: 16px; }
  .pipeline-card { background: var(--surface); border: 1px solid var(--border);
                   border-radius: 8px; padding: 20px; cursor: pointer; transition: border-color 0.15s; }
  .pipeline-card:hover { border-color: var(--accent); }
  .pipeline-card.selected { border-color: var(--accent); }
  .pipeline-header { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
  .pipeline-name { font-size: 16px; font-weight: 600; }
  .badge { font-size: 11px; padding: 2px 8px; border-radius: 12px; font-weight: 500; }
  .badge-completed { background: rgba(63,185,80,0.15); color: var(--green); }
  .badge-in_progress { background: rgba(210,153,34,0.15); color: var(--yellow); }
  .badge-unknown { background: rgba(139,148,158,0.15); color: var(--text2); }
  .badge-gate { background: rgba(248,81,73,0.15); color: var(--red); animation: pulse 2s infinite; }
  @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.6; } }
  .nodes-bar { display: flex; gap: 2px; margin-top: 8px; height: 6px; border-radius: 3px; overflow: hidden; }
  .node-seg { flex: 1; border-radius: 2px; }
  .node-seg.success { background: var(--green); }
  .node-seg.retry { background: var(--yellow); }
  .node-seg.pending { background: var(--border); }
  .node-seg.prior { background: var(--border); opacity: 0.4; }
  .node-seg.current { background: var(--yellow); }
  .badge-iter { background: rgba(188,140,255,0.15); color: var(--purple); }

  .detail-panel { margin-top: 24px; }
  .section { background: var(--surface); border: 1px solid var(--border);
             border-radius: 8px; padding: 16px; margin-bottom: 16px; }
  .section-title { font-size: 14px; font-weight: 600; margin-bottom: 12px; color: var(--accent); }
  .node-row { display: flex; align-items: center; gap: 8px; padding: 6px 0;
              border-bottom: 1px solid var(--border); font-size: 13px; }
  .node-row:last-child { border-bottom: none; }
  .node-icon { width: 16px; text-align: center; }
  .node-name { flex: 1; font-family: 'SF Mono', SFMono-Regular, Consolas, monospace; }
  .node-status { color: var(--text2); font-size: 12px; }
  .node-files { display: flex; gap: 4px; flex-wrap: wrap; }
  .file-chip { font-size: 11px; padding: 1px 6px; background: rgba(88,166,255,0.1);
               color: var(--accent); border-radius: 4px; cursor: pointer; font-family: monospace; }
  .file-chip:hover { background: rgba(88,166,255,0.2); }

  .gate-card { background: var(--bg); border: 1px solid var(--border);
               border-radius: 6px; padding: 12px; margin-bottom: 8px; }
  .gate-card.pending { border-color: var(--yellow); }
  .gate-id { font-size: 11px; color: var(--text2); font-family: monospace; }
  .gate-question { font-size: 14px; margin: 4px 0 8px; }
  .gate-options { display: flex; gap: 8px; flex-wrap: wrap; }
  .gate-btn { padding: 6px 16px; border: 1px solid var(--border); border-radius: 6px;
              background: var(--surface); color: var(--text); cursor: pointer; font-size: 13px;
              transition: all 0.15s; }
  .gate-btn:hover { border-color: var(--accent); color: var(--accent); }
  .gate-btn.revise { border-color: var(--yellow); color: var(--yellow); }
  .gate-btn.revise:hover { background: rgba(210,153,34,0.1); }
  .gate-btn.approve { border-color: var(--green); color: var(--green); }
  .gate-btn.approve:hover { background: rgba(63,185,80,0.1); }
  .gate-btn.answered { background: rgba(63,185,80,0.1); border-color: var(--green); color: var(--green);
                       cursor: default; }
  .gate-btn.answered.revise-answered { background: rgba(210,153,34,0.1); border-color: var(--yellow); color: var(--yellow); }
  .steer-form { margin-top: 10px; }
  .steer-textarea { background: var(--bg); border: 1px solid var(--border); border-radius: 6px;
                    color: var(--text); padding: 10px; font-size: 13px; font-family: inherit;
                    resize: vertical; min-height: 60px; width: 100%; }
  .steer-textarea:focus { outline: none; border-color: var(--accent); }
  .answer-note { margin-top: 8px; font-size: 12px; color: var(--text2); font-style: italic;
                 border-left: 2px solid var(--border); padding-left: 8px; white-space: pre-wrap; }
  .phase-summaries { margin: 8px 0 12px; }
  .phase-detail { margin-bottom: 4px; }
  .phase-detail summary { font-size: 12px; color: var(--accent); cursor: pointer; padding: 4px 0; }
  .phase-detail summary:hover { color: var(--text); }
  .phase-preview { font-size: 12px; color: var(--text2); white-space: pre-wrap; word-break: break-word;
                   line-height: 1.4; margin: 4px 0 8px; padding: 8px; background: var(--bg);
                   border: 1px solid var(--border); border-radius: 4px; max-height: 300px; overflow: auto; }

  .modal-overlay { position: fixed; top: 0; left: 0; width: 100%; height: 100%;
                   background: rgba(0,0,0,0.6); display: flex; align-items: center;
                   justify-content: center; z-index: 100; }
  .modal { background: var(--surface); border: 1px solid var(--border); border-radius: 12px;
           padding: 24px; max-width: 800px; width: 90%; max-height: 80vh; overflow: auto; }
  .modal-title { font-size: 14px; margin-bottom: 12px; color: var(--accent); }
  .modal pre { font-size: 13px; white-space: pre-wrap; word-break: break-word;
               line-height: 1.5; color: var(--text); }
  .modal-close { float: right; background: none; border: none; color: var(--text2);
                 cursor: pointer; font-size: 18px; }
  .modal-close:hover { color: var(--text); }
  .md-body { font-size: 14px; line-height: 1.6; color: var(--text); }
  .md-body h1 { font-size: 20px; font-weight: 700; margin: 16px 0 8px; padding-bottom: 4px; border-bottom: 1px solid var(--border); }
  .md-body h2 { font-size: 17px; font-weight: 600; margin: 14px 0 6px; padding-bottom: 3px; border-bottom: 1px solid var(--border); }
  .md-body h3 { font-size: 15px; font-weight: 600; margin: 12px 0 4px; }
  .md-body h4, .md-body h5, .md-body h6 { font-size: 14px; font-weight: 600; margin: 10px 0 4px; }
  .md-body p { margin: 6px 0; }
  .md-body ul, .md-body ol { margin: 6px 0; padding-left: 24px; }
  .md-body li { margin: 2px 0; }
  .md-body code { font-family: 'SF Mono', SFMono-Regular, Consolas, monospace; font-size: 12px;
                  background: var(--bg); border: 1px solid var(--border); border-radius: 3px; padding: 1px 4px; }
  .md-body pre { background: var(--bg); border: 1px solid var(--border); border-radius: 6px;
                 padding: 12px; margin: 8px 0; overflow-x: auto; }
  .md-body pre code { border: none; padding: 0; background: none; font-size: 12px; }
  .md-body blockquote { border-left: 3px solid var(--accent); padding-left: 12px; margin: 8px 0; color: var(--text2); }
  .md-body table { border-collapse: collapse; margin: 8px 0; width: 100%; }
  .md-body th, .md-body td { border: 1px solid var(--border); padding: 6px 10px; font-size: 13px; text-align: left; }
  .md-body th { background: var(--bg); font-weight: 600; }
  .md-body hr { border: none; border-top: 1px solid var(--border); margin: 12px 0; }
  .md-body strong { font-weight: 600; }
  .md-body em { font-style: italic; }
  .md-body a { color: var(--accent); text-decoration: none; }
  .md-body a:hover { text-decoration: underline; }
  .md-body img { max-width: 100%; }

  .empty { color: var(--text2); font-size: 13px; padding: 12px 0; }
  .refresh-note { color: var(--text2); font-size: 11px; margin-top: 4px; }

  .queue-section { margin-bottom: 24px; }
  .queue-title { font-size: 14px; font-weight: 600; margin-bottom: 12px; color: var(--text); }
  .queue-cards { display: flex; flex-direction: column; gap: 10px; }
  .queue-card { background: var(--surface); border: 1px solid var(--border); border-radius: 8px;
                padding: 14px 16px; display: flex; align-items: flex-start; gap: 12px; }
  .queue-card.gate { border-left: 3px solid var(--red); }
  .queue-card.failed { border-left: 3px solid var(--yellow); }
  .queue-card.running { border-left: 3px solid var(--accent); }
  .queue-icon { font-size: 18px; flex-shrink: 0; margin-top: 2px; }
  .queue-body { flex: 1; min-width: 0; }
  .queue-pipeline { font-size: 11px; color: var(--text2); margin-bottom: 2px; font-family: monospace; }
  .queue-label { font-size: 14px; font-weight: 500; margin-bottom: 4px; }
  .queue-detail { font-size: 12px; color: var(--text2); }
  .queue-actions { display: flex; gap: 6px; margin-top: 8px; flex-wrap: wrap; }
  .queue-empty { color: var(--text2); font-size: 13px; padding: 8px 0; }

  .node-summary { font-size: 12px; color: var(--text2); margin-top: 2px; padding-left: 24px; }
  .progress-commits { margin-top: 6px; padding-left: 24px; }
  .commit-row { display: flex; align-items: center; gap: 6px; font-size: 12px; padding: 2px 0; font-family: monospace; }
  .commit-row .done { color: var(--green); }
  .commit-row .failed { color: var(--red); }
  .commit-row .skipped { color: var(--text2); }
  .validation-scorecard { display: flex; flex-wrap: wrap; gap: 3px; margin-top: 6px; padding-left: 24px; }
  .check-dot { width: 14px; height: 14px; border-radius: 3px; display: inline-block; cursor: default; }
  .check-pass { background: var(--green); }
  .check-fail { background: var(--red); }
  .scorecard-failures { margin-top: 4px; padding-left: 24px; font-size: 11px; color: var(--red); }
  .current-task { background: rgba(210,153,34,0.12); border: 1px solid var(--yellow); border-radius: 6px;
                  padding: 8px 12px; margin-bottom: 12px; font-size: 13px; color: var(--yellow); }
</style>
</head>
<body>

<h1>Attractor Dashboard</h1>
<div class="subtitle">Auto-refreshes every 3s &middot; <span id="last-update"></span> &middot; <label style="cursor:pointer;user-select:none;"><input type="checkbox" id="hide-completed" onchange="refresh()" style="vertical-align:middle;"> Hide completed</label></div>

<div class="queue-section" id="queue-section"></div>
<div class="pipeline-list" id="pipelines"></div>
<div class="detail-panel" id="detail" style="display:none"></div>
<div id="modal-root"></div>

<script>
let selectedPipeline = null;
let pollTimer = null;

// ── DOM morpher: patches in-place instead of innerHTML rebuild ──────
function morph(oldNode, newNode) {
  if (oldNode.nodeType !== newNode.nodeType || oldNode.nodeName !== newNode.nodeName) {
    oldNode.parentNode.replaceChild(newNode.cloneNode(true), oldNode);
    return;
  }
  if (oldNode.nodeType === Node.TEXT_NODE) {
    if (oldNode.textContent !== newNode.textContent) oldNode.textContent = newNode.textContent;
    return;
  }
  if (oldNode.nodeType !== Node.ELEMENT_NODE) return;
  const tag = oldNode.tagName;
  if (tag === 'TEXTAREA' || tag === 'INPUT' || tag === 'SELECT') return;
  if (tag === 'DETAILS') {
    const wasOpen = oldNode.open;
    syncAttrs(oldNode, newNode);
    morphChildren(oldNode, newNode);
    oldNode.open = wasOpen;
    return;
  }
  syncAttrs(oldNode, newNode);
  morphChildren(oldNode, newNode);
}

function syncAttrs(oldEl, newEl) {
  for (const a of [...oldEl.attributes]) {
    if (!newEl.hasAttribute(a.name)) oldEl.removeAttribute(a.name);
  }
  for (const a of [...newEl.attributes]) {
    if (oldEl.getAttribute(a.name) !== a.value) oldEl.setAttribute(a.name, a.value);
  }
}

function morphChildren(oldEl, newEl) {
  const oldKids = [...oldEl.childNodes];
  const newKids = [...newEl.childNodes];
  const max = Math.max(oldKids.length, newKids.length);
  for (let i = 0; i < max; i++) {
    if (i >= oldKids.length) {
      oldEl.appendChild(newKids[i].cloneNode(true));
    } else if (i >= newKids.length) {
      while (oldEl.childNodes.length > newKids.length) oldEl.removeChild(oldEl.lastChild);
      break;
    } else {
      morph(oldKids[i], newKids[i]);
    }
  }
}

function patchHtml(el, html) {
  const tmp = document.createElement(el.tagName);
  tmp.innerHTML = html;
  // Only morph children — don't touch the root element's own attributes/style
  morphChildren(el, tmp);
}

// ── Helpers ─────────────────────────────────────────────────────────

async function fetchJson(url) {
  const r = await fetch(url);
  if (!r.ok) return null;
  return r.json();
}

function escapeHtml(s) {
  return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

// ── Pipeline list ───────────────────────────────────────────────────

async function loadPipelines() {
  let pipelines = await fetchJson('/api/pipelines');
  if (!pipelines) return;
  if (document.getElementById('hide-completed').checked) {
    pipelines = pipelines.filter(p => p.status !== 'completed');
  }
  const html = pipelines.map(p => {
    const pid = p.id || p.name;
    return `
    <div class="pipeline-card ${selectedPipeline === pid ? 'selected' : ''}"
         onclick="selectPipeline('${pid}')">
      <div class="pipeline-header">
        <span class="pipeline-name">${p.name}</span>
        <span class="badge badge-${p.status}">${p.status.replace('_',' ')}</span>
        ${p.has_pending_gate ? '<span class="badge badge-gate">gate pending</span>' : ''}
      </div>
      ${p.dotfile ? `<div style="font-size:11px;color:var(--text2);margin-top:4px;font-family:monospace;">${p.dotfile}</div>` : ''}
      ${p.started ? `<div style="font-size:11px;color:var(--text2);margin-top:2px;">Started: ${new Date(p.started).toLocaleString()}</div>` : ''}
    </div>`;
  }).join('');
  patchHtml(document.getElementById('pipelines'), html);
  document.getElementById('last-update').textContent = new Date().toLocaleTimeString();
}

async function selectPipeline(id) {
  if (selectedPipeline === id) {
    selectedPipeline = null;
    document.getElementById('detail').style.display = 'none';
    await loadPipelines();
    return;
  }
  selectedPipeline = id;
  await loadPipelines();
  await loadDetail(id);
  document.getElementById('detail').style.display = 'block';
}

// ── Detail panel ────────────────────────────────────────────────────

async function loadDetail(id) {
  const [status, gates, logs, summaries] = await Promise.all([
    fetchJson(`/api/pipeline/${id}/status`),
    fetchJson(`/api/pipeline/${id}/gates`),
    fetchJson(`/api/pipeline/${id}/logs`),
    fetchJson(`/api/pipeline/${id}/summaries`)
  ]);

  let html = '';
  const nodeSummaries = summaries?.nodes || {};

  // Current task yellow box
  if (summaries?.current_task && status?.status !== 'completed') {
    html += `<div class="current-task">&#9654; ${escapeHtml(summaries.current_task)}</div>`;
  }

  if (status) {
    const iter = status.iteration || 1;
    html += `<div class="section"><div class="section-title">Pipeline Status${iter > 1 ? ` &mdash; Iteration ${iter}` : ''}</div>`;
    html += `<div style="margin-bottom:8px;font-size:13px;">Status: <strong>${status.status.replace('_',' ')}</strong>`;
    if (iter > 1) html += ` &middot; <span class="badge badge-iter">iter ${iter}</span>`;
    if (status.current_node && status.status !== 'completed')
      html += ` &middot; Current: <code>${status.current_node}</code>`;
    if (status.pending_gate)
      html += ` &middot; <span style="color:var(--yellow)">Waiting on gate: ${status.pending_gate}</span>`;
    html += `</div>`;
    if (status.nodes && status.nodes.length > 0) {
      html += `<div class="nodes-bar">`;
      status.nodes.forEach(n => {
        const cls = n.current_iteration ? n.status : 'prior';
        html += `<div class="node-seg ${cls}" title="${n.id}: ${n.status}${n.current_iteration ? '' : ' (prior iteration)'}"></div>`;
      });
      if (status.status !== 'completed') {
        html += `<div class="node-seg current" title="${status.current_node}: running"></div>`;
      }
      html += `</div><div style="margin-top:12px;">`;
      status.nodes.forEach(n => {
        const cur = n.current_iteration;
        const icon = n.status === 'success' ? '&#10003;' : n.status === 'retry' ? '&#8635;' : '&#9679;';
        const color = cur ? (n.status === 'success' ? 'var(--green)' : n.status === 'retry' ? 'var(--yellow)' : 'var(--text2)') : 'var(--border)';
        const ns = nodeSummaries[n.id];
        html += `<div class="node-row" style="opacity:${cur ? '1' : '0.5'}">
          <span class="node-icon" style="color:${color}">${icon}</span>
          <span class="node-name">${n.id}</span>
          <span class="node-status">${n.status}${n.preferred_label ? ' &rarr; ' + n.preferred_label : ''}${!cur ? ' <span style="color:var(--text2);font-size:11px">(prev iter)</span>' : ''}</span>
        </div>`;
        if (ns && cur) {
          html += `<div class="node-summary">${escapeHtml(ns.summary || '')}</div>`;
          // Commit list for implement/progress nodes
          if (ns.commits && ns.commits.length > 0) {
            html += `<div class="progress-commits">`;
            ns.commits.forEach(c => {
              const cls = c.status === 'DONE' ? 'done' : c.status === 'FAILED' ? 'failed' : 'skipped';
              const icon2 = c.status === 'DONE' ? '&#10003;' : c.status === 'FAILED' ? '&#10007;' : '&#8211;';
              html += `<div class="commit-row"><span class="${cls}">${icon2}</span> ${escapeHtml(c.id)}: ${escapeHtml(c.message || '')}</div>`;
            });
            html += `</div>`;
          }
          // Validation scorecard
          if (ns.checks && ns.checks.length > 0) {
            html += `<div class="validation-scorecard">`;
            ns.checks.forEach(c => {
              html += `<span class="check-dot ${c.passed ? 'check-pass' : 'check-fail'}" title="${escapeHtml(c.name)}: ${c.passed ? 'pass' : 'fail'}"></span>`;
            });
            html += `</div>`;
            const failures = ns.checks.filter(c => !c.passed);
            if (failures.length > 0) {
              html += `<div class="scorecard-failures">${failures.map(c => escapeHtml(c.name)).join(', ')}</div>`;
            }
          }
        }
      });
      html += `</div>`;
    }
    html += `</div>`;
  }

  const gatesList = gates?.gates || gates || [];
  const phaseSummaries = gates?.phase_summaries || [];
  if (gatesList.length > 0) {
    html += `<div class="section"><div class="section-title">Gates</div>`;
    gatesList.forEach(g => {
      html += `<div class="gate-card ${g.is_pending ? 'pending' : ''}">
        <div class="gate-id">${g.gate_id} ${g.is_pending ? '(pending)' : ''}</div>
        <div class="gate-question">${g.question}</div>`;
      if (g.is_pending && phaseSummaries.length > 0) {
        html += `<div class="phase-summaries">`;
        const seen = new Set();
        phaseSummaries.filter(s => s.file !== 'response.md').forEach(s => {
          if (seen.has(s.node)) return; seen.add(s.node);
          html += `<div class="phase-detail"><a class="file-chip" href="/api/pipeline/${id}/logs/${s.node}/${s.file}" target="_blank" rel="noopener">${s.node} &mdash; ${s.file}</a></div>`;
        });
        html += `</div>`;
      }
      html += `<div class="gate-options">`;
      if (g.answer) {
        const choice = g.answer_choice || g.answer;
        const isRevise = choice.toLowerCase() === 'revise';
        g.options.forEach(o => {
          const sel = o === choice;
          html += `<button class="gate-btn ${sel ? (isRevise ? 'answered revise-answered' : 'answered') : ''}" disabled>${o}${sel ? ' &#10003;' : ''}</button>`;
        });
        if (g.answer !== choice && g.answer) html += `</div><div class="answer-note">${escapeHtml(g.answer)}</div>`;
        else html += `</div>`;
      } else if (g.is_pending) {
        g.options.forEach(o => {
          const cls = o.toLowerCase() === 'revise' ? 'revise' : (o.toLowerCase() === 'approve' ? 'approve' : '');
          html += `<button class="gate-btn ${cls}" data-gate-btn data-pipeline="${id}" data-gate="${g.gate_id}" data-choice="${o}">${o}</button>`;
        });
        html += `</div><div class="steer-form"><textarea class="steer-textarea" id="steer-text-${g.gate_id}" placeholder="Optional: add notes or steering instructions..."></textarea></div>`;
      } else { html += `</div>`; }
      html += `</div>`;
    });
    html += `</div>`;
  }

  if (logs && logs.length > 0) {
    html += `<div class="section"><div class="section-title">Artifacts</div>`;
    logs.forEach(n => {
      const icon = n.status === 'success' ? '&#10003;' : n.status === 'unknown' ? '&#9679;' : '&#8635;';
      const color = n.status === 'success' ? 'var(--green)' : 'var(--text2)';
      html += `<div class="node-row"><span class="node-icon" style="color:${color}">${icon}</span><span class="node-name">${n.id}</span><span class="node-files">`;
      n.files.forEach(f => {
        html += `<a class="file-chip" href="/api/pipeline/${id}/logs/${n.id}/${f}" target="_blank" rel="noopener">${f}</a>`;
      });
      html += `</span></div>`;
    });
    html += `</div>`;
  }

  patchHtml(document.getElementById('detail'), html);
}

// ── Gate actions ────────────────────────────────────────────────────

async function answerGate(pipeline, gateId, choice, text) {
  const body = { choice };
  if (text) body.text = text;
  await fetch(`/api/pipeline/${pipeline}/gates/${gateId}/answer`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
  await refresh();
}

// ── Queue ───────────────────────────────────────────────────────────

async function loadQueue() {
  const items = await fetchJson('/api/queue');
  const el = document.getElementById('queue-section');
  if (!items || items.length === 0) { patchHtml(el, ''); return; }

  let html = '<div class="queue-title">Attention Needed</div><div class="queue-cards">';
  const order = {gate: 0, failed_node: 1, running: 2};
  items.sort((a, b) => (order[a.type] ?? 9) - (order[b.type] ?? 9));
  items.forEach(item => {
    if (item.type === 'gate') {
      html += `<div class="queue-card gate"><div class="queue-icon">&#9888;</div><div class="queue-body">
        <div class="queue-pipeline">${item.pipeline_name}</div>
        <div class="queue-label">${item.question || 'Gate pending'}</div>
        <div class="queue-detail">${item.gate_id}</div>
        <div class="queue-actions">`;
      (item.options || []).forEach(o => {
        const cls = o.toLowerCase() === 'approve' ? 'approve' : (o.toLowerCase() === 'revise' ? 'revise' : '');
        html += `<button class="gate-btn ${cls}" data-gate-btn data-pipeline="${item.pipeline_id}" data-gate="${item.gate_id}" data-choice="${o}">${o}</button>`;
      });
      html += `</div><div class="steer-form"><textarea class="steer-textarea" id="steer-text-q-${item.gate_id}" placeholder="Optional: steering instructions..."></textarea></div></div></div>`;
    } else if (item.type === 'failed_node') {
      html += `<div class="queue-card failed"><div class="queue-icon">&#8635;</div><div class="queue-body">
        <div class="queue-pipeline">${item.pipeline_name}</div>
        <div class="queue-label">${item.node_id} &mdash; ${item.status}</div>
        <div class="queue-detail">${item.notes || ''}</div></div></div>`;
    } else if (item.type === 'running') {
      html += `<div class="queue-card running"><div class="queue-icon">&#9654;</div><div class="queue-body">
        <div class="queue-pipeline">${item.pipeline_name}</div>
        <div class="queue-label">${item.node_id}</div>
        <div class="queue-detail">Running...</div></div></div>`;
    }
  });
  html += '</div>';
  patchHtml(el, html);
}

// ── Refresh ─────────────────────────────────────────────────────────

async function refresh() {
  await loadQueue();
  await loadPipelines();
  if (selectedPipeline) await loadDetail(selectedPipeline);
}

// ── Markdown renderer ───────────────────────────────────────────────

function renderMarkdown(src) {
  // Normalize line endings
  src = src.replace(/\r\n/g, '\n');

  // Extract fenced code blocks first to protect them from inline processing
  const codeBlocks = [];
  src = src.replace(/^```(\w*)\n([\s\S]*?)^```/gm, (_, lang, code) => {
    codeBlocks.push(`<pre><code>${escapeHtml(code.replace(/\n$/, ''))}</code></pre>`);
    return `\x00CB${codeBlocks.length - 1}\x00`;
  });

  // Process block-level elements
  const lines = src.split('\n');
  let html = '';
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    // Code block placeholder
    const cbMatch = line.match(/^\x00CB(\d+)\x00$/);
    if (cbMatch) { html += codeBlocks[+cbMatch[1]]; i++; continue; }

    // Horizontal rule
    if (/^(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) { html += '<hr>'; i++; continue; }

    // Headings
    const hMatch = line.match(/^(#{1,6})\s+(.+)/);
    if (hMatch) { const lvl = hMatch[1].length; html += `<h${lvl}>${inline(hMatch[2])}</h${lvl}>`; i++; continue; }

    // Table: detect header + separator rows
    if (i + 1 < lines.length && /^\|/.test(line) && /^\|[\s:]*-/.test(lines[i + 1])) {
      let table = '<table><thead><tr>';
      line.split('|').filter(c => c.trim()).forEach(c => { table += `<th>${inline(c.trim())}</th>`; });
      table += '</tr></thead><tbody>';
      i += 2; // skip header + separator
      while (i < lines.length && /^\|/.test(lines[i])) {
        table += '<tr>';
        lines[i].split('|').filter(c => c.trim()).forEach(c => { table += `<td>${inline(c.trim())}</td>`; });
        table += '</tr>';
        i++;
      }
      html += table + '</tbody></table>';
      continue;
    }

    // Blockquote
    if (line.startsWith('> ') || line === '>') {
      let bq = '';
      while (i < lines.length && (lines[i].startsWith('> ') || lines[i] === '>')) {
        bq += lines[i].replace(/^>\s?/, '') + '\n';
        i++;
      }
      html += `<blockquote>${renderMarkdown(bq)}</blockquote>`;
      continue;
    }

    // Unordered list
    if (/^[\s]*[-*]\s/.test(line)) {
      html += '<ul>';
      while (i < lines.length && /^[\s]*[-*]\s/.test(lines[i])) {
        html += `<li>${inline(lines[i].replace(/^[\s]*[-*]\s+/, ''))}</li>`;
        i++;
      }
      html += '</ul>';
      continue;
    }

    // Ordered list
    if (/^[\s]*\d+[.)]\s/.test(line)) {
      html += '<ol>';
      while (i < lines.length && /^[\s]*\d+[.)]\s/.test(lines[i])) {
        html += `<li>${inline(lines[i].replace(/^[\s]*\d+[.)]\s+/, ''))}</li>`;
        i++;
      }
      html += '</ol>';
      continue;
    }

    // Empty line
    if (line.trim() === '') { i++; continue; }

    // Paragraph — collect consecutive non-empty, non-block lines
    let para = '';
    while (i < lines.length && lines[i].trim() !== '' &&
           !/^#{1,6}\s/.test(lines[i]) && !/^[-*]{3,}/.test(lines[i]) &&
           !/^\|/.test(lines[i]) && !/^>\s/.test(lines[i]) &&
           !/^[\s]*[-*]\s/.test(lines[i]) && !/^[\s]*\d+[.)]\s/.test(lines[i]) &&
           !/^```/.test(lines[i]) && !/^\x00CB/.test(lines[i])) {
      para += (para ? ' ' : '') + lines[i];
      i++;
    }
    if (para) html += `<p>${inline(para)}</p>`;
  }

  return html;
}

function inline(s) {
  // Inline code (must be first to protect content)
  s = s.replace(/`([^`]+)`/g, (_, c) => '<code>' + escapeHtml(c) + '</code>');
  // Images
  s = s.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '<img alt="$1" src="$2">');
  // Links
  s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
  // Bold+italic
  s = s.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
  // Bold
  s = s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
  // Italic
  s = s.replace(/\*(.+?)\*/g, '<em>$1</em>');
  // Strikethrough
  s = s.replace(/~~(.+?)~~/g, '<del>$1</del>');
  // Checkbox
  s = s.replace(/\[x\]/gi, '&#9745;');
  s = s.replace(/\[ \]/g, '&#9744;');
  return s;
}

// ── Delegated click handler ─────────────────────────────────────────

document.addEventListener('click', e => {
  // .md file modal viewer
  const fileChip = e.target.closest('a.file-chip');
  if (fileChip && fileChip.href && fileChip.textContent.trim().endsWith('.md')) {
    e.preventDefault(); e.stopPropagation();
    const url = fileChip.href;
    const rawText = fileChip.textContent.trim();
    const fileName = rawText.includes('\u2014') ? rawText.split('\u2014').pop().trim() : rawText;
    fetch(url).then(r => r.text()).then(content => {
      const modal = document.getElementById('modal-root');
      modal.innerHTML = `<div class="modal-overlay" onclick="if(event.target===this)this.remove()"><div class="modal">
        <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">&times;</button>
        <div class="modal-title">${escapeHtml(fileName)}</div>
        <div class="md-body">${renderMarkdown(content)}</div></div></div>`;
    });
    return;
  }
  // Close modal on overlay click
  const overlay = e.target.closest('.modal-overlay');
  if (overlay && e.target === overlay) { overlay.remove(); return; }

  const gateBtn = e.target.closest('[data-gate-btn]');
  if (gateBtn) {
    e.preventDefault(); e.stopPropagation();
    const { pipeline, gate: gateId, choice } = gateBtn.dataset;
    const textarea = document.getElementById('steer-text-' + gateId) || document.getElementById('steer-text-q-' + gateId);
    const text = textarea ? textarea.value.trim() : '';
    if (choice.toLowerCase() === 'revise' && !text) {
      textarea.style.borderColor = 'var(--red)';
      textarea.placeholder = 'Required for revise: describe what needs to change...';
      textarea.focus();
      return;
    }
    gateBtn.disabled = true; gateBtn.textContent = 'Submitting...';
    answerGate(pipeline, gateId, choice, text || undefined);
    return;
  }
});

// ── Keyboard shortcuts ──────────────────────────────────────────────
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    const overlay = document.querySelector('.modal-overlay');
    if (overlay) overlay.remove();
  }
});

// ── Boot ────────────────────────────────────────────────────────────
refresh();
pollTimer = setInterval(refresh, 3000);
</script>
</body>
</html>
""";

// ═════════════════════════════════════════════════════════════════════
// run — start a pipeline (existing behavior)
// ═════════════════════════════════════════════════════════════════════

static async Task<int> RunPipeline(string[] args)
{
    var dotFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "betrayal.dot");
    if (args.Length > 0)
        dotFilePath = args[0];

    dotFilePath = Path.GetFullPath(dotFilePath);
    if (!File.Exists(dotFilePath))
    {
        Console.Error.WriteLine($"DOT file not found: {dotFilePath}");
        return 1;
    }

    var dotName = Path.GetFileNameWithoutExtension(dotFilePath);
    var workingDir = Path.Combine(Path.GetDirectoryName(dotFilePath)!, "output", dotName);
    Directory.CreateDirectory(workingDir);

    var logsDir = Path.Combine(workingDir, "logs");
    Directory.CreateDirectory(logsDir);

    var gatesDir = Path.Combine(workingDir, "gates");
    Directory.CreateDirectory(gatesDir);

    RegisterRun(dotFilePath, workingDir);

    Console.WriteLine($"Pipeline:    {dotFilePath}");
    Console.WriteLine($"Working dir: {workingDir}");
    Console.WriteLine($"Logs dir:    {logsDir}");
    Console.WriteLine($"Gates dir:   {gatesDir}");
    Console.WriteLine();

    var dotSource = await File.ReadAllTextAsync(dotFilePath);
    var graph = DotParser.Parse(dotSource);

    Console.WriteLine($"Graph: {graph.Name}");
    Console.WriteLine($"Goal:  {graph.Goal?[..Math.Min(graph.Goal.Length, 80)]}...");
    Console.WriteLine($"Nodes: {graph.Nodes.Count}");
    Console.WriteLine($"Edges: {graph.Edges.Count}");
    Console.WriteLine();

    var backend = new AgentCodergenBackend(workingDir);

    var config = new PipelineConfig(
        LogsRoot: logsDir,
        Interviewer: new FileInterviewer(gatesDir),
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

    // Write result marker so the dashboard can detect completion
    var resultPayload = new
    {
        status = result.Status.ToString().ToLowerInvariant(),
        completed_nodes = result.CompletedNodes,
        finished = DateTime.UtcNow.ToString("o")
    };
    await File.WriteAllTextAsync(
        Path.Combine(logsDir, "result.json"),
        JsonSerializer.Serialize(resultPayload, JsonOpts()));

    return result.Status == OutcomeStatus.Success ? 0 : 1;
}

// ═════════════════════════════════════════════════════════════════════
// help
// ═════════════════════════════════════════════════════════════════════

static int ShowHelp()
{
    Console.WriteLine("attractor — pipeline runner CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  attractor run <dotfile>              Start a pipeline");
    Console.WriteLine("  attractor <dotfile>                  Shorthand for 'run'");
    Console.WriteLine("  attractor gate [--dir <dir>]         Show the current pending gate");
    Console.WriteLine("  attractor gate answer [<choice>]     Answer a gate (by label, number, or interactive)");
    Console.WriteLine("  attractor gate watch [--dir <dir>]   Watch for gates and answer interactively");
    Console.WriteLine("  attractor status [--dir <dir>]       Show pipeline progress");
    Console.WriteLine("  attractor logs [<node>] [--dir <dir>]  View node artifacts");
    Console.WriteLine("  attractor web [--dir <dir>] [--port N] Launch web dashboard");
    Console.WriteLine();
    Console.WriteLine("Output directory resolution:");
    Console.WriteLine("  1. --dir <path> flag");
    Console.WriteLine("  2. output/ sibling to a .dot file in cwd");
    Console.WriteLine("  3. dotfiles/output/ relative to cwd");
    return 0;
}

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
            MaxToolRoundsPerInput: 300,
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
        if (lower.StartsWith("gpt") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4") || lower.StartsWith("codex")) return "openai";
        if (lower.StartsWith("gemini")) return "gemini";
        return null;
    }

    private static void SetProfileModel(IProviderProfile profile, string model)
    {
        // Resolve alias (e.g. "codex-5.2" → "gpt-5.2-codex") before setting
        var resolved = Client.ResolveModelAlias(model);

        switch (profile)
        {
            case AnthropicProfile ap:
                ap.Model = resolved;
                break;
            case OpenAiProfile op:
                op.Model = resolved;
                break;
            case GeminiProfile gp:
                gp.Model = resolved;
                break;
        }
    }
}

class RunEntry
{
    public string dotfile { get; set; } = "";
    public string output_dir { get; set; } = "";
    public string name { get; set; } = "";
    public string started { get; set; } = "";
}
