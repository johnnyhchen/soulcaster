#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <repo_root> <baseline_screenshot> <state_dir>" >&2
  exit 2
fi

repo_root="$1"
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
dashboard_source_file="$state_dir/dashboard-source.html"

if [ ! -s "$proposal_file" ]; then
  echo "Missing proposal at $proposal_file" >&2
  exit 2
fi

perl -0ne 'if (/static string DashboardHtml\(\) => """\n(.*?)\n""";/s) { print $1; exit 0 } exit 1' \
  "$repo_root/runner/Program.cs" >"$dashboard_source_file"

cat >"$prompt_file" <<EOF
You are an exacting evaluator for a Soulcaster WebUI refresh proposal.

Inspect this real baseline screenshot directly if your model runtime supports local image paths:
- $baseline_screenshot

Review the candidate proposal below and decide whether it is strong enough to hand directly to Soulcaster for implementation.

Acceptance standard:
- The proposal preserves the existing dashboard workflow and core information architecture.
- The visual direction is noticeably stronger and more intentional than the current UI.
- The proposal improves scanability, hierarchy, and status clarity across queue, pipeline list, and detail views.
- The changes are concrete and bounded enough for one implementation mission in runner/Program.cs.
- The implementation brief avoids backend churn and stays centered on the dashboard surface.

Output format:
VERDICT: ACCEPT or VERDICT: REVISE
RATIONALE:
- ...
REQUIRED CHANGES:
- ...
SOULCASTER IMPLEMENTATION BRIEF:
...

If you choose ACCEPT, the REQUIRED CHANGES section may be empty or say "None".

Candidate proposal:
EOF

cat "$proposal_file" >>"$prompt_file"

{
  printf '\n\nCurrent dashboard source for comparison:\n'
  printf '\n### runner/Program.cs :: DashboardHtml()\n```html\n'
  cat "$dashboard_source_file"
  printf '\n```\n'
} >>"$prompt_file"

python3 - "$prompt_file" "$baseline_screenshot" "$evaluation_file" <<'PY'
import base64
import json
import mimetypes
import os
import sys
import urllib.error
import urllib.request

prompt_path, image_path, output_path = sys.argv[1:4]
api_key = os.environ.get("ANTHROPIC_API_KEY", "").strip()
if not api_key:
    print("ANTHROPIC_API_KEY is not set", file=sys.stderr)
    sys.exit(2)

with open(prompt_path, "r", encoding="utf-8") as f:
    prompt = f.read()

with open(image_path, "rb") as f:
    image_b64 = base64.b64encode(f.read()).decode("ascii")

mime_type, _ = mimetypes.guess_type(image_path)
if not mime_type:
    mime_type = "image/png"

payload = {
    "model": "claude-opus-4-6",
    "max_tokens": 4096,
    "messages": [
        {
            "role": "user",
            "content": [
                {"type": "text", "text": prompt},
                {
                    "type": "image",
                    "source": {
                        "type": "base64",
                        "media_type": mime_type,
                        "data": image_b64,
                    },
                },
            ],
        }
    ],
}

req = urllib.request.Request(
    "https://api.anthropic.com/v1/messages",
    data=json.dumps(payload).encode("utf-8"),
    headers={
        "content-type": "application/json",
        "x-api-key": api_key,
        "anthropic-version": "2023-06-01",
    },
    method="POST",
)

try:
    with urllib.request.urlopen(req, timeout=900) as resp:
        body = resp.read().decode("utf-8")
except urllib.error.HTTPError as e:
    err = e.read().decode("utf-8", errors="replace")
    print(err, file=sys.stderr)
    sys.exit(2)

doc = json.loads(body)
text_parts = []
for item in doc.get("content", []):
    if item.get("type") == "text":
        text_parts.append(item.get("text", ""))

text = "\n".join(part for part in text_parts if part).strip()
with open(output_path, "w", encoding="utf-8") as f:
    f.write(text)
    f.write("\n")
PY

cat "$evaluation_file"

if grep -Eq '^(VERDICT: ACCEPT|## Verdict: ACCEPT)' "$evaluation_file"; then
  cp "$proposal_file" "$accepted_file"
  rm -f "$feedback_file"
  exit 0
fi

cp "$evaluation_file" "$feedback_file"
exit 1
