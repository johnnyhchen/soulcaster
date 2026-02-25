# Soulcaster Spec Completion Plan

Bring the implementation from ~75% to 100% spec compliance across all three specs.

Current state: **221 tests passing, build clean (warnings only)**.

---

## Phase 1 — Quick Wins & Bug Fixes

Low-risk, small changes that can be done independently. Each is a single file or a few lines.

### 1.1 Add Codex 5.3 to Model Catalog
- **File**: `src/JcAttractor.UnifiedLlm/Models/ModelCatalog.cs`
- Add `codex-5.3` entry with alias `gpt-5.3-codex`
- Look up current pricing/context window from OpenAI docs
- Also update `README.md` Available Models table

### 1.2 Fix StylesheetTransform reasoning_effort Bug
- **File**: `src/JcAttractor.Attractor/Transforms/StylesheetTransform.cs`
- Line checks `if (node.ReasoningEffort == "high")` — should be `if (string.IsNullOrEmpty(node.ReasoningEffort))`
- Matches the pattern used for `model` and `provider` on the same page

### 1.3 Fix NullCodergenBackend Outcome
- **File**: `src/JcAttractor.Attractor/Handlers/CodergenHandler.cs` (or wherever `NullCodergenBackend` lives)
- Spec says simulation mode returns `OutcomeStatus.Success` with `"[Simulated] Response for stage: {node.id}"`
- Current impl returns `Fail`

### 1.4 Fix ToolHandler Attribute Name
- **File**: `src/JcAttractor.Attractor/Handlers/ToolHandler.cs`
- Add `tool_command` as the primary attribute lookup (spec §4.10), fall back to `command` and `tool` for backwards compat

### 1.5 Fix Session Abort State
- **File**: `src/JcAttractor.CodingAgent/Session/Session.cs`
- On `OperationCanceledException`, transition to `SessionState.Closed` instead of `Idle`

### 1.6 Add Checkpoint Timestamp and Logs
- **File**: `src/JcAttractor.Attractor/Execution/Checkpoint.cs`
- Add `DateTime Timestamp` and `List<string> Logs` fields to the `Checkpoint` record
- Set `Timestamp = DateTime.UtcNow` on save

---

## Phase 2 — Tool & Parameter Completeness

Fill in missing tool parameters and tools. Medium complexity, mostly additive.

### 2.1 grep Tool: Add Missing Parameters
- **Files**: All three profiles + `ToolExecutionEnvironment` (or wherever grep is implemented)
- Add `glob_filter` (string), `case_insensitive` (bool), `max_results` (int) parameters
- Wire them through to the actual grep invocation

### 2.2 glob Tool: Add `path` Parameter
- **Files**: All three profiles + glob implementation
- Add optional `path` parameter for search directory

### 2.3 Gemini Profile: Add `list_dir` Tool
- **File**: `src/JcAttractor.CodingAgent/Profiles/GeminiProfile.cs`
- Register `list_dir` tool (path → directory listing)
- Implement handler in tool execution environment

### 2.4 Gemini Profile: Add `read_many_files` Tool
- **File**: `src/JcAttractor.CodingAgent/Profiles/GeminiProfile.cs`
- Register `read_many_files` tool (paths array → concatenated file contents)
- Implement handler in tool execution environment

### 2.5 ToolHandler: Add Timeout and Env-Var Filtering
- **File**: `src/JcAttractor.Attractor/Handlers/ToolHandler.cs`
- Read `timeout` attribute from node, enforce via `Process.WaitForExit(timeout)`
- Strip env vars matching `*_API_KEY`, `*_SECRET`, `*_TOKEN`, `*_PASSWORD` from spawned process environment

---

## Phase 3 — Unified LLM Completeness

### 3.1 OpenAI Reasoning Effort Pass-Through
- **File**: `src/JcAttractor.UnifiedLlm/Providers/OpenAiAdapter.cs`
- Map `Request.ReasoningEffort` to `reasoning.effort` in the Responses API request body

### 3.2 Streaming Middleware
- **File**: `src/JcAttractor.UnifiedLlm/Client.cs`
- Apply middleware chain to `StreamAsync` the same way `CompleteAsync` does
- Middleware needs to handle the async enumerable pattern (wrap the stream)

