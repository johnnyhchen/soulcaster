using System.Text;

namespace Soulcaster.Runner;

public static class InteractiveEditorCommand
{
    public static async Task<int> RunAsync(string[] args, Func<string[], Task<int>> runPipeline)
    {
        if (runPipeline is null)
            throw new ArgumentNullException(nameof(runPipeline));

        var dotFilePath = ParseDotFilePath(args);
        if (string.IsNullOrWhiteSpace(dotFilePath))
        {
            Console.Error.WriteLine("interactive: missing <dotfile> path.");
            return 1;
        }

        dotFilePath = Path.GetFullPath(dotFilePath);
        var graph = File.Exists(dotFilePath)
            ? BuilderCommandSupport.Load(dotFilePath)
            : BuilderCommandSupport.InitializeGraph(
                Path.GetFileNameWithoutExtension(dotFilePath),
                ParseOption(args, "--goal"));

        Console.WriteLine($"interactive editor: {dotFilePath}");
        Console.WriteLine("Type 'help' for commands.");

        while (true)
        {
            Console.Write("editor> ");
            var line = Console.ReadLine();
            if (line is null)
                break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var tokens = Tokenize(trimmed);
            if (tokens.Count == 0)
                continue;

            var command = tokens[0].ToLowerInvariant();
            switch (command)
            {
                case "help":
                    ShowHelp();
                    break;
                case "goal":
                    BuilderCommandSupport.UpsertGraphAttributes(graph, new Dictionary<string, string>
                    {
                        ["goal"] = string.Join(' ', tokens.Skip(1))
                    });
                    break;
                case "attr":
                    BuilderCommandSupport.UpsertGraphAttributes(graph, ParseAssignments(tokens.Skip(1)));
                    break;
                case "stage":
                    if (tokens.Count < 2)
                    {
                        Console.Error.WriteLine("stage: usage: stage <node-id> [key=value ...]");
                        break;
                    }

                    BuilderCommandSupport.UpsertNode(graph, tokens[1], ParseAssignments(tokens.Skip(2)));
                    break;
                case "edge":
                    if (tokens.Count < 3)
                    {
                        Console.Error.WriteLine("edge: usage: edge <from> <to> [key=value ...]");
                        break;
                    }

                    BuilderCommandSupport.UpsertEdge(graph, tokens[1], tokens[2], ParseAssignments(tokens.Skip(3)));
                    break;
                case "model":
                case "provider":
                case "reasoning":
                    if (tokens.Count < 3)
                    {
                        Console.Error.WriteLine($"{command}: usage: {command} <node-id> <value>");
                        break;
                    }

                    var attrKey = command switch
                    {
                        "model" => "model",
                        "provider" => "provider",
                        _ => "reasoning_effort"
                    };
                    BuilderCommandSupport.UpsertNode(graph, tokens[1], new Dictionary<string, string>
                    {
                        [attrKey] = string.Join(' ', tokens.Skip(2))
                    });
                    break;
                case "prompt":
                    if (tokens.Count < 2)
                    {
                        Console.Error.WriteLine("prompt: usage: prompt <node-id>");
                        break;
                    }

                    Console.WriteLine("Enter prompt text. Finish with a single '.' line.");
                    var promptBuilder = new StringBuilder();
                    while (true)
                    {
                        var promptLine = Console.ReadLine();
                        if (promptLine is null || promptLine == ".")
                            break;

                        if (promptBuilder.Length > 0)
                            promptBuilder.AppendLine();
                        promptBuilder.Append(promptLine);
                    }

                    BuilderCommandSupport.UpsertNode(graph, tokens[1], new Dictionary<string, string>
                    {
                        ["prompt"] = promptBuilder.ToString()
                    });
                    break;
                case "inspect":
                    Console.WriteLine(BuilderCommandSupport.Describe(graph));
                    break;
                case "save":
                    if (tokens.Count > 1)
                        dotFilePath = Path.GetFullPath(tokens[1]);
                    BuilderCommandSupport.Save(dotFilePath, graph);
                    Console.WriteLine($"saved {dotFilePath}");
                    break;
                case "run":
                    BuilderCommandSupport.Save(dotFilePath, graph);
                    var exitCode = await runPipeline([dotFilePath, .. tokens.Skip(1)]);
                    Console.WriteLine($"run exit code: {exitCode}");
                    break;
                case "quit":
                case "exit":
                    BuilderCommandSupport.Save(dotFilePath, graph);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command: {tokens[0]}");
                    break;
            }
        }

        BuilderCommandSupport.Save(dotFilePath, graph);
        return 0;
    }

    private static string ParseDotFilePath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dotfile" && i + 1 < args.Length)
                return args[i + 1];

            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                return args[i];
        }

        return string.Empty;
    }

    private static string? ParseOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static Dictionary<string, string> ParseAssignments(IEnumerable<string> rawTokens)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in rawTokens)
        {
            var pivot = token.IndexOf('=', StringComparison.Ordinal);
            if (pivot <= 0)
                continue;

            var key = token[..pivot];
            var value = token[(pivot + 1)..];
            attributes[key] = value;
        }

        return attributes;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var ch in line)
        {
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                FlushToken(tokens, current);
                continue;
            }

            if (ch is '"' or '\'')
            {
                if (quote == '\0')
                {
                    quote = ch;
                    continue;
                }

                if (quote == ch)
                {
                    quote = '\0';
                    continue;
                }
            }

            current.Append(ch);
        }

        FlushToken(tokens, current);
        return tokens;
    }

    private static void FlushToken(ICollection<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  goal <text>");
        Console.WriteLine("  attr key=value [key=value ...]");
        Console.WriteLine("  stage <node-id> [key=value ...]");
        Console.WriteLine("  edge <from> <to> [key=value ...]");
        Console.WriteLine("  model <node-id> <model>");
        Console.WriteLine("  provider <node-id> <provider>");
        Console.WriteLine("  reasoning <node-id> <effort>");
        Console.WriteLine("  prompt <node-id>");
        Console.WriteLine("  inspect");
        Console.WriteLine("  save [path]");
        Console.WriteLine("  run [runner args ...]");
        Console.WriteLine("  quit");
    }
}
