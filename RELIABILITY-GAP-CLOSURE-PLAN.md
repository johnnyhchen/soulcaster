# Soulcaster Reliability Gap Closure Status

## Status

This document is no longer a forward-looking proposal. It is the current status of the reliability closure work as of 2026-04-24.

The tracked reliability gap for the local-first runner is now closed for the flows that matter most:

- crash and deterministic resume
- repeated crash injection across autoresume attempts
- audited human gates
- cancel, retry-stage, resume, and force-advance control mutations
- policy-constrained operator mutations with deny-audit events
- versioned artifact promotion and rollback
- replayable run history
- scripted provider degradation and transient-failure classification
- capability-aware model validation and routing
- dedicated `agent`, `leaf`, and `multimodal_leaf` execution paths
- multimodal image continuity across shared-thread follow-up turns
- image, document, and audio leaf attachments where provider support exists
- saved CLI and web query views over `workflow.sqlite`, including hotspots, lineage, leases, and mutations
- first-class image-input authoring plus runtime-artifact image handoff in multimodal templates
- first-class reference-document authoring via `document-critique-loop`
- dry-run preview and workflow templates
- per-model scorecards from real runs

## Shipped Baseline

### Runtime correctness

- `UnifiedLlm.Client.StreamAsync` now applies middleware across the full streaming lifecycle instead of only mutating the request.
- Request model aliases are normalized before provider execution.
- Resume semantics, checkpointing, and failure classification are materially more deterministic than the earlier file-only runner.
- Parallel execution now supports the stable policies used by the runner today: `wait_all`, `first_success`, `quorum`, and `k_of_n`.

### Asynchronous child sessions

- Child agents are real background workers with explicit lifecycle state.
- `spawn_agent` is non-blocking.
- `send_input` is queued and re-entrant.
- `wait_agent` waits on actual in-flight work instead of reading stale conversation state.
- Session-owned profiles stop model and tool registry state from bleeding across parent and child sessions.

### Durable run state and control plane

- The runner persists run state under `store/` and projects it into `store/workflow.sqlite`.
- Run ownership is lease-aware and operator mutations are version-guarded.
- SQLite now carries authoritative `lease_ownership` rows and a direct `operator_mutations` journal for control-plane activity.
- The CLI and web surface audited mutations for `cancel`, `retry-stage`, `resume`, and `force-advance`.
- The CLI and web now also expose saved query views for overview, attempts, failures, gates, artifacts, operators, providers, events, scorecards, hotspots, lineage, leases, and mutations.
- Retry budgets, force-advance allow-lists, and deny-audit events are enforced from workflow policy.
- Gate answers capture `actor`, `reason`, and `source`.
- Replay is backed by stored events instead of reconstructing only from log files.
- File mirrors now prefer the durable SQLite run-state snapshot when projecting the canonical run record.
- The web dashboard renders an Operational Views section from those saved queries rather than recomputing ad hoc summaries.

### Artifact registry

- Stage outputs are recorded as versioned artifacts with logical ids and immutable version ids.
- Promotion and rollback are first-class operations instead of ad hoc filename conventions.
- Artifact provenance is linked back to the producing run and stage attempt.
- Nested generated artifacts, including multimodal image outputs, are projected into the registry and SQLite.

### Capability-aware routing

- The static model catalog is now supplemented by a cache and override-backed registry.
- Stage requirements are validated before provider calls.
- Routing can honor preferred models, fallbacks, and latency ceilings.
- Real runs emit per-model scorecards into both JSON projections and SQLite.

### Execution lanes and authoring

- Non-agent stages no longer fall through the coding-agent helper path.
- `leaf` and `multimodal_leaf` use a tool-free request path with lane-aware output-modality forwarding.
- Gemini image responses preserve provider-issued continuity payloads for later turns.
- Leaf stages can attach images, documents, and audio from source files or deferred runtime artifacts.
- `run --dry-run --json` exposes lint, control policy, and node execution metadata before any provider call.
- Builder templates and preview output now encode the current control plane instead of the earlier file-only runner assumptions.
- Multimodal builder flows now support reference-image authoring, preview explicit source-file versus runtime-artifact inputs, and avoid false missing-file lint for stage-produced `logs/...` image handoff paths.
- The builder also ships a `document-critique-loop` template and preview support for explicit document/audio attachment paths.

### Operational proofing

- The runner has a built-in repeated crash hook via `--crash-after-stage` plus `--crash-after-stage-count`.
- Scripted backend runs can simulate transient provider failures and produce durable provider telemetry for query and replay inspection.
- Saved query filters now support actor, artifact, approval-state, and free-text search across the views that can use them.

## Definition Of Done Status

The core definition of done for the reliability gap is effectively met:

- a run can survive interruption and resume to a single unambiguous next step
- operators can inspect and steer the same run through auditable mutations
- stage outputs are versioned and reversible
- invalid model and capability combinations fail closed before provider execution
- the runtime can explain what happened after the fact through replay, durable query views, dashboard operational slices, and scorecards

The reliability roadmap items tracked in this document are implemented. What remains now is optional expansion work, not a blocking gap against the runner definition of done.

The remaining roadmap items are tracked in [PRODUCT-ROADMAP.md](PRODUCT-ROADMAP.md) and currently group into four buckets: richer operator surfaces, broader provider coverage, historical analytics, and authoring polish.

## Proof Points

- Full restart, retry, cancel, resume, and force-advance flows already have passing scenario coverage.
- The latest pass adds proof coverage for document/audio leaf attachments, the `document-critique-loop` template, durable saved queries plus web operational views, repeated crash/autoresume stress, and scripted provider degradation.
- The local/runtime repository suite currently exercises these paths end to end with `457` passing non-live tests. The live-provider `MultimodalIntegrationTests` remain environment- and provider-dependent.

## Optional Follow-On Work

1. Broaden provider-specific attachment and continuity support beyond the currently shipped image, document, and audio paths.
2. Build richer dashboards, saved filters, and comparative analytics on top of the existing query surface.
3. Add more prepackaged workflow templates and authoring affordances rather than expanding the orchestration substrate.
