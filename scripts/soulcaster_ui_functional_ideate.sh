#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: $0 <repo_root> <overview_screenshot> <detail_screenshot> <state_dir>" >&2
  exit 2
fi

repo_root="$1"
overview_screenshot="$2"
detail_screenshot="$3"
state_dir="$4"
stable_key_dir="${SOULCASTER_PROVIDER_KEY_DIR:-/tmp/soulcaster-provider-keys}"

mkdir -p "$state_dir"

if [ -z "${GEMINI_API_KEY:-}" ] && [ -s "$stable_key_dir/gemini.key" ]; then
  export GEMINI_API_KEY="$(cat "$stable_key_dir/gemini.key")"
fi

research_file="$state_dir/research.md"
attempt_file="$state_dir/attempt.txt"
proposal_file="$state_dir/proposal.md"
feedback_file="$state_dir/feedback.md"
prompt_file="$state_dir/ideate-prompt.txt"

if [ ! -s "$research_file" ]; then
  echo "Missing research brief at $research_file" >&2
  exit 2
fi

attempt=1
if [ -f "$attempt_file" ]; then
  last_attempt="$(cat "$attempt_file" 2>/dev/null || echo 0)"
  if [[ "$last_attempt" =~ ^[0-9]+$ ]]; then
    attempt=$((last_attempt + 1))
  fi
fi
printf '%s\n' "$attempt" >"$attempt_file"

cat >"$prompt_file" <<'EOF'
You are the UI/UX ideation model in a consensus-gated Soulcaster dashboard redesign loop.

You have:
- a functionality and user-journey brief produced from the real codebase
- an overview screenshot of the current dashboard
- a detail-state screenshot of the current dashboard
- prior evaluator feedback, if present

Your task:
1. Produce exactly 3 distinct UI/UX overhaul directions for the Soulcaster web UI.
2. Each direction must reflect the actual product workflow described in the research brief.
3. Make the directions meaningfully different in structure, hierarchy, and visual language.
4. Then recommend one direction and turn it into an implementation-ready brief.

Constraints:
- Preserve the real product model: queue, pipeline registry, pipeline detail drill-down, graph, telemetry, gates, artifacts, markdown viewing, phase summaries, gate history, and human gate answering.
- Keep the eventual implementation centered in runner/Program.cs.
- Use the current API surface rather than inventing a backend rewrite.
- Treat the redesign as an evolution of the current dashboard, not a greenfield rewrite.
- Make the visual identity feel deliberate, premium, and Soulcaster-specific rather than a generic dark admin UI.
- Improve information hierarchy, operator confidence, scanability, attention routing, and overview/detail cohesion.
- Use one decisive mobile fallback.
- Respect any prior evaluator feedback.
- Do not get truncated.

Available API surfaces to map explicitly:
- GET /api/queue
- GET /api/pipelines
- GET /api/pipeline/{id}/status
- GET /api/pipeline/{id}/gates
- GET /api/pipeline/{id}/logs
- GET /api/pipeline/{id}/graph
- GET /api/pipeline/{id}/telemetry
- GET /api/pipeline/{id}/summaries
- POST /api/pipeline/{id}/gates/{gateId}/answer

Important state/field details to account for:
- iteration
- current_iteration
- current_node
- pending_gate
- notes
- preferred_label
- preferred_next_label
- answered gate history
- dotfile
- fail vs retry

Actual current API contract notes:
- `/api/queue` returns a flat mixed array, not grouped buckets.
- Queue `gate` items include: `type`, `pipeline_id`, `pipeline_name`, `gate_id`, `question`, `options`.
- Queue `failed_node` items include: `type`, `pipeline_id`, `pipeline_name`, `node_id`, `status`, `notes`; retry is represented as `type: failed_node` with `status: retry`, not as a separate queue type.
- Queue `running` items include: `type`, `pipeline_id`, `pipeline_name`, `node_id`, `status`, `notes`.
- `/api/pipelines` in single-dir mode returns `{ id, name, status, has_pending_gate }`.
- `/api/pipelines` in global mode returns `{ id, name, dotfile, output_dir, started, status, has_pending_gate }`.
- `/api/pipeline/{id}/status` returns `{ status, current_node, pending_gate, iteration, nodes[] }` where each node is `{ id, status, preferred_label, notes, current_iteration }`.
- `/api/pipeline/{id}/gates` returns `{ gates, phase_summaries }`; gate history lives in `gates`, current-phase artifact previews live in `phase_summaries`.
- `/api/pipeline/{id}/logs` returns per-node file listings; artifact content is opened from `/api/pipeline/{id}/logs/{node}/{file}`.
- `/api/pipeline/{id}/summaries` returns `{ current_task, current_node, nodes }` and is where commit rows / validation summaries come from.
- `/api/pipeline/{id}/graph` returns server-rendered SVG with status coloring baked in.
- `/api/pipeline/{id}/telemetry` returns `{ events, per_node, stage_metrics, totals }`.

Output rules:
- Keep the full response under 2600 words.
- No code fences.
- Do not dump long CSS variable lists. If you define tokens, list at most 12 token names in a single bullet.
- Be decisive: choose one navigation model, one mobile model, one artifact-viewing model.
- Make the Implementation Brief concrete enough that another engineer can land it in DashboardHtml without guessing.
- Avoid contradictions between the recommended direction and the implementation brief.

