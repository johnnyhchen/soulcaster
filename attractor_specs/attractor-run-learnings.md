# Attractor Pipeline Run: Learnings & Fixes

Lessons learned from running `coppermind.dot` — a 46-node multi-model fan-out/fan-in consensus pipeline that generates a Swift note-taking app.

---

## 1. File Path References Between Nodes

**Problem:** Agents in later pipeline nodes couldn't find files written by earlier nodes. Prompts said "Read ORIENT.md" but files were written to `output/logs/ORIENT.md`.

**Fix:** All file references in node prompts must include the `logs/` prefix (the working directory for the pipeline output). Every prompt that references a prior node's output needs the full relative path.

**Lesson:** Never assume agents share an implicit working directory. Be explicit about paths in every prompt.

---

## 2. Stylesheet Property Key Names

**Problem:** The dotfile stylesheet used `llm_model` and `llm_provider` as property keys, but `StylesheetTransform.cs` (line 26, 29) checks for `model` and `provider`.

```dot
/* WRONG */
.plan_opus { llm_provider = "anthropic"; llm_model = "claude-opus-4-6" }

/* CORRECT */
.plan_opus { provider = "anthropic"; model = "claude-opus-4-6" }
```

**Fix:** Global find-replace: `llm_provider` → `provider`, `llm_model` → `model`.

**Lesson:** Check the actual transform source code (`StylesheetTransform.cs`) for the expected key names rather than guessing conventions.

---

## 3. Anthropic Extended Thinking + Multi-Turn Signature Error

**Problem:** Setting `reasoning_effort = "high"` in the stylesheet triggered Anthropic's extended thinking mode. On multi-turn conversations (which pipeline nodes become after tool calls), the adapter hit:
```
[Error: messages.1.content.0.thinking.signature: Field required]
```
The thinking block from turn 1 needs a `signature` field to be replayed in turn 2, but the adapter didn't handle this.

**Fix:** Removed all `reasoning_effort` settings from the stylesheet. Extended thinking is not needed for code generation tasks in the pipeline.

**Lesson:** Avoid `reasoning_effort` in multi-turn Attractor nodes unless the adapter explicitly handles thinking block signatures across turns.

---

## 4. OpenAI Strict Mode Tool Schema Validation

**Problem:** OpenAI's function calling with `"strict": true` and `"additionalProperties": false` requires **every** property in the schema to be listed in the `required` array — including optional parameters.

```
[Error: Invalid schema for function 'read_file': 'required' is required to be
supplied and to be an array including every key in properties. Missing 'offset'.]
```

**Fix:** Modified `OpenAiAdapter.cs` to:
1. Add ALL parameters to the `required` array (not just the ones marked required)
2. Make optional parameters nullable: `["type"] = new JsonArray(param.Type, "null")`

```csharp
// For optional params, make them nullable
if (!param.Required)
    prop["type"] = new JsonArray(param.Type, "null");

// OpenAI strict mode: all properties must be in required
required.Add(param.Name);
```

**Lesson:** OpenAI's strict schema mode has a different definition of "required" than JSON Schema. Every property must appear in `required`; optionality is expressed through nullable types instead.

---

## 5. OpenAI Model Names

**Problem (original):** The stylesheet initially used model names that didn't exist in the catalog. The pipeline silently fell back to defaults or errored.

**Fix:** Updated `ModelCatalog.cs` to include the correct model IDs. As of Feb 2026, the available OpenAI models are:

| Model ID | Use Case |
|----------|----------|
| `gpt-5.2` | Flagship general reasoning |
| `gpt-5.2-pro` | Higher-effort reasoning |
| `gpt-5.2-codex` | Agentic coding (alias: `codex-5.2`) |
| `gpt-5.1-codex-max` | Most capable coding model |
| `gpt-5.2-mini` | Fast, lightweight |

**Note:** `gpt-5.3-codex` has been announced but is not yet available via API.

**Lesson:** Verify model names against `ModelCatalog.cs` or hit the OpenAI `/v1/models` endpoint before running. The catalog must match what the API actually serves.

---

## 6. Large File Writes via Tool Calls

