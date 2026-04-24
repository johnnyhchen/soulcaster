# Soulcaster

A C# implementation of the [strongDM Attractor](https://github.com/strongdm/attractor) spec for long-running, multi-stage AI workflows.

Soulcaster is now more than a file-only DOT runner. The current runtime supports durable run state, policy-constrained control-plane mutations, versioned artifacts, replay, lane-aware multimodal execution, and capability-aware model routing while keeping the local-first DOT workflow intact.

## Architecture

```
src/
├── Soulcaster.Attractor/      # Pipeline engine, DOT parser, handlers, graph model
├── Soulcaster.CodingAgent/    # Agentic coding loop (tool use, multi-turn sessions)
└── Soulcaster.UnifiedLlm/     # Multi-provider LLM client (Anthropic, OpenAI, Gemini)

runner/                         # CLI runner that executes a .dot pipeline
tests/                          # Test suite
dotfiles/                       # Reference pipeline definitions
attractor_specs/                # Original spec documents and run learnings
```

**Three layers:**

1. **Unified LLM** — provider-agnostic LLM client with streaming, tool calling, and a model catalog. Supports Anthropic (Claude), OpenAI (GPT/Codex), and Google (Gemini).
2. **Coding Agent** — agentic loop that gives an LLM tools (file read/write, bash, grep, glob) and runs multi-turn sessions until the task is complete.
3. **Attractor** — pipeline engine that parses DOT files, resolves handlers by node shape, manages state/checkpoints, and traverses the graph.

## Current Runtime Status

- Durable run state under `store/`, including `store/workflow.sqlite`
- Authoritative SQLite lease ownership and direct `operator_mutations` journaling
- Audited operator mutations: `cancel`, `retry-stage`, `resume`, `force-advance`
- Workflow policy enforcement for retry budgets and force-advance allow-lists
- Versioned artifact registry with lineage, promotion, and rollback
- Nested generated artifacts, including multimodal image outputs, projected into JSON and SQLite
- Replayable run history from stored events
- Real asynchronous subagents with non-blocking `spawn_agent`
- Capability-aware model validation, registry refresh, latency-aware routing, and per-model scorecards
- Dedicated `agent`, `leaf`, and `multimodal_leaf` lanes with continuity-preserving multimodal follow-up turns
- Leaf attachment authoring for images, documents, and audio where the selected provider supports them
- OpenAI leaf document ingestion on `gpt-5.4` and audio ingestion on `gpt-audio`, both behind fail-closed capability validation
- OpenAI follow-up image continuity through `previous_response_id`, Gemini replayable media continuity through preserved provider-state plus `thoughtSignature` payloads, and Anthropic document follow-up continuity through persisted raw history
- Anthropic leaf document ingestion on Claude models, with Anthropic audio left as explicit fail-closed no-support
- Saved durable query views over `workflow.sqlite` from both CLI and web: `overview`, `attempts`, `failures`, `gates`, `artifacts`, `operators`, `providers`, `events`, `scorecards`, `hotspots`, `lineage`, `leases`, and `mutations`
- Operational Views in the web dashboard for failures, operators, hotspots, scorecards, leases, and mutations
- `run --dry-run --json`, builder templates, reference-image and reference-document authoring, and preview output for workflow authoring
- Built-in crash-injection and scripted-provider drills for restart and degradation testing

## Remaining Roadmap Items

The core reliability work is done. The remaining roadmap is productization and breadth work on top of the shipped runner:

- Richer operator surfaces: better dashboards, saved filters, and easier drill launch/review flows.
- Broader provider coverage: extend the shipped attachment and continuity discipline to more adapters and modalities beyond the current OpenAI/Gemini/Anthropic set.
- Historical analytics: cross-run trends, retention/pruning policy, and rollups for flaky stages, slow nodes, and expensive models.
- Authoring polish: more guided builder flows, more reference workflows, and better large-graph editing ergonomics.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- API keys for the LLM providers you want to use:
  ```bash
  export ANTHROPIC_API_KEY="sk-ant-..."
  export OPENAI_API_KEY="sk-..."
  # Optional:
  export GEMINI_API_KEY="..."
  ```

## Build

```bash
dotnet build
```

## Test

```bash
dotnet test tests/Soulcaster.Tests/Soulcaster.Tests.csproj --no-restore --filter "FullyQualifiedName!~MultimodalIntegrationTests"
```

That non-live suite is the stable local validation path and currently passes `477/477`. The live-provider `MultimodalIntegrationTests` still depend on external provider behavior.

## Run a Pipeline

```bash
dotnet run --project runner -- run <path-to-dotfile>
```

The shorthand form still works:

