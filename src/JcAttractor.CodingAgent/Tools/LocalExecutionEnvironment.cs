using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace JcAttractor.CodingAgent;

public class LocalExecutionEnvironment : IExecutionEnvironment
{
    public string WorkingDirectory { get; }

    public LocalExecutionEnvironment(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
    }

    public async Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {fullPath}");

        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        var startLine = offset ?? 0;
        var count = limit ?? lines.Length;

        if (startLine >= lines.Length)
            return string.Empty;

        var sb = new StringBuilder();
        var end = Math.Min(startLine + count, lines.Length);
        var lineNumWidth = end.ToString().Length;

        for (var i = startLine; i < end; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(lineNumWidth);
            sb.AppendLine($"{lineNum} | {lines[i]}");
        }

        return sb.ToString();
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content, ct);
    }

    public async Task<string> RunCommandAsync(string command, int? timeoutMs = null, CancellationToken ct = default)
    {
        var timeout = timeoutMs ?? 10000;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c {EscapeShellArgument(command)}",
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort kill
            }

            if (ct.IsCancellationRequested)
                throw;

            return $"[ERROR: Command timed out after {timeout}ms]\n{stdout}\n{stderr}".Trim();
        }

        var result = new StringBuilder();
        if (stdout.Length > 0)
            result.Append(stdout);
        if (stderr.Length > 0)
        {
            if (result.Length > 0)
                result.AppendLine();
            result.Append(stderr);
        }

        if (process.ExitCode != 0)
            result.Insert(0, $"[Exit code: {process.ExitCode}]\n");

        return result.ToString().Trim();
    }

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = new List<string>();

        try
        {
            // Determine search directory and file pattern from the glob
            var searchDir = WorkingDirectory;
            var filePattern = pattern;

            // Handle patterns like "**/*.cs" or "src/**/*.cs"
            var parts = pattern.Split('/', '\\');
            var fixedParts = new List<string>();
            var globStart = 0;

            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Contains('*') || parts[i].Contains('?'))
                {
                    globStart = i;
                    break;
                }
                fixedParts.Add(parts[i]);
                globStart = i + 1;
            }

            if (fixedParts.Count > 0)
            {
                var fixedPath = Path.Combine(fixedParts.ToArray());
                searchDir = Path.Combine(WorkingDirectory, fixedPath);
            }

            if (!Directory.Exists(searchDir))
                return Task.FromResult<IReadOnlyList<string>>(results);

            var remainingPattern = string.Join("/", parts.Skip(globStart));
            var isRecursive = remainingPattern.Contains("**");

            // Extract the file name pattern (last segment)
            var fileNamePattern = parts.Last();
            var searchOption = isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var files = Directory.EnumerateFiles(searchDir, fileNamePattern, searchOption);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(file);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Return empty
        }

        results.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    public async Task<IReadOnlyList<string>> GrepAsync(string pattern, string? path = null, CancellationToken ct = default)
    {
        var searchPath = path is not null ? ResolvePath(path) : WorkingDirectory;
        var results = new List<string>();
        var regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        if (File.Exists(searchPath))
        {
            await SearchFileAsync(searchPath, regex, results, ct);
        }
        else if (Directory.Exists(searchPath))
        {
            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                // Skip binary files and hidden directories
                if (IsBinaryExtension(file) || IsHiddenPath(file))
                    continue;

                await SearchFileAsync(file, regex, results, ct);
            }
        }

        return results;
    }

    public bool FileExists(string path)
    {
        var fullPath = ResolvePath(path);
        return File.Exists(fullPath);
    }

    public async Task<string> EditFileAsync(string path, string oldString, string newString, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {fullPath}");

        var content = await File.ReadAllTextAsync(fullPath, ct);

        var index = content.IndexOf(oldString, StringComparison.Ordinal);
        if (index < 0)
            return $"Error: old_string not found in {fullPath}";

        // Check for uniqueness - find if there's a second occurrence
        var secondIndex = content.IndexOf(oldString, index + oldString.Length, StringComparison.Ordinal);
        if (secondIndex >= 0)
            return $"Error: old_string appears multiple times in {fullPath}. Provide more context to make it unique.";

        var newContent = content[..index] + newString + content[(index + oldString.Length)..];
        await File.WriteAllTextAsync(fullPath, newContent, ct);

        return $"Successfully edited {fullPath}";
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(WorkingDirectory, path));
    }

    private static string EscapeShellArgument(string arg)
    {
        return "'" + arg.Replace("'", "'\"'\"'") + "'";
    }

    private static async Task SearchFileAsync(string filePath, Regex regex, List<string> results, CancellationToken ct)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, ct);
            for (var i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (regex.IsMatch(lines[i]))
                {
                    results.Add($"{filePath}:{i + 1}:{lines[i]}");
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Skip files that can't be read (binary, locked, etc.)
        }
    }

    private static bool IsBinaryExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".dll" or ".exe" or ".bin" or ".obj" or ".o" or ".so" or ".dylib"
            or ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp"
            or ".pdf" or ".zip" or ".tar" or ".gz" or ".7z" or ".rar"
            or ".woff" or ".woff2" or ".ttf" or ".eot"
            or ".mp3" or ".mp4" or ".wav" or ".avi" or ".mov";
    }

    private static bool IsHiddenPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.StartsWith('.') && p.Length > 1 && p != "..");
    }
}