**Problem:** Agents using the `write_file` tool struggled with very large files (1000+ line architecture documents). The tool call would fail or produce truncated output.

**Fix:** Added instructions to all prompts that produce large output:
> "For files over 100 lines, use the bash tool with a heredoc instead of write_file."

```bash
cat > logs/ARCH-OPUS.md << 'ARCHITECTURE_EOF'
... content ...
ARCHITECTURE_EOF
```

**Lesson:** Always provide agents with a fallback strategy for large file writes. The `bash` tool with heredoc is more reliable for large content than `write_file`.

---

## 7. Leftover Files From Previous Runs

**Problem:** The `output/logs/` directory contained `.cs` files from a previous pipeline run (`betrayal.dot`). The runner project tried to compile these, causing build errors:
```
error CS0246: The type or namespace name 'BetrayalEngine' could not be found
```

**Fix:** Cleaned the `output/logs/` directory before running. Only keep files relevant to the current pipeline.

**Lesson:** Always clean the output directory between pipeline runs, especially when switching between different dotfiles. Stale artifacts will be picked up by the build system.

---

## 8. Multi-Model Consensus Produces Architectural Misalignment

**Problem:** The fan-out/fan-in pattern had two models (Claude Opus and GPT o3) independently design architecture, then a judge merged them. Later implementation nodes built models and engines against different assumptions:
- The **@Model SwiftData classes** (Note, Connection, etc.) used rich property names: `title`, `body`, `priorityScore`, `dueDate`, relationships via `@Relationship`
- The **engine files** (NoteStore, PriorityRanker, etc.) assumed a simpler Note with: `text`, `priority`, `deadline`, `connectionIDs`

This created ~30+ compile errors from property name mismatches across 6 engine files and all integration tests.

**Fix (pending):** Mechanically update all engine files and tests to use the correct property names from the @Model classes.

**Lesson:** When using multi-model consensus for code generation:
1. The **judge/merge node** must produce a single canonical API surface (property names, types, method signatures) that all subsequent nodes reference
2. Implementation nodes should read the merged architecture doc, not their own model's version
3. Consider adding a "contract verification" node that checks interface compatibility before implementation begins

---

## 9. Pipeline Node Prompt Best Practices

Things that worked well in prompts:
- **Explicit file paths** with `logs/` prefix in every reference
- **Bash heredoc instructions** for large output files
- **"Read X then Y then Z"** ordering that matches the dependency graph
- **Concrete output format** specifications ("Write the file to `logs/FILENAME.swift`")

Things that didn't work:
- **Implicit path assumptions** ("Read ORIENT.md" without path)
- **Vague output instructions** ("Write your architecture plan") without specifying filename/location
- **Assuming agents remember context** from other nodes — each node starts fresh

---

## 10. Pipeline Run Statistics

| Metric | Value |
|--------|-------|
| Total nodes | 46 |
| Total edges | 57 |
| Model classes | 6 (plan_opus, plan_gpt, impl_codex, impl_opus, judge_opus, judge_gpt) |
| Fan-out/fan-in rounds | 8 |
| Generated Swift files | 55 |
| Architecture docs | 3 (ORIENT.md, ARCH-OPUS.md, ARCH-GPT.md) |
| Pipeline runtime | ~2 hours |
| Iterations to successful run | 4 (path fix → clean build → stylesheet fix → schema fix) |

---

## Recommended Dotfile Stylesheet Template

Based on what actually works:

```dot
stylesheet {
    * {
        provider = "anthropic";
        model = "claude-sonnet-4-5-20250514";
    }
    .plan_opus {
        provider = "anthropic";
        model = "claude-opus-4-6";
    }
    .plan_gpt {
        provider = "openai";
        model = "o3";
    }
    .impl_codex {
        provider = "openai";
        model = "o3";
    }
    .impl_opus {
        provider = "anthropic";
        model = "claude-opus-4-6";
    }
    .judge_opus {
        provider = "anthropic";
        model = "claude-opus-4-6";
    }
    .judge_gpt {
        provider = "openai";
        model = "o3";
    }
}
```

**Do NOT include:** `reasoning_effort`, `temperature`, or other unsupported stylesheet properties.
