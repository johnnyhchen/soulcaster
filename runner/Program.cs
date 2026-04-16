using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net;
using Soulcaster.Attractor;
using Soulcaster.CodingAgent;
using Soulcaster.Runner;
using Soulcaster.UnifiedLlm;
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
    "providers" => await RunProviders(args[1..]),
    "gate" when args.Length > 1 && args[1] == "answer" => await GateAnswer(args[2..]),
    "gate" when args.Length > 1 && args[1] == "watch" => await GateWatch(args[2..]),
    "gate" => await GateShow(args[1..]),
    "status" => await ShowStatus(args[1..]),
    "logs" => await ShowLogs(args[1..]),
    "web" => await RunWeb(args[1..]),
    "lint" => await RunLint(args[1..]),
    "builder" => await RunBuilder(args[1..]),
    "interactive" or "editor" => await RunInteractive(args[1..]),
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

static object? ConvertJsonValue(JsonElement value)
{
    return value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject()
            .ToDictionary(p => p.Name, p => ConvertJsonValue(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var i64) => i64,
        JsonValueKind.Number when value.TryGetDouble(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.ToString()
    };
}

static bool TryReadLong(JsonElement root, string property, out long value)
{
    value = 0;
    if (!root.TryGetProperty(property, out var el))
        return false;

    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var number))
    {
        value = number;
        return true;
    }

    if (el.ValueKind == JsonValueKind.String &&
        long.TryParse(el.GetString(), out var parsed))
    {
        value = parsed;
        return true;
    }

    return false;
}

static async Task<int> RunProviders(string[] args)
{
    var subcommand = args.Length > 0 ? args[0] : "help";

    return subcommand switch
    {
        "ping" => await RunProvidersPing(args[1..]),
        "invoke" => await RunProvidersInvoke(args[1..]),
        "sync-models" => await RunProvidersSyncModels(args[1..]),
        "help" or "--help" or "-h" => ShowProvidersHelp(),
        _ => ShowProvidersHelp()
    };
}

static async Task<int> RunProvidersPing(string[] args)
{
    string? provider = null;
    var json = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--provider" when i + 1 < args.Length:
                provider = args[++i];
                break;
            case "--json":
                json = true;
                break;
            default:
                Console.Error.WriteLine($"Unknown providers ping option: {args[i]}");
                return 1;
        }
    }

    var adapters = ProviderCommandSupport.CreateDiscoveryAdaptersFromEnv();
    if (adapters.Count == 0)
    {
        Console.Error.WriteLine("No provider API keys found in environment.");
        return 1;
    }

    var selectedProviders = new List<string>();
    if (string.IsNullOrWhiteSpace(provider))
    {
        selectedProviders.AddRange(adapters.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }
    else
    {
        var normalizedProvider = ProviderCommandSupport.NormalizeProvider(provider);
        if (!adapters.ContainsKey(normalizedProvider))
        {
            Console.Error.WriteLine($"Provider '{normalizedProvider}' is not configured in this environment.");
            return 1;
        }

        selectedProviders.Add(normalizedProvider);
    }

    var results = new List<ProviderPingResult>();
    foreach (var selectedProvider in selectedProviders)
    {
        var result = await adapters[selectedProvider].PingAsync();
        results.Add(result);
    }

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(results, JsonOpts()));
    }
    else
    {
        foreach (var result in results)
        {
            var status = result.Success ? "ok" : "fail";
            var modelCount = result.ModelCount is null ? "?" : result.ModelCount.Value.ToString();
            var statusCode = result.StatusCode is null ? "" : $" http={result.StatusCode}";
            var message = string.IsNullOrWhiteSpace(result.Message) ? "" : $" message={result.Message}";
            Console.WriteLine($"{result.Provider,-10} {status,-4} models={modelCount,-4}{statusCode} endpoint={result.Endpoint}{message}");
        }
    }

    return results.All(result => result.Success) ? 0 : 1;
}

