---
name: validating-macos-ios-apps
description: Generates and executes QA validation plans for macOS and iOS SwiftUI apps. Reads a plan/spec markdown, produces a test matrix with use cases, then launches apps, automates interactions via AppleScript and simctl, captures screenshots, and writes a results markdown. Use when validating a macOS or iOS app, running UI smoke tests, or generating a QA plan from a feature spec.
---

# Validating macOS & iOS Apps

Two-phase process: **generate** a QA plan from a spec, then **execute** it.

## Workflow

Copy this checklist and track progress:

```
QA Validation:
- [ ] Phase 1: Read plan markdown and scan project
- [ ] Phase 2: Generate QA plan (use cases + test matrix + script)
- [ ] Phase 3: Execute validation (launch, automate, screenshot)
- [ ] Phase 4: Review screenshots and write results
```

## Phase 1: Read plan and scan project

1. Read the plan/spec markdown the user provides
2. Extract: features, screens, platform targets, data models, navigation flows
3. Scan the project:
   ```
   Glob **/*.swift
   Grep "@main" to find app entry points
   Grep "struct.*View.*: View" for all views
   Read Package.swift or *.xcodeproj for targets/schemes
   ```
4. Identify: macOS scheme name, iOS scheme name, bundle ID, process name

## Phase 2: Generate QA plan

Write to `<project>/validation/qa-plan-<date>.md` using the templates in [templates.md](templates.md).

The QA plan must contain:
- **Targets** — scheme names, bundle ID, simulator UUID
- **Use cases** — generated from the spec (use the UC template)
- **Test matrix** — platform x feature grid + state matrix (fresh/populated/stress)
- **Automation script** — parameterized bash script ready to run

## Phase 3: Execute validation

**Launching apps**: See [launching-apps.md](launching-apps.md)
**Automating interactions**: See [automation-commands.md](automation-commands.md)

For each use case in the QA plan:
1. Set up precondition (clear data, seed records, etc.)
2. Run the automation commands
3. Capture screenshot after each step
4. Record result as PASS, FAIL, or MANUAL

### Screenshots

macOS:
```bash
osascript -e "tell application \"$APP_PROCESS_NAME\" to activate"
sleep 1
screencapture -x $SCREENSHOTS/mac-$TEST_ID.png
```

iOS:
```bash
xcrun simctl io $SIMULATOR_ID screenshot $SCREENSHOTS/ios-$TEST_ID.png
```

Read screenshots with the Read tool to visually verify expected state.

### Coordinate discovery for iOS taps

1. `xcrun simctl io $SIMULATOR_ID screenshot /tmp/screen.png`
2. `Read /tmp/screen.png`
3. Multiply displayed coordinates by the scale factor in the `[Image: ...]` annotation
4. Use adjusted coordinates with `xcrun simctl io $SIMULATOR_ID tap $X $Y`

## Phase 4: Write results

Append to `<project>/validation/validation-results-<date>.md`:

```markdown
# Validation Results

**Date:** <date>
**Commit:** <git rev>

| Test ID | Platform | Test | Result | Screenshot | Notes |
|---------|----------|------|--------|------------|-------|
| TC-001  | macOS    | ...  | PASS   | mac-...png | ...   |

## Summary

### Passing
- ...

### Failing
- ...

### Not Implemented
- ...
```

## Clearing persisted data between runs

```bash
# SwiftData / Core Data
rm -rf ~/Library/Application\ Support/default.store*

# iOS simulator full reset
xcrun simctl erase $SIMULATOR_ID
```

## Known limitations

| Limitation | Workaround |
|------------|------------|
| AppleScript paths fragile for SwiftUI | Add `.accessibilityIdentifier()` to views |
| `simctl tap` needs exact coordinates | Screenshot + coordinate math (see above) |
| SPM doesn't produce `.app` bundles | Manual bundle assembly (see [launching-apps.md](launching-apps.md)) |
| `screencapture -w` needs window focus | `osascript activate` before capture |
