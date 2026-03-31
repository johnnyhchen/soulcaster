# Running Project Pipelines

Three non-trivial project pipelines that build real software from scratch using the attractor engine. Each uses the full 5-phase cycle (plan → breakdown → implement → validate → critique) with multi-model routing and qa-agent integration.

## Prerequisites

```bash
# API keys
# These project pipelines assume Anthropic + OpenAI.
# Gemini is optional unless you run a dotfile that targets Gemini.
export ANTHROPIC_API_KEY="sk-ant-..."
export OPENAI_API_KEY="sk-..."
export GEMINI_API_KEY="..."

# Language toolchains (install whichever project you want to run)
rustc --version   # Rust — for tsk
go version        # Go — for marble
python3 --version # Python — for salvo

# Build soulcaster
cd ~/soulcaster
dotnet build
```

## Human Gates

All three pipelines have human approval gates. When the pipeline pauses at a gate, you have two options:

```bash
# See what's pending for a specific run
dotnet run --project runner -- gate --dir dotfiles/output/project-cli-task-tracker

# Approve and continue
dotnet run --project runner -- gate answer approve --dir dotfiles/output/project-cli-task-tracker

# Send back with feedback
dotnet run --project runner -- gate answer revise "Add error handling for empty input" --dir dotfiles/output/project-cli-task-tracker
```

Gates appear at: plan review, breakdown review, and ship/iterate decision.

---

## 1. tsk — Rust CLI Task Tracker

**What it builds:** A SQLite-backed CLI tool with add/list/done/delete/search commands, plus a thin REST wrapper for qa-agent validation.

### Run

```bash
dotnet run --project runner -- run dotfiles/project-cli-task-tracker.dot
```

### Watch Progress

```bash
# In another terminal
dotnet run --project runner -- status --dir dotfiles/output/project-cli-task-tracker
dotnet run --project runner -- logs --dir dotfiles/output/project-cli-task-tracker
```

### How to Know It Worked

Check these after the pipeline finishes (or at each phase):

**After orient + plan (Phase 1):**
```bash
# Plan artifact exists and is substantive
cat dotfiles/output/project-cli-task-tracker/logs/plan/PLAN-1.md
# Should contain: architecture, file list, dependencies, schema design
```

**After implement (Phase 3):**
```bash
# Progress log shows commits completed
cat dotfiles/output/project-cli-task-tracker/logs/implement/PROGRESS-1.md
# Should show: multiple commits marked DONE

# The binary exists and runs
ls -la target/release/tsk
./target/release/tsk --help
```

**After validate (Phase 4):**
```bash
# Validation report exists with PASS verdict
cat dotfiles/output/project-cli-task-tracker/logs/validate/VALIDATION-RUN-1.md
# Should contain: "Verdict: PASS" and per-check results

# Manual smoke test
./target/release/tsk add "Test from terminal" --priority high
./target/release/tsk list
./target/release/tsk search "terminal"
./target/release/tsk done 1
./target/release/tsk delete 1
```

**After qa-agent validate (Phase 4b):**
```bash
# qa-agent verdict file exists
cat /tmp/qa-tsk-run/*/verdict.json
# Should contain: "status": "pass"

# API transcripts show real HTTP calls
cat /tmp/qa-tsk-run/*/artifacts/tasks/*/api-transcript.json | python3 -m json.tool | head -20
# Should show: GET/POST/PUT/DELETE requests with 200/201 responses
```

**After critique (Phase 5):**
```bash
cat dotfiles/output/project-cli-task-tracker/logs/critique/CRITIQUE-PARETO-1.md
# Should contain: MUST FIX / SHOULD FIX / NICE TO HAVE categories
```

**Final validation — pipeline result:**
```bash
cat dotfiles/output/project-cli-task-tracker/logs/result.json
# Should contain: "status": "success"

# All tests pass independently
cargo test
cargo clippy -- -D warnings
```

---

## 2. marble — Go Static Site Generator

**What it builds:** A Markdown-to-HTML static site generator with front matter, templates, tag pages, RSS feed, incremental builds, and a dev server with live reload.

### Run

```bash
dotnet run --project runner -- run dotfiles/project-markdown-static-site.dot
```

### How to Know It Worked

**After implement (Phase 3):**
```bash
# Binary exists
ls -la marble
./marble --help

# Init creates a scaffold
./marble init /tmp/marble-test
ls /tmp/marble-test/
# Should contain: content/, layouts/, config.yaml
```