static async Task<int> RunProvidersSyncModels(string[] args)
{
    string? provider = null;
    string? name = null;
    int? maxModels = null;
    var run = false;
    var json = false;
    var requestedModels = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--provider" when i + 1 < args.Length:
                provider = args[++i];
                break;
            case "--name" when i + 1 < args.Length:
                name = args[++i];
                break;
            case "--max-models" when i + 1 < args.Length:
            case "--limit" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out var parsedMaxModels) || parsedMaxModels <= 0)
                {
                    Console.Error.WriteLine("Expected a positive integer after --max-models/--limit.");
                    return 1;
                }

                maxModels = parsedMaxModels;
                break;
            case "--model" when i + 1 < args.Length:
                requestedModels.Add(args[++i]);
                break;
            case "--run":
                run = true;
                break;
            case "--json":
                json = true;
                break;
            default:
                Console.Error.WriteLine($"Unknown providers sync-models option: {args[i]}");
                return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(provider))
    {
        Console.Error.WriteLine("providers sync-models requires --provider <anthropic|openai|gemini>.");
        return 1;
    }

    try
    {
        var normalizedProvider = ProviderCommandSupport.NormalizeProvider(provider);
        var adapters = ProviderCommandSupport.CreateDiscoveryAdaptersFromEnv();
        if (!adapters.TryGetValue(normalizedProvider, out var adapter))
        {
            Console.Error.WriteLine($"Provider '{normalizedProvider}' is not configured in this environment.");
            return 1;
        }

        var repositoryRoot = ProviderCommandSupport.ResolveRepositoryRoot(Directory.GetCurrentDirectory());
        var discoveredModels = await adapter.ListModelsAsync();
        var selection = ProviderCommandSupport.BuildSyncSelection(
            normalizedProvider,
            discoveredModels,
            maxModels,
            requestedModels);
        var artifacts = ProviderCommandSupport.WriteSyncArtifacts(repositoryRoot, selection, name);

        int? runExitCode = null;
        if (run)
            runExitCode = await RunPipeline([artifacts.DotfilePath, "--no-autoresume"]);

        var payload = new
        {
            provider = normalizedProvider,
            discovered_count = selection.DiscoveredModels.Count,
            candidate_count = selection.CandidateModels.Count,
            unknown_count = selection.UnknownModels.Count,
            unknown_models = selection.UnknownModels.Select(model => model.Id).ToList(),
            dotfile = artifacts.DotfilePath,
            manifest = artifacts.ManifestPath,
            output_dir = artifacts.OutputDirectory,
            validation_report = artifacts.ValidationReportPath,
            orchestrator_provider = artifacts.OrchestratorProvider,
            orchestrator_model = artifacts.OrchestratorModel,
            run_requested = run,
            run_exit_code = runExitCode
        };

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts()));
        }
        else
        {
            Console.WriteLine($"Provider:        {normalizedProvider}");
            Console.WriteLine($"Discovered:      {selection.DiscoveredModels.Count}");
            Console.WriteLine($"Candidates:      {selection.CandidateModels.Count}");
            Console.WriteLine($"Unknown:         {selection.UnknownModels.Count}");
            Console.WriteLine($"Orchestrator:    {artifacts.OrchestratorProvider}/{artifacts.OrchestratorModel}");
            Console.WriteLine($"Manifest:        {artifacts.ManifestPath}");
            Console.WriteLine($"Dotfile:         {artifacts.DotfilePath}");
            Console.WriteLine($"Output dir:      {artifacts.OutputDirectory}");
            Console.WriteLine($"Validation file: {artifacts.ValidationReportPath}");
            if (selection.UnknownModels.Count > 0)
                Console.WriteLine($"Models:          {string.Join(", ", selection.UnknownModels.Select(model => model.Id))}");
            else
                Console.WriteLine("Models:          none (no unknown sync candidates)");

            if (runExitCode is not null)
                Console.WriteLine($"Run exit code:   {runExitCode}");
        }

        return runExitCode ?? 0;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderError)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> RunProvidersInvoke(string[] args)
{
    string? provider = null;
    string? model = null;
    string? prompt = null;
    string? promptFile = null;
    string? saveText = null;
    string? saveImagesDir = null;
    string? reasoningEffort = null;
    IReadOnlyList<ResponseModality>? outputModalities = null;
    var imagePaths = new List<string>();
    int? maxTokens = null;
    var json = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--provider" when i + 1 < args.Length:
                provider = args[++i];
                break;
            case "--model" when i + 1 < args.Length:
                model = args[++i];
                break;
            case "--prompt" when i + 1 < args.Length:
                prompt = args[++i];
                break;
            case "--prompt-file" when i + 1 < args.Length:
                promptFile = args[++i];
                break;
            case "--image" when i + 1 < args.Length:
                imagePaths.Add(args[++i]);
                break;
            case "--output-modalities" when i + 1 < args.Length:
                outputModalities = ProviderCommandSupport.ParseOutputModalities(args[++i]);
                break;
            case "--reasoning-effort" when i + 1 < args.Length:
                reasoningEffort = args[++i];
                break;
            case "--max-tokens" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out var parsedMaxTokens) || parsedMaxTokens <= 0)
                {
                    Console.Error.WriteLine("Expected a positive integer after --max-tokens.");
                    return 1;
                }

                maxTokens = parsedMaxTokens;
                break;
            case "--save-text" when i + 1 < args.Length:
                saveText = args[++i];
                break;
            case "--save-images-dir" when i + 1 < args.Length:
                saveImagesDir = args[++i];
                break;
            case "--json":
                json = true;
                break;
            default:
                Console.Error.WriteLine($"Unknown providers invoke option: {args[i]}");
                return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(model))
    {
        Console.Error.WriteLine("providers invoke requires --model <id>.");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(promptFile) && imagePaths.Count == 0)
    {
        Console.Error.WriteLine("providers invoke requires --prompt, --prompt-file, or at least one --image.");
        return 1;
    }

    if (!string.IsNullOrWhiteSpace(prompt) && !string.IsNullOrWhiteSpace(promptFile))
    {
        Console.Error.WriteLine("Specify only one of --prompt or --prompt-file.");
        return 1;
    }

    try
    {
        var adapters = ProviderCommandSupport.CreateCompletionAdaptersFromEnv();
        if (adapters.Count == 0)
        {
            Console.Error.WriteLine("No provider API keys found in environment.");
            return 1;
        }

        string? normalizedProvider = null;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            normalizedProvider = ProviderCommandSupport.NormalizeProvider(provider);
            if (!adapters.ContainsKey(normalizedProvider))
            {
                Console.Error.WriteLine($"Provider '{normalizedProvider}' is not configured in this environment.");
                return 1;
            }
        }

        var content = new List<ContentPart>();
        if (!string.IsNullOrWhiteSpace(promptFile))
            prompt = await File.ReadAllTextAsync(promptFile);
        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(ContentPart.TextPart(prompt));
        foreach (var imagePath in imagePaths)
            content.Add(ContentPart.ImagePart(ImageData.FromFile(imagePath)));

        var request = new Request
        {
            Provider = normalizedProvider,
            Model = model,
            Messages = [new Message(Role.User, content)],
            OutputModalities = outputModalities?.Count > 0 ? outputModalities.ToList() : null,
            ReasoningEffort = reasoningEffort,
            MaxTokens = maxTokens
        };

        var client = new Client(new Dictionary<string, IProviderAdapter>(adapters, StringComparer.OrdinalIgnoreCase));
        var response = await client.CompleteAsync(request);

        if (!string.IsNullOrWhiteSpace(saveText))
        {
            var saveTextPath = Path.GetFullPath(saveText);
            Directory.CreateDirectory(Path.GetDirectoryName(saveTextPath)!);
            await File.WriteAllTextAsync(saveTextPath, response.Text ?? string.Empty);
        }

        var savedImages = new List<string>();
        if (!string.IsNullOrWhiteSpace(saveImagesDir) && response.Images.Count > 0)
        {
            var imagesDir = Path.GetFullPath(saveImagesDir);
            Directory.CreateDirectory(imagesDir);
            for (var idx = 0; idx < response.Images.Count; idx++)
            {
                var image = response.Images[idx];
                if (image.Data is null || image.Data.Length == 0)
                    continue;

                var fileName = $"image-{idx + 1}{ProviderCommandSupport.GetImageExtension(image.MediaType)}";
                var path = Path.Combine(imagesDir, fileName);
                await File.WriteAllBytesAsync(path, image.Data);
                savedImages.Add(path);
            }
        }

        var payload = new
        {
            response.Id,
            response.Model,
            response.Provider,
            text = response.Text,
            image_count = response.Images.Count,
            image_paths = savedImages,
            finish_reason = response.FinishReason.Reason,
            usage = response.Usage
        };

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts()));
        else
            Console.Write(response.Text);

        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderError or ConfigurationError)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int ShowProvidersHelp()
{
    Console.WriteLine("attractor providers");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  attractor providers ping [--provider <name>] [--json]");
    Console.WriteLine("  attractor providers invoke --model <id> [--provider <name>] [--prompt <text> | --prompt-file <path>] [--image <path>]... [--output-modalities text,image] [--reasoning-effort <level>] [--max-tokens N] [--save-text <path>] [--save-images-dir <path>] [--json]");
    Console.WriteLine("  attractor providers sync-models --provider <name> [--model <id>] [--max-models N] [--name <run-name>] [--run] [--json]");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("  ping checks provider reachability by listing models.");
    Console.WriteLine("  invoke sends a direct SDK-backed request to a provider and can attach local images.");
    Console.WriteLine("  sync-models discovers provider models, filters unknown catalog candidates,");
    Console.WriteLine("  writes a manifest plus a dotfile under dotfiles/, and optionally runs it.");
    return 0;
}

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
            var preferred = sRoot.TryGetProperty("preferred_next_label", out var pNext)
                ? pNext.GetString()
                : sRoot.TryGetProperty("preferred_label", out var pLegacy)
                    ? pLegacy.GetString()
                    : null;
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
                preferred = sr.TryGetProperty("preferred_next_label", out var pNext)
                    ? pNext.GetString()
                    : sr.TryGetProperty("preferred_label", out var pLegacy)
                        ? pLegacy.GetString()
                        : null;
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

    app.MapGet("/api/pipeline/{id}/graph", async (string id) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null || !Directory.Exists(pipelineDir)) return Results.NotFound();

        var dotPath = ResolvePipelineDotfilePath(pipelineDir);
        if (dotPath == null || !File.Exists(dotPath))
            return Results.NotFound("dotfile not found for pipeline");

        var checkpointPath = Path.Combine(pipelineDir, "logs", "checkpoint.json");
        string? currentNode = null;
        var completedNodes = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(checkpointPath))
        {
            try
            {
                var cp = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
                currentNode = cp.RootElement.TryGetProperty("CurrentNodeId", out var current) ? current.GetString() : null;
                if (cp.RootElement.TryGetProperty("CompletedNodes", out var completed) && completed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in completed.EnumerateArray())
                    {
                        var idText = node.GetString();
                        if (!string.IsNullOrWhiteSpace(idText))
                            completedNodes.Add(idText);
                    }
                }
            }
            catch
            {
                // Best effort.
            }
        }

        var svg = await RenderGraphSvgAsync(dotPath, currentNode, completedNodes);
        return Results.Text(svg, "image/svg+xml");
    });

    app.MapGet("/api/pipeline/{id}/telemetry", async (string id) =>
    {
        var pipelineDir = globalMode ? DecodePipelineId(id) : ResolvePipelineDir(outputDir!, id);
        if (pipelineDir == null || !Directory.Exists(pipelineDir)) return Results.NotFound();

        var eventsPath = Path.Combine(pipelineDir, "logs", "events.jsonl");
        if (!File.Exists(eventsPath))
            return Results.Json(new
            {
                events = Array.Empty<object>(),
                per_node = new Dictionary<string, object>(),
                stage_metrics = new Dictionary<string, object>(),
                totals = new { tool_calls = 0L, tool_errors = 0L, total_tokens = 0L }
            });

        var events = new List<Dictionary<string, object?>>();
        var perNode = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var stageMetrics = new Dictionary<string, object?>(StringComparer.Ordinal);
        long totalToolCalls = 0;
        long totalToolErrors = 0;
        long totalTokens = 0;

        foreach (var line in await File.ReadAllLinesAsync(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var evt = new Dictionary<string, object?>();
                foreach (var prop in root.EnumerateObject())
                    evt[prop.Name] = ConvertJsonValue(prop.Value);
                events.Add(evt);

                var node = root.TryGetProperty("node_id", out var n) ? n.GetString() ?? "unknown" : "unknown";
                var eventType = root.TryGetProperty("event_type", out var t) ? t.GetString() ?? "unknown" : "unknown";
                if (!perNode.TryGetValue(node, out var nodeStats))
                {
                    nodeStats = new Dictionary<string, int>(StringComparer.Ordinal);
                    perNode[node] = nodeStats;
                }
                nodeStats[eventType] = nodeStats.GetValueOrDefault(eventType, 0) + 1;

                if (eventType == "stage_end" && root.TryGetProperty("stage_metrics", out var metrics))
                    stageMetrics[node] = ConvertJsonValue(metrics);

                if (TryReadLong(root, "tool_calls", out var toolCalls))
                    totalToolCalls += toolCalls;
                if (TryReadLong(root, "tool_errors", out var toolErrors))
                    totalToolErrors += toolErrors;
                if (TryReadLong(root, "total_tokens", out var tokens))
                    totalTokens += tokens;
            }
            catch
            {
                // Ignore malformed line.
            }
        }

        return Results.Json(new
        {
            events,
            per_node = perNode,
            stage_metrics = stageMetrics,
            totals = new
            {
                tool_calls = totalToolCalls,
                tool_errors = totalToolErrors,
                total_tokens = totalTokens
            }
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

static string? ResolvePipelineDotfilePath(string pipelineDir)
{
    // 1) run-manifest.json (authoritative for new runs)
    var manifestPath = Path.Combine(pipelineDir, "run-manifest.json");
    if (File.Exists(manifestPath))
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath));
            if (!string.IsNullOrWhiteSpace(manifest?.graph_path) && File.Exists(manifest.graph_path))
                return manifest.graph_path;
        }
        catch
        {
            // Ignore malformed manifest.
        }
    }

    // 2) registry lookup
    var registry = LoadRegistry();
    var match = registry.FirstOrDefault(r =>
        Path.GetFullPath(r.output_dir).Equals(Path.GetFullPath(pipelineDir), StringComparison.OrdinalIgnoreCase));
    if (match is not null && File.Exists(match.dotfile))
        return Path.GetFullPath(match.dotfile);

    // 3) conventional path: {dotfiles}/{runname}.dot where pipelineDir is {dotfiles}/output/{runname}
    var outputDir = Path.GetDirectoryName(pipelineDir);
    if (outputDir is not null && Path.GetFileName(outputDir).Equals("output", StringComparison.OrdinalIgnoreCase))
    {
        var dotfilesDir = Path.GetDirectoryName(outputDir);
        if (dotfilesDir is not null)
        {
            var candidate = Path.Combine(dotfilesDir, Path.GetFileName(pipelineDir) + ".dot");
            if (File.Exists(candidate))
                return candidate;
        }
    }

    return null;
}

