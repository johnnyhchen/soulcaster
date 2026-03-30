#!/usr/bin/env bash
set -euo pipefail

SOULCASTER_DIR="${SOULCASTER_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
DOTFILES_DIR="$SOULCASTER_DIR/dotfiles"
QA_AGENT_DIR="${QA_AGENT_DIR:-$HOME/qa-agent}"
RESULTS_DIR="${RESULTS_DIR:-$SOULCASTER_DIR/qa-results/$(date +%Y%m%d-%H%M%S)}"
SOLUTION_FILE="$SOULCASTER_DIR/JcAttractor.sln"

MODE_UNIT=false
MODE_SCENARIO=false
MODE_LIVE=false
LIVE_ONLY=""
SKIP_QA_AGENT=false

UNIT_STATUS="SKIP"
SCENARIO_STATUS="SKIP"
LIVE_STATUS="SKIP"
LIVE_SMOKE_STATUS="SKIP"
LIVE_CHECKPOINT_STATUS="SKIP"
LIVE_PARALLEL_STATUS="SKIP"
LIVE_QUEUE_STATUS="SKIP"
LIVE_SUPERVISOR_STATUS="SKIP"
LIVE_BUILDER_STATUS="SKIP"
LIVE_LINT_STATUS="SKIP"

HARNESS_LOG="$RESULTS_DIR/harness.log"
BUILD_LOG="$RESULTS_DIR/build.log"
UNIT_LOG="$RESULTS_DIR/unit-results.log"
SCENARIO_LOG="$RESULTS_DIR/scenario-results.log"
LIVE_LOG="$RESULTS_DIR/live-results.log"
SUMMARY_JSON="$RESULTS_DIR/summary.json"

BUILD_READY=false

usage() {
    cat <<'EOF'
Soulcaster QA Harness

Usage:
  ./scripts/qa-harness.sh --unit
  ./scripts/qa-harness.sh --scenario
  ./scripts/qa-harness.sh --live
  ./scripts/qa-harness.sh --full

Options:
  --unit                 Run the non-scenario automated test suite
  --scenario             Run the deterministic scenario harness tests
  --live                 Run the required live validation pass
  --full                 Run --unit, --scenario, then --live
  --live-scenario <id>   Run one live check only: smoke | checkpoint | parallel | queue | supervisor | builder | lint
  --skip-qa-agent        Reserved compatibility flag for older harness flows
  --help, -h             Show this help

Default:
  If no mode is specified, the harness runs --live.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --unit)
            MODE_UNIT=true
            shift
            ;;
        --scenario)
            MODE_SCENARIO=true
            shift
            ;;
        --live)
            MODE_LIVE=true
            shift
            ;;
        --full)
            MODE_UNIT=true
            MODE_SCENARIO=true
            MODE_LIVE=true
            shift
            ;;
        --live-scenario)
            LIVE_ONLY="$2"
            shift 2
            ;;
        --skip-qa-agent)
            SKIP_QA_AGENT=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown flag: $1" >&2
            usage
            exit 1
            ;;
    esac
done

if [[ "$MODE_UNIT" == "false" && "$MODE_SCENARIO" == "false" && "$MODE_LIVE" == "false" ]]; then
    MODE_LIVE=true
fi

mkdir -p "$RESULTS_DIR"
: > "$HARNESS_LOG"

log() {
    echo "[$(date +%H:%M:%S)] $*" | tee -a "$HARNESS_LOG"
}

log_live() {
    echo "[$(date +%H:%M:%S)] $*" | tee -a "$HARNESS_LOG" "$LIVE_LOG"
}

run_and_tee() {
    local log_file="$1"
    shift

    set +e
    "$@" > >(tee -a "$log_file") 2> >(tee -a "$log_file" >&2)
    local status=$?
    set -e
    return "$status"
}

ensure_build() {
    if [[ "$BUILD_READY" == "true" ]]; then
        return 0
    fi

    : > "$BUILD_LOG"
    log "Building solution..."
    if run_and_tee "$BUILD_LOG" dotnet build "$SOLUTION_FILE"; then
        BUILD_READY=true
        log "Build complete."
        return 0
    fi

    log "Build failed."
    return 1
}

require_anthropic_key() {
    if [[ -z "${ANTHROPIC_API_KEY:-}" ]]; then
        log_live "Missing ANTHROPIC_API_KEY."
        return 1
    fi
}

should_run_live() {
    [[ -z "$LIVE_ONLY" || "$LIVE_ONLY" == "$1" ]]
}

