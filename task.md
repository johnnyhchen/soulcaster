# Soulcaster Codergen Consensus Adoption Plan

## Decision

Adopt a hybrid plan:

- use **Proposal 1** as the immediate execution path to stop false-green runs now
- use **Proposal 2** as the target architecture, but stage it behind the hardening work
- keep the concrete code-level fixes from the previous `task.md`, but correct two implementation gaps it missed:
  - per-node timeout cannot be fixed only inside `CodergenHandler` because `ICodergenBackend.RunAsync(...)` does not accept runtime execution options today
  - timeout policy currently exists in two layers, `PipelineEngine` node timeout and `SessionConfig.MaxProviderResponseMs`, and they must be aligned as one runtime policy

This means we do **not** jump straight to a full codergen v2 redesign, and we do **not** stop at one-off bug fixes that would need to be torn back out later.

## Why This Is The Right Plan

### What Proposal 1 gets right

- it fixes the live correctness hole quickly
- it preserves DOT and the current handler/engine shape
- it targets the real false-green path in `PipelineEngine`, `AgentCodergenBackend`, `StageStatusContract`, and `CodergenHandler`

### What Proposal 2 gets right

- the runtime must become the authority for execution truth
- stage policy should be explicit, not inferred from prompt wording
- edit evidence and verification evidence need to be first-class
- richer failure taxonomy is the right long-term routing model

### What the original task.md gets right

- it identifies the exact code paths that are currently wrong
- it gives the correct minimum patch order
- it keeps the rollout tied to tests and concrete validation runs

### What changes in this consensus plan

- we explicitly separate **correctness now** from **architecture next**
- we add a backend/runtime options layer before attempting timeout or policy work
- we introduce runtime-authored status artifacts early, but keep routing on v1 semantics until the artifacts are proven

## Non-Negotiable Principles

1. A node must never advance on an outcome the runtime cannot defend.
2. Runtime-observed facts outrank model-authored summaries.
3. Implementation success requires edit evidence.
4. Timeout and failure semantics must be truthful and diagnosable.
5. New policy should be additive and backward compatible with existing DOT graphs.

## Current Gaps Confirmed In Code

| Gap | Current code |
|---|---|
| Retry falls through when no retry budget is configured | `src/JcAttractor.Attractor/Execution/PipelineEngine.cs:241-307` |
| Sentinel timeout/error responses are all mapped to `Retry` | `runner/AgentCodergenBackend.cs:695-705` |
| Fallback status still writes `contract_validated = true` | `src/JcAttractor.Attractor/Execution/StageStatusContract.cs:48-76` |
| Node timeout and provider timeout are separate, inconsistent policies | `src/JcAttractor.Attractor/Execution/PipelineEngine.cs:490-508`, `runner/AgentCodergenBackend.cs:288-295`, `src/JcAttractor.CodingAgent/Session/SessionConfig.cs:3-13` |
| `CodergenHandler` has no success guard for zero-edit implementation work | `src/JcAttractor.Attractor/Handlers/CodergenHandler.cs:118-168` |
| The backend interface has no runtime options channel | `src/JcAttractor.Attractor/Handlers/CodergenHandler.cs:7-10` |

## Adopted Plan

## Phase 0: Stop The False-Green Hole

Ship these first, together:

1. `PipelineEngine`: make exhausted-or-unconfigured `Retry` fail closed.
2. `StageStatusContract`: make fallback status truthful with `contract_validated = !usedFallback`.
3. Tests: update or add regressions so this behavior is pinned.

### Required behavior

- `Retry` is transitional, never terminal success
- if retries are unavailable or exhausted, runtime converts the result to `Fail` or `PartialSuccess` only when `AllowPartial` explicitly allows it
- downstream normal-edge routing must never happen from a stale `Retry`

### Why this phase stands alone

This is the minimum patch that stops the most damaging behavior without waiting for a larger redesign.

## Phase 1: Make Failure Semantics Truthful In The Current Runtime