**After validate (Phase 4):**
```bash
cat dotfiles/output/project-markdown-static-site/logs/validate/VALIDATION-RUN-1.md
# Should contain: "Verdict: PASS"

# Build produces output
cd /tmp/marble-test
../marble build
ls output/
# Should contain: index.html, posts/, tags/, feed.xml

# Output files have content
cat output/index.html | head -20
# Should contain: HTML with post links

cat output/feed.xml | head -10
# Should contain: <?xml ...> with <rss> and <item> elements

ls output/tags/
# Should contain: one directory per tag
```

**After qa-agent validate (Phase 4b):**
```bash
cat /tmp/qa-marble-run/*/verdict.json
# Should contain: "status": "pass"

# API transcripts show served pages
cat /tmp/qa-marble-run/*/artifacts/tasks/*/api-transcript.json | python3 -c "
import json, sys
for f in sys.stdin:
    data = json.loads(f)
# Just check the files exist
" 2>/dev/null
ls /tmp/qa-marble-run/*/artifacts/tasks/*/api-transcript.json
# Should show: 4 transcript files (index, feed.xml, posts, tags)
```

**Final validation:**
```bash
cat dotfiles/output/project-markdown-static-site/logs/result.json
go test ./...
go vet ./...
```

---

## 3. salvo — Python HTTP Load Tester

**What it builds:** An async HTTP load testing tool with configurable concurrency, rate limiting, real-time stats via rich, HAR export, and a built-in echo server.

### Run

```bash
dotnet run --project runner -- run dotfiles/project-http-load-tester.dot
```

### How to Know It Worked

**After implement (Phase 3):**
```bash
# Package installs
pip install -e .
python -m salvo --help
# Should show: usage with -n, -c, -d, --rps, --har, -X, echo-server commands
```

**After validate (Phase 4):**
```bash
cat dotfiles/output/project-http-load-tester/logs/validate/VALIDATION-RUN-1.md
# Should contain: "Verdict: PASS"

# Manual smoke test with echo server
python -m salvo echo-server --port 9300 &
ECHO_PID=$!

# Basic load test
python -m salvo http://localhost:9300 -n 50 -c 5
# Should show: progress bar, then stats table with p50/p95/p99

# Rate-limited run
python -m salvo http://localhost:9300 -n 30 --rps 10
# Should show: RPS staying near 10

# Duration mode
python -m salvo http://localhost:9300 -d 2s -c 3
# Should run for ~2 seconds then print stats

# HAR export
python -m salvo http://localhost:9300 -n 10 --har /tmp/test.har
cat /tmp/test.har | python3 -c "import json,sys; d=json.load(sys.stdin); print(f'{len(d[\"log\"][\"entries\"])} entries')"
# Should show: 10 entries

kill $ECHO_PID
```

**After qa-agent validate (Phase 4b):**
```bash
cat /tmp/qa-salvo-run/*/verdict.json
# Should contain: "status": "pass"

ls /tmp/qa-salvo-run/*/artifacts/tasks/*/api-transcript.json
# Should show: transcript files with 200 responses from echo server
```

**Final validation:**
```bash
cat dotfiles/output/project-http-load-tester/logs/result.json
python -m pytest tests/ -v
```

---

## Troubleshooting

**Pipeline stuck at a gate:**
```bash
dotnet run --project runner -- gate --dir dotfiles/output/project-cli-task-tracker
# Answer it to continue
dotnet run --project runner -- gate answer approve --dir dotfiles/output/project-cli-task-tracker
```

**Pipeline failed at validate:**
The pipeline will loop back to plan automatically (validate has `retry_target="plan"`). Check the validation report to see what failed:
```bash
cat dotfiles/output/*/logs/validate/VALIDATION-RUN-*.md
```

**qa-agent validation failed:**
Check the tool node output:
```bash
cat dotfiles/output/*/logs/qa_agent_validate/stdout.txt
cat dotfiles/output/*/logs/qa_agent_validate/stderr.txt
```

**Resume after interruption:**
Just rerun the same command. The pipeline reads checkpoint.json and resumes from where it left off:
```bash
dotnet run --project runner -- run dotfiles/project-cli-task-tracker.dot
```

**Clean start:**
```bash
rm -rf dotfiles/output/project-cli-task-tracker
dotnet run --project runner -- run dotfiles/project-cli-task-tracker.dot
```

**View web dashboard:**
```bash
dotnet run --project runner -- web --dir dotfiles/output/project-cli-task-tracker --port 5099
# Open http://localhost:5099
```