```bash
dotnet run --project runner -- dotfiles/reference-run.dot
```

For example, to start the reference pipeline explicitly:

```bash
dotnet run --project runner -- run dotfiles/reference-run.dot
```

Each run writes:

- `logs/` for human-readable stage artifacts
- `store/` for run events, gate answers, and SQLite projections
- `result.json` and `run-manifest.json` for summarized run state

To resume from an existing run directory:

```bash
dotnet run --project runner -- run dotfiles/reference-run.dot --resume-from dotfiles/output/reference-run
```

To preview a workflow without spending provider calls:

```bash
dotnet run --project runner -- run dotfiles/reference-run.dot --dry-run --json
```

For restart drills or QA fault injection:

```bash
dotnet run --project runner -- run dotfiles/qa-autoresume-crash.dot --backend scripted --backend-script crash-plan.json --autoresume-policy always --crash-after-stage crash_point --crash-after-stage-count 2
```

## Preview and Author Workflows

The runner now has first-class preview and builder commands on top of raw DOT editing.

```bash
# Preview lint, node policy, and control policy without execution
dotnet run --project runner -- run dotfiles/reference-run.dot --dry-run --json
dotnet run --project runner -- builder preview dotfiles/reference-run.dot

# Scaffold a workflow template
dotnet run --project runner -- builder template coding-loop my-workflow.dot --goal "Ship the requested change"
dotnet run --project runner -- builder template multimodal-edit-loop visual.dot --reference-image assets/reference.png
dotnet run --project runner -- builder template document-critique-loop review.dot --reference-document docs/brief.md

# Inspect or edit an existing graph
dotnet run --project runner -- builder inspect dotfiles/reference-run.dot
dotnet run --project runner -- builder node review.dot review_document --input-documents docs/brief.md --input-audio recordings/note.mp3
```

`builder preview` now resolves image, document, and audio inputs, distinguishes source files from deferred `logs/...` runtime artifacts, and lint-checks lane compatibility before execution.

## Inspect and Steer a Run

The runner is not just fire-and-forget anymore. You can inspect, answer gates, replay history, mutate run state, and inspect artifact lineage from the CLI.

```bash
# Run status and artifacts
dotnet run --project runner -- status --dir dotfiles/output/reference-run
dotnet run --project runner -- logs --dir dotfiles/output/reference-run
dotnet run --project runner -- replay --dir dotfiles/output/reference-run
dotnet run --project runner -- query list
dotnet run --project runner -- query overview --dir dotfiles/output/reference-run --json
dotnet run --project runner -- query operators --dir dotfiles/output/reference-run --json
dotnet run --project runner -- query hotspots --dir dotfiles/output/reference-run --json
dotnet run --project runner -- query lineage --dir dotfiles/output/reference-run --artifact plan --json
dotnet run --project runner -- query mutations --dir dotfiles/output/reference-run --actor johnny --json

# Human gate inspection and answer
dotnet run --project runner -- gate --dir dotfiles/output/reference-run
dotnet run --project runner -- gate answer <choice> --dir dotfiles/output/reference-run --actor johnny --reason "Looks good" --source cli

# Audited control-plane mutations
dotnet run --project runner -- control cancel --dir dotfiles/output/reference-run --reason "Operator stop" --actor johnny
dotnet run --project runner -- control retry-stage --dir dotfiles/output/reference-run --node validate --reason "Rerun after fix" --actor johnny --expected-version 12
dotnet run --project runner -- control resume --dir dotfiles/output/reference-run --reason "Continue run" --actor johnny --expected-version 13
dotnet run --project runner -- control force-advance --dir dotfiles/output/reference-run --to-node done --reason "Manual override" --actor johnny --expected-version 14

# Artifact lineage and promotion
dotnet run --project runner -- artifact list --dir dotfiles/output/reference-run
dotnet run --project runner -- artifact lineage --dir dotfiles/output/reference-run plan
dotnet run --project runner -- artifact promote --dir dotfiles/output/reference-run plan artifact-version-id --reason "Approved plan" --actor johnny
dotnet run --project runner -- artifact rollback --dir dotfiles/output/reference-run plan --reason "Revert promotion" --actor johnny

# Provider registry and scorecards
dotnet run --project runner -- providers registry refresh --provider openai --json
dotnet run --project runner -- providers registry show --json
dotnet run --project runner -- scorecard --dir dotfiles/output/reference-run --json

# Web dashboard
dotnet run --project runner -- web --dir dotfiles/output/reference-run --port 5051
```

The query surface supports shared filters such as `--node`, `--status`, `--event-type`, `--provider`, `--model`, `--actor`, `--artifact`, `--approval-state`, and `--search`. The web dashboard consumes the same saved query views and renders an Operational Views section over the durable SQLite state.

