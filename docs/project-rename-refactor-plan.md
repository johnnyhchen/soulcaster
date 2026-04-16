# Project Rename Refactor Plan

## Goal

Rename the codebase identity from `JcAttractor` to `Soulcaster` in project names, namespaces, file paths, and hard-coded repository references while preserving runtime behavior.

## Scope

In scope:

- Rename the solution file
- Rename project folders and `.csproj` files under `src/` and `tests/`
- Rename namespaces from `JcAttractor.*` to `Soulcaster.*`
- Rename runner namespace references from `JcAttractor.Runner` to `Soulcaster.Runner`
- Update scripts, docs, tests, and DOT files that reference repo-local paths under `src/JcAttractor.*` or `tests/JcAttractor.Tests`

Out of scope:

- Replacing the `dotfiles/` convention
- Renaming the `runner/` directory itself
- Large functional rewrites unrelated to the rename

## Pre-Refactor Behavior Contract

These checks passed before the rename and must still pass after it:

- `dotnet build`
- `dotnet test --no-build`

Baseline result on 2026-04-15:

- Build: success, `0` warnings, `0` errors
- Tests: `394` passed, `0` failed, `0` skipped

Additional runtime validation required after the rename:

- Run `dotfiles/qa-multimodel.dot` with all three provider keys present
- Confirm the run completes successfully
- Confirm the generated artifacts show Anthropic, OpenAI, and Gemini stages all executed

## Execution Plan

1. Rename the filesystem layout from `JcAttractor.*` to `Soulcaster.*`.
2. Update solution and project references to the new file names and paths.
3. Update namespaces and `using` directives across source, runner, and tests.
4. Update scripts, docs, and DOT files that use repo-local `JcAttractor` paths.
5. Rebuild and rerun the full test suite.
6. Run the multi-provider QA DOT file with a clean working directory and inspect the output artifacts.

## Success Criteria

- The repo builds under the renamed solution/project structure.
- The existing automated suite remains green.
- The multi-provider QA run still completes and writes provider-specific artifacts for Anthropic, OpenAI, and Gemini.

## Execution Notes

- The rename was completed across the solution, project folders, `.csproj` files, namespaces, runner references, scripts, tests, and DOT files.
- During post-rename runtime validation, the OpenAI adapter exposed a pre-existing schema issue for array-valued tool parameters. That path was fixed so OpenAI now emits `items` metadata for array tool schemas.
- The live multi-provider run then exposed a separate engine bug: graph-level `default_max_retry` was parsed but not honored by the runtime. That path was fixed, and the engine now applies graph-level retry budgets when a node does not override `max_retries`.

## Post-Refactor Validation

Validated on 2026-04-15:

- `dotnet build Soulcaster.sln`: success
- `dotnet test Soulcaster.sln --no-build`: `396` passed, `0` failed, `0` skipped
- `dotfiles/qa-multimodel.dot`: Anthropic and OpenAI completed successfully; Gemini hit a retryable upstream `503` (`This model is currently experiencing high demand`) and exhausted the configured retry budget. The retry path itself is now validated because the runner retried Gemini once instead of failing immediately with `Retry requested but no retry budget is configured`.

Revalidated on 2026-04-16 after switching the Gemini leg in `dotfiles/qa-multimodel.dot` to `gemini-3-flash-preview` and tightening provider-failure reporting:

- `dotnet build Soulcaster.sln --nologo --no-restore`: success, `0` warnings, `0` errors
- `dotnet test Soulcaster.sln --nologo --no-restore`: `399` passed, `0` failed, `0` skipped
- `dotnet run --project runner -- run dotfiles/qa-multimodel.dot --resume-from /tmp/... --no-autoresume`: success
- Generated provider artifacts recorded:
  - `provider=anthropic model=claude-sonnet-4-6`
  - `provider=openai model=codex-5.2`
  - `provider=gemini model=gemini-3-flash-preview`
- Stage status artifacts for Anthropic, OpenAI, and Gemini all recorded `provider_state=completed` and `contract_state=validated`.
