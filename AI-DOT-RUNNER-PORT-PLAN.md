# AI-DOT-RUNNER Port Plan

## Goal

Port the highest-value runtime, authoring, and operational features from `ai-dot-runner` into `soulcaster` without losing the current C# architecture:

- `JcAttractor.Attractor` remains the DOT runtime and orchestration layer.
- `JcAttractor.CodingAgent` remains the agent loop and tool executor.
- `runner` remains the operator-facing CLI and dashboard surface.

As of 2026-03-31, this is no longer a greenfield port plan. Most of the original parity work has already landed. This document now tracks:

1. what is already shipped,
2. what is still missing for meaningful parity,
3. how to validate that shipped behavior stays green.

## Current Snapshot

| Area | Status | Notes |
| --- | --- | --- |
| Phase 1: structured stage contract | Shipped | `StageStatusContract`, contract retries, routing validation, canonical `status.json` |
| Phase 2: fidelity and thread reuse | Shipped | session pooling, runtime thread/fidelity resolution, resume downgrade to `summary:high` |
| Phase 3: resume/start-at/steering | Shipped | `--resume`, `--resume-from`, `--start-at`, `--steer-text`, run manifest, run lock |
| Phase 4: graph + telemetry observability | Shipped | dashboard graph SVG, active node highlighting, telemetry API, `events.jsonl` |
| Phase 5: DOT linting | Mostly shipped | lint CLI and severity model exist; autofix is still missing |
| Phase 6: authoring workflows | Partial | CLI builder exists; browser editor is still missing |
| Phase 7: queue-based parallelism | Mostly shipped | directory/manifest queues work; fan-in heuristics can still improve |
| Phase 8: telemetry-driven supervision | Mostly shipped | telemetry-aware manager loop exists; explicit observe/guard/steer split is still open |
| Phase 9: scenario harness and regression coverage | Shipped | deterministic backend, scenario runner, parity regression suites exist |

## Shipped Parity

### 1. Stage contract and routing discipline

Shipped behavior:

- Codergen stages emit a validated structured contract via `StageStatusContract`.
- `CodergenHandler` retries when the contract is missing or malformed and persists canonical `status.json`.
- invalid `preferred_next_label` values are caught before routing falls back.
- runtime artifacts include prompt, response, raw assistant response, reminder artifacts, and structured status.

Primary implementation points:

- `src/JcAttractor.Attractor/Execution/StageStatusContract.cs`
- `src/JcAttractor.Attractor/Handlers/CodergenHandler.cs`
- `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- `tests/JcAttractor.Tests/Phase1RegressionTests.cs`

### 2. Fidelity, threads, and session reuse

Shipped behavior:

- runtime resolves fidelity and thread from graph, node, and edge settings.
- pooled sessions are reused only for `full` fidelity.
- non-full modes use carryover summaries instead of full reuse.
- the first resumed node is downgraded from `full` to `summary:high`.

Primary implementation points:

- `src/JcAttractor.CodingAgent/Session/SessionPool.cs`
- `runner/Program.cs` (`AgentCodergenBackend`)
- `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- `tests/JcAttractor.Tests/ScenarioHarnessTests.cs`

### 3. Explicit operator controls

Shipped behavior:

- CLI supports `--resume`, `--autoresume`, `--no-autoresume`, `--resume-from`, `--start-at`, and `--steer-text`.
- runs materialize `run-manifest.json` and `run.lock`.
- start-at overrides can be applied to checkpointed runs.
- steering text is injected into the first coding session turn only.

Primary implementation points:

- `runner/RunCommandSupport.cs`
- `runner/Program.cs`
- `tests/JcAttractor.Tests/Phase1RegressionTests.cs`

### 4. Dashboard graph and telemetry

Shipped behavior:

- the web dashboard renders graph SVG with current-node highlighting.
- telemetry is aggregated from `events.jsonl`.
- per-node tool counts, token usage, and touched-file summaries are surfaced in the UI.
- telemetry is also consumed by the supervisor flow.

Primary implementation points:

- `runner/Program.cs` (`RunWeb`, `/api/pipeline/{id}/graph`, `/api/pipeline/{id}/telemetry`)
- `src/JcAttractor.Attractor/Execution/PipelineEngine.cs`
- `src/JcAttractor.Attractor/Handlers/SupervisorTelemetry.cs`

### 5. Lint, builder, queue parallelism, supervisor, and scenario harness

Shipped behavior:

- `attractor lint` validates DOT files and returns non-zero on errors.
- `attractor builder` supports init, graph, node, edge, and inspect workflows.
- queue-backed parallel nodes support directory and manifest enumeration, scoped worker threads, and per-item results.
- `ManagerLoopHandler` supports telemetry-driven stalling, steering cooldowns, and escalation thresholds.
- deterministic test harnesses cover phase-parity scenarios.