## Writing a Dotfile

The reference pipeline at [`dotfiles/reference-run.dot`](dotfiles/reference-run.dot) demonstrates the standard 5-phase pattern:

1. **Plan & Interview** — Orient on the codebase, draft a plan, human reviews and clarifies
2. **Break Down** — Decompose into single-commit chunks with QA plans, human reviews
3. **Implement** — Execute the commits, writing a progress log after each one
4. **Validate** — Run the validation agent ([`VALIDATION.md`](VALIDATION.md)), loop back to plan on failure
5. **Critique** — Adversarial review, Pareto-optimal distillation, human decides ship or loop

### Artifact Trail and Resumability

Every phase writes numbered markdown artifacts to `output/logs/`. The runtime also records operational state under `output/store/`, which is what enables replay, audited mutations, and SQLite-backed inspection in addition to the human-readable files.

```
output/logs/
├── orient/
│   ├── ORIENT-1.md          # First orientation / gap analysis
│   └── ORIENT-2.md          # Re-orientation after a loop
├── plan/
│   ├── PLAN-1.md            # Initial plan
│   └── PLAN-2.md            # Replan after validation failure
├── breakdown/
│   ├── BREAKDOWN-1.md       # First commit breakdown
│   └── BREAKDOWN-2.md       # Revised breakdown (excludes completed work)
├── implement/
│   ├── PROGRESS-1.md        # Per-commit status log (matches BREAKDOWN-1)
│   └── PROGRESS-2.md        # Second run (matches BREAKDOWN-2)
├── validate/
│   ├── VALIDATION-RUN-1.md  # First QA pass
│   └── VALIDATION-RUN-2.md  # Re-validation (includes regression tracking)
└── critique/
    ├── CRITIQUE-HARSH-1.md  # Adversarial review
    ├── CRITIQUE-PARETO-1.md # Filtered actionable items
    ├── CRITIQUE-HARSH-2.md  # Second critique loop
    └── CRITIQUE-PARETO-2.md
```

**How each phase uses prior artifacts:**

| Phase | Reads | Writes | Resumability behavior |
|-------|-------|--------|----------------------|
| **Orient** | Prior ORIENT, PROGRESS, VALIDATION-RUN | `ORIENT-{N}.md` | Notes what changed since last orientation. Skips work already done per PROGRESS logs. |
| **Plan** | Latest ORIENT, prior PLAN, PROGRESS, VALIDATION-RUN, CRITIQUE-PARETO | `PLAN-{N}.md` | Skips DONE items from progress. Incorporates MUST FIX from critique. Focuses on validation failures. |
| **Breakdown** | Latest PLAN, PROGRESS | `BREAKDOWN-{N}.md` | Excludes commits marked DONE in progress log. Only breaks down remaining work. |
| **Implement** | Latest BREAKDOWN, prior PROGRESS | `PROGRESS-{N}.md` | Skips DONE commits. Resumes from first TODO or FAILED. Updates log after each commit. |
| **Validate** | Latest PLAN, BREAKDOWN, PROGRESS, prior VALIDATION-RUN | `VALIDATION-RUN-{N}.md` | Compares against prior runs: fixed, regressed, persistent, new items. |
| **Critique** | Latest PLAN, BREAKDOWN, PROGRESS, VALIDATION-RUN, prior CRITIQUE-PARETO | `CRITIQUE-HARSH-{N}.md`, `CRITIQUE-PARETO-{N}.md` | Checks whether prior MUST FIX items were addressed. |

**Progress log format** (written incrementally by the implement phase):

```markdown
# Implementation Progress — Run {N}

## Commit 1: [title from breakdown]
- **Status**: DONE | FAILED | SKIPPED
- **Files modified**: [list]
- **Build result**: PASS | FAIL (error summary)
- **Test result**: PASS | FAIL (error summary)
- **Notes**: [issues encountered]

## Commit 2: [title]
...

## Summary
- **Total commits**: X
- **Done**: X
- **Failed**: X
- **Skipped**: X
- **Final build**: PASS | FAIL
- **Final tests**: PASS | FAIL
```

The progress log is written **incrementally** — updated after each commit, not batched at the end. If the pipeline is interrupted mid-implementation, the next run reads the progress log and resumes from the first incomplete commit.

### Prompting an Agent to Write a Dotfile

Give your AI agent this prompt, adjusting the goal to your task:

