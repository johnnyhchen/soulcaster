#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <repo_root> <artifact_dir>" >&2
  exit 2
fi

repo_root="$1"
artifact_dir="$2"

mkdir -p "$artifact_dir"

stable_key_dir="${SOULCASTER_PROVIDER_KEY_DIR:-/tmp/soulcaster-provider-keys}"

openai_key_file="$artifact_dir/openai.key"
if [ -z "${OPENAI_API_KEY:-}" ] && [ -s "$openai_key_file" ]; then
  export OPENAI_API_KEY="$(cat "$openai_key_file")"
elif [ -z "${OPENAI_API_KEY:-}" ] && [ -s "$stable_key_dir/openai.key" ]; then
  export OPENAI_API_KEY="$(cat "$stable_key_dir/openai.key")"
fi

gemini_key_file="$artifact_dir/gemini.key"
if [ -z "${GEMINI_API_KEY:-}" ] && [ -s "$gemini_key_file" ]; then
  export GEMINI_API_KEY="$(cat "$gemini_key_file")"
elif [ -z "${GEMINI_API_KEY:-}" ] && [ -s "$stable_key_dir/gemini.key" ]; then
  export GEMINI_API_KEY="$(cat "$stable_key_dir/gemini.key")"
fi

input_png="$artifact_dir/input.png"

python3 - "$input_png" <<'PY'
import base64
import sys

png_b64 = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR42mP4b2xMEmIY1TCqYfhqAACXHWUQdfT2ygAAAABJRU5ErkJggg=="
with open(sys.argv[1], "wb") as f:
    f.write(base64.b64decode(png_b64))
PY

openai_prompt="$artifact_dir/openai-prompt.txt"
gemini_prompt="$artifact_dir/gemini-prompt.txt"

cat >"$openai_prompt" <<'EOF'
Use the attached tiny red sample as inspiration. Return a simple badge-style icon that keeps a strong red center and include a one-sentence caption.
EOF

cat >"$gemini_prompt" <<'EOF'
Using the attached tiny red sample, create a playful polished icon variation and return both a caption and an image.
EOF

run_provider() {
  local provider="$1"
  local model="$2"
  local prompt_file="$3"
  local max_tokens="$4"
  local provider_dir="$artifact_dir/$provider"

  mkdir -p "$provider_dir"

  dotnet run --project "$repo_root/runner/Soulcaster.Runner.csproj" -- providers invoke \
    --provider "$provider" \
    --model "$model" \
    --prompt-file "$prompt_file" \
    --image "$input_png" \
    --output-modalities text,image \
    --max-tokens "$max_tokens" \
    --save-text "$provider_dir/response.txt" \
    --save-images-dir "$provider_dir/images" \
    --json >"$provider_dir/result.json"

  shopt -s nullglob
  local images=("$provider_dir"/images/image-*)
  shopt -u nullglob

  if [ "${#images[@]}" -eq 0 ]; then
    echo "No images were returned for provider '$provider'." >&2
    exit 1
  fi
}

run_provider "openai" "${OPENAI_IMAGE_MODEL:-gpt-5.4}" "$openai_prompt" "512"
run_provider "gemini" "${GEMINI_IMAGE_MODEL:-gemini-3.1-flash-image-preview}" "$gemini_prompt" "1024"

validation_file="$artifact_dir/VALIDATION-1.md"

{
  echo "# Multimodal SDK Validation"
  echo
  echo "Validated direct SDK-backed image round-trips against live providers."
  echo
  echo "## Providers"
  echo "- OpenAI model: ${OPENAI_IMAGE_MODEL:-gpt-5.4}"
  echo "- Gemini model: ${GEMINI_IMAGE_MODEL:-gemini-3.1-flash-image-preview}"
  echo
  echo "## Artifacts"
  echo "- Input image: $input_png"
  echo "- OpenAI response text: $artifact_dir/openai/response.txt"
  echo "- OpenAI images: $artifact_dir/openai/images"
  echo "- Gemini response text: $artifact_dir/gemini/response.txt"
  echo "- Gemini images: $artifact_dir/gemini/images"
  echo
  echo "## Result"
  echo "- PASS: both providers accepted an input image and returned at least one output image through the SDK layer."
} >"$validation_file"

cat "$validation_file"
