#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════════════════════════════════
#  Soulcaster QA Harness
#  Runs all 4 non-automated QA scenarios using qa-agent for validation.
#
#  Usage:
#    ./scripts/qa-harness.sh                  # Run all scenarios
#    ./scripts/qa-harness.sh --scenario 1     # Run only scenario 1
#    ./scripts/qa-harness.sh --skip-qa-agent  # Run pipelines only, skip qa-agent
#
#  Prerequisites:
#    - ANTHROPIC_API_KEY set (required)
#    - OPENAI_API_KEY set (required)
#    - GEMINI_API_KEY set (optional — scenario 4 skipped without it)
#    - Go installed (for qa-agent)
#    - .NET 10 SDK installed
# ═══════════════════════════════════════════════════════════════════════

SOULCASTER_DIR="${SOULCASTER_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
QA_AGENT_DIR="${QA_AGENT_DIR:-$HOME/qa-agent}"
DOTFILES_DIR="$SOULCASTER_DIR/dotfiles"
WEB_PORT="${WEB_PORT:-5099}"
RESULTS_DIR="$SOULCASTER_DIR/qa-results/$(date +%Y%m%d-%H%M%S)"
ONLY_SCENARIO=""
SKIP_QA_AGENT=false

# Parse flags
while [[ $# -gt 0 ]]; do
    case "$1" in
        --scenario) ONLY_SCENARIO="$2"; shift 2 ;;
        --skip-qa-agent) SKIP_QA_AGENT=true; shift ;;
        *) echo "Unknown flag: $1"; exit 1 ;;
    esac
done

mkdir -p "$RESULTS_DIR"

# ─── Helpers ──────────────────────────────────────────────────────────

log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$RESULTS_DIR/harness.log"; }

should_run() {
    [[ -z "$ONLY_SCENARIO" ]] || [[ "$ONLY_SCENARIO" == "$1" ]]
}

WEB_PID=""
PIPELINE_PID=""

cleanup() {
    log "Cleaning up..."
    [[ -n "$WEB_PID" ]] && kill "$WEB_PID" 2>/dev/null && wait "$WEB_PID" 2>/dev/null || true
    [[ -n "$PIPELINE_PID" ]] && kill "$PIPELINE_PID" 2>/dev/null && wait "$PIPELINE_PID" 2>/dev/null || true
}
trap cleanup EXIT

run_qa_agent() {
    local scenario_name="$1"
    local feature="$2"
    local budget_steps="${3:-50}"
    local budget_minutes="${4:-5}"

    if [[ "$SKIP_QA_AGENT" == "true" ]]; then
        log "  [skip] qa-agent skipped (--skip-qa-agent)"
        return 0
    fi

    if [[ ! -d "$QA_AGENT_DIR" ]]; then
        log "  [skip] qa-agent not found at $QA_AGENT_DIR"
        return 0
    fi

    local qa_out="$RESULTS_DIR/qa-agent-$scenario_name"
    mkdir -p "$qa_out"

    log "  Running qa-agent for $scenario_name..."
    (
        cd "$QA_AGENT_DIR"
        go run ./cmd/qa-agent run \
            --feature "$feature" \
            --surfaces api \
            --budget-steps "$budget_steps" \
            --budget-minutes "$budget_minutes" \
            --output-dir "$qa_out" \
            2>&1
    ) | tee "$RESULTS_DIR/${scenario_name}-qa-agent.log" || true

    # Generate report if run completed
    local run_id
    run_id=$(ls -1 "$qa_out" 2>/dev/null | grep "^run_" | head -1 || true)
    if [[ -n "$run_id" ]]; then
        (
            cd "$QA_AGENT_DIR"
            go run ./cmd/qa-agent report \
                --run-id "$run_id" \
                --output-dir "$qa_out" 2>&1
        ) || true
        log "  qa-agent report: $qa_out/$run_id/report.md"
    fi
}