json_status() {
    python3 - "$1" <<'PY'
import json, sys
path = sys.argv[1]
with open(path, "r", encoding="utf-8") as fh:
    data = json.load(fh)
print(data.get("status", "unknown"))
PY
}

checkpoint_completed_count() {
    python3 - "$1" <<'PY'
import json, sys
with open(sys.argv[1], "r", encoding="utf-8") as fh:
    data = json.load(fh)
print(len(data.get("CompletedNodes", [])))
PY
}

checkpoint_has_nodes() {
    python3 - "$1" "$2" <<'PY'
import json, sys
with open(sys.argv[1], "r", encoding="utf-8") as fh:
    data = json.load(fh)
nodes = set(data.get("CompletedNodes", []))
required = [item for item in sys.argv[2].split(",") if item]
missing = [item for item in required if item not in nodes]
if missing:
    print(",".join(missing))
    raise SystemExit(1)
print("ok")
PY
}

write_summary_json() {
    python3 - "$SUMMARY_JSON" <<PY
import json
import os
import sys

payload = {
    "results_dir": os.environ["RESULTS_DIR"],
    "unit": {"status": os.environ["UNIT_STATUS"]},
    "scenario": {"status": os.environ["SCENARIO_STATUS"]},
    "live": {
        "status": os.environ["LIVE_STATUS"],
            "checks": {
                "smoke_steer": os.environ["LIVE_SMOKE_STATUS"],
                "checkpoint_resume": os.environ["LIVE_CHECKPOINT_STATUS"],
                "parallel_multihop": os.environ["LIVE_PARALLEL_STATUS"],
                "queue_parallelism": os.environ["LIVE_QUEUE_STATUS"],
                "telemetry_supervisor": os.environ["LIVE_SUPERVISOR_STATUS"],
                "builder_editor": os.environ["LIVE_BUILDER_STATUS"],
                "lint": os.environ["LIVE_LINT_STATUS"],
            },
    },
}

with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(payload, fh, indent=2)
PY
}

run_unit_mode() {
    : > "$UNIT_LOG"
    if ! ensure_build; then
        UNIT_STATUS="FAIL"
        return 1
    fi

    log "Running unit suite..."
    if run_and_tee "$UNIT_LOG" dotnet test "$SOLUTION_FILE" --no-restore --filter "Harness!=Scenario"; then
        UNIT_STATUS="PASS"
        log "Unit suite passed."
        return 0
    fi

    UNIT_STATUS="FAIL"
    log "Unit suite failed."
    return 1
}

run_scenario_mode() {
    : > "$SCENARIO_LOG"
    if ! ensure_build; then
        SCENARIO_STATUS="FAIL"
        return 1
    fi

    log "Running deterministic scenario suite..."
    if run_and_tee "$SCENARIO_LOG" dotnet test "$SOLUTION_FILE" --no-restore --filter "Harness=Scenario"; then
        SCENARIO_STATUS="PASS"
        log "Scenario suite passed."
        return 0
    fi

    SCENARIO_STATUS="FAIL"
    log "Scenario suite failed."
    return 1
}

run_live_smoke() {
    local output_dir="$DOTFILES_DIR/output/qa-smoke"
    local haiku_file="$output_dir/logs/smoke/HAIKU-1.md"
    local steer_text="Include the exact word STEERED somewhere in every user-visible artifact while still completing the task."

    log_live ""
    log_live "Running live smoke + steer check..."
    rm -rf "$output_dir"
    rm -rf "$SOULCASTER_DIR/logs/smoke"

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-smoke.dot" --steer-text "$steer_text"; then
        LIVE_SMOKE_STATUS="FAIL"
        log_live "Smoke pipeline exited non-zero."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/result.json" ]]; then
        LIVE_SMOKE_STATUS="FAIL"
        log_live "Smoke result.json missing."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/result.json")" != "success" ]]; then
        LIVE_SMOKE_STATUS="FAIL"
        log_live "Smoke result status was not success."
        return 1
    fi

    if [[ ! -f "$haiku_file" ]]; then
        LIVE_SMOKE_STATUS="FAIL"
        log_live "Smoke artifact missing: $haiku_file"
        return 1
    fi

    if ! rg -q "STEERED" "$haiku_file"; then
        LIVE_SMOKE_STATUS="FAIL"
        log_live "Smoke artifact did not reflect steer text."
        return 1
    fi

    LIVE_SMOKE_STATUS="PASS"
    log_live "Smoke + steer check passed."
}

