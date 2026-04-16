# Naming And Namespace Refactor Plan

## Goal

Make the codebase more internally consistent without changing runtime behavior.

## Findings

- Folder layout and namespaces do not match. Most files under `Execution`, `Handlers`, `HumanInTheLoop`, `Profiles`, `Session`, `Tools`, `Models`, `Providers`, and related folders still declare the root project namespace.
- `OpenAi*` type names conflict with the rest of the repo's `OpenAI` spelling in docs, strings, and test names.
- `SubAgent` conflicts with `SubagentTools`, `SpawnSubagent`, `CloseSubagent`, `GetSubagent`, and `MaxSubagentDepth`.
- The test project is also flat and does not reflect the production layout, which makes large files harder to navigate.

## Pre-Refactor Behavior Contract

These checks passed before the refactor and must still pass after it:

- `dotnet build`
- `dotnet test --no-build`

Baseline result on 2026-04-15:

- Build: success
- Tests: `394` passed, `0` failed, `0` skipped

Known pre-existing warnings observed during the baseline run:

- CA2024 in provider streaming adapters
- xUnit analyzer warnings in `tests/JcAttractor.Tests/AttractorTests.cs`

Those warnings are not part of this refactor.

## Scope

In scope:

- Align namespaces with folder structure in:
  - `src/JcAttractor.Attractor`
  - `src/JcAttractor.CodingAgent`
  - `src/JcAttractor.UnifiedLlm`
  - `tests/JcAttractor.Tests`
- Rename `OpenAiAdapter` to `OpenAIAdapter`
- Rename `OpenAiProfile` to `OpenAIProfile`
- Rename `SubAgent` to `Subagent`
- Update all references and tests accordingly

Out of scope:

- Renaming `JcAttractor.*` projects or namespaces to `Soulcaster`
- Replacing the `dotfiles/` convention
- Moving roadmap/proposal/root docs
- Splitting large source or test files beyond what is necessary for this naming pass

Intentional exceptions:

- `GraphModel.cs` stays in `JcAttractor.Attractor` because `JcAttractor.Attractor.Graph.Graph` creates a namespace/type collision around `Graph`
- Session files stay in `JcAttractor.CodingAgent` because `JcAttractor.CodingAgent.Session.Session` creates the same collision around `Session`

## Execution Plan

1. Create folder-aligned namespaces in production projects.
2. Update tests to follow the same namespace layout where it improves coherence.
3. Normalize inconsistent type names for `OpenAI` and `Subagent`.
4. Rebuild and run the full test suite.
5. Compare results against the pre-refactor behavior contract.

## Success Criteria

- The code compiles without new warnings introduced by the refactor.
- The full existing test suite remains green.
- Navigation improves because namespace names now mirror directory structure.
- Type naming is internally consistent for `OpenAI` and `Subagent`.