```
Read dotfiles/reference-run.dot as a template for how to structure an Attractor
pipeline dotfile. Write a new .dot file for this goal:

[YOUR GOAL HERE]

Rules:
- Use the same 5-phase structure (plan, breakdown, implement, validate, critique)
- Set the `goal` graph attribute to describe the objective
- Use the model stylesheet with `provider` and `model` keys (not llm_provider/llm_model)
- Every codergen node (shape=box) needs an explicit `prompt` attribute
- Prompts must use explicit file paths with `logs/` prefix
- All artifacts must be numbered ({N}) for resumability — never overwrite prior artifacts
- Each phase must check for and read prior artifacts before starting work
- The implement phase must write a PROGRESS-{N}.md log incrementally (after each commit)
- Include "use bash heredoc for files over 100 lines" in prompts that produce large output
- Use shape=hexagon for human review gates
- Use edge conditions like `condition="outcome=success"` for routing
- Reference $goal in prompts for variable expansion
```

### Node Shapes

| Shape | Handler | Purpose |
|-------|---------|---------|
| `Mdiamond` | `start` | Pipeline entry point (exactly one) |
| `Msquare` | `exit` | Pipeline exit point (exactly one) |
| `box` | `codergen` | LLM agent task — the workhorse |
| `hexagon` | `wait.human` | Human approval gate — outgoing edge labels become choices |
| `diamond` | `conditional` | Routes based on edge conditions |
| `component` | `parallel` | Fan-out — runs children concurrently |
| `tripleoctagon` | `parallel.fan_in` | Fan-in — merges branch results |

### Model Stylesheet

The `model_stylesheet` graph attribute assigns LLM providers and models to nodes by CSS-like class selectors:

```dot
model_stylesheet = "
    *       { provider = \"anthropic\"; model = \"claude-sonnet-4-6\" }
    .opus   { provider = \"anthropic\"; model = \"claude-opus-4-6\" }
    .review { provider = \"openai\";    model = \"gpt-5.4\" }
    .code   { provider = \"openai\";    model = \"gpt-5.3-codex\" }
    .fast   { provider = \"openai\";    model = \"gpt-5.2-mini\" }
"
```

Then assign classes to nodes: `my_node [class="opus", prompt="..."]`

The runtime can also discover provider models dynamically through the registry cache, so built-in catalog entries are not the only models you can route to.

### Available Built-In Models

| ID | Provider | Use Case |
|----|----------|----------|
| `claude-opus-4-6` | Anthropic | Planning, architecture, complex reasoning |
| `claude-sonnet-4-6` | Anthropic | General purpose (default) |
| `claude-haiku-4-5` | Anthropic | Fast, lightweight tasks |
| `gpt-5.4` | OpenAI | General reasoning, critique |
| `gpt-audio` | OpenAI | Audio-input leaf review and transcription-style flows |
| `gpt-5.2-codex` / `codex-5.2` | OpenAI | Compatibility alias for coding flows |
| `gpt-5.3-codex` / `codex-5.3` | OpenAI | Primary coding model |
| `gpt-5.2-mini` | OpenAI | Fast, lightweight tasks |
| `gemini-3.0-pro-preview` | Google | Large-context reasoning |
| `gemini-3.0-flash-preview` | Google | Faster lower-latency tasks |
| `gemini-2.5-pro` | Google | Capability validation regression target and compatibility path |

Discovered models such as `gpt-5.4-mini` can also appear at runtime after `providers registry refresh`, even when they are not hard-coded into the built-in catalog.

### Visualizing a Dotfile

Render any dotfile to SVG with Graphviz:

```bash
dot -Tsvg dotfiles/reference-run.dot -o dotfiles/reference-run.svg
open dotfiles/reference-run.svg
```

## Validation Agent

[`VALIDATION.md`](VALIDATION.md) defines the validation agent used in Phase 4. It:
- Derives a Definition of Done from the plan
- Builds a test matrix
- Tests the implementation using available tools (bash, browser automation, macOS/iOS app testing)
- Outputs numbered run results to track validation history

## Learn More

- [Attractor Spec](https://github.com/strongdm/attractor) — the upstream spec this implements
- [`attractor_specs/`](attractor_specs/) — local copies of the upstream specs and run learnings
- [`dotfiles/reference-run.dot`](dotfiles/reference-run.dot) — annotated reference pipeline
- [`RELIABILITY-GAP-CLOSURE-PLAN.md`](RELIABILITY-GAP-CLOSURE-PLAN.md) — shipped reliability status and proof baseline
- [`PRODUCT-ROADMAP.md`](PRODUCT-ROADMAP.md) — active follow-up roadmap from the current baseline
- [`PROVIDER-COVERAGE-PLAN.md`](PROVIDER-COVERAGE-PLAN.md) — ranked implementation plan for broader provider support
- [`VALIDATION.md`](VALIDATION.md) — validation agent contract used by the reference workflow