run_live_checkpoint() {
    local output_dir="$DOTFILES_DIR/output/qa-checkpoint"
    local checkpoint_file="$output_dir/logs/checkpoint.json"
    local pipeline_pid=""

    log_live ""
    log_live "Running live checkpoint + resume check..."
    rm -rf "$output_dir"
    rm -rf "$SOULCASTER_DIR/logs/checkpoint_test"

    set +e
    dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-checkpoint.dot" \
        > >(tee -a "$LIVE_LOG") 2> >(tee -a "$LIVE_LOG" >&2) &
    pipeline_pid=$!
    set -e

    local killed=false
    local waited=0
    while [[ $waited -lt 180 ]]; do
        if ! kill -0 "$pipeline_pid" 2>/dev/null; then
            break
        fi

        if [[ -f "$checkpoint_file" ]]; then
            local completed
            completed="$(checkpoint_completed_count "$checkpoint_file" 2>/dev/null || echo 0)"
            if [[ "$completed" -ge 3 ]]; then
                kill "$pipeline_pid" 2>/dev/null || true
                wait "$pipeline_pid" 2>/dev/null || true
                killed=true
                break
            fi
        fi

        sleep 2
        waited=$((waited + 2))
    done

    if [[ "$killed" != "true" ]]; then
        LIVE_CHECKPOINT_STATUS="FAIL"
        log_live "Checkpoint run did not pause in a resumable state."
        return 1
    fi

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-checkpoint.dot" --resume-from "$output_dir" --resume; then
        LIVE_CHECKPOINT_STATUS="FAIL"
        log_live "Checkpoint resume exited non-zero."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/result.json" ]]; then
        LIVE_CHECKPOINT_STATUS="FAIL"
        log_live "Checkpoint result.json missing after resume."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/result.json")" != "success" ]]; then
        LIVE_CHECKPOINT_STATUS="FAIL"
        log_live "Checkpoint result status was not success."
        return 1
    fi

    if ! checkpoint_has_nodes "$checkpoint_file" "step_a,step_b,step_c,step_d" >/dev/null 2>&1; then
        LIVE_CHECKPOINT_STATUS="FAIL"
        log_live "Checkpoint file did not contain all expected completed nodes."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/checkpoint_test/STEP-D.md" ]]; then
        LIVE_CHECKPOINT_STATUS="FAIL"
        log_live "Final checkpoint artifact missing."
        return 1
    fi

    LIVE_CHECKPOINT_STATUS="PASS"
    log_live "Checkpoint + resume check passed."
}

run_live_parallel() {
    local output_dir="$DOTFILES_DIR/output/qa-parallel-multihop"

    log_live ""
    log_live "Running live parallel multi-hop check..."
    rm -rf "$output_dir"
    rm -rf "$SOULCASTER_DIR/logs/parallel_test"

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-parallel-multihop.dot"; then
        LIVE_PARALLEL_STATUS="FAIL"
        log_live "Parallel multi-hop pipeline exited non-zero."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/result.json" ]]; then
        LIVE_PARALLEL_STATUS="FAIL"
        log_live "Parallel result.json missing."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/result.json")" != "success" ]]; then
        LIVE_PARALLEL_STATUS="FAIL"
        log_live "Parallel result status was not success."
        return 1
    fi

    for artifact in \
        "$output_dir/logs/parallel_test/BRANCH-A-STEP1.md" \
        "$output_dir/logs/parallel_test/BRANCH-A-STEP2.md" \
        "$output_dir/logs/parallel_test/BRANCH-B.md"; do
        if [[ ! -f "$artifact" ]]; then
            LIVE_PARALLEL_STATUS="FAIL"
            log_live "Parallel artifact missing: $artifact"
            return 1
        fi
    done

    if [[ ! -f "$output_dir/logs/merge/status.json" ]]; then
        LIVE_PARALLEL_STATUS="FAIL"
        log_live "Fan-in status artifact missing."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/merge/status.json")" != "success" ]]; then
        LIVE_PARALLEL_STATUS="FAIL"
        log_live "Fan-in node did not finish successfully."
        return 1
    fi

    LIVE_PARALLEL_STATUS="PASS"
    log_live "Parallel multi-hop check passed."
}

