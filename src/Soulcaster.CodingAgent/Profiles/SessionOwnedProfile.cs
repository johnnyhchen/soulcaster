namespace Soulcaster.CodingAgent.Profiles;

internal sealed class SessionOwnedProfile : IProviderProfile
{
    private readonly IProviderProfile _template;

    public SessionOwnedProfile(IProviderProfile template)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        Model = template.Model;
        ToolRegistry = template.ToolRegistry.Clone();
    }

    public string Id => _template.Id;
    public string Model { get; set; }
    public ToolRegistry ToolRegistry { get; }
    public bool SupportsReasoning => _template.SupportsReasoning;
    public bool SupportsStreaming => _template.SupportsStreaming;
    public bool SupportsParallelToolCalls => _template.SupportsParallelToolCalls;
    public int ContextWindowSize => _template.ContextWindowSize;

    public string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null) =>
        _template.BuildSystemPrompt(env, projectDocs);

    public IReadOnlyList<ToolDefinition> Tools() => ToolRegistry.GetDefinitions();

    public Dictionary<string, object>? ProviderOptions()
    {
        var options = _template.ProviderOptions();
        return options is null ? null : new Dictionary<string, object>(options);
    }
}
