namespace JcAttractor.Attractor;

public static class RuntimeStageResolver
{
    public static string ResolveStageId(PipelineContext context, string nodeId)
    {
        var stageId = context.Get("runtime.stage_id");
        return string.IsNullOrWhiteSpace(stageId) ? nodeId : stageId;
    }

    public static string ResolveStageDir(string logsRoot, PipelineContext context, string nodeId)
    {
        return Path.Combine(logsRoot, ResolveStageId(context, nodeId));
    }
}
