# Provider Coverage Plan

## Goal

Broaden provider support for the attachment and continuity features already present in the runner without weakening fail-closed behavior.

This plan turns the current high-level roadmap bucket into an execution order.

## Status

- Item 1 is shipped: attachment-input capability flags now exist in the model catalog, registry overlays, and fail-closed validation path.
- Item 2 is shipped: OpenAI document inputs now execute through the adapter and are covered by unit and runner proofs.
- Item 3 is shipped: OpenAI audio inputs now execute through the Chat Completions audio path, route to `gpt-audio`, and are covered by unit and runner proofs.
- Item 4 is shipped: Anthropic document inputs now execute through the Messages API path and are covered by unit and runner proofs.
- Item 5 is shipped as explicit no-support: Anthropic audio inputs now fail closed in capability validation and are covered by unit and runner proofs.
- Item 6 is shipped: cross-provider continuity is now explicit and provider-specific. OpenAI chains follow-up image turns through `previous_response_id`, Gemini preserves replayable media state plus text/media `thoughtSignature` payloads, and Anthropic follow-up document continuity is verified through persisted raw history.

## Current State

- Gemini is still the broadest adapter today. It can serialize and parse image, document, and audio parts, and it now preserves replayable media state plus `thoughtSignature` payloads across follow-up turns.
- OpenAI now supports text, image, document, and audio input paths in the current runtime, and image-generation follow-up turns chain through `previous_response_id` instead of lossy assistant-message reconstruction.
- Anthropic now supports text, image, and document inputs in the current runtime, with document follow-up continuity verified through persisted raw history. Anthropic audio remains intentionally unsupported and fail closed, and Anthropic still does not support assistant image output in the current adapter.
- The capability layer now validates image, document, and audio inputs explicitly in addition to tools, vision, reasoning, latency, budget, and image output.

## Priority Order

### 1. Add attachment capability flags and fail-closed validation [Shipped]

Why first:

- The runtime should reject unsupported attachment requests before hitting provider adapters.
- Provider-expansion work is safer once the capability model can describe the new surface area.

Scope:

- Extend model/provider capability data with explicit attachment-input flags.
- Extend the registry and override path so those flags can be discovered or patched.
- Extend capability validation so stages with image, document, or audio inputs fail closed when the selected provider/model cannot satisfy them.
- Decide whether continuity support needs an explicit capability flag in addition to the current `RequiresContinuityTokens` field.

Primary files:

- `src/Soulcaster.UnifiedLlm/Models/ModelInfo.cs`
- `src/Soulcaster.UnifiedLlm/Providers/IProviderDiscoveryAdapter.cs`
- `src/Soulcaster.UnifiedLlm/ModelCatalog.cs`
- `src/Soulcaster.UnifiedLlm/ModelRegistry.cs`
- `src/Soulcaster.UnifiedLlm/ModelCapabilityValidator.cs`
- `src/Soulcaster.Attractor/Handlers/CodergenHandler.cs`
- `tests/Soulcaster.Tests/UnifiedLlmTests.cs`
- `tests/Soulcaster.Tests/CodergenConsensusPlanTests.cs`

Definition of done:

- Stages with unsupported attachment inputs fail in capability validation rather than adapter-specific runtime exceptions.
- Registry snapshots and override files can represent the new capability flags.

### 2. Add OpenAI document input support [Shipped]

Why second:

- OpenAI already has strong Responses API plumbing, image support, discovery, scorecards, and routing in this repo.
- Document support removes the biggest gap in non-Gemini leaf review flows.

Scope:

- Serialize `ContentKind.Document` for OpenAI leaf requests.
- Preserve any provider-state payload that should be replayable on later turns.
- Update catalog and registry metadata for the OpenAI models that can actually take document inputs.
- Add real leaf-lane proof coverage with document attachments.

Primary files:

- `src/Soulcaster.UnifiedLlm/Providers/OpenAIAdapter.cs`
- `src/Soulcaster.UnifiedLlm/ModelCatalog.cs`
- `src/Soulcaster.UnifiedLlm/ModelRegistry.cs`
- `tests/Soulcaster.Tests/UnifiedLlmTests.cs`
- `tests/Soulcaster.Tests/ProcessRunScenarioTests.cs`

Definition of done:

- An OpenAI leaf stage can ingest an attached document and complete successfully.
- Capability validation and adapter behavior agree on which OpenAI models support the path.

### 3. Add OpenAI audio input support [Shipped]

Why third:

- It shares most of the same plumbing and validation surface as OpenAI document input.
- It closes the next most obvious parity gap for leaf review and transcription-style flows.

Scope:

- Serialize `ContentKind.Audio` for OpenAI leaf requests.
- Carry file name and media type correctly through the adapter.
- Update model metadata and tests for the supported OpenAI models.

