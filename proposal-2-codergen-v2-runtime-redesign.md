# Proposal 2: Codergen V2 With Typed Runtime-Owned Stage Semantics

## Goal

Replace the ambiguous parts of the current codergen architecture with a runtime-owned execution model. The runtime, not the model, becomes the source of truth for whether a stage succeeded, failed, retried, edited files, or produced a valid contract.

This proposal is the strategic path. It is more invasive than Proposal 1, but it fixes the root ambiguity instead of hardening around it.

## Why This Proposal

The current design asks the model to do too much:

- produce the work
- summarize the work
- emit the final machine-readable stage contract
- implicitly signal whether the stage should advance

That coupling is fragile. If the model times out, emits malformed JSON, or gets stuck in a tool loop, the runtime has to reconstruct intent after the fact. Proposal 2 removes that ambiguity by making the runtime authoritative.

## Architectural Principles

1. Runtime-observed facts outrank model-authored summaries.
2. Stage classes should have explicit success criteria.
3. A stage should be decomposed into subphases with separate budgets.
4. Failure taxonomy should drive routing.
5. Edit evidence must be first-class, not inferred from a best-effort transcript parse.

## Scope

### In Scope

- new codergen execution envelope
- typed stage classes
- runtime-owned status and failure taxonomy
- phase-based execution budgets
- evidence-backed edit validation
- migration path from current codergen handler

### Out of Scope

- replacing provider adapters
- changing DOT as the authoring format
- broad dashboard redesign unrelated to execution truth

## Proposed Design

## 1. Introduce Stage Classes With Policy-Driven Success Criteria

Every codergen node declares a stage class:

- `analysis`
- `implementation`
- `validation`
- `evaluation`

The runtime enforces different rules for each class:

- `analysis`: may succeed with no file edits
- `implementation`: must produce edit evidence
- `validation`: must run verification commands or checks
- `evaluation`: must produce a structured decision artifact

This replaces the current implicit assumptions tied to node naming or prompt wording.

## 2. Make the Runtime the Authoritative Source of Status

### Current Problem

Today the model is expected to emit machine-readable status JSON at the end of the session. If it fails to do so, the runtime falls back.

### Change

The final status becomes runtime-authored:

- the model can still emit a summary artifact
- the runtime writes the authoritative stage envelope from observed execution evidence

### Example Envelope

```json
{
  "stage_id": "implement",
  "stage_class": "implementation",
  "execution_status": "failed",
  "failure_kind": "provider_timeout",
  "contract_status": "missing",
  "edit_status": "none",
  "verification_status": "not_run",
  "advance_allowed": false
}
```

The model no longer has the power to accidentally certify its own success.

## 3. Split Codergen Into Explicit Subphases

The current codergen run is one long opaque session. V2 splits it into:

1. `discover`
2. `plan`
3. `edit`
4. `verify`
5. `finalize`

Each phase has:

- its own timeout
- its own loop/tool-call budget
- explicit exit conditions

### Why This Matters

- a discovery loop can fail without pretending the whole stage succeeded
- edit work can be retried without redoing all planning
- verification can be required for `implementation` nodes

## 4. Promote Failure Taxonomy to a First-Class Engine Concept

Replace the generic overloaded `Retry` pathway with explicit failure kinds:

- `provider_timeout`
- `provider_error`
- `contract_invalid`
- `loop_budget_exhausted`
- `edit_policy_unmet`
- `verification_failed`
- `user_blocked`
- `retryable_planning_stall`

The engine routes based on failure kind and node policy:

- some failures are retryable
- some route to fallback or evaluator nodes
- some are terminal

This eliminates the current ambiguity where a provider timeout and an exploration stall are both just "retry-ish."

## 5. Make Edit Evidence First-Class

### Current Problem

Edit success is inferred from touched file counts and tool calls.

### Change

V2 persists explicit edit evidence:

- before/after file hashes for modified files
- recorded patch operations
- workspace diff summary
- touched path list
- artifact links for generated content

For `implementation` stages:

- no edit evidence means `edit_status=none`
- `edit_status=none` means `execution_status=failed`

This makes issue `#5` structurally impossible.

## 6. Convert the Model Contract Into an Optional Summary Channel

The model can still be asked for:

- intent summary
- change summary
- known limitations
- next-step recommendation

