# Proposal 3: Runtime-Owned Validation and Evidence System

## Goal

Make validation a first-class runtime subsystem instead of a best-effort inference from model tool calls.

The runtime, not the model, should become the authority for:

- whether validation was required
- which checks were supposed to run
- which checks actually ran
- which checks passed or failed
- whether the stage is allowed to advance

This proposal is narrower than the full codergen v2 redesign. It focuses on the validation gap specifically and can be adopted incrementally inside the current Soulcaster architecture.

## Why This Proposal

The current validation path is useful but not robust enough for production gating:

- verification is inferred from `shell` or `bash` tool calls
- command detection is heuristic and can miss wrapper scripts or custom build entrypoints
- `require_verification=true` reports verification state but does not hard-enforce success
- the model can still claim stage success even when validation semantics are ambiguous
- operator artifacts exist, but validation is not yet a runtime-owned decision function

The core issue is that validation is observed indirectly. Proposal 3 makes validation explicit, structured, and evidence-backed.

## Architectural Principles

1. Validation is a runtime concern, not a language-model summary concern.
2. A stage may only advance when required validations have runtime evidence.
3. Validation commands and assertions must be explicit and serializable.
4. Validation evidence must be persisted as artifacts, not only summarized in telemetry.
5. Heuristics may assist migration, but they must not remain the long-term source of truth.

## Scope

### In Scope

- explicit validation policy per node
- structured validation registration and execution
- runtime-owned validation artifacts
- stage advancement based on validation evidence
- migration from heuristic verification detection

### Out of Scope

- replacing DOT as the graph format
- replacing provider adapters
- full codergen phase decomposition unless needed to support validation
- UI redesign or dashboard work beyond what the artifacts require

## Current Problems

## 1. Validation Is Inferred, Not Declared

Today the runtime scans tool calls and guesses whether a command looked like verification.

That works for:

- `dotnet build`
- `dotnet test`
- `pytest`
- `pnpm build`

But it is still guesswork. A repo-specific script like `./scripts/ci_validate.sh` is only recognized if the heuristic knows how to read the name.

## 2. Validation Is Observational, Not Authoritative

If the model runs a build command in the middle of the task, the runtime currently treats that as "verification happened." That is useful telemetry, but it does not mean the runtime explicitly required, scheduled, or completed validation as a formal step.

## 3. Validation Is Not Yet a Hard Gate

`require_verification=true` currently gives us reporting, but not a hard runtime rule that says:

- required validation was missing
- therefore the stage must fail or retry

## 4. Evidence Is Too Thin

We record summary fields like:

- `verification_state`
- `verification_commands`
- `verification_errors`

That is not enough for production-grade diagnosis. We need:

- exact checks
- exit codes
- durations
- stdout and stderr artifact links
- whether the check was required or optional
- whether the check was model-requested, graph-declared, or runtime-defaulted

## Proposed Design

## 1. Introduce Explicit Validation Policy

Each codergen node gets a validation policy resolved by the runtime.

### Example policy inputs

- `require_verification=true|false`
- `validation_profile=build|test|lint|custom`
- `validation_mode=none|advisory|required`
- `validation_commands=[...]`
- `validation_timeout=...`
- `validation_fail_action=fail|retry`

### Example DOT

```dot
implement [
  node_kind="implementation",
  require_edits="true",
  require_verification="true",
  validation_mode="required",
  validation_profile="build",
  validation_commands="dotnet build src/Soulcaster.Attractor/Soulcaster.Attractor.csproj --nologo --no-restore",
  validation_timeout="5m"
]
```

### Policy resolution order

1. explicit node attributes
2. graph-level defaults
3. stage-class defaults
4. compatibility fallback for legacy nodes

### Stage-class defaults

- `analysis`: validation optional by default
- `implementation`: validation required by default for v2-style nodes
- `validation`: validation required and stage success equals check success
- `evaluation`: validation optional unless explicitly requested

## 2. Separate Work Execution From Validation Execution

Validation should not be inferred from arbitrary shell traffic inside the editing loop.

Instead, the runtime should operate in two distinct segments:

1. `work segment`
2. `validation segment`

### Work segment

The model edits files, reads files, asks questions, and may run exploratory commands.

### Validation segment

The runtime freezes the intended validation plan and executes it under structured control.

This avoids conflating:

- exploration
- local sanity checks
- final stage-gating validation

## 3. Add a Structured Validation Manifest

The runtime should persist an explicit manifest for each stage, for example:

```json
{
  "node_id": "implement",
  "mode": "required",
  "checks": [
    {
      "id": "build-1",
      "kind": "command",
      "name": "project-build",
      "command": "dotnet build src/Soulcaster.Attractor/Soulcaster.Attractor.csproj --nologo --no-restore",
      "workdir": "/repo",
      "timeout_ms": 300000,
      "required": true,
      "source": "node_policy"
    }
  ]
}
```

### Manifest sources

A validation check may come from:

- `node_policy`
- `graph_default`
- `runtime_default`
- `model_requested`

That source field is important for debugging why a check existed.

## 4. Add Explicit Validation Check Types

The system should support more than shell commands.

### Command check

Runs a command and captures:

- exit code
- duration
- stdout path
- stderr path

### File existence check

Asserts that a path exists after the work segment.

### File content check

Asserts that a file contains text, regex, or JSON fields.

### Diff check

Asserts that expected files changed or that a diff exists at all.

### Artifact check

Asserts that an expected output artifact was produced by the work segment.

### Schema check

Validates that JSON output conforms to a schema.

This lets validation mean more than "a build happened."

## 5. Add a Runtime Tool for Validation Registration

For new nodes and v2-like behavior, the model should not need to smuggle validation through `shell`.

Add a dedicated tool such as:

