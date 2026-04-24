namespace Soulcaster.Runner.Storage;

internal static class WorkflowStoreFactory
{
    public static IWorkflowStore CreateDefault(string workingDirectory)
    {
        return new CompositeWorkflowStore(
        [
            new FileWorkflowStore(workingDirectory),
            new SqliteWorkflowStore(workingDirectory)
        ]);
    }
}