run_pipeline() {
    local dotfile="$1"
    local log_name="$2"

    log "  Running $dotfile..."
    dotnet run --project "$SOULCASTER_DIR/runner" -- run "$dotfile" \
        2>&1 | tee "$RESULTS_DIR/${log_name}.log"
}

check_file() {
    local path="$1"
    local desc="$2"
    if [[ -f "$path" ]]; then
        log "  [ok] $desc"
        return 0
    else
        log "  [FAIL] $desc — file missing: $path"
        return 1
    fi
}

# ─── Phase 0: Build ──────────────────────────────────────────────────

log "═══ Phase 0: Build ═══"
cd "$SOULCASTER_DIR"
dotnet build 2>&1 | tee "$RESULTS_DIR/build.log" || true
# Check for real compilation errors (not MSBUILD diagnostic lines)
if grep -q ": error CS" "$RESULTS_DIR/build.log"; then
    log "ERROR: Build has compilation errors."
    exit 1
fi
log "Build complete."

# Check API keys
if [[ -z "${ANTHROPIC_API_KEY:-}" ]]; then
    log "ERROR: ANTHROPIC_API_KEY is not set. Required for QA scenarios."
    exit 1
fi
if [[ -z "${OPENAI_API_KEY:-}" ]]; then
    log "ERROR: OPENAI_API_KEY is not set. Required for QA scenarios."
    exit 1
fi

if [[ -z "${GEMINI_API_KEY:-}" ]]; then
    log "WARNING: GEMINI_API_KEY not set — scenario 4 (multi-model) will be skipped."
    SKIP_MULTIMODEL=true
else
    SKIP_MULTIMODEL=false
fi

# Track pass/fail
RESULT_S1="SKIP"
RESULT_S2="SKIP"
RESULT_S3="SKIP"
RESULT_S4="SKIP"

# ═══════════════════════════════════════════════════════════════════════
#  Scenario 1: Real LLM Calls (Smoke Test)
# ═══════════════════════════════════════════════════════════════════════
if should_run 1; then
    log ""
    log "═══ Scenario 1: Real LLM Calls ═══"

    # Clean prior output
    rm -rf "$DOTFILES_DIR/output/qa-smoke"

    # Run the smoke test pipeline
    if run_pipeline "$DOTFILES_DIR/qa-smoke.dot" "scenario1-run"; then
        log "  Pipeline exited 0."
    else
        log "  Pipeline exited non-zero."
    fi

    # Verify artifacts
    S1_PASS=true
    OUTPUT_DIR="$DOTFILES_DIR/output/qa-smoke"

    check_file "$OUTPUT_DIR/logs/checkpoint.json" "checkpoint.json exists" || S1_PASS=false
    check_file "$OUTPUT_DIR/logs/result.json" "result.json exists" || S1_PASS=false
    check_file "$OUTPUT_DIR/logs/write_haiku/status.json" "write_haiku/status.json exists" || S1_PASS=false
    check_file "$OUTPUT_DIR/logs/verify_haiku/status.json" "verify_haiku/status.json exists" || S1_PASS=false
    check_file "$OUTPUT_DIR/logs/write_haiku/response.md" "write_haiku/response.md exists" || S1_PASS=false

    # Check response is non-empty
    if [[ -f "$OUTPUT_DIR/logs/write_haiku/response.md" ]]; then
        RESP_SIZE=$(wc -c < "$OUTPUT_DIR/logs/write_haiku/response.md" | tr -d ' ')
        if [[ "$RESP_SIZE" -gt 10 ]]; then
            log "  [ok] LLM response is non-empty ($RESP_SIZE bytes)"
        else
            log "  [FAIL] LLM response is too short ($RESP_SIZE bytes)"
            S1_PASS=false
        fi
    fi

    # Check checkpoint has Timestamp
    if [[ -f "$OUTPUT_DIR/logs/checkpoint.json" ]]; then
        if python3 -c "import json,sys; cp=json.load(open(sys.argv[1])); assert cp.get('Timestamp')" "$OUTPUT_DIR/logs/checkpoint.json" 2>/dev/null; then
            log "  [ok] checkpoint has Timestamp"
        else
            log "  [FAIL] checkpoint missing Timestamp"
            S1_PASS=false
        fi
    fi

    if [[ "$S1_PASS" == "true" ]]; then
        RESULT_S1="PASS"
        log "  Scenario 1 pre-checks: PASS"
    else
        RESULT_S1="FAIL"
        log "  Scenario 1 pre-checks: FAIL"
    fi

    # Start web dashboard for qa-agent and subsequent scenarios
    log "  Starting web dashboard on port $WEB_PORT..."
    dotnet run --project "$SOULCASTER_DIR/runner" -- web --port "$WEB_PORT" > "$RESULTS_DIR/web-dashboard.log" 2>&1 &
    WEB_PID=$!
    sleep 3

    if curl -sf "http://localhost:$WEB_PORT/api/pipelines" > /dev/null 2>&1; then
        log "  [ok] Web dashboard is up."
    else
        log "  [FAIL] Web dashboard failed to start. Continuing without qa-agent."
        WEB_PID=""
    fi

    if [[ -n "$WEB_PID" ]]; then
        run_qa_agent "scenario1" \
            "The soulcaster pipeline runner completed a smoke test. GET http://localhost:$WEB_PORT/api/pipelines returns a JSON array with status 200 containing at least one entry. GET http://localhost:$WEB_PORT/api/pipeline/{id}/status for the smoke pipeline returns JSON with a status field equal to completed and a nodes array where each node object has a status field equal to done." \
            50 5
    fi
