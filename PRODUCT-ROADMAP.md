# Soulcaster Product Roadmap

## Goal

Turn Soulcaster from a strong single-user prototype into a durable product surface for authoring, operating, and supervising long-running AI workflows.

This roadmap covers five implementation tracks:

1. Real asynchronous subagents
2. Model evaluation and budget-aware routing
3. Durable run store and multi-user control plane
4. Authoring UX upgrades
5. Platform hardening for remaining runtime gaps

## Product Outcomes

- Make agent orchestration genuinely parallel instead of pseudo-parallel.
- Improve cost, latency, and quality tradeoffs with measurable model routing.
- Move run state out of local files as the only control surface.
- Reduce the friction of creating and editing DOT workflows.
- Close runtime gaps that would otherwise make later product work brittle.

## Roadmap Principles

- Ship enabling runtime work before new product surfaces.
- Preserve the current file-artifact workflow while introducing durable storage.
- Add automated coverage and real runnable validation assets for each milestone.
- Keep CLI and local-first workflows working even as the control plane grows.

## Recommended Sequence

1. Platform hardening for remaining runtime gaps
2. Real asynchronous subagents
3. Model evaluation and budget-aware routing
4. Durable run store and multi-user control plane
5. Authoring UX upgrades

This order keeps the runtime honest first, unlocks stronger orchestration second, and only then layers on product surfaces that depend on stable state and telemetry.

## Phase 0: Platform Hardening

### Objective

Close the remaining runtime gaps that materially affect orchestration, streaming, and routing correctness.

### Scope

- Finish `ParallelHandler` join semantics, especially `k_of_n`, and make policy handling explicit.
- Upgrade `UnifiedLlm.Client.StreamAsync` so middleware can wrap stream lifecycles instead of only transforming requests.
- Review remaining spec-plan drift and update stale planning docs to match shipped behavior.
- Add targeted tests for stream middleware, join policies, and regression cases around partial and failed branches.

### Primary Files

- `src/JcAttractor.Attractor/Handlers/ParallelHandler.cs`
- `src/JcAttractor.Attractor/Handlers/FanInHandler.cs`
- `src/JcAttractor.UnifiedLlm/Client.cs`
- `tests/JcAttractor.Tests/AttractorTests.cs`
- `tests/JcAttractor.Tests/UnifiedLlmTests.cs`
- `SPEC-COMPLETION-PLAN.md`

### Deliverables

- A complete and documented policy matrix for parallel execution.
- Streaming middleware behavior that is symmetrical with non-streaming middleware.
- Updated roadmap and parity docs that reflect the real repo state.

### Exit Criteria

- `k_of_n` is implemented and covered by automated tests.
- Stream middleware tests prove pre/post behavior around live streams.
- The repo has a single accurate source of truth for remaining runtime gaps.

## Phase 1: Real Asynchronous Subagents

### Objective

Turn subagents into true background workers that can be spawned, messaged, awaited, and closed independently.

### Scope

- Decouple `spawn_agent` from immediate completion so it returns a handle, not a synchronous result.
- Make `wait_agent` actually await in-flight work.
- Support re-entrant `send_input` semantics with clear state transitions.
- Add lifecycle metadata, result snapshots, and cancellation semantics for child sessions.
- Decide isolation rules for history, working directory, and tool access at the child-session boundary.

### Primary Files

- `src/JcAttractor.CodingAgent/Session/Session.cs`
- `src/JcAttractor.CodingAgent/Session/SubAgent.cs`
- `src/JcAttractor.CodingAgent/Profiles/SubagentTools.cs`
- `src/JcAttractor.CodingAgent/Session/SessionState.cs`
- `tests/JcAttractor.Tests/CodingAgentTests.cs`

### Deliverables

- A proper subagent lifecycle model: `spawned`, `running`, `completed`, `failed`, `closed`.
- Tool behavior that matches the intended mental model of parallel delegation.
- Telemetry for subagent starts, completions, failures, and cancellation.

### Dependencies

- Phase 0 parallel and streaming hardening.

### Exit Criteria

- A parent session can launch multiple child sessions without blocking on spawn.
- `wait_agent` returns the actual final result of background work.
- Tests cover concurrent child work, cancellation, depth limits, and repeated messaging.

## Phase 2: Model Evaluation and Budget-Aware Routing

### Objective

Make provider and model selection measurable, configurable, and cost-aware rather than static.

### Scope

- Add an evaluation harness that can run the same stage or workflow against multiple providers and models.
- Record cost, token usage, latency, success rate, and selected-path quality metrics.
- Add routing policy inputs such as budget ceiling, latency target, quality floor, and fallback order.
- Use existing telemetry and stage results to build a model scorecard for real workloads.
- Support per-node or per-class routing policies in DOT and runner configuration.

### Primary Files

