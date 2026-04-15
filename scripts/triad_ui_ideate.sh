#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <triad_repo> <baseline_screenshot> <state_dir>" >&2
  exit 2
fi

triad_repo="$1"
baseline_screenshot="$2"
state_dir="$3"

mkdir -p "$state_dir"

gemini_key_file="$state_dir/gemini.key"
if [ -z "${GEMINI_API_KEY:-}" ] && [ -s "$gemini_key_file" ]; then
  export GEMINI_API_KEY="$(cat "$gemini_key_file")"
fi

attempt_file="$state_dir/attempt.txt"
proposal_file="$state_dir/proposal.md"
prompt_file="$state_dir/ideate-prompt.txt"
feedback_file="$state_dir/feedback.md"

attempt=1
if [ -f "$attempt_file" ]; then
  last_attempt="$(cat "$attempt_file" 2>/dev/null || echo 0)"
  if [[ "$last_attempt" =~ ^[0-9]+$ ]]; then
    attempt=$((last_attempt + 1))
  fi
fi
printf '%s\n' "$attempt" >"$attempt_file"

cat >"$prompt_file" <<EOF
You are a UX and UI design strategist working on Triad, a local mission-control workbench for multi-step coding workflows.

This is a real repo and a real UI.

Inspect this real baseline screenshot directly if your model runtime supports local image paths:
- $baseline_screenshot

Product constraints:
- Preserve the 3-column mission-control information architecture.
- Keep all current controls and workflow concepts: workspace list, mission creation, room stream, approvals, mission memory, merge gate, and capability cards.
- Stay inside the static frontend surface only. The implementation should be primarily in static/styles.css and static/index.html, with static/app.js touched only if necessary for presentation hooks.
- Aim for a noticeably stronger visual identity than the current UI. The current app reads as washed-out and low-contrast, especially in scanability and hierarchy.
- Prioritize: typography, spacing rhythm, status badge clarity, event stream hierarchy, mission header presence, merge gate readability, and mobile responsiveness.
- Do not suggest backend or Rust changes.

If prior evaluator critique exists and is substantive, address it fully. If it is absent, ignore this section.

Deliver a concrete design brief in markdown with exactly these sections:

# UI/UX Direction Iteration $attempt
## Executive Summary
## Visual Direction
## UX Improvements
## Concrete Implementation Brief
- static/index.html: ...
- static/styles.css: ...
- static/app.js: ...
## Triad Mission Prompt
Write a single implementation-ready prompt that Triad can execute directly.
## Acceptance Checklist
- ...

The brief must be implementation-oriented, not aspirational. Make it bold enough to matter, but small enough for one focused Triad mission.
EOF

{
  printf '\n## Current Frontend Source\n'
  printf '\n### static/index.html\n```html\n'
  cat "$triad_repo/static/index.html"
  printf '\n```\n'
  printf '\n### static/styles.css\n```css\n'
  cat "$triad_repo/static/styles.css"
  printf '\n```\n'
  printf '\n### static/app.js\n```js\n'
  cat "$triad_repo/static/app.js"
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
