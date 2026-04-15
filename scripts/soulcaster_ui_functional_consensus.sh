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

if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -s "$stable_key_dir/anthropic.key" ]; then
  export ANTHROPIC_API_KEY="$(cat "$stable_key_dir/anthropic.key")"
fi
if [ -z "${OPENAI_API_KEY:-}" ] && [ -s "$stable_key_dir/openai.key" ]; then
  export OPENAI_API_KEY="$(cat "$stable_key_dir/openai.key")"
fi

proposal_file="$state_dir/proposal.md"
research_file="$state_dir/research.md"
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
if [ ! -s "$research_file" ]; then
  echo "Missing research brief at $research_file" >&2
  exit 2
fi

perl -0ne 'if (/static string DashboardHtml\(\) => """\n(.*?)\n""";/s) { print $1; exit 0 } exit 1' \
  "$repo_root/runner/Program.cs" >"$dashboard_source_file"

cat >"$prompt_file" <<'EOF'
You are evaluating a Soulcaster dashboard overhaul proposal.

Use the research brief, the current UI screenshots, the current DashboardHtml source, and the proposal to decide whether the proposal is strong enough to implement now.

Acceptance standard:
- The proposal reflects the real Soulcaster workflow and operator journey from the research brief.
- It presents 3 genuinely distinct directions and converges on the strongest one.
- The recommended direction materially improves hierarchy, scanability, state clarity, and overview/detail cohesion.
- It preserves queue, pipeline, graph, telemetry, gates, artifacts, phase summaries, markdown viewing, and human gate actions.
- It includes a decisive mobile fallback.
- It gives enough implementation direction to land in runner/Program.cs without product ambiguity.
- The design feels product-specific and intentional rather than a generic dark admin restyle.

Output format:
VERDICT: ACCEPT or VERDICT: REVISE
RATIONALE:
- ...
REQUIRED CHANGES:
- ...

If you accept the proposal, REQUIRED CHANGES may be "None".

## Research Brief
EOF

cat "$research_file" >>"$prompt_file"

{
  printf '\n## Candidate Proposal\n'
  cat "$proposal_file"
  printf '\n## Current DashboardHtml Source\n```html\n'
  cat "$dashboard_source_file"
  printf '\n```\n'
} >>"$prompt_file"

anthropic_args=(
  --provider anthropic
  --model claude-opus-4-6
  --prompt-file "$prompt_file"
  --image "$overview_screenshot"
  --image "$detail_screenshot"
  --save-text "$opus_eval_file"
)

openai_args=(
  --provider openai
  --model gpt-5.4
  --prompt-file "$prompt_file"
  --image "$overview_screenshot"
  --image "$detail_screenshot"
  --reasoning-effort xhigh
  --save-text "$gpt_eval_file"
)

dotnet run --project "$repo_root/runner/Runner.csproj" -- providers invoke "${anthropic_args[@]}" >/dev/null
dotnet run --project "$repo_root/runner/Runner.csproj" -- providers invoke "${openai_args[@]}" >/dev/null

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
  echo "Address every required change before the next iteration."
  echo
  echo "## Claude Opus 4.6"
  cat "$opus_eval_file"
  echo
  echo "## GPT-5.4"
  cat "$gpt_eval_file"
} >"$feedback_file"

exit 1