- `src/JcAttractor.UnifiedLlm/Client.cs`
- `src/JcAttractor.UnifiedLlm/ModelCatalog.cs`
- `src/JcAttractor.UnifiedLlm/Models/Response.cs`
- `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- `runner/Program.cs`
- `runner/RunnerRuntimeObserver.cs`
- new `runner/ModelEval*.cs` support files
- `tests/JcAttractor.Tests/UnifiedLlmTests.cs`

### Deliverables

- A model-eval command or workflow that produces comparable reports.
- A routing policy layer that can choose cheaper or faster models when acceptable.
- Stored telemetry that supports model scorecards over time.

### Dependencies

- Phase 0 stream middleware work.
- Phase 1 subagent lifecycle work if model evaluations are parallelized through helpers.

### Exit Criteria

- The same workflow can be evaluated across multiple models with a single command.
- Routing policies can enforce a cost or latency budget.
- Reports show per-model quality, latency, and token/cost summaries for real runs.

## Phase 3: Durable Run Store and Multi-User Control Plane

### Objective

Move Soulcaster from file-backed local operation to a durable run service that supports shared visibility and control.

### Scope

- Introduce a durable store for runs, checkpoints, gates, stage events, telemetry, and answers.
- Preserve artifact files, but treat them as exports or mirrors rather than the only source of truth.
- Add a control-plane API for listing runs, viewing status, answering gates, steering sessions, and inspecting telemetry.
- Add multi-user concepts: actor identity, audit trail, optimistic locking, and role-based mutation paths.
- Support migration from pure file-backed runs into the persistent store.

### Primary Files

- `runner/Program.cs`
- `runner/RunManifest.cs`
- `src/JcAttractor.Attractor/Execution/Checkpoint.cs`
- `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- `src/JcAttractor.Attractor/HumanInTheLoop/FileInterviewer.cs`
- new `runner/Storage/*.cs` or equivalent
- new API and persistence tests

### Deliverables

- A persistence model for runs, stages, events, gates, and answers.
- A versioned HTTP API for run operations.
- A migration path for current local output directories.

### Dependencies

- Phase 1 subagent telemetry.
- Phase 2 routing and cost telemetry if those metrics are to appear in the control plane.

### Exit Criteria

- A run can be resumed, inspected, and answered through a durable store after process restart.
- Multiple users can view and act on the same run with auditability.
- Local file artifacts remain available, but the system no longer depends on them as the only operational state.

## Phase 4: Authoring UX Upgrades

### Objective

Reduce the effort required to create, edit, lint, and visualize workflows.

### Scope

- Replace the current line-oriented authoring experience with richer editing surfaces.
- Build a VS Code extension or web editor with node/edge editing, lint feedback, graph preview, and schema-aware autocomplete.
- Expose routing, models, retry policies, parallel policies, and gate behavior as discoverable editing controls.
- Add workflow templates for common patterns such as plan-breakdown-implement-validate-critique, queue-backed fan-out, and supervisor-worker flows.
- Integrate run-preview and validation feedback directly into the authoring surface.

### Primary Files

- `runner/InteractiveEditorCommand.cs`
- `runner/BuilderCommandSupport.cs`
- `runner/Program.cs`
- `README.md`
- new editor-specific project files

### Deliverables

- A richer authoring surface than the current CLI editor and builder commands.
- Template-based workflow creation.
- Inline linting and graph visualization during authoring.

### Dependencies

- Phase 3 durable run APIs if the editor is web-backed.
- Phase 0 runtime hardening so the editor reflects stable semantics.

### Exit Criteria

- A new workflow can be created, validated, and previewed without hand-editing DOT.
- Common runtime policies are exposed through structured UI controls.
- The editor can run a workflow preview or validation pass against the current draft.

## Cross-Cutting Work

### Validation

- Keep `dotnet test` green for every phase.
- Add real validation assets or committed demo workflows for each major feature.
- Expand regression coverage around resumability, telemetry, and policy interactions.

### Documentation

- Update README and planning docs at the end of each phase.
- Add examples that show the intended user workflow, not only the internal implementation shape.

### Migration and Compatibility

- Preserve current CLI workflows while adding persistent or interactive surfaces.
- Add compatibility shims where existing DOT attributes or runner flags are already in use.

## Success Metrics

- Subagents reduce wall-clock time for multi-branch tasks without increasing failure rates.
- Routing policies measurably reduce cost or latency on representative workflows.
- Runs survive process restarts without losing operational state.
- New users can author a valid workflow faster than they can by hand-editing DOT today.
- Remaining runtime edge cases stop being the main blocker for product work.

## Risks

- Subagent concurrency may expose session-state assumptions that are currently hidden by synchronous behavior.
- Durable storage can accidentally fork the current artifact model if file and DB state drift.
- Authoring UX can become expensive before the workflow schema fully stabilizes.
- Budget-aware routing can create confusing behavior if evaluation criteria are not explicit and inspectable.

## Suggested Milestone Review Order

1. Runtime hardening review
2. Subagent orchestration review
3. Model-eval and routing review
4. Control-plane persistence review
5. Authoring UX review

At each review, the question should be the same: did this phase materially improve Soulcaster as a product surface, not just as an internal runtime?