else
    log "Scenario 1: SKIPPED"
    RESULT_S1="SKIP"
fi

# ═══════════════════════════════════════════════════════════════════════
#  Scenario 2: Resume from Checkpoint
# ═══════════════════════════════════════════════════════════════════════
if should_run 2; then
    log ""
    log "═══ Scenario 2: Resume from Checkpoint ═══"

    rm -rf "$DOTFILES_DIR/output/qa-checkpoint"
    OUTPUT_DIR="$DOTFILES_DIR/output/qa-checkpoint"
    CHECKPOINT_FILE="$OUTPUT_DIR/logs/checkpoint.json"

    # Run in background
    log "  Starting qa-checkpoint.dot (will kill mid-execution)..."
    dotnet run --project "$SOULCASTER_DIR/runner" -- run "$DOTFILES_DIR/qa-checkpoint.dot" \
        > "$RESULTS_DIR/scenario2-initial.log" 2>&1 &
    PIPELINE_PID=$!

    # Poll checkpoint until at least start + step_a + step_b are completed
    WAIT_MAX=180
    WAIT_COUNT=0
    KILLED=false
    while [[ $WAIT_COUNT -lt $WAIT_MAX ]]; do
        # Check if process already finished
        if ! kill -0 "$PIPELINE_PID" 2>/dev/null; then
            log "  Pipeline finished before kill signal."
            break
        fi

        if [[ -f "$CHECKPOINT_FILE" ]]; then
            COMPLETED=$(python3 -c "
import json, sys
try:
    cp = json.load(open(sys.argv[1]))
    print(len(cp.get('CompletedNodes', [])))
except: print(0)
" "$CHECKPOINT_FILE" 2>/dev/null || echo 0)
            if [[ "$COMPLETED" -ge 3 ]]; then
                log "  $COMPLETED nodes completed. Killing pipeline."
                kill "$PIPELINE_PID" 2>/dev/null || true
                wait "$PIPELINE_PID" 2>/dev/null || true
                KILLED=true
                break
            fi
        fi
        sleep 2
        WAIT_COUNT=$((WAIT_COUNT + 2))
    done
    PIPELINE_PID=""

    S2_PASS=true

    if [[ "$KILLED" == "true" ]]; then
        # Save pre-resume checkpoint
        cp "$CHECKPOINT_FILE" "$RESULTS_DIR/checkpoint-before-resume.json"
        PRE_NODES=$(python3 -c "
import json, sys
cp = json.load(open(sys.argv[1]))
print(','.join(cp.get('CompletedNodes', [])))
" "$RESULTS_DIR/checkpoint-before-resume.json" 2>/dev/null || echo "unknown")
        log "  Pre-resume completed nodes: $PRE_NODES"

        # Verify result.json does NOT exist (pipeline was killed before finishing)
        if [[ -f "$OUTPUT_DIR/logs/result.json" ]]; then
            log "  [WARN] result.json exists before resume — pipeline may have finished before kill"
        fi

        # Resume
        log "  Resuming pipeline..."
        run_pipeline "$DOTFILES_DIR/qa-checkpoint.dot" "scenario2-resume"

        # Verify completion
        check_file "$OUTPUT_DIR/logs/result.json" "result.json exists after resume" || S2_PASS=false

        # Verify all 4 steps in CompletedNodes
        if [[ -f "$CHECKPOINT_FILE" ]]; then
            POST_NODES=$(python3 -c "
import json, sys
cp = json.load(open(sys.argv[1]))
nodes = cp.get('CompletedNodes', [])
for n in ['step_a', 'step_b', 'step_c', 'step_d']:
    if n not in nodes:
        print(f'MISSING:{n}')
        sys.exit(1)
print('ALL_PRESENT')
" "$CHECKPOINT_FILE" 2>/dev/null || echo "ERROR")

            if [[ "$POST_NODES" == "ALL_PRESENT" ]]; then
                log "  [ok] All 4 step nodes in CompletedNodes after resume"
            else
                log "  [FAIL] $POST_NODES"
                S2_PASS=false
            fi
        fi

        # Verify post-resume timestamp is newer
        if [[ -f "$RESULTS_DIR/checkpoint-before-resume.json" ]] && [[ -f "$CHECKPOINT_FILE" ]]; then
            TIMESTAMP_CHECK=$(python3 -c "
import json, sys
pre = json.load(open(sys.argv[1]))
post = json.load(open(sys.argv[2]))
pre_ts = pre.get('Timestamp', '')
post_ts = post.get('Timestamp', '')
if post_ts > pre_ts:
    print('NEWER')
else:
    print(f'NOT_NEWER:{pre_ts}:{post_ts}')
" "$RESULTS_DIR/checkpoint-before-resume.json" "$CHECKPOINT_FILE" 2>/dev/null || echo "ERROR")

            if [[ "$TIMESTAMP_CHECK" == "NEWER" ]]; then
                log "  [ok] Post-resume checkpoint timestamp is newer"
            else
                log "  [WARN] $TIMESTAMP_CHECK"
            fi
        fi
    else
        log "  [FAIL] Could not kill pipeline mid-execution (timed out or finished too fast)"
        S2_PASS=false
    fi

    if [[ "$S2_PASS" == "true" ]]; then
        RESULT_S2="PASS"
        log "  Scenario 2 pre-checks: PASS"
    else
        RESULT_S2="FAIL"
        log "  Scenario 2 pre-checks: FAIL"
    fi

    # qa-agent verification
    if [[ -n "$WEB_PID" ]]; then
        run_qa_agent "scenario2" \
            "A soulcaster pipeline was interrupted and resumed from checkpoint. GET http://localhost:$WEB_PORT/api/pipelines returns JSON with status 200. GET http://localhost:$WEB_PORT/api/pipeline/{id}/status for the qa-checkpoint pipeline returns JSON with status completed and a nodes array containing step_a and step_b and step_c and step_d all with status done." \
            50 5
    fi
else
    log "Scenario 2: SKIPPED"
    RESULT_S2="SKIP"
fi

# ═══════════════════════════════════════════════════════════════════════
#  Scenario 3: Web Dashboard
# ═══════════════════════════════════════════════════════════════════════
if should_run 3; then
    log ""
    log "═══ Scenario 3: Web Dashboard ═══"

    S3_PASS=true

    if [[ -z "$WEB_PID" ]]; then
        # Start dashboard if not already running
        log "  Starting web dashboard on port $WEB_PORT..."
        dotnet run --project "$SOULCASTER_DIR/runner" -- web --port "$WEB_PORT" > "$RESULTS_DIR/web-dashboard.log" 2>&1 &
        WEB_PID=$!
        sleep 3
    fi

    # Direct HTTP checks
    for ENDPOINT in "/" "/api/pipelines" "/api/queue"; do
        HTTP_CODE=$(curl -sf -o /dev/null -w "%{http_code}" "http://localhost:$WEB_PORT$ENDPOINT" 2>/dev/null || echo "000")
        if [[ "$HTTP_CODE" == "200" ]]; then
            log "  [ok] GET $ENDPOINT → $HTTP_CODE"
        else
            log "  [FAIL] GET $ENDPOINT → $HTTP_CODE"
            S3_PASS=false
        fi
    done

    # Check /api/pipelines returns valid JSON array
    PIPELINES_BODY=$(curl -sf "http://localhost:$WEB_PORT/api/pipelines" 2>/dev/null || echo "")
    if echo "$PIPELINES_BODY" | python3 -c "import json,sys; data=json.load(sys.stdin); assert isinstance(data, list)" 2>/dev/null; then
        log "  [ok] /api/pipelines returns JSON array"

        # Get first pipeline ID and check /api/pipeline/{id}/status
        FIRST_ID=$(echo "$PIPELINES_BODY" | python3 -c "
import json, sys
data = json.load(sys.stdin)
if data: print(data[0].get('id', ''))
else: print('')
" 2>/dev/null || echo "")

        if [[ -n "$FIRST_ID" ]]; then
            for SUB_ENDPOINT in "status" "summaries" "logs"; do
                HTTP_CODE=$(curl -sf -o /dev/null -w "%{http_code}" "http://localhost:$WEB_PORT/api/pipeline/$FIRST_ID/$SUB_ENDPOINT" 2>/dev/null || echo "000")
                if [[ "$HTTP_CODE" == "200" ]]; then
                    log "  [ok] GET /api/pipeline/{id}/$SUB_ENDPOINT → $HTTP_CODE"
                else
                    log "  [FAIL] GET /api/pipeline/{id}/$SUB_ENDPOINT → $HTTP_CODE"
                    S3_PASS=false
                fi
            done
        else
            log "  [WARN] No pipeline ID found to test sub-endpoints"
        fi
    else
        log "  [FAIL] /api/pipelines did not return valid JSON array"
        S3_PASS=false
    fi

    if [[ "$S3_PASS" == "true" ]]; then
        RESULT_S3="PASS"
        log "  Scenario 3 pre-checks: PASS"
    else
        RESULT_S3="FAIL"
        log "  Scenario 3 pre-checks: FAIL"
    fi

    run_qa_agent "scenario3" \
        "The soulcaster web dashboard is at http://localhost:$WEB_PORT. GET / returns HTML with status 200. GET /api/pipelines returns a JSON array with status 200. GET /api/queue returns a JSON array with status 200. For a pipeline from /api/pipelines, GET /api/pipeline/{id}/status returns JSON with status 200 containing status and nodes fields. GET /api/pipeline/{id}/summaries returns JSON with status 200. GET /api/pipeline/{id}/logs returns a JSON array with status 200." \
        100 10
else
    log "Scenario 3: SKIPPED"
    RESULT_S3="SKIP"
fi

# ═══════════════════════════════════════════════════════════════════════
#  Scenario 4: Multi-Model Pipeline
# ═══════════════════════════════════════════════════════════════════════
if should_run 4; then
    if [[ "$SKIP_MULTIMODEL" == "true" ]]; then
        log ""
        log "═══ Scenario 4: SKIPPED (GEMINI_API_KEY not set) ═══"
        RESULT_S4="SKIP"
    else
        log ""
        log "═══ Scenario 4: Multi-Model Pipeline ═══"

        rm -rf "$DOTFILES_DIR/output/qa-multimodel"
        OUTPUT_DIR="$DOTFILES_DIR/output/qa-multimodel"

        run_pipeline "$DOTFILES_DIR/qa-multimodel.dot" "scenario4-run"

        S4_PASS=true

        check_file "$OUTPUT_DIR/logs/result.json" "result.json exists" || S4_PASS=false

        # Check each provider node
        for NODE in anthropic_node openai_node gemini_node; do
            check_file "$OUTPUT_DIR/logs/$NODE/status.json" "$NODE/status.json exists" || S4_PASS=false

            if [[ -f "$OUTPUT_DIR/logs/$NODE/status.json" ]]; then
                NODE_STATUS=$(python3 -c "
import json, sys
doc = json.load(open(sys.argv[1]))
print(doc.get('status', 'unknown'))
" "$OUTPUT_DIR/logs/$NODE/status.json" 2>/dev/null || echo "unknown")

                if [[ "$NODE_STATUS" == "success" ]]; then
                    log "  [ok] $NODE status: $NODE_STATUS"
                else
                    log "  [FAIL] $NODE status: $NODE_STATUS (expected: success)"
                    S4_PASS=false
                fi

                # Check provider field
                NODE_PROVIDER=$(python3 -c "
import json, sys
doc = json.load(open(sys.argv[1]))
print(doc.get('provider', 'unknown') or 'null')
" "$OUTPUT_DIR/logs/$NODE/status.json" 2>/dev/null || echo "unknown")
                log "  [info] $NODE provider: $NODE_PROVIDER"
            fi

            check_file "$OUTPUT_DIR/logs/$NODE/response.md" "$NODE/response.md exists" || S4_PASS=false
        done

        if [[ "$S4_PASS" == "true" ]]; then
            RESULT_S4="PASS"
            log "  Scenario 4 pre-checks: PASS"
        else
            RESULT_S4="FAIL"
            log "  Scenario 4 pre-checks: FAIL"
        fi

        if [[ -n "$WEB_PID" ]]; then
            run_qa_agent "scenario4" \
                "A multi-model soulcaster pipeline completed. GET http://localhost:$WEB_PORT/api/pipelines returns JSON with status 200. GET http://localhost:$WEB_PORT/api/pipeline/{id}/status for the qa-multimodel pipeline returns JSON with status completed and nodes array containing anthropic_node and openai_node and gemini_node all with status done." \
                50 5
        fi
    fi
else
    log "Scenario 4: SKIPPED"
    RESULT_S4="SKIP"
fi

# ═══════════════════════════════════════════════════════════════════════
#  Summary
# ═══════════════════════════════════════════════════════════════════════
log ""
log "═══════════════════════════════════════"
log "  QA Harness Summary"
log "═══════════════════════════════════════"

OVERALL_PASS=true
for PAIR in "scenario1:$RESULT_S1" "scenario2:$RESULT_S2" "scenario3:$RESULT_S3" "scenario4:$RESULT_S4"; do
    SCENARIO="${PAIR%%:*}"
    STATUS="${PAIR#*:}"
    case "$STATUS" in
        PASS) ICON="+" ;;
        FAIL) ICON="x"; OVERALL_PASS=false ;;
        SKIP) ICON="-" ;;
        *) ICON="?"; OVERALL_PASS=false ;;
    esac
    log "  [$ICON] $SCENARIO: $STATUS"
done

log ""
log "  Results directory: $RESULTS_DIR"
log ""

# List qa-agent reports
for d in "$RESULTS_DIR"/qa-agent-scenario*; do
    if [[ -d "$d" ]]; then
        REPORT=$(find "$d" -name "report.md" 2>/dev/null | head -1)
        if [[ -n "$REPORT" ]]; then
            log "  Report: $REPORT"
        fi
    fi
done

log ""
if [[ "$OVERALL_PASS" == "true" ]]; then
    log "  Overall: ALL PASSED"
    exit 0
else
    log "  Overall: SOME FAILED"
    exit 1
fi
