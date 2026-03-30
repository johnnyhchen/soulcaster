using JcAttractor.Attractor;

namespace JcAttractor.Tests;

internal static class ScenarioAssert
{
    public static void NodesExecutedInOrder(ScenarioRun run, params string[] expectedNodeIds)
    {
        Assert.Equal(expectedNodeIds, run.StageOrder);
    }

    public static void AppearsBefore(ScenarioRun run, string firstNodeId, string secondNodeId)
    {
        var stageOrder = run.StageOrder.ToList();
        var firstIndex = stageOrder.IndexOf(firstNodeId);
        var secondIndex = stageOrder.IndexOf(secondNodeId);

        Assert.True(firstIndex >= 0, $"Node '{firstNodeId}' was not executed.");
        Assert.True(secondIndex >= 0, $"Node '{secondNodeId}' was not executed.");
        Assert.True(firstIndex < secondIndex, $"Expected '{firstNodeId}' before '{secondNodeId}', got: {string.Join(", ", stageOrder)}");
    }

    public static void ContextContains(ScenarioRun run, string key, string expectedValue)
    {
        Assert.True(run.Result.FinalContext.Has(key), $"Expected final context to contain '{key}'.");
        Assert.Equal(expectedValue, run.Result.FinalContext.Get(key));
    }

    public static void NodeStatus(ScenarioRun run, string nodeId, OutcomeStatus expectedStatus)
    {
        Assert.True(run.Result.NodeOutcomes.ContainsKey(nodeId), $"Expected node outcome for '{nodeId}'.");
        Assert.Equal(expectedStatus, run.Result.NodeOutcomes[nodeId].Status);
    }
}
