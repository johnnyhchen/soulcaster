#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <repo_root> <baseline_screenshot> <state_dir>" >&2
  exit 2
fi

repo_root="$1"
baseline_screenshot="$2"
state_dir="$3"

mkdir -p "$state_dir"

gemini_key_file="$state_dir/gemini.key"
if [ -z "${GEMINI_API_KEY:-}" ] && [ -s "$gemini_key_file" ]; then
  export GEMINI_API_KEY="$(cat "$gemini_key_file")"
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
You are a UX and UI design strategist improving Soulcaster's real WebUI dashboard.

This is a real repo and a real interface.

Inspect this real baseline screenshot directly if your model runtime supports local image paths:
- $baseline_screenshot

Product constraints:
- Preserve the current dashboard workflow and information architecture: attention queue, pipeline list, pipeline detail, live graph panel, telemetry, gates, and artifacts.
- Stay inside the existing dashboard surface implemented in runner/Program.cs inside DashboardHtml().
- Avoid backend/API churn unless a tiny presentation hook is absolutely necessary.
- Keep the UI implementation practical for one focused Soulcaster coding mission.
- The current UI reads as flat, washed out, and cramped. The hierarchy between queue, list, and detail is weak, and the dashboard lacks a clear visual identity.
- Prioritize: stronger visual direction, better typography, clearer section hierarchy, higher scanability, more intentional spacing rhythm, stronger status treatment, better selected-state affordances, clearer error/empty styling, and responsive layout behavior.
- Do not remove existing workflow concepts or controls.

If prior evaluator critique exists and is substantive, address it fully. If it is absent, ignore this section.

Deliver a concrete design brief in markdown with exactly these sections:

# UI/UX Direction Iteration $attempt
## Executive Summary
## Visual Direction
## UX Improvements
## Concrete Implementation Brief
- runner/Program.cs: ...
## Soulcaster Mission Prompt
Write a single implementation-ready prompt that Soulcaster can execute directly.
## Acceptance Checklist
- ...

The brief must be implementation-oriented, not aspirational. Make it bold enough to matter, but small enough for one focused Soulcaster mission.
EOF

{
  printf '\n## Current Dashboard Source\n'
  printf '\n### runner/Program.cs :: DashboardHtml()\n```html\n'
  cat "$dashboard_source_file"
  printf '\n```\n'
} >>"$prompt_file"

if [ -s "$feedback_file" ]; then
  {
    printf '\n## Prior Evaluator Critique To Address\n'
    cat "$feedback_file"
  } >>"$prompt_file"
fi

gemini \
  --model gemini-3-pro-image-preview \
  --approval-mode plan \
  --output-format text \
  -p "$(cat "$prompt_file")" >"$proposal_file"

cat "$proposal_file"