run_live_queue() {
    local output_dir="$DOTFILES_DIR/output/qa-queue-parallel"
    local alpha_stage="$output_dir/logs/worker[alpha.txt]"
    local beta_stage="$output_dir/logs/worker[beta.txt]"
    local gamma_stage="$output_dir/logs/worker[gamma.txt]"

    log_live ""
    log_live "Running live queue parallelism check..."
    rm -rf "$output_dir"
    rm -rf "$output_dir/logs/queue_inputs" "$output_dir/logs/queue_outputs"

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-queue-parallel.dot"; then
        LIVE_QUEUE_STATUS="FAIL"
        log_live "Queue parallel pipeline exited non-zero."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/result.json" ]]; then
        LIVE_QUEUE_STATUS="FAIL"
        log_live "Queue result.json missing."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/result.json")" != "success" ]]; then
        LIVE_QUEUE_STATUS="FAIL"
        log_live "Queue result status was not success."
        return 1
    fi

    if ! python3 - <<'PY' "$output_dir/logs/result.json"
import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as handle:
    payload = json.load(handle)

completed = set(payload.get("completed_nodes", []))
required = {"worker[alpha.txt]", "worker[beta.txt]", "worker[gamma.txt]"}
if not required.issubset(completed):
    raise SystemExit(1)
PY
    then
        LIVE_QUEUE_STATUS="FAIL"
        log_live "Queue result.json did not record per-item worker stage completions."
        return 1
    fi

    for artifact in \
        "$output_dir/logs/queue_outputs/alpha.txt.done.md" \
        "$output_dir/logs/queue_outputs/beta.txt.done.md" \
        "$output_dir/logs/queue_outputs/gamma.txt.done.md"; do
        if [[ ! -f "$artifact" ]]; then
            LIVE_QUEUE_STATUS="FAIL"
            log_live "Queue artifact missing: $artifact"
            return 1
        fi
        if ! rg -q "^Processed " "$artifact"; then
            LIVE_QUEUE_STATUS="FAIL"
            log_live "Queue artifact did not contain the expected marker: $artifact"
            return 1
        fi
    done

    if [[ ! -f "$output_dir/logs/merge/status.json" ]]; then
        LIVE_QUEUE_STATUS="FAIL"
        log_live "Queue fan-in status artifact missing."
        return 1
    fi

    for stage_dir in "$alpha_stage" "$beta_stage" "$gamma_stage"; do
        if [[ ! -f "$stage_dir/prompt.md" ]]; then
            LIVE_QUEUE_STATUS="FAIL"
            log_live "Queue worker stage artifact missing: $stage_dir/prompt.md"
            return 1
        fi
    done

    LIVE_QUEUE_STATUS="PASS"
    log_live "Queue parallelism check passed."
}

run_live_supervisor() {
    local output_dir="$DOTFILES_DIR/output/qa-supervisor"
    local manager_cycle="$output_dir/logs/manager/cycle-1.md"

    log_live ""
    log_live "Running live telemetry supervisor check..."
    rm -rf "$output_dir"

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-supervisor.dot"; then
        LIVE_SUPERVISOR_STATUS="FAIL"
        log_live "Supervisor pipeline exited non-zero."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/result.json" ]]; then
        LIVE_SUPERVISOR_STATUS="FAIL"
        log_live "Supervisor result.json missing."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/result.json")" != "success" ]]; then
        LIVE_SUPERVISOR_STATUS="FAIL"
        log_live "Supervisor result status was not success."
        return 1
    fi

    if [[ ! -f "$manager_cycle" ]]; then
        LIVE_SUPERVISOR_STATUS="FAIL"
        log_live "Supervisor steering artifact missing: $manager_cycle"
        return 1
    fi

    if [[ ! -f "$output_dir/logs/manager/status.json" ]]; then
        LIVE_SUPERVISOR_STATUS="FAIL"
        log_live "Supervisor status.json missing."
        return 1
    fi

    if [[ "$(json_status "$output_dir/logs/manager/status.json")" != "success" ]]; then
        LIVE_SUPERVISOR_STATUS="FAIL"
        log_live "Supervisor manager node did not finish with success status."
        return 1
    fi

    LIVE_SUPERVISOR_STATUS="PASS"
    log_live "Telemetry supervisor check passed."
}