### 3.3 Response.RateLimitInfo and Warnings
- **File**: `src/JcAttractor.UnifiedLlm/Models/Response.cs`
- Add `RateLimitInfo` record (requests remaining, tokens remaining, reset timestamps)
- Add `List<string> Warnings` field
- Parse from response headers (Anthropic/OpenAI) and response body (Gemini)

### 3.4 Anthropic Prompt Caching: Conversation History Breakpoints
- **File**: `src/JcAttractor.UnifiedLlm/Providers/AnthropicAdapter.cs`
- Add cache breakpoint to the last tool-result message in the conversation history (not just system blocks)
- Target the boundary between "repeated prefix" and "new turn"

### 3.5 Provider Options Escape Hatch for OpenAI and Gemini
- **Files**: `OpenAiAdapter.cs`, `GeminiAdapter.cs`
- Read provider-specific keys from `Request.ProviderOptions` and apply them

---

## Phase 4 — Parallel Execution Overhaul

The parallel handler is the most complex gap. Tackle it as a focused unit.

### 4.1 Parallel Tool Call Execution in Agent Loop
- **File**: `src/JcAttractor.CodingAgent/Session/Session.cs`
- When `profile.SupportsParallelToolCalls && toolCalls.Count > 1`, use `Task.WhenAll` instead of sequential foreach
- Collect all results and append as a single tool-results turn

### 4.2 Parallel Handler: Join Policy, Error Policy, Max Parallel
- **File**: `src/JcAttractor.Attractor/Handlers/ParallelHandler.cs`
- Read `join_policy` attribute: `wait_all` (default), `k_of_n`, `first_success`, `quorum`
- Read `error_policy` attribute: `fail_fast`, `continue` (default), `ignore`
- Read `max_parallel` attribute: concurrency cap via `SemaphoreSlim`
- Implement each join strategy