- `queue_validation_check`

### Example call

```json
{
  "kind": "command",
  "name": "unit-tests",
  "command": "uv run pytest tests/unit -q",
  "timeout_ms": 180000,
  "required": true
}
```

### Runtime behavior

- the tool does not execute the check immediately
- it records a check in the validation manifest
- the runtime executes queued checks after the work segment

This gives the model a structured way to request validation without making the model the authority on pass/fail.

## 6. Keep Heuristic Detection Only As Migration Support

Legacy nodes and providers will still use raw `shell` or `bash` commands for some time.

During migration:

- continue collecting heuristic `observed_verification`
- store it separately from `authoritative_validation`
- never let heuristic-only evidence satisfy a `required` validation policy for v2 nodes

### Example split

- `observed_verification_state`: `passed`
- `authoritative_validation_state`: `missing`

That distinction avoids false greens during rollout.

## 7. Introduce a Validation Executor

Add a runtime component responsible only for validation checks.

### Responsibilities

- deduplicate checks
- normalize working directories and timeouts
- run checks in deterministic order
- allow optional parallel execution for independent checks
- collect results and artifact paths
- produce a final validation verdict

### Suggested execution rules

- required checks default to sequential execution
- optional checks may run in parallel
- stop on first required failure unless policy says to continue and collect all failures

## 8. Persist Validation Evidence As First-Class Artifacts

For each stage, write:

- `validation-manifest.json`
- `validation-results.json`
- `validation-summary.json`
- `validation-logs/<check-id>.stdout.log`
- `validation-logs/<check-id>.stderr.log`

### Example result

```json
{
  "node_id": "implement",
  "mode": "required",
  "overall_state": "failed",
  "checks": [
    {
      "id": "build-1",
      "state": "failed",
      "required": true,
      "exit_code": 1,
      "duration_ms": 18432,
      "stdout_path": "validation-logs/build-1.stdout.log",
      "stderr_path": "validation-logs/build-1.stderr.log"
    }
  ]
}
```

This makes incident response and CI triage much faster than reading a model summary.

## 9. Make Validation a Hard Stage Gate

The runtime policy evaluator should compute final stage advancement from:

1. contract validity
2. edit evidence
3. validation result
4. failure taxonomy

### Required validation rule

If `validation_mode=required` and:

- no authoritative validation checks were run, or
- any required validation check failed

then:

- `advance_allowed=false`
- `execution_status=fail` or `retry` depending on `validation_fail_action`

### Advisory validation rule

If `validation_mode=advisory`, validation failures are recorded but do not automatically block advancement.

## 10. Expand Failure Taxonomy For Validation

Validation needs its own failure kinds, separate from provider failures.

### Suggested validation failure kinds

- `validation_missing`
- `validation_failed`
- `validation_timeout`
- `validation_misconfigured`
- `validation_artifact_missing`
- `validation_schema_failed`

These should never be collapsed into `provider_error`.

## Detailed Runtime Flow

## Step 1: Resolve Policy

The node enters codergen with:

- stage class
- edit policy
- validation policy
- provider timeout
- validation timeout

The runtime writes `effective-policy.json`.

## Step 2: Execute Work Segment

The model performs work as it does today.

During this segment, the runtime records:

- tool calls
- touched files
- optional queued validation checks
- blocking questions
- provider errors

## Step 3: Freeze Evidence

Before validation starts, the runtime freezes:

- touched path list
- diff summary
- queued validation checks
- graph-declared validation checks

This prevents the final stage status from depending on mutable model narration.

## Step 4: Build Validation Manifest

The runtime combines:

- graph-declared checks
- model-queued checks
- runtime defaults

and writes `validation-manifest.json`.

## Step 5: Run Validation Executor

The runtime executes each check and persists logs and results.

At this point, validation is no longer "something the model says happened." It is an explicit runtime subphase.

## Step 6: Compute Final Validation Verdict

The runtime computes:

- `authoritative_validation_state`
- `required_checks_total`
- `required_checks_passed`
- `optional_checks_failed`

## Step 7: Compute Final Stage Status

The policy evaluator decides:

- `execution_status`
- `failure_kind`
- `advance_allowed`

Example:

- edits happened
- model emitted valid status JSON
- required build failed

Result:

- `execution_status=fail`
- `failure_kind=validation_failed`
- `advance_allowed=false`

## Migration Plan

## Phase A: Improve Artifacts Without Changing Routing

- add validation manifest and result files
- keep current heuristic verification state
- record both heuristic and authoritative channels when possible

## Phase B: Add Explicit Validation Registration

- add `queue_validation_check`
- allow nodes to declare validation commands explicitly
- prefer authoritative validation for new nodes

## Phase C: Turn On Hard Enforcement For Opt-In Nodes

For nodes with `codergen_version=v2` or `validation_mode=required`:

- heuristic evidence no longer satisfies required validation
- authoritative validation becomes the stage gate

## Phase D: Deprecate Heuristic-Only Success

Once the migration is stable:

- plain `shell`-detected verification remains telemetry only
- stage advancement depends on runtime-owned validation results

## Why This Is Better

- it removes ambiguity between "the model ran a command" and "the runtime validated the stage"
- it gives operators exact evidence for every validation decision
- it allows strict enforcement for critical nodes and advisory mode for legacy nodes
- it works across providers because the validation authority moves out of provider-specific tool semantics
- it composes naturally with the current sidecar artifact model

## Concrete Recommendation

Adopt Proposal 3 as the next step after the current hardening work:

1. add validation manifest and result artifacts
2. add explicit validation registration
3. make validation a hard gate for opt-in nodes
4. remove heuristic verification from the critical path over time

This gives Soulcaster a production-grade validation system without requiring an immediate full codergen rewrite.
