namespace JcAttractor.Attractor;

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
}
