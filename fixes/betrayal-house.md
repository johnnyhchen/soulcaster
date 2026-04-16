# Betrayal House — Attractor/Runner Fixes

Fixes made to soulcaster attractor and/or dotfiles while running the betrayal-house pipeline.

---

## Fix 1: Gate answer.json format

**Date:** 2026-02-18
**Symptom:** Interview gate crashed with "The given key was not present in the dictionary"
**Root cause:** `FileInterviewer.AskAsync()` calls `root.GetProperty("text")` but the answer.json was written with `{"choice": "approve"}` (wrong key).
**Fix:** Use `{"text": "approve"}` format for all gate answers.
**File:** N/A (operational knowledge, not code fix)

---

## Fix 2: Implement prompt uses relative artifact paths

**Date:** 2026-02-18
**Symptom:** Codex 5.2 agent said "I couldn't find any BREAKDOWN-*.md files under `<betrayal-house-root>`"
**Root cause:** The implement node's prompt said `Read the latest logs/breakdown/BREAKDOWN-*.md` (relative). The runner sets the agent's working directory to `dotfiles/output/`, so `logs/` should resolve to `dotfiles/output/logs/`. However, the prompt also said `The project is at <betrayal-house-root>`; Codex interpreted `logs/` as relative to the project root, not the working directory. The BREAKDOWN file was at `<run-logs-root>/breakdown/BREAKDOWN-1.md` but Codex searched `<betrayal-house-root>/logs/breakdown/`.
**Fix:** Changed all artifact paths in the implement node prompt to absolute paths under the run logs root:
- `logs/breakdown/BREAKDOWN-*.md` → `<run-logs-root>/breakdown/BREAKDOWN-1.md`
- `logs/implement/PROGRESS-{N}.md` → `<run-logs-root>/implement/PROGRESS-{N}.md`
- Added explicit `ARTIFACT LOCATIONS` and `PROJECT LOCATION` sections to the prompt
**File:** `dotfiles/betrayal-run.dot` (implement node prompt)

---

## Fix 3: apply_patch uses wrong -p level for absolute paths

**Date:** 2026-02-18
**Symptom:** Codex ran ~95 tool calls including many `apply_patch` calls, builds reported PASS in progress log, but `git diff` showed zero source modifications. Only `write_file` calls (new files) persisted.
**Root cause:** `OpenAiProfile.ApplyPatchAsync()` unconditionally uses `patch -p1`. Codex generates patches with absolute paths like `--- a/<betrayal-house-root>/src/...`. With `-p1`, `patch` strips the first component (`a`) leaving `<betrayal-house-root>/...` which is correct for absolute paths. However, the patch content is passed via `echo '...'` shell escaping which breaks on single quotes and special characters in the patch body. The real fix needed is `-p0` when paths are already absolute (no `a/b/` prefix) or ensuring proper escaping.
**Fix:** Changed `ApplyPatchAsync` in `OpenAiProfile.cs` to detect absolute paths and use `-p0`:
```csharp
var useP0 = targetFile.StartsWith('/');
var stripLevel = useP0 ? "0" : "1";
```
**File:** `src/Soulcaster.CodingAgent/Profiles/OpenAiProfile.cs` (lines 211-220)
**Status:** PARTIALLY EFFECTIVE — `write_file` calls now work (GameStateDto.cs created), but `apply_patch` for existing files may still fail due to shell escaping of patch content via `echo '...'`. The `echo` quoting breaks on single quotes, backticks, and `$` in patch content. Needs further investigation — may need to write patch to a temp file instead of piping via echo.

---

## Fix 4: apply_patch shell escaping — write to temp file

**Date:** 2026-02-18
**Symptom:** Even with `-p0` fix, `apply_patch` calls for existing files still don't persist changes. `GameStateDto.cs` was created (via `write_file`) but `GameLobby.razor`, `GameHub.cs`, and `GameBoard.razor` were not modified.
**Root cause:** The patch content was passed via `echo '...'` which breaks on single quotes (C# code is full of them — `'{'`, string literals, char literals). The escaping `patch.Replace("'", "'\"'\"'")` is fragile and fails on multi-line patches with mixed quoting.
**Fix:** Write the patch to a temp file (`/tmp/attractor-patch-{guid}.diff`) and redirect via `patch -pN < tmpfile` instead of piping through echo. Temp file is cleaned up in a `finally` block.
**File:** `src/Soulcaster.CodingAgent/Profiles/OpenAiProfile.cs` (lines 211-228)
**Status:** APPLIED (but insufficient — see Fix 5)

---

## Fix 5: apply_patch generates incorrect context lines — use write_file instead

**Date:** 2026-02-18
**Symptom:** Even with temp-file fix, `patch -p0` found the correct files but hunks failed because Codex generated patches with incorrect context lines (e.g., expected `@implements IAsyncDisposable` on line 2 but actual file has `@rendermode InteractiveServer`). Hunks silently fail with `--forward --no-backup-if-mismatch`.
**Root cause:** Codex 5.2's `apply_patch` tool generates unified diffs with stale/incorrect context lines. The `patch` command rejects hunks that don't match. With `--forward --no-backup-if-mismatch`, failures are silent.
**Fix (dotfile):** Updated implement prompt to instruct Codex: "Do NOT use apply_patch — it is unreliable. Instead, use write_file to rewrite the entire file with your changes. For files over 200 lines, use the shell tool with a bash heredoc."
**File:** `dotfiles/betrayal-run.dot` (implement node prompt)

---

## Fix 6: Tool round limit too low for implementation

**Date:** 2026-02-18
**Symptom:** Codex hit `[Tool round limit reached]` after completing only 2 of 10 commits. Used ~95 of 100 allowed tool rounds, mostly on grep/read calls.
**Root cause:** `SessionConfig.MaxToolRoundsPerInput` was set to 100 in `runner/Program.cs`. Codex's implementation style uses many grep/read calls for exploration before each change.
**Fix:** Increased `MaxToolRoundsPerInput` from 100 to 300. Also added prompt guidance: "Be efficient with tool calls — read a file, make ALL changes to it at once, write it back."
**File:** `runner/Program.cs` (line 131)
