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

if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -s "$stable_key_dir/anthropic.key" ]; then
  export ANTHROPIC_API_KEY="$(cat "$stable_key_dir/anthropic.key")"
fi
if [ -z "${OPENAI_API_KEY:-}" ] && [ -s "$stable_key_dir/openai.key" ]; then
  export OPENAI_API_KEY="$(cat "$stable_key_dir/openai.key")"
fi

proposal_file="$state_dir/proposal.md"
feedback_file="$state_dir/feedback.md"
accepted_file="$state_dir/accepted-proposal.md"
prompt_file="$state_dir/evaluate-prompt.txt"
dashboard_source_file="$state_dir/dashboard-source.html"
opus_eval_file="$state_dir/evaluation-opus.md"
gpt_eval_file="$state_dir/evaluation-gpt54.md"
consensus_file="$state_dir/evaluation.md"

if [ ! -s "$proposal_file" ]; then
  echo "Missing proposal at $proposal_file" >&2
  exit 2
fi

perl -0ne 'if (/static string DashboardHtml\(\) => """\n(.*?)\n""";/s) { print $1; exit 0 } exit 1' \
  "$repo_root/runner/Program.cs" >"$dashboard_source_file"

cat >"$prompt_file" <<EOF
You are evaluating a Soulcaster dashboard overhaul proposal.

Inspect the real current UI screenshot and the proposal. Decide whether the design is strong enough to implement now.

Baseline screenshot:
- $baseline_screenshot

Acceptance standard:
- Preserves the workflow and information architecture.
- Produces a materially stronger visual identity, not just a restyle.
- Improves hierarchy, scanability, and selected/detail-state clarity.
- Makes the detail panel feel purposeful rather than tacked on.
- Is concrete and bounded enough to implement in runner/Program.cs in one mission.

Output format:
VERDICT: ACCEPT or VERDICT: REVISE
RATIONALE:
- ...
REQUIRED CHANGES:
- ...

If you accept the proposal, REQUIRED CHANGES may be "None".

Candidate proposal:
EOF

cat "$proposal_file" >>"$prompt_file"

{
  printf '\n\nCurrent dashboard source for comparison:\n'
  printf '\n### runner/Program.cs :: DashboardHtml()\n```html\n'
  cat "$dashboard_source_file"
  printf '\n```\n'
} >>"$prompt_file"

dotnet run --project "$repo_root/runner/Soulcaster.Runner.csproj" -- providers invoke \
  --provider anthropic \
  --model claude-opus-4-6 \
  --prompt-file "$prompt_file" \
  --image "$baseline_screenshot" \
  --save-text "$opus_eval_file" >/dev/null

dotnet run --project "$repo_root/runner/Soulcaster.Runner.csproj" -- providers invoke \
  --provider openai \
  --model gpt-5.4 \
  --prompt-file "$prompt_file" \
  --image "$baseline_screenshot" \
  --reasoning-effort xhigh \
  --save-text "$gpt_eval_file" >/dev/null

{
  echo "# Consensus Gate"
  echo
  echo "## Claude Opus 4.6"
  cat "$opus_eval_file"
  echo
  echo "## GPT-5.4"
  cat "$gpt_eval_file"
} >"$consensus_file"

cat "$consensus_file"

if grep -Eq '^(VERDICT: ACCEPT)' "$opus_eval_file" && grep -Eq '^(VERDICT: ACCEPT)' "$gpt_eval_file"; then
  cp "$proposal_file" "$accepted_file"
  rm -f "$feedback_file"
  exit 0
fi

{
  echo "# Consensus Revisions"
  echo
  echo "Address all required changes before the next iteration."
  echo
  echo "## Claude Opus 4.6"
  cat "$opus_eval_file"
  echo
  echo "## GPT-5.4"
  cat "$gpt_eval_file"
} >"$feedback_file"

exit 1