Keep the existing execution model, but make status classification honest:

### `AgentCodergenBackend`

- map provider timeout sentinel to `Fail`
- map explicit provider error sentinel to `Fail`
- keep exploration stall, tool-round exhaustion, and turn-limit exhaustion as `Retry`

### `PipelineEngine`

- align engine-level timeout semantics with the same principle
- a node timeout should not come back as an ambiguous retry by default
- introduce a typed timeout note or failure reason so operator telemetry distinguishes timeout from other failure kinds

### Status output

Extend `status.json` with additive fields:

- `provider_state`: `completed | timeout | error`
- `contract_state`: `validated | fallback | invalid | missing`
- `edit_state`: `none | modified`
- `verification_state`: `not_required | not_run | passed | failed`

Keep current top-level `status` for compatibility.

## Phase 2: Add Runtime Policy Plumbing Before More Semantics

This is the missing step between Proposal 1 and Proposal 2.

### Problem

Per-node timeout, edit policy, and verification policy cannot be implemented cleanly if the backend contract remains:

```csharp
Task<CodergenResult> RunAsync(string prompt, string? model, string? provider, string? reasoningEffort, CancellationToken ct)
```

There is no place to pass execution policy.

### Adopted change

Introduce a runtime options object, for example:

```csharp
public sealed record CodergenExecutionOptions(
    string? StageClass = null,
    int? MaxProviderResponseMs = null,
    bool RequireEdits = false,
    bool RequireVerification = false,
    bool AllowContractFallback = true,
    string? CodergenVersion = null);
```

Then extend the backend contract to accept it.

### Why this matters

- `CodergenHandler` can pass node policy once
- `AgentCodergenBackend` can create the session with the correct provider timeout
- future v2 routing and status logic has a stable place to read execution policy

## Phase 3: Introduce Explicit Node Policy, Additively

Do this without breaking existing graphs.

### Policy source

Start by reading from `GraphNode.RawAttributes`, because DOT already preserves unknown attributes end to end:

- `node_kind`
- `require_edits`
- `require_verification`
- `allow_contract_fallback`
- `codergen_version`

If these prove durable, they can later be promoted to first-class typed `GraphNode` fields.

### Backward compatibility

- default legacy graphs to current behavior
- allow a temporary heuristic fallback for implementation detection, but do not make naming conventions the permanent contract
- prefer explicit `node_kind=implementation` over `"implement"` string matching as soon as policy attributes are available

## Phase 4: Enforce Evidence-Based Success In V1-Compatible Runtime

This is still in the existing handler shape, but uses the new policy plumbing.

### Implementation nodes

- if `require_edits=true` or `node_kind=implementation`, success requires non-empty edit evidence
- initial evidence source can be current touched-file telemetry from `write_file` and `apply_patch`
- if touched files are zero, downgrade `Success` or `PartialSuccess` to `Fail`

### Verification-required nodes

- if `require_verification=true`, record whether verification actually ran
- phase 1 can use command/tool evidence and artifact presence
- do not fully block routing on verification until the evidence path is stable and tested

### Important constraint

`TouchedFilesCount` is acceptable as a first enforcement pass, but the plan should explicitly treat it as transitional, not final.

## Phase 5: Add Runtime-Owned Status Envelope Side By Side

This is the first Proposal 2 concept we should adopt structurally.

### Add new artifacts

Write runtime-authored artifacts alongside existing `status.json`:

- `runtime-status.json`
- `provider-events.json`
- `diff-summary.json`
- `verification.json`

### Runtime envelope contents

- stage metadata
- stage class and effective policy
- execution status
- failure kind
- contract status
- edit status and touched paths
- verification status
- advance allowed or not

### Routing rule

In this phase, emit the new artifacts but do **not** yet switch all routing to them. Use them for observability first.

## Phase 6: Move Toward Codergen V2 Selectively

Only after Phases 0 through 5 are complete and stable:

