#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <triad_repo> <baseline_screenshot> <state_dir>" >&2
  exit 2
fi

triad_repo="$1"
baseline_screenshot="$2"
state_dir="$3"

anthropic_key_file="$state_dir/anthropic.key"
if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -s "$anthropic_key_file" ]; then
  export ANTHROPIC_API_KEY="$(cat "$anthropic_key_file")"
fi

proposal_file="$state_dir/proposal.md"
evaluation_file="$state_dir/evaluation.md"
feedback_file="$state_dir/feedback.md"
accepted_file="$state_dir/accepted-proposal.md"
prompt_file="$state_dir/evaluate-prompt.txt"

if [ ! -s "$proposal_file" ]; then
  echo "Missing proposal at $proposal_file" >&2
  exit 2
fi

cat >"$prompt_file" <<EOF
You are an exacting evaluator for a Triad UI refresh proposal.

Inspect this real baseline screenshot directly if your model runtime supports local image paths:
- $baseline_screenshot

Review the candidate proposal below and decide whether it is strong enough to hand directly to Triad for implementation.

Acceptance standard:
- The proposal preserves the existing information architecture and workflow.
- The visual direction is noticeably stronger and more intentional than the current UI.
- The proposal improves scanability, status clarity, and room readability.
- The changes are concrete and bounded enough for one implementation mission.
- The implementation brief stays inside the static frontend and avoids backend churn.

Output format:
VERDICT: ACCEPT or VERDICT: REVISE
RATIONALE:
- ...
REQUIRED CHANGES:
- ...
TRIAD IMPLEMENTATION BRIEF:
...

If you choose ACCEPT, the REQUIRED CHANGES section may be empty or say "None".

Candidate proposal:
EOF

cat "$proposal_file" >>"$prompt_file"

{
  printf '\n\nCurrent frontend source for comparison:\n'
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

claude \
  -p \
  --model claude-opus-4-6 \
  --permission-mode plan \
  "$(cat "$prompt_file")" >"$evaluation_file" 2>&1

plan_path="$(perl -ne 'if (m{(/Users/[^`[:space:]]+\\.md)}) { print $1; exit }' "$evaluation_file")"
if [ -n "$plan_path" ] && [ -f "$plan_path" ]; then
  cp "$plan_path" "$evaluation_file"
fi

cat "$evaluation_file"

if grep -Eq '^(VERDICT: ACCEPT|## Verdict: ACCEPT)' "$evaluation_file"; then
  cp "$proposal_file" "$accepted_file"
  rm -f "$feedback_file"
  exit 0
fi

cp "$evaluation_file" "$feedback_file"
exit 1
