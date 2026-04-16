namespace Soulcaster.Attractor.Execution;

public class PipelineContext
{
    private readonly Dictionary<string, string> _data = new();

    public void Set(string key, string value) => _data[key] = value;
    public string Get(string key) => _data.GetValueOrDefault(key, "");
    public bool Has(string key) => _data.ContainsKey(key);
    public IReadOnlyDictionary<string, string> All => _data;

    public void MergeUpdates(Dictionary<string, string>? updates)
    {
        if (updates == null) return;
        foreach (var (k, v) in updates)
            _data[k] = v;
    }

    /// <summary>
    /// Creates an isolated clone of this context for parallel branch execution.
    /// </summary>
    public PipelineContext Clone()
    {
        var clone = new PipelineContext();
        foreach (var (k, v) in _data)
            clone._data[k] = v;
        return clone;
    }
}