1. add a policy evaluator that decides whether a node may advance
2. make runtime-authored envelope the routing truth for `codergen_version=v2` nodes
3. split codergen into bounded subphases only where the extra complexity pays for itself:
   - `discover`
   - `plan`
   - `edit`
   - `verify`
   - `finalize`
4. add resumability or chunking only after typed runtime status is already in place

This keeps Proposal 2’s best ideas, but delays the most invasive parts until the foundations are trustworthy.

## Specific Fixes To Carry Forward From The Previous Task Plan

These remain correct and should be implemented inside the adopted phases above:

- `PipelineEngine`: remove the `maxRetries > 0` guard from the exhausted-retry conversion path
- `AgentCodergenBackend`: classify timeout/error sentinels as hard failure, exploration/tool/turn stalls as retryable
- `StageStatusContract`: set `contract_validated` from `usedFallback`
- `CodergenHandler`: add implementation success guard using runtime edit evidence
- regression tests: update old assertions that encode flaky behavior

## Additional Fixes Required By This Consensus Plan

These were missing or under-specified before:

1. Add a shared duration parser used by both engine timeout and provider timeout wiring.
2. Stop relying on `TimeSpan.TryParse` alone if the supported policy syntax includes values like `600s` and `10m`.
3. Extend `ICodergenBackend` to accept execution options instead of threading more positional parameters.
4. Emit runtime-authored status artifacts before attempting v2 routing changes.
5. Treat stage policy as additive DOT attributes first, not as a mandatory graph schema migration.

## Test Plan

### Phase 0 and 1 tests

- `PipelineEngine_RetryWithNoRetryBudget_TerminatesWithFail`
- `PipelineEngine_RetryWithExhaustedBudget_TerminatesWithFail`
- `AgentCodergenBackend_ModelTimeout_ReturnsFail`
- `AgentCodergenBackend_ExplorationStall_ReturnsRetryWithoutExtraStatusReminders`
- `StageStatusContract_ToStatusJson_FallbackSetsContractValidatedFalse`

### Phase 3 and 4 tests

- `CodergenHandler_ImplementationNode_ZeroTouchedFiles_DowngradesToFail`
- `CodergenHandler_ExplicitRequireEdits_ZeroTouchedFiles_DowngradesToFail`
- timeout policy tests proving the same node timeout config reaches both engine and provider layers
- duration parser tests for raw milliseconds and suffix formats

### Phase 5 and 6 tests

- runtime envelope is written even when model contract is missing
- `advance_allowed=false` for provider timeout and edit-policy failure
- verification-required nodes record `verification_state` correctly
- v1 and v2 nodes can coexist in the same graph without breaking legacy behavior

## Rollout Order

1. Phase 0
2. Phase 1
3. Phase 2
4. Phase 3 and Phase 4 together
5. Phase 5
6. Phase 6 for selected pipelines only

## Acceptance Criteria

1. A codergen timeout can no longer advance to downstream nodes through normal routing.
2. Fallback status is visibly marked as fallback and not falsely validated.
3. Heavy nodes can set a larger timeout once, and that policy reaches both runtime timeout layers.
4. An implementation node with zero edits cannot report success.
5. Runtime-authored status artifacts exist before any v2 routing cutover.
6. Existing DOT graphs continue to run unless they explicitly opt into stronger policy.

## Execution Status

Completed on April 14, 2026:

- Phase 0 fail-closed retry handling and truthful fallback validation status
- Phase 1 timeout and provider-error classification plus additive status fields
- Phase 2 runtime options plumbing and shared timeout parsing
- Phase 3/4 additive node policy handling and edit-evidence success guard
- Phase 5 runtime-owned sidecar status artifacts

Deferred intentionally:

- Phase 6 v2 routing cutover and selective multi-phase codergen runtime changes

## Recommendation

Adopt this plan as the working plan.

In short: **patch Proposal 1 first, shape the code so Proposal 2 becomes an additive migration, and use the original task.md’s concrete fix list as the implementation checklist.**