### 4.3 Parallel Handler: Isolated Branch Context
- **File**: `src/JcAttractor.Attractor/Handlers/ParallelHandler.cs`
- Clone context for each branch (don't pass the shared parent)
- Do NOT merge branch context updates back into parent
- Instead, store all branch outcomes in `context["parallel.results"]` as structured data

### 4.4 Parallel Handler: Multi-Hop Subgraph Execution
- **File**: `src/JcAttractor.Attractor/Handlers/ParallelHandler.cs` + `PipelineEngine.cs`
- Extract `ExecuteSubgraph(startNode, endNode, context)` from the engine
- Each branch runs a full sub-traversal from its entry node until it reaches a fan-in node or dead end

### 4.5 Fan-In Handler: Full Implementation
- **File**: `src/JcAttractor.Attractor/Handlers/FanInHandler.cs`
- Read `context["parallel.results"]`
- If node has a `prompt` attribute, run LLM-based evaluation using the codergen backend
- Otherwise, run heuristic ranking: sort branches by `(outcome_rank, score, id)`
- Store merged result in context

---

## Phase 5 — Human Gate & Codergen Enhancements

### 5.1 WaitHuman: Timeout, Default Choice, Skip
- **Files**: `WaitHumanHandler.cs`, `IInterviewer.cs`, `InterviewAnswer.cs`
- Add `Timeout` and `Skipped` states to `InterviewAnswer`
- On timeout: check `node.RawAttributes["human.default_choice"]`, use it or return `Retry`
- On skip: return `Fail`
- Add structured `Choice` record with parsed accelerator keys

### 5.2 Codergen: preferred_label and suggested_next_ids
- **Files**: `CodergenHandler.cs`, `ICodergenBackend.cs`, `CodergenResult.cs`
- Add `PreferredLabel` and `SuggestedNextIds` fields to `CodergenResult`
- Backend implementations parse these from LLM response when present
- Handler forwards them to the outcome for edge routing

---

## Phase 6 — Subagent System

Most complex feature. Builds on the coding agent loop.

### 6.1 Subagent Tools Registration
- **Files**: All three profiles
- Register four tools: `spawn_agent`, `send_input`, `wait_agent`, `close_agent`
- Each tool creates/manages `SubAgent` instances on the parent `Session`

### 6.2 Subagent Lifecycle Implementation
- **File**: `src/JcAttractor.CodingAgent/Session/SubAgent.cs`
- `spawn_agent(prompt, model?)` → create child `Session` with its own history, tools, and working directory
- `send_input(agent_id, message)` → call `childSession.ProcessInputAsync(message)`
- `wait_agent(agent_id)` → await child session completion, return final output
- `close_agent(agent_id)` → cancel child session, clean up

### 6.3 Depth Limiting and Context Isolation
- Enforce max depth (default 3) — refuse to spawn if `Depth >= MaxDepth`
- Child sessions get an isolated working directory (or same dir, configurable)
- Parent receives child's final text output as the tool result

---

## Phase 7 — Pipeline Engine Completeness

### 7.1 stack.manager_loop Handler
- **New file**: `src/JcAttractor.Attractor/Handlers/ManagerLoopHandler.cs`
- Register shape `"house"` in `HandlerRegistry`
- Implement: launch child pipeline, poll for telemetry, apply steering, check stop conditions, enforce max cycles
- Attributes: `max_cycles`, `stop_condition`, `steer_cooldown`, `child_dotfile`

### 7.2 INITIALIZE Phase Formalization
- **File**: `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- Mirror all graph attributes into context during initialization
- Explicitly create the run directory structure

### 7.3 FINALIZE Phase
- **File**: `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- Emit completion event after exit node
- Write final checkpoint with completion timestamp
- Log total duration, nodes executed, final outcome

### 7.4 Checkpoint Fidelity Degradation on Resume
- **File**: `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- On resume, if previous node used `full` fidelity, degrade first resumed node to `summary:high`

### 7.5 Additional Lint Rules
- **File**: `src/JcAttractor.Attractor/Validation/Validator.cs`
- Warn: non-terminal node with no outgoing edges
- Warn: parallel node with no downstream fan-in
- Warn: goal_gate node with no retry_target

---

## Phase 8 — System Prompts & Project Docs

### 8.1 Project Docs Discovery
- **File**: `src/JcAttractor.CodingAgent/Session/Session.cs` (or new `ProjectDocs.cs`)
- Implement `DiscoverProjectDocs(workingDirectory)` — scan for CLAUDE.md, GEMINI.md, AGENTS.md, etc.
- Pass discovered docs to `ProviderProfile.BuildSystemPrompt(env, projectDocs)`

### 8.2 Provider-Aligned System Prompts
- **Files**: `AnthropicProfile.cs`, `OpenAiProfile.cs`, `GeminiProfile.cs`
- Align system prompts more closely with reference agents (Claude Code, codex-rs, gemini-cli)
- This is best-effort — exact 1:1 copies would be too large and change frequently

### 8.3 ProviderOptions on Requests
- **File**: `src/JcAttractor.CodingAgent/Session/Session.cs`
- Call `ProviderProfile.ProviderOptions()` and attach to every `Request` sent to the LLM

---

## Dependency Graph

```
Phase 1 (quick fixes) ─── no dependencies, do first
Phase 2 (tool params) ─── no dependencies, can parallel with Phase 1
Phase 3 (LLM)        ─── no dependencies, can parallel with Phase 1-2
Phase 4 (parallel)    ─── Phase 1.2 (stylesheet fix helps testing)
Phase 5 (human/coder) ─── no hard deps
Phase 6 (subagents)   ─── Phase 2 (tool infrastructure)
Phase 7 (engine)      ─── Phase 4 (parallel handler), Phase 5 (codergen)
Phase 8 (prompts)     ─── no hard deps, do last (lowest priority)
```

## Testing Strategy

Each phase must:
1. Add unit tests for new behavior
2. Not break existing 221 tests
3. Build with 0 errors

Tests to add per phase:
- **Phase 1**: Test stylesheet transform with empty reasoning_effort, test null backend returns Success
- **Phase 2**: Test grep with glob_filter, test glob with path, test list_dir and read_many_files
- **Phase 3**: Test OpenAI reasoning effort serialization, test streaming middleware application
- **Phase 4**: Test each join policy, test context isolation, test fan-in evaluation
- **Phase 5**: Test timeout/skip in human gate, test preferred_label routing
- **Phase 6**: Test spawn/send/wait/close lifecycle, test depth limiting
- **Phase 7**: Test manager loop cycle counting, test lint rule warnings
- **Phase 8**: Test project docs discovery, test provider options attachment