static async Task<string> RenderGraphSvgAsync(string dotPath, string? currentNode, HashSet<string> completedNodes)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dot",
        ArgumentList = { "-Tsvg", dotPath },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    try
    {
        using var process = Process.Start(psi);
        if (process is null)
            return "<svg xmlns=\"http://www.w3.org/2000/svg\"><text x=\"10\" y=\"24\">Unable to start graphviz dot.</text></svg>";

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var svg = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(svg))
        {
            var message = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(stderr) ? "dot failed to render graph" : stderr);
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\"><text x=\"10\" y=\"24\">{message}</text></svg>";
        }

        svg = Regex.Replace(svg, @"<\?xml[^>]*\?>\s*", string.Empty, RegexOptions.IgnoreCase);
        svg = Regex.Replace(svg, @"<!DOCTYPE[^>]*>\s*", string.Empty, RegexOptions.IgnoreCase);

        return DecorateGraphSvg(svg, currentNode, completedNodes);
    }
    catch (Exception ex)
    {
        var message = WebUtility.HtmlEncode(ex.Message);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\"><text x=\"10\" y=\"24\">{message}</text></svg>";
    }
}

static string DecorateGraphSvg(string svg, string? currentNode, HashSet<string> completedNodes)
{
    var styleBlock =
        "<style>" +
        ".node.completed ellipse,.node.completed polygon,.node.completed path{fill:#e6ffed !important;stroke:#1a7f37 !important;stroke-width:1.8px !important;}" +
        ".node.active ellipse,.node.active polygon,.node.active path{fill:#fff4ce !important;stroke:#b54708 !important;stroke-width:2.4px !important;}" +
        ".node.active text{font-weight:700;}" +
        "</style>";

    svg = Regex.Replace(
        svg,
        @"(<svg\b[^>]*>)",
        "$1" + styleBlock,
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    var nodeGroupPattern = new Regex(
        @"<g(?<attrs>[^>]*\bclass=""node""[^>]*)>(?<body>.*?)</g>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    svg = nodeGroupPattern.Replace(svg, match =>
    {
        var attrs = match.Groups["attrs"].Value;
        var body = match.Groups["body"].Value;

        var titleMatch = Regex.Match(body, @"<title>(?<id>[^<]+)</title>", RegexOptions.IgnoreCase);
        if (!titleMatch.Success)
            return match.Value;

        var nodeId = WebUtility.HtmlDecode(titleMatch.Groups["id"].Value);
        var classes = new List<string> { "node" };

        if (completedNodes.Contains(nodeId))
            classes.Add("completed");
        if (!string.IsNullOrWhiteSpace(currentNode) && currentNode.Equals(nodeId, StringComparison.Ordinal))
            classes.Add("active");

        if (classes.Count == 1)
            return match.Value;

        var updatedAttrs = Regex.Replace(
            attrs,
            @"class=""[^""]*""",
            $"class=\"{string.Join(" ", classes)}\"",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        return "<g" + updatedAttrs + ">" + body + "</g>";
    });

    return svg;
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
    --bg-app: #04070c;
    --bg-surface: rgba(9, 16, 24, 0.92);
    --bg-panel: rgba(12, 19, 29, 0.96);
    --bg-inset: #081018;
    --bg-soft: rgba(51, 153, 255, 0.08);
    --border-subtle: rgba(127, 146, 168, 0.16);
    --border-strong: rgba(127, 146, 168, 0.28);
    --text-primary: #edf4fb;
    --text-secondary: #96a7bb;
    --text-tertiary: #6c7d90;
    --accent: #63b3ff;
    --accent-strong: #9ad1ff;
    --accent-muted: rgba(99, 179, 255, 0.14);
    --green: #37d67a;
    --green-muted: rgba(55, 214, 122, 0.14);
    --yellow: #f5c35b;
    --yellow-muted: rgba(245, 195, 91, 0.16);
    --red: #ff7d7d;
    --red-muted: rgba(255, 125, 125, 0.16);
    --purple: #b596ff;
    --purple-muted: rgba(181, 150, 255, 0.16);
    --shadow-panel: 0 20px 60px rgba(0, 0, 0, 0.34);
    --radius-sm: 8px;
    --radius-md: 14px;
    --radius-lg: 22px;
    --font-sans: "IBM Plex Sans", "Aptos", "Segoe UI", sans-serif;
    --font-mono: "IBM Plex Mono", "SFMono-Regular", Menlo, Consolas, monospace;
    --sidebar-width: 360px;
    --bg: var(--bg-app);
    --surface: var(--bg-surface);
    --border: var(--border-strong);
    --text: var(--text-primary);
    --text2: var(--text-secondary);
  }

  * { box-sizing: border-box; }

  html, body { height: 100%; margin: 0; }

  body {
    font-family: var(--font-sans);
    color: var(--text-primary);
    background:
      radial-gradient(circle at top left, rgba(99, 179, 255, 0.14), transparent 22%),
      radial-gradient(circle at top right, rgba(181, 150, 255, 0.10), transparent 18%),
      linear-gradient(180deg, rgba(255,255,255,0.03), transparent 22%),
      linear-gradient(90deg, rgba(99,179,255,0.03) 1px, transparent 1px),
      linear-gradient(0deg, rgba(99,179,255,0.02) 1px, transparent 1px),
      var(--bg-app);
    background-size: auto, auto, auto, 32px 32px, 32px 32px, auto;
    overflow: hidden;
    letter-spacing: -0.01em;
  }

  code, pre, .pipeline-meta, .pipeline-status-line, .fact-pill, .queue-pipeline, .file-chip, .gate-id, .commit-row, .node-title, .stage-metric {
    font-family: var(--font-mono) !important;
  }

  .shell {
    height: 100%;
    display: flex;
    flex-direction: column;
    padding: 18px;
    gap: 16px;
  }

  .cmd-header {
    flex-shrink: 0;
    background: linear-gradient(180deg, rgba(11, 20, 32, 0.95), rgba(8, 15, 24, 0.92));
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-lg);
    padding: 18px 22px 16px;
    box-shadow: var(--shadow-panel);
    overflow: hidden;
    position: relative;
  }

  .cmd-header::before {
    content: "";
    position: absolute;
    inset: 0;
    background:
      linear-gradient(90deg, rgba(99, 179, 255, 0.10), transparent 18%),
      linear-gradient(180deg, transparent, rgba(255,255,255,0.02));
    pointer-events: none;
  }

  .cmd-top {
    display: flex;
    justify-content: space-between;
    gap: 18px;
    align-items: flex-start;
    margin-bottom: 14px;
    position: relative;
    z-index: 1;
  }

  .title-block { display: flex; flex-direction: column; gap: 4px; }
  .eyebrow {
    font-size: 11px;
    letter-spacing: 0.22em;
    text-transform: uppercase;
    color: var(--accent-strong);
    opacity: 0.8;
  }

  h1 {
    margin: 0;
    font-size: 30px;
    font-weight: 700;
    line-height: 1;
  }

  .subtitle {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: flex-end;
    gap: 10px 14px;
    color: var(--text-secondary);
    font-size: 12px;
  }

  .subtitle label {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
    user-select: none;
  }

  .subtitle input { accent-color: var(--accent); }

  .attention-stack {
    position: relative;
    z-index: 1;
    max-height: 220px;
    overflow: auto;
    padding-right: 4px;
  }

  .attention-stack:empty::before {
    content: "No retries, pending gates, or running nodes.";
    display: block;
    padding: 14px 16px;
    color: var(--text-tertiary);
    background: rgba(255,255,255,0.02);
    border: 1px dashed var(--border-subtle);
    border-radius: var(--radius-md);
    font-style: italic;
  }

  .queue-title {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.18em;
    color: var(--text-tertiary);
    margin-bottom: 10px;
  }

  .queue-cards { display: flex; flex-direction: column; gap: 10px; }

  .queue-card {
    display: grid;
    grid-template-columns: 28px 1fr;
    gap: 14px;
    padding: 14px 16px;
    border: 1px solid var(--border-subtle);
    border-left: 4px solid var(--border-strong);
    border-radius: var(--radius-md);
    background: rgba(255,255,255,0.025);
    backdrop-filter: blur(6px);
  }

  .queue-card.gate { border-left-color: var(--red); background: linear-gradient(90deg, var(--red-muted), rgba(255,255,255,0.02) 20%); }
  .queue-card.failed { border-left-color: var(--yellow); background: linear-gradient(90deg, var(--yellow-muted), rgba(255,255,255,0.02) 20%); }
  .queue-card.running { border-left-color: var(--accent); background: linear-gradient(90deg, var(--accent-muted), rgba(255,255,255,0.02) 20%); }

  .queue-icon {
    width: 28px;
    height: 28px;
    border-radius: 999px;
    display: grid;
    place-items: center;
    background: rgba(255,255,255,0.04);
    font-size: 15px;
  }

  .queue-pipeline {
    font-size: 11px;
    color: var(--text-tertiary);
    margin-bottom: 4px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .queue-label {
    font-size: 15px;
    font-weight: 650;
    margin-bottom: 5px;
  }

  .queue-detail {
    color: var(--text-secondary);
    font-size: 13px;
    line-height: 1.45;
  }

  .queue-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-top: 10px;
  }

  .work-area {
    flex: 1;
    min-height: 0;
    display: flex;
    gap: 16px;
  }

  .sidebar-wrap,
  .detail-panel {
    min-height: 0;
    border: 1px solid var(--border-strong);
    box-shadow: var(--shadow-panel);
  }

  .sidebar-wrap {
    width: var(--sidebar-width);
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    background: linear-gradient(180deg, rgba(6, 12, 19, 0.94), rgba(9, 15, 23, 0.98));
    border-radius: var(--radius-lg);
    overflow: hidden;
  }

  .sidebar-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 16px 18px;
    border-bottom: 1px solid var(--border-subtle);
    background: rgba(255,255,255,0.02);
  }

  .sidebar-title {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.18em;
    color: var(--text-tertiary);
  }

  .sidebar-summary {
    font-size: 11px;
    color: var(--text-secondary);
    padding: 4px 8px;
    border-radius: 999px;
    background: rgba(255,255,255,0.04);
  }

  .pipeline-list {
    flex: 1;
    overflow: auto;
    display: flex;
    flex-direction: column;
    padding: 8px;
    gap: 8px;
  }

  .pipeline-card {
    position: relative;
    padding: 14px 16px 12px;
    border: 1px solid transparent;
    border-left: 4px solid var(--border-subtle);
    border-radius: 16px;
    background: rgba(255,255,255,0.02);
    cursor: pointer;
    transition: background 0.16s ease, border-color 0.16s ease, transform 0.16s ease, box-shadow 0.16s ease;
  }

  .pipeline-card:hover {
    background: rgba(99, 179, 255, 0.08);
    border-color: var(--border-subtle);
    transform: translateX(2px);
  }

  .pipeline-card.status-in_progress { border-left-color: var(--accent); }
  .pipeline-card.status-failed { border-left-color: var(--red); }
  .pipeline-card.status-completed { border-left-color: var(--green); }
  .pipeline-card.status-unknown { border-left-color: var(--purple); }

  .pipeline-card.selected {
    background: linear-gradient(90deg, rgba(99, 179, 255, 0.12), rgba(255,255,255,0.03) 18%);
    border-color: rgba(99, 179, 255, 0.28);
    box-shadow: inset 0 0 0 1px rgba(99, 179, 255, 0.28), 0 18px 32px rgba(0, 0, 0, 0.24);
  }

  .pipeline-card.selected::after {
    content: "";
    position: absolute;
    top: 16px;
    bottom: 16px;
    right: -9px;
    width: 18px;
    background: linear-gradient(90deg, rgba(99, 179, 255, 0.55), rgba(99, 179, 255, 0));
    border-radius: 999px;
    pointer-events: none;
  }

  .pipeline-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 8px;
  }

  .pipeline-name {
    font-size: 14px;
    font-weight: 650;
    line-height: 1.3;
  }

  .badge {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 4px 9px;
    border-radius: 999px;
    font-size: 10px;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    font-weight: 700;
  }

  .badge-completed { background: var(--green-muted); color: var(--green); }
  .badge-in_progress { background: var(--accent-muted); color: var(--accent-strong); }
  .badge-failed { background: var(--red-muted); color: var(--red); }
  .badge-unknown { background: var(--purple-muted); color: var(--purple); }
  .badge-gate { background: var(--yellow-muted); color: var(--yellow); }
  .badge-iter { background: var(--purple-muted); color: var(--purple); }

  .pipeline-status-line {
    height: 4px;
    margin: 10px 0 9px;
    border-radius: 999px;
    background: rgba(255,255,255,0.06);
    overflow: hidden;
  }

  .pipeline-status-line span {
    display: block;
    height: 100%;
    border-radius: inherit;
    background: linear-gradient(90deg, var(--accent), var(--accent-strong));
  }

  .pipeline-status-line.status-completed span { background: linear-gradient(90deg, var(--green), #7cffb0); }
  .pipeline-status-line.status-failed span { background: linear-gradient(90deg, #ff926f, var(--red)); }
  .pipeline-status-line.status-unknown span { background: linear-gradient(90deg, var(--purple), #d1beff); }

  .pipeline-meta {
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: 11px;
    color: var(--text-tertiary);
  }

  .detail-panel {
    flex: 1;
    overflow: auto;
    border-radius: var(--radius-lg);
    background:
      linear-gradient(180deg, rgba(255,255,255,0.02), transparent 22%),
      linear-gradient(180deg, rgba(10, 18, 27, 0.96), rgba(7, 13, 20, 0.98));
    padding: 18px;
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .detail-empty {
    min-height: 100%;
    display: grid;
    place-items: center;
    padding: 40px;
  }

  .detail-empty-card {
    max-width: 560px;
    text-align: center;
    border: 1px dashed var(--border-subtle);
    border-radius: var(--radius-lg);
    background: rgba(255,255,255,0.02);
    padding: 34px;
  }

  .detail-empty-card h2 {
    margin: 0 0 10px;
    font-size: 24px;
  }

  .detail-empty-card p {
    margin: 0;
    color: var(--text-secondary);
    line-height: 1.6;
  }

  .detail-hero {
    display: flex;
    justify-content: space-between;
    align-items: flex-end;
    gap: 16px;
    padding: 18px 20px;
    border: 1px solid var(--border-subtle);
    border-radius: var(--radius-lg);
    background: linear-gradient(135deg, rgba(99,179,255,0.10), rgba(181,150,255,0.08) 28%, rgba(255,255,255,0.02) 60%);
  }

  .detail-kicker {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.18em;
    color: var(--text-tertiary);
    margin-bottom: 7px;
  }

  .detail-title {
    margin: 0;
    font-size: 26px;
    font-weight: 700;
    line-height: 1.1;
  }

  .detail-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    justify-content: flex-end;
  }

  .fact-pill {
    padding: 6px 10px;
    border-radius: 999px;
    background: rgba(255,255,255,0.05);
    border: 1px solid var(--border-subtle);
    color: var(--text-secondary);
    font-size: 11px;
  }

  .section {
    border: 1px solid var(--border-subtle);
    border-radius: var(--radius-lg);
    background: rgba(255,255,255,0.03);
    padding: 18px;
    box-shadow: inset 0 1px 0 rgba(255,255,255,0.03);
  }

  .section-title {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.16em;
    color: var(--text-tertiary);
    margin-bottom: 14px;
  }

  .graph-wrap {
    background: rgba(7, 13, 20, 0.9);
    border: 1px solid var(--border-subtle);
    border-radius: var(--radius-md);
    padding: 14px;
    overflow: auto;
  }

  .graph-wrap svg { width: 100%; height: auto; display: block; }

  .current-task {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 12px 14px;
    margin-bottom: 14px;
    border-radius: var(--radius-md);
    background: linear-gradient(90deg, var(--yellow-muted), rgba(255,255,255,0.02));
    border: 1px solid rgba(245, 195, 91, 0.28);
    color: var(--yellow);
    font-size: 13px;
    font-weight: 600;
  }

  .execution-list {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .node-card {
    border: 1px solid var(--border-subtle);
    border-radius: 18px;
    background: rgba(8, 14, 21, 0.88);
    overflow: hidden;
  }

  .node-card.prior { opacity: 0.62; }
  .node-card.current {
    border-color: rgba(99, 179, 255, 0.26);
    box-shadow: inset 0 0 0 1px rgba(99,179,255,0.16);
  }

  .node-card-head {
    display: flex;
    align-items: flex-start;
    gap: 12px;
    padding: 14px 16px 12px;
  }

  .node-icon {
    width: 28px;
    height: 28px;
    border-radius: 999px;
    display: grid;
    place-items: center;
    background: rgba(255,255,255,0.04);
    font-size: 15px;
    flex-shrink: 0;
  }

  .node-copy { flex: 1; min-width: 0; }

  .node-title {
    display: flex;
    flex-wrap: wrap;
    gap: 8px 12px;
    align-items: center;
    margin-bottom: 6px;
  }

  .node-name {
    font-size: 13px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .node-status {
    color: var(--text-secondary);
    font-size: 12px;
  }

  .node-summary {
    color: var(--text-secondary);
    font-size: 13px;
    line-height: 1.5;
  }

  .node-card-facts {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    padding: 0 16px 14px;
  }

  .node-context {
    border-top: 1px solid var(--border-subtle);
    padding: 14px 16px 16px;
    display: grid;
    gap: 12px;
  }

  .node-context-block {
    padding: 12px 14px;
    border-radius: var(--radius-md);
    border: 1px solid var(--border-subtle);
    background: rgba(255,255,255,0.025);
  }

  .block-title {
    font-size: 10px;
    text-transform: uppercase;
    letter-spacing: 0.14em;
    color: var(--text-tertiary);
    margin-bottom: 8px;
  }

  .node-files {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }

  .file-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    border-radius: 999px;
    border: 1px solid rgba(99, 179, 255, 0.24);
    background: rgba(99, 179, 255, 0.12);
    color: var(--accent-strong);
    text-decoration: none;
    font-size: 11px;
    transition: background 0.15s ease, transform 0.15s ease;
  }

  .file-chip:hover {
    background: rgba(99, 179, 255, 0.20);
    transform: translateY(-1px);
  }

  .progress-commits {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .commit-row {
    display: flex;
    gap: 8px;
    align-items: center;
    color: var(--text-secondary);
    font-size: 11px;
  }

  .commit-row .done { color: var(--green); }
  .commit-row .failed { color: var(--red); }
  .commit-row .skipped { color: var(--text-tertiary); }

  .validation-scorecard {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }

  .check-dot {
    width: 12px;
    height: 12px;
    border-radius: 999px;
    display: inline-block;
  }

  .check-pass { background: var(--green); }
  .check-fail { background: var(--red); }
  .scorecard-failures {
    margin-top: 8px;
    color: var(--red);
    font-size: 12px;
    line-height: 1.5;
  }

  .stage-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 10px;
  }

  .metric-card {
    padding: 12px 14px;
    border: 1px solid var(--border-subtle);
    border-radius: var(--radius-md);
    background: rgba(255,255,255,0.025);
  }

  .metric-card strong {
    display: block;
    font-size: 18px;
    margin-top: 4px;
  }

  .gate-card {
    border: 1px solid var(--border-subtle);
    border-radius: var(--radius-md);
    background: rgba(255,255,255,0.025);
    padding: 16px;
    margin-bottom: 12px;
  }

  .gate-card.pending {
    border-color: rgba(245, 195, 91, 0.24);
    background: linear-gradient(90deg, rgba(245,195,91,0.10), rgba(255,255,255,0.025) 18%);
  }

  .gate-id {
    display: inline-block;
    margin-bottom: 8px;
    color: var(--text-tertiary);
    font-size: 11px;
  }

  .gate-question {
    font-size: 15px;
    line-height: 1.45;
    font-weight: 650;
    margin-bottom: 12px;
  }

  .gate-options {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .gate-btn {
    padding: 8px 14px;
    border: 1px solid var(--border-strong);
    border-radius: 999px;
    background: rgba(255,255,255,0.03);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 12px;
    font-weight: 700;
    transition: border-color 0.15s ease, background 0.15s ease, color 0.15s ease;
  }

  .gate-btn:hover:not(:disabled) {
    border-color: var(--accent);
    background: var(--accent-muted);
    color: var(--accent-strong);
  }

  .gate-btn.revise { border-color: rgba(245, 195, 91, 0.28); color: var(--yellow); }
  .gate-btn.revise:hover:not(:disabled) { background: var(--yellow-muted); border-color: var(--yellow); color: var(--yellow); }
  .gate-btn.approve { border-color: rgba(55, 214, 122, 0.28); color: var(--green); }
  .gate-btn.approve:hover:not(:disabled) { background: var(--green-muted); border-color: var(--green); color: var(--green); }
  .gate-btn[disabled] { opacity: 0.65; cursor: default; }
  .gate-btn.answered { background: var(--green-muted); border-color: var(--green); color: var(--green); }
  .gate-btn.answered.revise-answered { background: var(--yellow-muted); border-color: var(--yellow); color: var(--yellow); }

  .steer-form { margin-top: 10px; }

  .steer-textarea {
    width: 100%;
    min-height: 76px;
    padding: 12px;
    border-radius: 12px;
    border: 1px solid var(--border-strong);
    background: rgba(4, 7, 12, 0.94);
    color: var(--text-primary);
    font-family: var(--font-mono);
    font-size: 12px;
    resize: vertical;
  }

  .steer-textarea:focus {
    outline: none;
    border-color: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-muted);
  }

  .answer-note {
    margin-top: 12px;
    padding-left: 12px;
    border-left: 2px solid var(--border-strong);
    color: var(--text-secondary);
    font-size: 13px;
    white-space: pre-wrap;
  }

  .phase-summaries { margin: 12px 0 2px; }
  .phase-detail { margin-bottom: 6px; }

  .phase-preview {
    font-size: 11px;
    color: var(--text-secondary);
    background: rgba(4,7,12,0.88);
    border: 1px solid var(--border-subtle);
    border-radius: 8px;
    padding: 8px;
    margin: 4px 0 8px;
    white-space: pre-wrap;
    max-height: 200px;
    overflow: auto;
  }

  .empty, .queue-empty {
    color: var(--text-secondary);
    text-align: center;
    font-style: italic;
    padding: 16px 0;
  }

  .modal-overlay {
    position: fixed;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(0, 0, 0, 0.78);
    backdrop-filter: blur(3px);
    z-index: 1000;
  }

  .modal {
    width: min(920px, 92vw);
    max-height: 88vh;
    overflow: auto;
    padding: 28px;
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-lg);
    background: rgba(10, 18, 27, 0.98);
    box-shadow: 0 24px 60px rgba(0, 0, 0, 0.48);
  }

  .modal-title {
    font-size: 18px;
    font-weight: 700;
    margin-bottom: 20px;
    color: var(--accent-strong);
    border-bottom: 1px solid var(--border-subtle);
    padding-bottom: 10px;
  }

  .modal pre { font-size: 13px; white-space: pre-wrap; line-height: 1.5; color: var(--text-primary); }

  .modal-close {
    float: right;
    background: none;
    border: none;
    color: var(--text-tertiary);
    cursor: pointer;
    font-size: 24px;
    margin-top: -4px;
  }

  .modal-close:hover { color: var(--text-primary); }

  .md-body { font-size: 15px; line-height: 1.6; color: var(--text-primary); }
  .md-body h1, .md-body h2, .md-body h3, .md-body h4, .md-body h5, .md-body h6 {
    margin-top: 24px;
    margin-bottom: 12px;
    font-weight: 650;
    color: var(--text-primary);
  }

  .md-body h1 { font-size: 22px; padding-bottom: 8px; border-bottom: 1px solid var(--border-subtle); }
  .md-body h2 { font-size: 20px; padding-bottom: 6px; border-bottom: 1px solid var(--border-subtle); }
  .md-body h3 { font-size: 18px; }
  .md-body p, .md-body ul, .md-body ol { margin-bottom: 16px; }
  .md-body ul, .md-body ol { padding-left: 24px; color: var(--text-secondary); }
  .md-body li { margin-bottom: 6px; }
  .md-body a { color: var(--accent-strong); text-decoration: none; }
  .md-body a:hover { text-decoration: underline; }

  .md-body code {
    font-size: 85%;
    background: rgba(255,255,255,0.04);
    border: 1px solid var(--border-subtle);
    border-radius: 6px;
    padding: 2px 6px;
  }

  .md-body pre {
    background: rgba(4, 7, 12, 0.9);
    border: 1px solid var(--border-strong);
    border-radius: 12px;
    padding: 16px;
    overflow-x: auto;
    margin-bottom: 24px;
  }

  .md-body pre code { background: none; border: none; padding: 0; font-size: 13px; }
  .md-body blockquote {
    border-left: 4px solid var(--border-strong);
    padding-left: 16px;
    color: var(--text-secondary);
    margin-bottom: 16px;
    font-style: italic;
  }

  .md-body table { border-collapse: collapse; width: 100%; margin-bottom: 24px; }
  .md-body th, .md-body td {
    border: 1px solid var(--border-strong);
    padding: 8px 12px;
    font-size: 14px;
    text-align: left;
  }

  .md-body th { background: var(--bg-inset); font-weight: 600; color: var(--text-primary); }
  .md-body img { max-width: 100%; border-radius: var(--radius-sm); }
  .md-body hr { border: none; height: 1px; background: var(--border-strong); margin: 24px 0; }
  .md-body strong { font-weight: 700; color: var(--text-primary); }
  .md-body del { color: var(--text-tertiary); }

  @media (max-width: 900px) {
    .shell { padding: 12px; gap: 12px; }
    .cmd-top, .detail-hero { flex-direction: column; align-items: stretch; }
    .subtitle, .detail-meta { justify-content: flex-start; }
    .work-area { flex-direction: column; }
    .sidebar-wrap {
      width: 100%;
      max-height: 32vh;
    }
    .pipeline-card.selected::after { display: none; }
  }

  @media (max-width: 640px) {
    h1 { font-size: 24px; }
    .cmd-header, .detail-panel, .section, .modal { padding: 14px; }
    .pipeline-card { padding: 12px; }
    .queue-card { padding: 12px; }
    .detail-title { font-size: 22px; }
  }
</style>
</head>
<body>
<div class="shell">
  <header class="cmd-header">
    <div class="cmd-top">
      <div class="title-block">
        <div class="eyebrow">Execution Command Deck</div>
        <h1>Attractor Dashboard</h1>
      </div>
      <div class="subtitle">
        <span>Auto-refreshes every 3s</span>
        <span id="last-update"></span>
        <label><input type="checkbox" id="hide-completed" onchange="refresh()"> Hide completed</label>
      </div>
    </div>
    <div class="attention-stack" id="queue-section"></div>
  </header>

  <main class="work-area">
    <nav class="sidebar-wrap">
      <div class="sidebar-header">
        <div class="sidebar-title">Pipeline Navigation</div>
        <div class="sidebar-summary" id="pipeline-summary">0 visible</div>
      </div>
      <div class="pipeline-list" id="pipelines"></div>
    </nav>
    <section class="detail-panel" id="detail"></section>
  </main>
</div>
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

async function fetchText(url) {
  const r = await fetch(url);
  if (!r.ok) return null;
  return r.text();
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
    const statusClass = p.status ? `status-${p.status}` : '';
    return `
    <div class="pipeline-card ${statusClass} ${selectedPipeline === pid ? 'selected' : ''}"
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
  document.getElementById('detail').style.display = 'flex';
}

// ── Detail panel ────────────────────────────────────────────────────

async function loadDetail(id) {
  const [status, gates, logs, summaries, graphSvg, telemetry] = await Promise.all([
    fetchJson(`/api/pipeline/${id}/status`),
    fetchJson(`/api/pipeline/${id}/gates`),
    fetchJson(`/api/pipeline/${id}/logs`),
    fetchJson(`/api/pipeline/${id}/summaries`),
    fetchText(`/api/pipeline/${id}/graph`),
    fetchJson(`/api/pipeline/${id}/telemetry`)
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

  if (graphSvg) {
    html += `<div class="section"><div class="section-title">Live Graph</div><div class="graph-wrap">${graphSvg}</div></div>`;
  }

  if (telemetry && ((telemetry.per_node && Object.keys(telemetry.per_node).length > 0) || (telemetry.stage_metrics && Object.keys(telemetry.stage_metrics).length > 0))) {
    html += `<div class="section"><div class="section-title">Telemetry</div>`;
    if (telemetry.totals) {
      const tc = telemetry.totals.tool_calls || 0;
      const te = telemetry.totals.tool_errors || 0;
      const tt = telemetry.totals.total_tokens || 0;
      html += `<div style="margin-bottom:10px;font-size:12px;color:var(--text2);">tool calls: <strong>${tc}</strong> &middot; tool errors: <strong>${te}</strong> &middot; total tokens: <strong>${tt}</strong></div>`;
    }
    const perNode = telemetry.per_node || {};
    const entries = Object.entries(perNode);
    entries.sort((a, b) => a[0].localeCompare(b[0]));
    entries.forEach(([node, stats]) => {
      const start = stats.stage_start || 0;
      const end = stats.stage_end || 0;
      const retry = stats.stage_retry || 0;
      const metrics = telemetry.stage_metrics ? telemetry.stage_metrics[node] : null;
      let details = '';
      if (metrics) {
        const toolCalls = metrics.tool_calls || 0;
        const toolErrors = metrics.tool_errors || 0;
        const touched = metrics.touched_files_count || 0;
        const tokens = metrics.token_usage?.total_tokens || 0;
        details = ` &middot; tools:${toolCalls} errors:${toolErrors} files:${touched} tokens:${tokens}`;
      }
      html += `<div class="node-row"><span class="node-name">${node}</span><span class="node-status">start:${start} end:${end} retry:${retry}${details}</span></div>`;
      if (metrics && metrics.touched_files && metrics.touched_files.length > 0) {
        const files = metrics.touched_files.slice(0, 6).map(f => escapeHtml(String(f))).join(', ');
        html += `<div class="node-summary">touched: ${files}${metrics.touched_files.length > 6 ? ', ...' : ''}</div>`;
      }
    });
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
// builder — assisted DOT authoring workflow
// ═════════════════════════════════════════════════════════════════════

static Task<int> RunBuilder(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("builder: missing subcommand. Use init | graph | node | edge | inspect.");
        return Task.FromResult(1);
    }

    return args[0] switch
    {
        "init" => RunBuilderInit(args[1..]),
        "graph" => RunBuilderGraph(args[1..]),
        "node" => RunBuilderNode(args[1..]),
        "edge" => RunBuilderEdge(args[1..]),
        "inspect" => RunBuilderInspect(args[1..]),
        _ => Task.FromResult(ShowBuilderHelp())
    };
}

static Task<int> RunBuilderInit(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("builder init: missing <dotfile> path.");
        return Task.FromResult(1);
    }

    var dotFilePath = Path.GetFullPath(args[0]);
    var name = Path.GetFileNameWithoutExtension(dotFilePath);
    string? goal = null;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--name" when i + 1 < args.Length:
                name = args[++i];
                break;
            case "--goal" when i + 1 < args.Length:
                goal = args[++i];
                break;
        }
    }

    var graph = BuilderCommandSupport.InitializeGraph(name, goal);
    BuilderCommandSupport.Save(dotFilePath, graph);
    Console.WriteLine($"builder: initialized {dotFilePath}");
    return Task.FromResult(0);
}

static Task<int> RunBuilderGraph(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("builder graph: missing <dotfile> path.");
        return Task.FromResult(1);
    }

    var dotFilePath = Path.GetFullPath(args[0]);
    if (!File.Exists(dotFilePath))
    {
        Console.Error.WriteLine($"builder graph: dotfile not found: {dotFilePath}");
        return Task.FromResult(1);
    }

    var graph = BuilderCommandSupport.Load(dotFilePath);
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--goal" when i + 1 < args.Length:
                attributes["goal"] = args[++i];
                break;
            case "--label" when i + 1 < args.Length:
                attributes["label"] = args[++i];
                break;
            case "--retry-target" when i + 1 < args.Length:
                attributes["retry_target"] = args[++i];
                break;
            case "--fallback-retry-target" when i + 1 < args.Length:
                attributes["fallback_retry_target"] = args[++i];
                break;
            case "--default-fidelity" when i + 1 < args.Length:
                attributes["default_fidelity"] = args[++i];
                break;
            case "--default-max-retry" when i + 1 < args.Length:
                attributes["default_max_retry"] = args[++i];
                break;
            case "--attr" when i + 1 < args.Length:
                if (!TryParseBuilderAttribute(args[++i], out var key, out var value))
                    return Task.FromResult(FailBuilderArgument("builder graph", $"invalid --attr '{args[i]}' (expected key=value)."));
                attributes[key] = value;
                break;
        }
    }

    BuilderCommandSupport.UpsertGraphAttributes(graph, attributes);
    BuilderCommandSupport.Save(dotFilePath, graph);
    Console.WriteLine($"builder: updated graph attributes for {dotFilePath}");
    return Task.FromResult(0);
}

static Task<int> RunBuilderNode(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("builder node: usage: attractor builder node <dotfile> <node-id> [options]");
        return Task.FromResult(1);
    }

    var dotFilePath = Path.GetFullPath(args[0]);
    if (!File.Exists(dotFilePath))
    {
        Console.Error.WriteLine($"builder node: dotfile not found: {dotFilePath}");
        return Task.FromResult(1);
    }

    var nodeId = args[1];
    var graph = BuilderCommandSupport.Load(dotFilePath);
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--shape" when i + 1 < args.Length:
                attributes["shape"] = args[++i];
                break;
            case "--label" when i + 1 < args.Length:
                attributes["label"] = args[++i];
                break;
            case "--prompt" when i + 1 < args.Length:
                attributes["prompt"] = args[++i];
                break;
            case "--type" when i + 1 < args.Length:
                attributes["type"] = args[++i];
                break;
            case "--max-retries" when i + 1 < args.Length:
                attributes["max_retries"] = args[++i];
                break;
            case "--goal-gate":
                attributes["goal_gate"] = "true";
                break;
            case "--retry-target" when i + 1 < args.Length:
                attributes["retry_target"] = args[++i];
                break;
            case "--fallback-retry-target" when i + 1 < args.Length:
                attributes["fallback_retry_target"] = args[++i];
                break;
            case "--fidelity" when i + 1 < args.Length:
                attributes["fidelity"] = args[++i];
                break;
            case "--thread-id" when i + 1 < args.Length:
                attributes["thread_id"] = args[++i];
                break;
            case "--class" when i + 1 < args.Length:
                attributes["class"] = args[++i];
                break;
            case "--timeout" when i + 1 < args.Length:
                attributes["timeout"] = args[++i];
                break;
            case "--model" when i + 1 < args.Length:
                attributes["model"] = args[++i];
                break;
            case "--provider" when i + 1 < args.Length:
                attributes["provider"] = args[++i];
                break;
            case "--reasoning-effort" when i + 1 < args.Length:
                attributes["reasoning_effort"] = args[++i];
                break;
            case "--auto-status":
                attributes["auto_status"] = "true";
                break;
            case "--allow-partial":
                attributes["allow_partial"] = "true";
                break;
            case "--attr" when i + 1 < args.Length:
                if (!TryParseBuilderAttribute(args[++i], out var key, out var value))
                    return Task.FromResult(FailBuilderArgument("builder node", $"invalid --attr '{args[i]}' (expected key=value)."));
                attributes[key] = value;
                break;
        }
    }

    BuilderCommandSupport.UpsertNode(graph, nodeId, attributes);
    BuilderCommandSupport.Save(dotFilePath, graph);
    Console.WriteLine($"builder: upserted node '{nodeId}' in {dotFilePath}");
    return Task.FromResult(0);
}

static Task<int> RunBuilderEdge(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("builder edge: usage: attractor builder edge <dotfile> <from-node> <to-node> [options]");
        return Task.FromResult(1);
    }

    var dotFilePath = Path.GetFullPath(args[0]);
    if (!File.Exists(dotFilePath))
    {
        Console.Error.WriteLine($"builder edge: dotfile not found: {dotFilePath}");
        return Task.FromResult(1);
    }

    var fromNode = args[1];
    var toNode = args[2];
    var graph = BuilderCommandSupport.Load(dotFilePath);
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var i = 3; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--label" when i + 1 < args.Length:
                attributes["label"] = args[++i];
                break;
            case "--condition" when i + 1 < args.Length:
                attributes["condition"] = args[++i];
                break;
            case "--weight" when i + 1 < args.Length:
                attributes["weight"] = args[++i];
                break;
            case "--fidelity" when i + 1 < args.Length:
                attributes["fidelity"] = args[++i];
                break;
            case "--thread-id" when i + 1 < args.Length:
                attributes["thread_id"] = args[++i];
                break;
            case "--loop-restart":
                attributes["loop_restart"] = "true";
                break;
            case "--attr" when i + 1 < args.Length:
                if (!TryParseBuilderAttribute(args[++i], out var key, out var value))
                    return Task.FromResult(FailBuilderArgument("builder edge", $"invalid --attr '{args[i]}' (expected key=value)."));
                attributes[key] = value;
                break;
        }
    }

    BuilderCommandSupport.UpsertEdge(graph, fromNode, toNode, attributes);
    BuilderCommandSupport.Save(dotFilePath, graph);
    Console.WriteLine($"builder: upserted edge {fromNode} -> {toNode} in {dotFilePath}");
    return Task.FromResult(0);
}

static Task<int> RunBuilderInspect(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("builder inspect: missing <dotfile> path.");
        return Task.FromResult(1);
    }

    var dotFilePath = Path.GetFullPath(args[0]);
    if (!File.Exists(dotFilePath))
    {
        Console.Error.WriteLine($"builder inspect: dotfile not found: {dotFilePath}");
        return Task.FromResult(1);
    }

    var graph = BuilderCommandSupport.Load(dotFilePath);
    Console.WriteLine(BuilderCommandSupport.Describe(graph));
    return Task.FromResult(0);
}

static bool TryParseBuilderAttribute(string text, out string key, out string value)
{
    key = string.Empty;
    value = string.Empty;
    var separator = text.IndexOf('=');
    if (separator <= 0 || separator == text.Length - 1)
        return false;

    key = text[..separator];
    value = text[(separator + 1)..];
    return !string.IsNullOrWhiteSpace(key);
}

static int FailBuilderArgument(string commandName, string message)
{
    Console.Error.WriteLine($"{commandName}: {message}");
    return 1;
}

static int ShowBuilderHelp()
{
    Console.WriteLine("builder — assisted DOT authoring workflow");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  attractor builder init <dotfile> [--name <name>] [--goal <goal>]");
    Console.WriteLine("  attractor builder graph <dotfile> [--goal <goal>] [--label <label>] [--attr key=value]");
    Console.WriteLine("  attractor builder node <dotfile> <node-id> [--shape <shape>] [--label <label>] [--prompt <prompt>] [--attr key=value]");
    Console.WriteLine("  attractor builder edge <dotfile> <from-node> <to-node> [--label <label>] [--condition <expr>] [--attr key=value]");
    Console.WriteLine("  attractor builder inspect <dotfile>");
    return 0;
}

// ═════════════════════════════════════════════════════════════════════
// lint — validate DOT files
// ═════════════════════════════════════════════════════════════════════

static Task<int> RunLint(string[] args)
{
    var targets = new List<string>();
    var recursive = false;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--all":
            case "--recursive":
                recursive = true;
                break;
            default:
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    targets.Add(arg);
                break;
        }
    }

    if (targets.Count == 0)
    {
        var cwdDot = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.dot");
        if (cwdDot.Length > 0)
            targets.AddRange(cwdDot);
        else
            targets.Add("dotfiles");
    }

    var dotFiles = new List<string>();
    foreach (var target in targets)
    {
        var full = Path.GetFullPath(target);
        if (File.Exists(full))
        {
            dotFiles.Add(full);
            continue;
        }

        if (Directory.Exists(full))
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            dotFiles.AddRange(Directory.GetFiles(full, "*.dot", option));
            continue;
        }

        Console.Error.WriteLine($"lint: target not found: {target}");
    }

    if (dotFiles.Count == 0)
    {
        Console.Error.WriteLine("lint: no .dot files found.");
        return Task.FromResult(1);
    }

    var totalErrors = 0;
    var totalWarnings = 0;

    foreach (var dotFile in dotFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Linting {dotFile}");
        try
        {
            var source = File.ReadAllText(dotFile);
            var graph = DotParser.Parse(source);
            var results = Validator.Validate(graph);

            var errors = results.Where(r => r.Severity == LintSeverity.Error).ToList();
            var warnings = results.Where(r => r.Severity == LintSeverity.Warning).ToList();
            totalErrors += errors.Count;
            totalWarnings += warnings.Count;

            if (results.Count == 0)
            {
                Console.WriteLine("  ✓ no issues");
                continue;
            }

            foreach (var issue in results)
            {
                var location = issue.NodeId ?? issue.EdgeId ?? "graph";
                var marker = issue.Severity == LintSeverity.Error ? "error" : "warn";
                Console.WriteLine($"  [{marker}] {issue.Rule} ({location}) {issue.Message}");
            }
        }
        catch (Exception ex)
        {
            totalErrors++;
            Console.WriteLine($"  [error] parse {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"lint summary: {dotFiles.Count} files, {totalErrors} errors, {totalWarnings} warnings");

    return Task.FromResult(totalErrors == 0 ? 0 : 1);
}

// ═════════════════════════════════════════════════════════════════════
// run — start a pipeline (existing behavior)
// ═════════════════════════════════════════════════════════════════════

static async Task<int> RunPipeline(string[] args)
{
    var options = RunOptions.Parse(args);

    var dotFilePath = options.DotFilePath;
    if (string.IsNullOrWhiteSpace(dotFilePath))
        dotFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "betrayal.dot");

    dotFilePath = Path.GetFullPath(dotFilePath);
    if (!File.Exists(dotFilePath))
    {
        Console.Error.WriteLine($"DOT file not found: {dotFilePath}");
        return 1;
    }

    var workingDir = RunCommandSupport.ResolveWorkingDirectory(dotFilePath, options);
    Directory.CreateDirectory(workingDir);

    // Convention: dotfile lives at {project}/dotfiles/{name}.dot,
    // so the project root is the dotfile directory's parent.
    var projectRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dotFilePath)!, ".."));

    var logsDir = Path.Combine(workingDir, "logs");
    Directory.CreateDirectory(logsDir);

    var gatesDir = Path.Combine(workingDir, "gates");
    Directory.CreateDirectory(gatesDir);

    var checkpointPath = Path.Combine(logsDir, "checkpoint.json");
    var checkpointExists = File.Exists(checkpointPath);
    var manifestPath = Path.Combine(workingDir, "run-manifest.json");
    var resumeDecision = AutoResumeSupport.DecideResume(options, checkpointPath, manifestPath);
    var shouldResume = resumeDecision.ShouldResume;

    if (options.Resume && !checkpointExists)
    {
        Console.Error.WriteLine("Resume requested but no checkpoint exists for this run.");
        return 1;
    }

    if (!shouldResume && checkpointExists)
    {
        Console.WriteLine("Starting fresh run (autoresume disabled).");
        try
        {
            Directory.Delete(logsDir, recursive: true);
            Directory.Delete(gatesDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures; we'll recreate directories below.
        }
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(gatesDir);
        checkpointExists = false;
    }

    if (!TryAcquireRunLock(Path.Combine(workingDir, "run.lock"), out var lockError))
    {
        Console.Error.WriteLine(lockError);
        return 1;
    }

    var manifest = resumeDecision.ExistingManifest ?? new RunManifest();
    var resultPath = Path.Combine(logsDir, "result.json");
    manifest.run_id = string.IsNullOrWhiteSpace(manifest.run_id) ? Guid.NewGuid().ToString("N") : manifest.run_id;
    manifest.pid = Environment.ProcessId;
    manifest.graph_path = dotFilePath;
    manifest.started_at = string.IsNullOrWhiteSpace(manifest.started_at) ? DateTime.UtcNow.ToString("o") : manifest.started_at;
    manifest.updated_at = DateTime.UtcNow.ToString("o");
    manifest.active_stage = Checkpoint.Load(logsDir)?.CurrentNodeId ?? "start";
    manifest.status = "starting";
    manifest.crash = null;
    manifest.auto_resume_policy = AutoResumeSupport.FormatPolicy(options.AutoResumePolicy);
    manifest.resume_source = string.IsNullOrWhiteSpace(options.StartAt) ? resumeDecision.ResumeSource : "start-at";
    manifest.checkpoint_path = checkpointPath;
    manifest.result_path = resultPath;
    manifest.Save(manifestPath);

    RegisterRun(dotFilePath, workingDir);

    Console.WriteLine($"Pipeline:    {dotFilePath}");
    Console.WriteLine($"Project root:{projectRoot}");
    Console.WriteLine($"Working dir: {workingDir}");
    Console.WriteLine($"Logs dir:    {logsDir}");
    Console.WriteLine($"Gates dir:   {gatesDir}");
    Console.WriteLine($"Run ID:      {manifest.run_id}");
    Console.WriteLine($"Resume mode: {(shouldResume ? "resume" : "fresh")}");
    Console.WriteLine($"Autoresume:  {AutoResumeSupport.FormatPolicy(options.AutoResumePolicy)}");
    if (!string.IsNullOrWhiteSpace(options.StartAt))
        Console.WriteLine($"Start-at:    {options.StartAt}");
    if (!string.IsNullOrWhiteSpace(options.SteerText))
        Console.WriteLine("Steer text:  [provided]");
    if (!string.IsNullOrWhiteSpace(options.SteerFilePath))
        Console.WriteLine($"Steer file:  {options.SteerFilePath}");
    if (!string.Equals(options.BackendMode, "live", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"Backend:     {options.BackendMode}");
    Console.WriteLine();

    var dotSource = await File.ReadAllTextAsync(dotFilePath);
    var graph = DotParser.Parse(dotSource);
    graph.Attributes["source_path"] = dotFilePath;

    if (!string.IsNullOrWhiteSpace(options.StartAt))
    {
        if (!RunCommandSupport.TryApplyStartAt(graph, logsDir, options.StartAt, out var startAtError))
        {
            Console.Error.WriteLine(startAtError);
            ReleaseRunLock(Path.Combine(workingDir, "run.lock"));
            return 1;
        }
        shouldResume = true;
    }

    Console.WriteLine($"Graph: {graph.Name}");
    Console.WriteLine($"Goal:  {graph.Goal?[..Math.Min(graph.Goal.Length, 80)]}...");
    Console.WriteLine($"Nodes: {graph.Nodes.Count}");
    Console.WriteLine($"Edges: {graph.Edges.Count}");
    Console.WriteLine();

    var backend = RunnerBackendFactory.Create(workingDir, projectRoot, options);
    var runtimeObserver = new RunnerRuntimeObserver(manifest, manifestPath, checkpointPath, options.CrashAfterStage);
    var supervisorController = new SupervisorController(options, projectRoot, dotFilePath);

    var config = new PipelineConfig(
        LogsRoot: logsDir,
        Interviewer: new FileInterviewer(gatesDir),
        Backend: backend,
        Transforms: new List<IGraphTransform>
        {
            new StylesheetTransform(),
            new VariableExpansionTransform()
        },
        RuntimeObserver: runtimeObserver,
        SupervisorController: supervisorController
    );

    var engine = new PipelineEngine(config);

    Console.WriteLine("Starting pipeline...");
    Console.WriteLine(new string('─', 60));
    manifest.status = "running";
    manifest.active_stage = Checkpoint.Load(logsDir)?.CurrentNodeId ?? "start";
    manifest.updated_at = DateTime.UtcNow.ToString("o");
    manifest.Save(manifestPath);

    var exitCode = 0;
    var shouldRespawn = false;
    try
    {
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
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(resultPayload, RunnerJson.Options));

        manifest.status = result.Status == OutcomeStatus.Success ? "completed" : "failed";
        manifest.active_stage = result.CompletedNodes.LastOrDefault() ?? "unknown";
        manifest.updated_at = DateTime.UtcNow.ToString("o");
        manifest.result_path = resultPath;
        manifest.Save(manifestPath);

        exitCode = result.Status == OutcomeStatus.Success ? 0 : 1;
    }
    catch (Exception ex)
    {
        manifest.status = options.AutoResumePolicy == AutoResumePolicy.Always ? "respawning" : "crashed";
        manifest.active_stage = Checkpoint.Load(logsDir)?.CurrentNodeId ?? manifest.active_stage;
        manifest.updated_at = DateTime.UtcNow.ToString("o");
        manifest.crash = ex.Message;
        if (options.AutoResumePolicy == AutoResumePolicy.Always)
        {
            manifest.respawn_count += 1;
            manifest.last_respawned_at = DateTime.UtcNow.ToString("o");
            shouldRespawn = true;
        }
        manifest.Save(manifestPath);
        Console.Error.WriteLine($"Pipeline crashed: {ex.Message}");
        exitCode = options.AutoResumePolicy == AutoResumePolicy.Always ? 134 : 1;
    }
    finally
    {
        ReleaseRunLock(Path.Combine(workingDir, "run.lock"));
        backend.Dispose();
    }

    if (shouldRespawn)
    {
        if (!AutoResumeSupport.TrySpawnResumeProcess(options, dotFilePath, workingDir, environmentOverrides: null, out var respawnProcess, out var respawnError))
        {
            manifest.status = "crashed";
            manifest.updated_at = DateTime.UtcNow.ToString("o");
            manifest.crash = $"{manifest.crash} | Respawn failed: {respawnError}";
            manifest.Save(manifestPath);
            Console.Error.WriteLine($"Unable to respawn runner: {respawnError}");
            return 1;
        }

        Console.Error.WriteLine($"Respawned runner pid {respawnProcess!.Id} for autoresume.");
    }

    return exitCode;
}

static bool TryAcquireRunLock(string lockPath, out string error)
{
    error = string.Empty;
    try
    {
        if (File.Exists(lockPath))
        {
            var raw = File.ReadAllText(lockPath);
            var existing = JsonSerializer.Deserialize<RunLock>(raw);
            if (existing is not null && existing.pid > 0 && IsProcessAlive(existing.pid))
            {
                error = $"Run is already active (pid={existing.pid}). Lock: {lockPath}";
                return false;
            }
        }

        var lockPayload = new RunLock
        {
            pid = Environment.ProcessId,
            started_at = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(lockPath, JsonSerializer.Serialize(lockPayload, JsonOpts()));
        return true;
    }
    catch (Exception ex)
    {
        error = $"Unable to create run lock '{lockPath}': {ex.Message}";
        return false;
    }
}

static void ReleaseRunLock(string lockPath)
{
    try
    {
        if (!File.Exists(lockPath))
            return;

        var raw = File.ReadAllText(lockPath);
        var existing = JsonSerializer.Deserialize<RunLock>(raw);
        if (existing?.pid == Environment.ProcessId)
            File.Delete(lockPath);
    }
    catch
    {
        // Ignore lock release failures.
    }
}

static bool IsProcessAlive(int pid)
{
    try
    {
        var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch
    {
        return false;
    }
}

// ═════════════════════════════════════════════════════════════════════
// help
// ═════════════════════════════════════════════════════════════════════

static int ShowHelp()
{
    Console.WriteLine("attractor — pipeline runner CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  attractor run <dotfile> [options]    Start a pipeline");
    Console.WriteLine("  attractor <dotfile>                  Shorthand for 'run'");
    Console.WriteLine("  attractor gate [--dir <dir>]         Show the current pending gate");
    Console.WriteLine("  attractor gate answer [<choice>]     Answer a gate (by label, number, or interactive)");
    Console.WriteLine("  attractor gate watch [--dir <dir>]   Watch for gates and answer interactively");
    Console.WriteLine("  attractor status [--dir <dir>]       Show pipeline progress");
    Console.WriteLine("  attractor logs [<node>] [--dir <dir>]  View node artifacts");
    Console.WriteLine("  attractor web [--dir <dir>] [--port N] Launch web dashboard");
    Console.WriteLine("  attractor providers <subcommand>      Ping providers or sync new provider models");
    Console.WriteLine("  attractor lint [path] [--recursive]  Lint one file or a directory of dotfiles");
    Console.WriteLine("  attractor builder <subcommand>        Create or edit DOT pipelines");
    Console.WriteLine("  attractor interactive <dotfile>       Open the line-oriented workflow editor");
    Console.WriteLine();
    Console.WriteLine("Run options:");
    Console.WriteLine("  --resume                 Resume only if a checkpoint exists");
    Console.WriteLine("  --autoresume             Auto-resume incomplete runs (default: on)");
    Console.WriteLine("  --autoresume-policy <policy>  Set autoresume policy: off | on | always");
    Console.WriteLine("  --no-autoresume          Force a fresh run even if checkpoint exists");
    Console.WriteLine("  --resume-from <dir>      Use a prior run directory as the working directory");
    Console.WriteLine("  --start-at <node>        Override checkpoint start node");
    Console.WriteLine("  --steer-text <text>      Inject steering text into the coding session");
    Console.WriteLine("  --steer-file <path>      Read steering instructions from a file before each stage");
    Console.WriteLine("  --backend <mode>         Backend mode: live | scripted");
    Console.WriteLine("  --backend-script <path>  JSON plan for the scripted backend");
    Console.WriteLine("  --crash-after-stage <id> Inject a crash after checkpointing a stage (QA/test)");
    Console.WriteLine();
    Console.WriteLine("Output directory resolution:");
    Console.WriteLine("  1. --dir <path> flag");
    Console.WriteLine("  2. output/ sibling to a .dot file in cwd");
    Console.WriteLine("  3. dotfiles/output/ relative to cwd");
    return 0;
}

static Task<int> RunInteractive(string[] args)
{
    return InteractiveEditorCommand.RunAsync(args, RunPipeline);
}

class RunEntry
{
    public string dotfile { get; set; } = "";
    public string output_dir { get; set; } = "";
    public string name { get; set; } = "";
    public string started { get; set; } = "";
}