run_live_builder() {
    local work_dir="$RESULTS_DIR/builder-live"
    local dot_file="$work_dir/builder-generated.dot"
    local output_dir="$work_dir/output/builder-generated"

    log_live ""
    log_live "Running live builder/editor workflow check..."
    rm -rf "$work_dir"
    mkdir -p "$work_dir"

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- builder init "$dot_file" --name builder_generated --goal "Exercise the builder/editor workflow"; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Builder init failed."
        return 1
    fi

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- builder node "$dot_file" write --shape box --label "Write Artifact" --prompt "Write 'Builder workflow complete' to logs/builder/BUILDER-LIVE.md. That is all."; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Builder node edit failed."
        return 1
    fi

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- builder edge "$dot_file" start write; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Builder edge start->write failed."
        return 1
    fi

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- builder edge "$dot_file" write done; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Builder edge write->done failed."
        return 1
    fi

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- lint "$dot_file"; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Generated builder DOT did not lint cleanly."
        return 1
    fi

    if ! run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- run "$dot_file"; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Generated builder DOT did not run successfully."
        return 1
    fi

    if [[ ! -f "$output_dir/logs/builder/BUILDER-LIVE.md" ]]; then
        LIVE_BUILDER_STATUS="FAIL"
        log_live "Builder live artifact missing."
        return 1
    fi

    LIVE_BUILDER_STATUS="PASS"
    log_live "Builder/editor workflow check passed."
}

run_live_lint() {
    log_live ""
    log_live "Running live lint check..."

    if run_and_tee "$LIVE_LOG" dotnet run --project "$SOULCASTER_DIR/runner" -- lint "$DOTFILES_DIR/project-cli-task-tracker.dot"; then
        LIVE_LINT_STATUS="PASS"
        log_live "Lint check passed."
        return 0
    fi

    LIVE_LINT_STATUS="FAIL"
    log_live "Lint check failed."
    return 1
}

run_live_mode() {
    : > "$LIVE_LOG"
    if ! ensure_build; then
        LIVE_STATUS="FAIL"
        return 1
    fi

    if ! require_anthropic_key; then
        LIVE_STATUS="FAIL"
        return 1
    fi

    if [[ "$SKIP_QA_AGENT" == "false" && -d "$QA_AGENT_DIR" ]]; then
        log_live "qa-agent integration is available but not required for this harness pass."
    fi

    local overall=true

    if should_run_live smoke; then
        run_live_smoke || overall=false
    fi

    if should_run_live checkpoint; then
        run_live_checkpoint || overall=false
    fi

    if should_run_live parallel; then
        run_live_parallel || overall=false
    fi

    if should_run_live queue; then
        run_live_queue || overall=false
    fi

    if should_run_live supervisor; then
        run_live_supervisor || overall=false
    fi

    if should_run_live builder; then
        run_live_builder || overall=false
    fi

    if should_run_live lint; then
        run_live_lint || overall=false
    fi

    if [[ "$overall" == "true" ]]; then
        LIVE_STATUS="PASS"
        return 0
    fi

    LIVE_STATUS="FAIL"
    return 1
}

log "Results directory: $RESULTS_DIR"

HARNESS_EXIT=0

if [[ "$MODE_UNIT" == "true" ]]; then
    run_unit_mode || HARNESS_EXIT=1
fi

if [[ "$MODE_SCENARIO" == "true" ]]; then
    run_scenario_mode || HARNESS_EXIT=1
fi

if [[ "$MODE_LIVE" == "true" ]]; then
    run_live_mode || HARNESS_EXIT=1
fi

export RESULTS_DIR UNIT_STATUS SCENARIO_STATUS LIVE_STATUS
export LIVE_SMOKE_STATUS LIVE_CHECKPOINT_STATUS LIVE_PARALLEL_STATUS LIVE_QUEUE_STATUS LIVE_SUPERVISOR_STATUS LIVE_BUILDER_STATUS LIVE_LINT_STATUS
write_summary_json

log ""
log "Summary:"
log "  unit:     $UNIT_STATUS"
log "  scenario: $SCENARIO_STATUS"
log "  live:     $LIVE_STATUS"
if [[ "$MODE_LIVE" == "true" ]]; then
    log "  smoke:    $LIVE_SMOKE_STATUS"
    log "  checkpoint: $LIVE_CHECKPOINT_STATUS"
    log "  parallel: $LIVE_PARALLEL_STATUS"
    log "  queue:    $LIVE_QUEUE_STATUS"
    log "  supervisor: $LIVE_SUPERVISOR_STATUS"
    log "  builder:  $LIVE_BUILDER_STATUS"
    log "  lint:     $LIVE_LINT_STATUS"
fi
log "  summary:  $SUMMARY_JSON"

exit "$HARNESS_EXIT"
