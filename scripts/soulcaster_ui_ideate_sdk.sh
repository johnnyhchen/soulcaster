#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <repo_root> <baseline_screenshot> <state_dir>" >&2
  exit 2
fi

repo_root="$1"
baseline_screenshot="$2"
state_dir="$3"
stable_key_dir="${SOULCASTER_PROVIDER_KEY_DIR:-/tmp/soulcaster-provider-keys}"

mkdir -p "$state_dir"

if [ -z "${GEMINI_API_KEY:-}" ] && [ -s "$stable_key_dir/gemini.key" ]; then
  export GEMINI_API_KEY="$(cat "$stable_key_dir/gemini.key")"
fi

attempt_file="$state_dir/attempt.txt"
proposal_file="$state_dir/proposal.md"
feedback_file="$state_dir/feedback.md"
prompt_file="$state_dir/ideate-prompt.txt"
dashboard_source_file="$state_dir/dashboard-source.html"

attempt=1
if [ -f "$attempt_file" ]; then
  last_attempt="$(cat "$attempt_file" 2>/dev/null || echo 0)"
  if [[ "$last_attempt" =~ ^[0-9]+$ ]]; then
    attempt=$((last_attempt + 1))
  fi
fi
printf '%s\n' "$attempt" >"$attempt_file"

perl -0ne 'if (/static string DashboardHtml\(\) => """\n(.*?)\n""";/s) { print $1; exit 0 } exit 1' \
  "$repo_root/runner/Program.cs" >"$dashboard_source_file"

cat >"$prompt_file" <<EOF
You are designing the next Soulcaster dashboard overhaul.

You are looking at the real current UI screenshot and the live DashboardHtml source.

Baseline screenshot:
- $baseline_screenshot

Constraints:
- Preserve the current workflow: attention queue, pipeline list, expandable detail panel, live graph, telemetry, gates, and artifacts.
- Stay centered in runner/Program.cs inside DashboardHtml().
- Avoid backend churn unless a tiny presentation hook is clearly justified.
- The current design is still too monotonous and list-heavy. It needs a stronger sense of structure, clearer information hierarchy, a more distinctive visual identity, and a better desktop/mobile rhythm.
- Make the dashboard feel deliberate and premium, not generic dark admin UI.
- Improve scanability, selected-state clarity, section transitions, density control, and detail-view presentation.
- Keep implementation realistic for one focused coding mission.

If prior evaluator feedback exists, address it directly and completely.

Deliver a markdown brief with exactly these sections:

# UI Overhaul Iteration $attempt
## Executive Summary
## Visual Direction
## Layout Changes
## UX Improvements
## Concrete Implementation Brief
- runner/Program.cs: ...
## Soulcaster Mission Prompt
Write one implementation-ready prompt.
## Acceptance Checklist
- ...
EOF

{
  printf '\n## Current Dashboard Source\n'
  printf '\n### runner/Program.cs :: DashboardHtml()\n```html\n'
  cat "$dashboard_source_file"
  printf '\n```\n'
} >>"$prompt_file"

if [ -s "$feedback_file" ]; then
  {
    printf '\n## Prior Consensus Feedback To Address\n'
    cat "$feedback_file"
  } >>"$prompt_file"
fi

dotnet run --project "$repo_root/runner/Runner.csproj" -- providers invoke \
  --provider gemini \
  --model "${SOULCASTER_UI_IDEATION_MODEL:-gemini-3.1-flash-image-preview}" \
  --prompt-file "$prompt_file" \
  --image "$baseline_screenshot" \
  --save-text "$proposal_file" >/dev/null

cat "$proposal_file"
