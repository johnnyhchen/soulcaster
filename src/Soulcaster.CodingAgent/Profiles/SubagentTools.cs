using System.Text.Json;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.CodingAgent.Profiles;

/// <summary>
/// Shared subagent tool definitions registered on all profiles.
/// </summary>
public static class SubagentTools
{
    public static void Register(ToolRegistry registry)
    {
        registry.Register(new RegisteredTool(
            "spawn_agent",
            new ToolDefinition(
                "spawn_agent",
                "Spawns a child agent with its own session and returns immediately with the agent ID.",
                new List<ToolParameter>
                {
                    new("prompt", "string", "The initial prompt/task for the child agent", true),
                    new("model", "string", "Optional model override for the child agent", false)
                }),
            async (args, env) =>
            {
                // This tool requires session context — it's injected via a wrapper at execution time
                return "Error: spawn_agent requires session context. This tool must be executed through a session.";
            }));

        registry.Register(new RegisteredTool(
            "send_input",
            new ToolDefinition(
                "send_input",
                "Queues a message for an existing child agent and returns immediately.",
                new List<ToolParameter>
                {
                    new("agent_id", "string", "The ID of the child agent", true),
                    new("message", "string", "The message to send to the agent", true)
                }),
            async (args, env) =>
            {
                return "Error: send_input requires session context. This tool must be executed through a session.";
            }));

        registry.Register(new RegisteredTool(
            "wait_agent",
            new ToolDefinition(
                "wait_agent",
                "Waits for a child agent to complete its current work and returns its final output.",
                new List<ToolParameter>
                {
                    new("agent_id", "string", "The ID of the child agent to wait for", true)
                }),
            async (args, env) =>
            {
                return "Error: wait_agent requires session context. This tool must be executed through a session.";
            }));

        registry.Register(new RegisteredTool(
            "close_agent",
            new ToolDefinition(
                "close_agent",
                "Closes a child agent, cancelling any running work and freeing resources.",
                new List<ToolParameter>
                {
                    new("agent_id", "string", "The ID of the child agent to close", true)
                }),
            async (args, env) =>
            {
                return "Error: close_agent requires session context. This tool must be executed through a session.";
            }));
    }

    /// <summary>
    /// Creates session-aware tool implementations that can actually manage subagents.
    /// Call this after session creation to override the placeholder implementations.
    /// </summary>
    public static void BindToSession(Session session)
    {
        session.ProviderProfile.ToolRegistry.Register(new RegisteredTool(
            "spawn_agent",
            new ToolDefinition(
                "spawn_agent",
                "Spawns a child agent with its own session and returns immediately with the agent ID.",
                new List<ToolParameter>
                {
                    new("prompt", "string", "The initial prompt/task for the child agent", true),
                    new("model", "string", "Optional model override for the child agent", false)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var prompt = json.RootElement.GetProperty("prompt").GetString()!;
                string? model = json.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;

                try
                {
                    var subagent = session.SpawnSubagent(model);
                    await subagent.EnqueueInputAsync(prompt);
                    return $"Agent {subagent.Id} spawned.\nState: {subagent.State.ToString().ToLowerInvariant()}\nPending inputs: {subagent.PendingInputCount}";
                }
                catch (InvalidOperationException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }));

        session.ProviderProfile.ToolRegistry.Register(new RegisteredTool(
            "send_input",
            new ToolDefinition(
                "send_input",
                "Queues a message for an existing child agent and returns immediately.",
                new List<ToolParameter>
                {
                    new("agent_id", "string", "The ID of the child agent", true),
                    new("message", "string", "The message to send to the agent", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var agentId = json.RootElement.GetProperty("agent_id").GetString()!;
                var message = json.RootElement.GetProperty("message").GetString()!;

                var subagent = session.GetSubagent(agentId);
                if (subagent is null)
                    return $"Error: Agent '{agentId}' not found.";

                await subagent.EnqueueInputAsync(message);
                return $"Agent {agentId} accepted input.\nState: {subagent.State.ToString().ToLowerInvariant()}\nPending inputs: {subagent.PendingInputCount}";
            }));

        session.ProviderProfile.ToolRegistry.Register(new RegisteredTool(
            "wait_agent",
            new ToolDefinition(
                "wait_agent",
                "Waits for a child agent to complete its current work and returns its final output.",
                new List<ToolParameter>
                {
                    new("agent_id", "string", "The ID of the child agent to wait for", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var agentId = json.RootElement.GetProperty("agent_id").GetString()!;

                var subagent = session.GetSubagent(agentId);
                if (subagent is null)
                    return $"Error: Agent '{agentId}' not found.";

                return await subagent.WaitForCompletionAsync();
            }));

        session.ProviderProfile.ToolRegistry.Register(new RegisteredTool(
            "close_agent",
            new ToolDefinition(
                "close_agent",
                "Closes a child agent, cancelling any running work and freeing resources.",
                new List<ToolParameter>
                {
                    new("agent_id", "string", "The ID of the child agent to close", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var agentId = json.RootElement.GetProperty("agent_id").GetString()!;

                session.CloseSubagent(agentId);
                return $"Agent '{agentId}' closed.";
            }));
    }
}
