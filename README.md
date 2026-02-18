# Soulcaster

A C# implementation of the [strongDM Attractor](https://github.com/strongdm/attractor) spec — a DOT-based pipeline runner that orchestrates multi-stage AI workflows using directed graphs.

You define your workflow as a Graphviz DOT file: nodes are AI tasks (LLM calls, human review gates, parallel fan-outs), edges are transitions with conditions. The engine walks the graph, dispatching each node to the appropriate LLM, and routes between nodes based on outcomes.

## Architecture

```
src/
├── JcAttractor.Attractor/      # Pipeline engine, DOT parser, handlers, graph model
├── JcAttractor.CodingAgent/    # Agentic coding loop (tool use, multi-turn sessions)
└── JcAttractor.UnifiedLlm/     # Multi-provider LLM client (Anthropic, OpenAI, Gemini)

runner/                         # CLI runner that executes a .dot pipeline
tests/                          # Test suite
dotfiles/                       # Reference pipeline definitions
attractor_specs/                # Original spec documents and run learnings
```

**Three layers:**

1. **Unified LLM** — provider-agnostic LLM client with streaming, tool calling, and a model catalog. Supports Anthropic (Claude), OpenAI (GPT/Codex), and Google (Gemini).
2. **Coding Agent** — agentic loop that gives an LLM tools (file read/write, bash, grep, glob) and runs multi-turn sessions until the task is complete.
3. **Attractor** — pipeline engine that parses DOT files, resolves handlers by node shape, manages state/checkpoints, and traverses the graph.

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
dotnet test
```

## Run a Pipeline

```bash
dotnet run --project runner -- <path-to-dotfile>
```

For example, to run the reference pipeline:

```bash
dotnet run --project runner -- dotfiles/reference-run.dot
```

The runner will:
1. Parse the DOT file
2. Create an `output/` directory next to the dotfile for artifacts
3. Walk the graph from `start` to `exit`, executing each node
4. Print progress and outcomes to the console

## Writing a Dotfile

The reference pipeline at [`dotfiles/reference-run.dot`](dotfiles/reference-run.dot) demonstrates the standard 5-phase pattern:

1. **Plan & Interview** — Orient on the codebase, draft a plan, human reviews and clarifies
2. **Break Down** — Decompose into single-commit chunks with QA plans, human reviews
3. **Implement** — Execute the commits, writing a progress log after each one
4. **Validate** — Run the validation agent ([`VALIDATION.md`](VALIDATION.md)), loop back to plan on failure
5. **Critique** — Adversarial review, Pareto-optimal distillation, human decides ship or loop

### Artifact Trail and Resumability

Every phase writes numbered markdown artifacts to `output/logs/`. This serves two purposes: (1) the pipeline can resume from any point if interrupted, and (2) loop iterations (validate→plan, critique→plan) build on prior work rather than starting over.

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
    * { provider = \"anthropic\"; model = \"claude-sonnet-4-6\" }
    .opus  { provider = \"anthropic\"; model = \"claude-opus-4-6\" }
    .codex { provider = \"openai\";    model = \"codex-5.2\" }
    .gpt   { provider = \"openai\";    model = \"gpt-5.2\" }
"
```

Then assign classes to nodes: `my_node [class="opus", prompt="..."]`

### Available Models

| ID | Provider | Use Case |
|----|----------|----------|
| `claude-opus-4-6` | Anthropic | Planning, architecture, complex reasoning |
| `claude-sonnet-4-6` | Anthropic | General purpose (default) |
| `gpt-5.2` | OpenAI | General reasoning, critique |
| `gpt-5.2-codex` / `codex-5.2` | OpenAI | Agentic coding, implementation |
| `gpt-5.2-mini` | OpenAI | Fast, lightweight tasks |
| `gemini-3.0-pro-preview` | Google | Large context (1M tokens) |

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
- [`attractor_specs/`](attractor_specs/) — local copies of the spec, coding agent loop spec, unified LLM spec, and run learnings
- [`dotfiles/reference-run.dot`](dotfiles/reference-run.dot) — annotated reference pipeline
