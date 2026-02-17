namespace JcAttractor.CodingAgent;

public interface IExecutionEnvironment
{
    string WorkingDirectory { get; }
    Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken ct = default);
    Task WriteFileAsync(string path, string content, CancellationToken ct = default);
    Task<string> RunCommandAsync(string command, int? timeoutMs = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GlobAsync(string pattern, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GrepAsync(string pattern, string? path = null, CancellationToken ct = default);
    bool FileExists(string path);
    Task<string> EditFileAsync(string path, string oldString, string newString, CancellationToken ct = default);
}