But that output becomes advisory. It is no longer the control plane.

This resolves the malformed-JSON problem by removing malformed JSON from the critical path.

## 7. Add Budgeted, Resumable Execution for Large Workloads

Large UI overhauls should not be one giant monolithic agent loop.

V2 supports:

- bounded edit scopes
- resumable subphase state
- optional chunking by file or work package

For example, a UI overhaul node can be decomposed into:

- dashboard shell changes
- card/detail interaction fixes
- visual redesign pass
- validation/build pass

If one chunk fails, the runtime has precise state about what already landed.

## 8. Add Migration-Compatible DOT Policies

Keep DOT, but extend it with explicit runtime policy:

```dot
implement [
  node_kind="implementation",
  codergen_version="v2",
  timeout="10m",
  require_edits="true",
  require_verification="true"
]
```

Legacy nodes can continue using v1 behavior while new or critical pipelines migrate to v2.

## Issue-by-Issue Resolution Map

| Issue | Solution in Proposal 2 |
|---|---|
| 1. Retry fallthrough | Routing is driven by failure taxonomy and policy, not loose `Retry` semantics |
| 2. Timeout/plaintext ambiguity | Provider timeout becomes a typed runtime failure, not model output |
| 3. Fallback marked validated | Fallback is replaced by explicit contract and evidence status |
| 4. Timeout too low | Timeouts are set per phase and per node, not one flat budget |
| 5. No edit guard | `implementation` success requires first-class edit evidence |
| 6. Tests encode bad behavior | New tests target typed runtime guarantees and policy routing |

## Component Design

## A. Codergen Runtime Envelope

New core data model written by the runtime after every stage:

- stage metadata
- phase execution timings
- provider call outcomes
- tool usage summary
- edit evidence
- verification evidence
- final route decision

## B. Phase Executors

Each phase becomes a bounded executor with a narrow responsibility:

- `DiscoverExecutor`
- `PlanExecutor`
- `EditExecutor`
- `VerifyExecutor`
- `FinalizeExecutor`

This avoids a single giant handler owning every responsibility.

## C. Policy Evaluator

A new policy evaluator determines whether a stage may advance:

- did required edits happen
- did required verification run
- was the stage contract present if required
- is the failure retryable

This is the component that replaces the current implicit fallthrough behavior.

## D. Evidence Store

Persist evidence artifacts under each node log directory:

- `runtime-status.json`
- `diff-summary.json`
- `verification.json`
- `provider-events.json`

This improves debuggability and gives the dashboard real execution truth to render.

## Rollout Plan

## Phase 1: Build V2 Envelope and Failure Taxonomy

- define new status models
- emit runtime-authored status alongside existing status files
- do not switch routing yet

## Phase 2: Add Stage Classes and Policy Evaluator

- add node kind parsing
- implement success criteria per stage class
- enforce `implementation` edit requirements

## Phase 3: Split Handler Into Subphase Executors

- break codergen into discover/plan/edit/verify/finalize
- add phase budgets and timeout control

## Phase 4: Enable V2 Routing for Selected Pipelines

- opt in high-value pipelines
- collect telemetry
- compare against current handler behavior

## Phase 5: Migrate Critical Pipelines and Deprecate Legacy Fallback

- move important implementation pipelines to v2
- reduce reliance on model-authored status JSON

## Validation Plan

1. A provider timeout produces `failure_kind=provider_timeout` and `advance_allowed=false`.
2. An exploration stall produces a retryable failure kind and only retries when policy allows.
3. An implementation stage with no diff fails regardless of model summary text.
4. A verification-required node cannot advance without recorded verification evidence.
5. Real heavy workloads can be resumed or chunked instead of timing out as a single opaque session.

## Risks

- Larger implementation effort than Proposal 1
- introduces new models and migration complexity
- requires dashboard and tooling consumers to understand richer status artifacts

## Mitigations

- run v1 and v2 side by side
- keep DOT backward compatible
- start with opt-in pipelines and compare telemetry before broad rollout

## Recommendation

Use this proposal if Soulcaster is meant to be a dependable multi-stage agent runtime rather than a lightly orchestrated wrapper around model sessions. It is the cleaner long-term architecture, but it should follow Proposal 1 unless there is appetite for a deeper runtime investment immediately.

