#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <repo_root> <state_dir>" >&2
  exit 2
fi

repo_root="$1"
state_dir="$2"
stable_key_dir="${SOULCASTER_PROVIDER_KEY_DIR:-/tmp/soulcaster-provider-keys}"

mkdir -p "$state_dir"

if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -s "$stable_key_dir/anthropic.key" ]; then
  export ANTHROPIC_API_KEY="$(cat "$stable_key_dir/anthropic.key")"
fi

research_file="$state_dir/research.md"
prompt_file="$state_dir/research-prompt.txt"
web_help_file="$state_dir/web-help.txt"
providers_help_file="$state_dir/providers-help.txt"
builder_help_file="$state_dir/builder-help.txt"
dashboard_api_file="$state_dir/dashboard-api-snippet.txt"
engine_files_file="$state_dir/engine-files.txt"
dotfiles_file="$state_dir/example-dotfiles.txt"

dotnet run --project "$repo_root/runner/Runner.csproj" -- help >"$web_help_file"
dotnet run --project "$repo_root/runner/Runner.csproj" -- providers help >"$providers_help_file"
dotnet run --project "$repo_root/runner/Runner.csproj" -- builder help >"$builder_help_file"

sed -n '1111,1505p' "$repo_root/runner/Program.cs" >"$dashboard_api_file"

find "$repo_root/src/JcAttractor.Attractor" -maxdepth 2 -type f | sed "s|$repo_root/||" | sort >"$engine_files_file"
find "$repo_root/dotfiles" -maxdepth 1 -name '*.dot' | sed "s|$repo_root/||" | sort >"$dotfiles_file"

cat >"$prompt_file" <<'EOF'
You are examining Soulcaster so a dashboard overhaul can reflect the real product, not just the current UI chrome.

Study the supplied CLI help, dashboard server code, engine file map, and example dotruns.

Your job is to synthesize how Soulcaster works, what operators are actually doing in the dashboard, and what the next web UI must support.

Output rules:
- Keep the response under 1800 words.
- Prefer tight bullets and short paragraphs over long prose.
- Focus on operator workflow, state model, endpoint-backed capabilities, and UI-critical semantics.
- Be concrete about gate answer behavior, failure recovery, iteration handling, artifact browsing, and the difference between queue/home surfaces and pipeline-detail surfaces.

Return markdown with exactly these top-level sections:

# Functionality Summary
## Product Overview
## Core Capabilities
## Important Objects And States
## Dashboard Responsibilities
## Failure, Retry, Gate, And Artifact Behaviors

# Primary User Journey
## Primary Persona
## Happy Path
## Debug Path
## Human Gate Path
## Desired Emotional Tone

# UI Design Requirements
## Must Preserve
## Highest-Value UX Opportunities
## Information Hierarchy Recommendations
## Visual And Interaction Principles

Ground everything in the supplied repo context. Be concrete and implementation-relevant.

## Runner Help
EOF

cat "$web_help_file" >>"$prompt_file"

{
  printf '\n## Providers Help\n'
  cat "$providers_help_file"
  printf '\n## Builder Help\n'
  cat "$builder_help_file"
  printf '\n## Dashboard API And Web Server Snippet\n```csharp\n'
  cat "$dashboard_api_file"
  printf '\n```\n'
  printf '\n## Attractor Engine File Map\n'
  cat "$engine_files_file"
  printf '\n## Example Dotruns\n'
  cat "$dotfiles_file"
} >>"$prompt_file"

dotnet run --project "$repo_root/runner/Runner.csproj" -- providers invoke \
  --provider anthropic \
  --model claude-opus-4-6 \
  --prompt-file "$prompt_file" \
  --max-tokens 5500 \
  --save-text "$research_file" >/dev/null

cat "$research_file"
