# Proposal 1: Fail-Closed Hardening of the Existing Codergen Pipeline

## Goal

Stabilize codergen without changing the overall Soulcaster execution model. Keep DOT graphs, keep the existing codergen handler shape, and keep the current provider/session stack. Fix the misleading success cases by making the runtime fail closed and report truthful stage status.

This proposal is the fast path. It addresses the six diagnosed issues with targeted changes to the engine, handler, status contract, timeout wiring, and tests.

## Why This Proposal

The current flakiness is mostly semantic, not model-specific:

- `Retry` can advance as if the node succeeded.
- timeout/plaintext sentinel output can be treated as a soft retry instead of a hard failure.
- fallback status can be labeled as validated.
- long-running implementation nodes use a timeout budget that is too small.
- implementation nodes can "succeed" without touching files.
- tests currently enshrine those behaviors.

The cheapest way to regain predictability is to keep the architecture and harden the runtime contracts around it.

## Architectural Principles

1. A node only advances when the runtime can defend that outcome.
2. Infrastructure failure, contract failure, and business-stage failure must not share the same status.
3. Implementation stages must prove they edited something.
4. Fallback is permitted only when explicitly safe.
5. Telemetry must be honest enough to drive operator decisions.

## Scope

### In Scope

- `PipelineEngine` retry semantics
- `CodergenHandler` status validation and edit guards
- `StageStatusContract` truthfulness
- timeout propagation from graph node to session config
- updated regression coverage
- minimal graph metadata additions if needed

### Out of Scope

- replacing DOT with a new workflow format
- replacing the model-authored stage contract entirely
- building a new execution store
- deep decomposition of codergen into new subphases

## Proposed Design

## 1. Outcome Semantics Become Fail-Closed

### Current Problem

When a node returns `Retry` and there is no retry budget or retry target, the engine can still mark the node completed and continue along the normal outgoing edge.

### Change

Treat `Retry` as a transitional state, not a final stage outcome:

- if retry budget remains, re-run
- if explicit retry or fallback edge exists, route there
- otherwise convert to terminal `Fail`

### Implementation Shape

- Update `PipelineEngine` so exhausted or unconfigured retries cannot fall through to normal edge selection.
- Preserve existing `AllowPartial` behavior, but only after the runtime has concluded the node cannot be retried.

### Result

Issue `#1` is eliminated. Downstream stages no longer inherit a lie.

## 2. Split Failure Meaning Without Rebuilding the Whole Stack

### Current Problem

Timeout sentinels, tool-loop limits, and provider errors are all flattened into a small set of stage statuses, which makes operator diagnosis blurry.

### Change

Retain `OutcomeStatus`, but add a second layer of meaning in the stage contract and telemetry:

- `provider_state`: `completed`, `timeout`, `error`
- `contract_state`: `validated`, `fallback`, `invalid`, `missing`
- `edit_state`: `none`, `modified`
- `stage_state`: `success`, `partial_success`, `retry`, `fail`

### Implementation Shape

- Keep the current top-level stage result shape compatible.
- Add new JSON fields in `status.json` so dashboards and operators can distinguish the cause of failure without a large migration.
- Update `AgentCodergenBackend` sentinel mapping:
  - provider timeout -> `Fail`
  - explicit provider error -> `Fail`
  - exploration stall / tool budget / turn budget -> `Retry`

### Result

Issues `#2` and `#3` become diagnosable instead of opaque.

## 3. Make Fallback Honest

### Current Problem

Fallback status synthesis is useful for backward compatibility, but it currently reports `contract_validated=true`, which is false.

### Change

When fallback is used:

- `contract_validated=false`
- `used_fallback=true`
- `contract_source=legacy_fallback`

### Implementation Shape

- One-line truth fix in `StageStatusContract`
- Add tests that pin the new contract semantics

### Result

Operators and the UI can trust the contract flags again.

## 4. Introduce Lightweight Node Policy

### Current Problem

The runtime has no first-class way to know whether a node is supposed to edit files, tolerate fallback, or require a larger timeout budget.

### Change

Add small, additive node-policy attributes:

- `node_kind=analysis|implementation|validation|evaluation`
- `require_edits=true|false`
- `allow_contract_fallback=true|false`
- `timeout=<duration>`