Return markdown with exactly these top-level sections:

# Soulcaster Dashboard Overhaul Iteration __ATTEMPT__
# Product Readback
# Direction 1
# Direction 2
# Direction 3
# Recommended Direction
# Implementation Brief
# Acceptance Checklist

For each direction include:
- Name
- Operator promise
- Visual direction
- Layout changes
- Interaction model
- Why it fits Soulcaster

Implementation Brief must use exactly these subsection headings in this order:
## 0. Preserve From Current Dashboard
## 1. View Model And Navigation
## 2. Inbox Home View
## 3. Gate Decision View
## 4. Failure And Recovery Card
## 5. Pipeline Detail View
## 6. Sidebar Registry
## 7. Artifact And Markdown Viewing
## 8. API And Data Mapping
## 9. Mobile Behavior
## 10. Soulcaster-Specific Visual Language
## 11. DashboardHtml Landing Plan

Inside the Implementation Brief, be explicit about:
- what stays from the current dashboard: patchHtml/morph DOM diffing, delegated click handlers, renderMarkdown, inline gate flow, atmospheric radial background treatment, node progress bar, commit rows, validation scorecards, completed-run access, and the current API endpoint shapes
- if recommending the split-pane desktop console, keep the left pane visible and use the right pane for context switching; do not drift into a separate full-page desktop detail route
- preserve the queue as a true inbox with pending gates, failed/retry nodes, and running items with current node context
- exact section order for Inbox and Detail views
- click path from inbox to detail and back
- if gate review is the primary action, make it a dedicated route/state rather than a vague sub-panel
- fully specify the dedicated gate route/state layout: question, options, freeform text, revise-requires-text, phase summary previews scoped to the current phase, prior gate history, artifact drill-down, submit/loading/success/error states, and post-submit navigation
- make the gate interaction match the real contract exactly: pending gate is the `gates[]` item with `is_pending`, submissions send `choice` plus optional `text`, answered gates use `answer` / `answer_choice`, and the brief must state the exact rule for when freeform text becomes required
- how phase summaries render inline
- how prior gate history appears
- how choice vs text behaves when answering a gate
- revise-requires-text behavior
- pending, submitting, and answered gate states
- realistic copyable CLI recovery commands using dotfile and node id
- only show fully copyable CLI commands when the required data is actually available from the current API shape; otherwise provide precise guidance text instead of fabricating a command
- graph placement in the detail view
- what compact graph/topology or route context remains visible in Overview so the operator is not forced to tab-hop for basic state comprehension
- choose the final pipeline-detail hierarchy decisively; Overview should keep the graph context and gate-history context visible enough that graph is not buried as an isolated destination
- that the graph endpoint returns server-colored SVG and should live in a scroll container; no external graph library
- iteration and current_iteration rendering
- where iteration count appears, how current vs prior loop passes are shown, and how the UI communicates what changed this iteration
- explicit visual/state rules for gate pending, fail, retry, running, and completed/done across queue items, badges, node rows, and graph context
- a required node-detail area in Overview (or a persistent adjacent area) including node id, status, preferred_label, notes, and artifact links
- explicitly preserve current-task banner, `/summaries` node summaries, commit rows, validation scorecards, phase summary preview chips, markdown modal behavior, artifact file lists, and fail/retry notes
- markdown modal behavior vs ordinary file links
- polling cadence and DOM morphing / textarea preservation
- polling cadence per view/tab, lazy vs eager data loading, and how refresh ownership changes between inbox, gate route, and pipeline detail
- whether gate cards lazy-load /api/pipeline/{id}/gates on expand
- how completed pipelines are accessed or hidden
- how the sidebar inbox summary differs from the full Inbox Home View
- explicit empty/error states for no runs, no pending gates, no failures, no selected pipeline, unavailable graph, and missing artifacts
- a single explicit status-color mapping for gate pending, fail, retry, running, and completed/done that is used consistently across queue cards, badges, graph context, and detail headers
- graph node interaction expectations and whether Overview keeps a mini topology snapshot or route context visible
- how keyboard navigation works for inbox cards, gate options, Escape, and artifact modal focus
- refer to the existing custom DOM morphing implementation accurately; if 1-9 gate shortcuts are added, frame them as a new addition rather than an already-existing behavior
- frame the implementation as an incremental refactor of the current DashboardHtml DOM regions, route handlers, and refresh flow rather than a rewrite
- whether any backend/API changes are needed; prefer none unless unavoidable
- choose one exact mobile control pattern and one exact polling behavior; remove “or” branches

## Research Brief
EOF

perl -0pi -e 's/__ATTEMPT__/'"$attempt"'/g' "$prompt_file"

cat "$research_file" >>"$prompt_file"

if [ -s "$feedback_file" ]; then
  {
    printf '\n## Prior Consensus Feedback To Address\n'
    cat "$feedback_file"
  } >>"$prompt_file"
fi

dotnet run --project "$repo_root/runner/Soulcaster.Runner.csproj" -- providers invoke \
  --provider gemini \
  --model "${SOULCASTER_UI_IDEATION_MODEL:-gemini-3-pro-image-preview}" \
  --prompt-file "$prompt_file" \
  --image "$overview_screenshot" \
  --image "$detail_screenshot" \
  --max-tokens 6000 \
  --save-text "$proposal_file" >/dev/null

cat "$proposal_file"
