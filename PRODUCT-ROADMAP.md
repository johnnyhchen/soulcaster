# Soulcaster Product Roadmap

## Current Baseline

The reliability program that dominated this roadmap is now shipped:

- asynchronous child sessions and real `wait_agent` behavior
- durable run state under `store/` with SQLite-backed run ownership
- audited, policy-constrained control-plane mutations with optimistic concurrency guards
- authoritative SQLite `lease_ownership` plus direct `operator_mutations` journaling
- versioned artifacts, rollback, nested generated outputs, and replay
- capability-aware routing plus lane-aware `agent`, `leaf`, and `multimodal_leaf` execution
- image, document, and audio attachment authoring where provider support exists
- cross-provider continuity that is now explicit per adapter: OpenAI response chaining, Gemini provider-state plus `thoughtSignature` replay, and Anthropic raw-history document follow-up
- saved CLI/web query views over `workflow.sqlite`, including hotspots, lineage, leases, and mutations
- web Operational Views over the durable query surface
- dry-run preview, workflow templates, lint coverage, reference-image multimodal authoring, reference-document critique templates, and multimodal continuity across shared-thread follow-up turns
- built-in repeated crash/autoresume drills plus scripted provider degradation proof paths

What remains is no longer “gap closure.” It is optional expansion work on top of a stable orchestration baseline.

## Short Summary

If you want the remaining roadmap in one pass, it is this:

1. Make operator-facing inspection and drill workflows easier to use.
2. Broaden provider support for the attachment and continuity features that already exist in the runner.
3. Add cross-run analytics instead of stopping at single-run inspection.
4. Smooth out workflow authoring so common patterns feel productized rather than manual.

## Expansion Tracks

### 1. Richer operator surfaces

The durable data surface is now broad enough. The next leverage is better presentation and higher-level workflows.

- add richer dashboards and saved filters over runs, artifacts, operators, and scorecards
- add comparative views across runs instead of only single-run inspection
- package common operational drill flows so they are easier to launch and review

### 2. Broader provider coverage

The runner now supports image, document, and audio attachments, but provider behavior is still uneven.

- OpenAI document/audio attachment support is now shipped, Gemini/OpenAI/Anthropic continuity semantics are now shipped, and Anthropic document support is shipped with Anthropic audio intentionally fail-closed
- extend attachment support across more provider adapters instead of failing closed where support is still absent
- extend the same fail-closed attachment and continuity discipline to future adapters and output modalities instead of assuming today’s three providers are the ceiling
- add more reference dotfiles and templates for generation, edit, critique, and review loops

Detailed execution order for this track lives in `PROVIDER-COVERAGE-PLAN.md`.

### 3. Historical analytics

Single-run query views are strong now. The next expansion is cross-run analysis.

- add trend and regression views across multiple runs, not just per-run scorecards
- surface retention and pruning policies for long-lived stores
- build rollups that let operators spot slow stages, flaky nodes, or expensive models over time

### 4. Authoring polish

Authoring is now viable from the CLI. The next step is smoothing the workflow, not inventing a new runtime model.

- add more guided builder flows on top of the existing `template`, `node`, and `preview` commands
- add more reference workflows that demonstrate attached documents/audio, degradation drills, and operator controls
- tighten interactive editing and preview ergonomics for larger graphs

## Recommended Sequence

1. Richer operator surfaces
2. Broader provider coverage
3. Historical analytics
4. Authoring polish

## Success Signals

- operators can answer most run-debugging questions from dashboards and saved views without digging through raw files
- provider-specific attachment support becomes broader without weakening fail-closed behavior
- long-lived installations can reason about trends, regressions, and cost over many runs
- workflow authoring feels productized rather than merely scriptable