### Implementation Shape

- Parse these as optional DOT attributes.
- Default behavior remains compatible:
  - nodes without `node_kind` behave as they do today
  - legacy graphs keep running
- The codergen handler can use these fields to enforce postconditions.

### Result

Issue `#5` can be solved cleanly without relying forever on naming conventions like `implement`.

## 5. Require Edit Evidence for Implementation Nodes

### Current Problem

The runtime can accept an implementation stage that did work but produced no patch.

### Change

For nodes marked `require_edits=true` or `node_kind=implementation`:

- success requires non-empty edit evidence
- zero touched files downgrades the stage to `Fail`

### Evidence Model

Short term:

- reuse current `TouchedFilesCount`
- capture touched file paths from `write_file` and `apply_patch`

Hardening step:

- add before/after workspace diff snapshots or file hash comparison

### Result

Implementation nodes become materially meaningful. Reading code for 10 minutes and changing nothing can no longer pass as a successful implementation.

## 6. Propagate Per-Node Timeout to the Session Layer

### Current Problem

Large implementation nodes inherit a flat default timeout even when the graph author clearly intends a bigger budget.

### Change

Bridge graph timeout settings into `SessionConfig.MaxProviderResponseMs`.

### Implementation Shape

- Accept `300000`, `600s`, `10m` style durations
- node timeout overrides session default
- keep `120000` as the global default when unspecified

### Operational Guidance

- `analysis` nodes keep moderate timeouts
- `implementation` nodes at `xhigh` can request larger budgets
- timeouts become a graph-level policy instead of a hidden constant

### Result

Issue `#4` becomes solvable without code edits every time a heavy node appears.

## 7. Update the Tests to Reflect Correct Semantics

### Current Problem

The regression suite currently codifies some of the broken behavior as expected behavior.

### Change

Revise tests so they assert:

- timeout -> fail
- exploration stall -> retry
- retry without budget -> fail closed
- implementation with no edits -> fail
- fallback means `contract_validated=false`

### Result

Issue `#6` is resolved and the new guarantees stop regressing.

## Issue-by-Issue Resolution Map

| Issue | Solution in Proposal 1 |
|---|---|
| 1. Retry fallthrough | Fail-closed retry handling in `PipelineEngine` |
| 2. Timeout/plaintext mapped too softly | Distinguish hard provider failures from retryable stalls |
| 3. Fallback marked validated | Truthful contract metadata |
| 4. Timeout too low | Per-node timeout propagation and duration parsing |
| 5. No edit guard | `require_edits` / `node_kind=implementation` enforcement |
| 6. Tests encode bad behavior | Rewrite regressions to match hardened semantics |

## Rollout Plan

## Phase 1: Minimum Viable Patch

- Fix retry fallthrough
- fix `contract_validated`
- add tests for both

This phase alone stops the most damaging false-green cases.

## Phase 2: Failure Classification

- update sentinel handling in `AgentCodergenBackend`
- add provider/contract/edit state fields

This phase makes failures diagnosable.

## Phase 3: Implementation Guarantees

- add node policy fields
- add `require_edits` enforcement
- add tests for zero-edit implementation nodes

This phase closes the silent no-op gap.

## Phase 4: Timeout Control

- wire node timeout into session config
- add duration parsing tests
- validate with a real heavy workload

This phase makes large runs operable.

## Validation Plan

1. `dotnet test` passes with new regression coverage.
2. A codergen node that times out with no retries fails closed and does not reach downstream validation.
3. `status.json` for fallback cases shows:
   - `contract_validated=false`
   - `used_fallback=true`
4. An implementation node with zero edits fails with an explicit reason.
5. A heavy graph node with `timeout="600s"` can complete using the larger budget.

## Risks

- Adding node policy fields introduces some graph/schema churn.
- `TouchedFilesCount` is still an imperfect proxy until diff-based evidence lands.
- Some existing graphs may rely on the current overly-permissive fallback behavior.

## Mitigations

- Keep defaults backward compatible.
- gate stronger semantics behind explicit node attributes where needed
- roll out the fail-closed engine change first because it is the most critical correctness fix

## Recommendation

Adopt this proposal first. It gives Soulcaster a defensible runtime quickly and addresses the current false-green behavior without forcing a full codergen redesign.

