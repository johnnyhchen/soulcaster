namespace Soulcaster.CodingAgent.Tools;

/// <summary>
/// Discovers project documentation files (CLAUDE.md, GEMINI.md, AGENTS.md, etc.)
/// in the working directory and its parent directories.
/// </summary>
public static class ProjectDocs
{
    private static readonly string[] DocFileNames =
    {
        "CLAUDE.md",
        "GEMINI.md",
        "AGENTS.md",
        ".claude/CLAUDE.md",
        ".gemini/GEMINI.md",
        "CONTRIBUTING.md"
    };

    /// <summary>
    /// Discovers project documentation files by scanning the working directory
    /// and its parent directories up to the filesystem root.
    /// </summary>
    public static IReadOnlyList<string> Discover(string workingDirectory)
    {
        var docs = new List<string>();

        if (!Directory.Exists(workingDirectory))
            return docs;

        var dir = workingDirectory;

        // Walk up from working directory to find project docs
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var docName in DocFileNames)
            {
                var docPath = Path.Combine(dir, docName);
                if (File.Exists(docPath))
                {
                    try
                    {
                        var content = File.ReadAllText(docPath);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            docs.Add($"# {docName} (from {dir})\n\n{content}");
                        }
                    }
                    catch
                    {
                        // Skip unreadable files
                    }
                }
            }

            // Check if we've reached a project root indicator (git repo, package.json, etc.)
            try
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) ||
                    File.Exists(Path.Combine(dir, "package.json")) ||
                    File.Exists(Path.Combine(dir, "Cargo.toml")) ||
                    Directory.GetFiles(dir, "*.sln").Length > 0 ||
                    Directory.GetFiles(dir, "*.csproj").Length > 0)
                {
                    break; // Don't go above project root
                }
            }
            catch
            {
                break; // Can't access directory, stop
            }

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }

        return docs;
    }
}