Primary files:

- `src/Soulcaster.UnifiedLlm/Providers/OpenAIAdapter.cs`
- `src/Soulcaster.UnifiedLlm/Models/ContentData.cs`
- `src/Soulcaster.UnifiedLlm/ModelCatalog.cs`
- `tests/Soulcaster.Tests/UnifiedLlmTests.cs`
- `tests/Soulcaster.Tests/ProcessRunScenarioTests.cs`

Definition of done:

- An OpenAI leaf stage can ingest an attached audio file and complete successfully.
- OpenAI document and audio support are both covered by real runner proofs, not only unit tests.

### 4. Add Anthropic document input support [Shipped]

Why fourth:

- Claude remains a key reasoning provider in the repo.
- Document review is more central than audio for current workflows.

Scope:

- Serialize document inputs through the Anthropic messages API if the target model supports them.
- If the API or a model family does not support the path, encode that explicitly in capability metadata and keep the runtime fail closed.
- Add document-attachment tests and at least one real document review proof flow.

Primary files:

- `src/Soulcaster.UnifiedLlm/Providers/AnthropicAdapter.cs`
- `src/Soulcaster.UnifiedLlm/ModelCatalog.cs`
- `src/Soulcaster.UnifiedLlm/ModelRegistry.cs`
- `tests/Soulcaster.Tests/UnifiedLlmTests.cs`
- `tests/Soulcaster.Tests/ProcessRunScenarioTests.cs`

Definition of done:

- Anthropic document support is either implemented end to end or explicitly blocked in capability validation with accurate metadata.

### 5. Add Anthropic audio input support or codify permanent no-support [Shipped as explicit no-support]

Why fifth:

- It is lower-value than document review for present workflows.
- It is also the most likely path to be API-constrained.

Scope:

- Attempt clean audio-input support through the Anthropic adapter if the target models and API shape allow it.
- If not, turn the current implicit adapter exception into an explicit capability outcome with tests and docs.

Primary files:

- `src/Soulcaster.UnifiedLlm/Providers/AnthropicAdapter.cs`
- `src/Soulcaster.UnifiedLlm/ModelCatalog.cs`
- `src/Soulcaster.UnifiedLlm/ModelRegistry.cs`
- `src/Soulcaster.UnifiedLlm/ModelCapabilityValidator.cs`
- `tests/Soulcaster.Tests/UnifiedLlmTests.cs`

Definition of done:

- Anthropic audio handling has an intentional final state: supported with proof, or explicitly unsupported with fail-closed validation.

### 6. Normalize cross-provider continuity and replay behavior [Shipped]

Why last:

- Basic attachment parity matters more than continuity polish.
- The runtime already has a solid Gemini image continuity path; the next step is to make provider-state behavior explicit and inspectable across adapters.

Scope:

- Define which media provider-state payloads are safe and necessary to replay on later turns.
- Normalize replay behavior across Gemini, OpenAI, and Anthropic where provider APIs expose similar semantics.
- Tighten tests so replayable media continuity is asserted intentionally rather than incidentally.

Primary files:

- `src/Soulcaster.UnifiedLlm/Models/ContentData.cs`
- `src/Soulcaster.UnifiedLlm/Providers/GeminiAdapter.cs`
- `src/Soulcaster.UnifiedLlm/Providers/OpenAIAdapter.cs`
- `src/Soulcaster.UnifiedLlm/Providers/AnthropicAdapter.cs`
- `runner/AgentCodergenBackend.cs`
- `tests/Soulcaster.Tests/UnifiedLlmTests.cs`
- `tests/Soulcaster.Tests/ProcessRunScenarioTests.cs`

Definition of done:

- Media continuity semantics are intentional, provider-specific where needed, and covered by replay-focused tests.

## Execution Batches

Batch A:

- Item 1 only. Status: shipped.

Batch B:

- Items 2 and 3 together. Status: shipped.

Batch C:

- Items 4 and 5 together. Status: shipped.

Batch D:

- Item 6. Status: shipped.

## Validation Strategy

- Keep `UnifiedLlmTests` focused on request/response shaping, capability validation, and provider-state replay.
- Keep `ProcessRunScenarioTests` focused on real leaf-lane flows using document and audio attachments.
- Preserve fail-closed behavior: if a provider cannot support a capability, tests should prove that the runtime rejects the configuration before provider execution.
- For each shipped provider expansion, run at least one real Soulcaster flow and save proof artifacts under a temporary bundle.

## Success Criteria

- Provider coverage no longer means "Gemini-only" for document and audio leaf flows.
- Capability metadata, adapter behavior, and runner proofs all agree.
- Unsupported provider paths fail in validation, not deep inside request execution.

This plan is now complete. Any further provider-expansion work should be tracked in `PRODUCT-ROADMAP.md`.