Primary implementation points:

- `src/JcAttractor.Attractor/Validation/LintRule.cs`
- `runner/BuilderCommandSupport.cs`
- `src/JcAttractor.Attractor/Handlers/ParallelQueueLoader.cs`
- `src/JcAttractor.Attractor/Handlers/ManagerLoopHandler.cs`
- `tests/JcAttractor.Tests/Phase567PortPlanTests.cs`
- `tests/JcAttractor.Tests/Helpers/ScenarioRunner.cs`

### 6. Adjacent improvements beyond the original port list

Already landed and worth preserving:

- provider discovery and health checks via `attractor providers ping`
- provider model sync scaffolding via `attractor providers sync-models`
- live provider validation pipelines for newly discovered models

These are not part of the original `ai-dot-runner` parity plan, but they materially improve operator workflows in `soulcaster`.

## Remaining Gaps

The remaining work is now concentrated in four areas:

### Gap A: Browser-based DOT editor

Still missing:

- a dedicated browser editor that can open, edit, lint, render, and save a DOT file in one flow
- round-trip authoring without dropping to the CLI builder

Target outcome:

- `attractor editor <dotfile>` opens a local UI that reuses the current renderer and linter.

### Gap B: Lint autofix and deeper authoring guidance

Still missing:

- deterministic autofix for safe cases
- richer maintainability/style guidance beyond current validation coverage

Target outcome:

- `attractor lint --fix` can normalize obvious shape/attribute issues without changing semantics.

### Gap C: Fan-in candidate selection

Still missing:

- richer fan-in heuristics for choosing the best queue result
- optional candidate scoring and explicit candidate-set exposure in context

Target outcome:

- queue pipelines can choose, rank, or pass through multiple candidates in a controlled way.

### Gap D: Supervisor decomposition

Still missing:

- a clearer separation of observe, guard, steer, and escalate roles
- more explicit dashboard surfacing of supervisor decisions

Target outcome:

- supervisor behavior is easier to reason about, test, and tune than the current single-handler design.

## Suggested Delivery Order

### Milestone 1: Authoring parity

- browser editor
- lint autofix
- save/render/lint round-trip tests

### Milestone 2: Orchestration depth

- fan-in candidate selection
- explicit supervisor role decomposition
- dashboard surfacing of supervisor state

### Milestone 3: QA hardening

- representative real-run fixtures for the remaining gaps
- documentation refresh for authoring and supervision features

## QA Plan

### Automated QA

1. `dotnet build`
2. `dotnet test tests/JcAttractor.Tests/JcAttractor.Tests.csproj`
3. `dotnet run --project runner -- lint dotfiles/qa-smoke.dot`
4. `dotnet run --project runner -- builder inspect dotfiles/qa-smoke.dot`

### Runtime QA

1. Resume and steering flow:
   - `dotnet run --project runner -- dotfiles/qa-checkpoint.dot --resume-from dotfiles/output/qa-checkpoint --resume --start-at step_d --steer-text "Focus on tests"`
2. Dashboard validation:
   - `dotnet run --project runner -- web --dir dotfiles/output/qa-checkpoint --port 5099`
   - verify graph and telemetry views load and highlight the active node correctly
3. Provider discovery validation:
   - `dotnet run --project runner -- providers ping --json`

### Artifact Checks

1. Each executed codergen node writes `status.json` with the structured contract fields.
2. `run-manifest.json` contains a non-empty run ID, PID, graph path, and status.
3. `run.lock` is removed after a clean run.
4. Queue runs emit per-item results and worker stage artifacts.
5. Supervisor runs emit telemetry-derived state and steering/escalation counts.

## Definition Of Done

The port effort should now be considered done when all of the following are true:

1. The already-shipped parity areas remain green:
   - stage contract and routing
   - fidelity/thread/session reuse
   - resume/start-at/steering controls
   - run manifest and run lock
   - graph and telemetry dashboard
   - lint CLI
   - builder CLI
   - queue-based parallel execution
   - telemetry-driven manager loop
   - deterministic scenario coverage
2. The remaining gaps are closed:
   - browser DOT editor exists and round-trips cleanly
   - lint autofix exists for deterministic cases
   - fan-in candidate selection is implemented and tested
   - supervisor role decomposition is implemented and surfaced
3. `dotnet build` and `dotnet test tests/JcAttractor.Tests/JcAttractor.Tests.csproj` pass.
4. Representative runtime evidence exists for resume/steering, dashboard graph rendering, and provider discovery.
5. This document still matches the branch reality instead of describing already-completed work as future work.
