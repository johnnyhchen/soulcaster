# Templates

## Use case template

Generate one per user-facing feature found in the plan:

```markdown
#### UC-<NNN>: <action-oriented name>

- **Platform:** macOS / iOS / Both
- **Precondition:** <required app state>
- **Steps:**
  1. <user action> → `<automation command>`
  2. <user action> → `<automation command>`
- **Expected Result:** <what should be visible>
- **Verification:** Screenshot `<filename>`, look for `<specific text or layout>`
```

## Test matrix template

### Platform x Feature

Generate from the plan. One row per feature:

```markdown
| Feature | macOS | iOS | Notes |
|---------|:-----:|:---:|-------|
| App launch (no crash) | TEST | TEST | |
| Empty state display | TEST | TEST | |
| <feature from plan> | TEST / N/A / -- | TEST / N/A / -- | <context> |
```

- `TEST` = should be tested
- `--` = not implemented
- `N/A` = not applicable to platform

### State matrix

Each TEST cell should be run in:

| State | Description | Setup |
|-------|-------------|-------|
| **Fresh** | No persisted data | `rm -rf ~/Library/Application\ Support/default.store*` |
| **Populated** | 5-10 records across types | Seed via code or UI automation |
| **Stress** | 100+ records | Seed programmatically |

## Validation script template

Generate a project-specific version, substituting all variables:

```bash
#!/bin/bash
set -euo pipefail

# ── Configuration ─────────────────────────────────────────────
PROJECT_DIR="<absolute path>"
MACOS_SCHEME="<scheme>"
IOS_SCHEME="<scheme>"
SIMULATOR_ID="<uuid>"
BUNDLE_ID="<bundle.id>"
APP_PROCESS_NAME="<process name>"
SCREENSHOTS="$PROJECT_DIR/validation/screenshots"
RESULTS_FILE="$PROJECT_DIR/validation/validation-results.md"

mkdir -p "$SCREENSHOTS"

timestamp() { date "+%Y%m%d-%H%M%S"; }
screenshot_mac() { screencapture -x "$SCREENSHOTS/mac-$1-$(timestamp).png"; }
screenshot_ios() { xcrun simctl io $SIMULATOR_ID screenshot "$SCREENSHOTS/ios-$1-$(timestamp).png"; }
log() { echo "[$(timestamp)] $1" | tee -a "$RESULTS_FILE"; }

cat > "$RESULTS_FILE" << 'HEADER'
# Validation Results

| Test ID | Platform | Test | Result | Screenshot | Notes |
|---------|----------|------|--------|------------|-------|
HEADER

record() { echo "| $1 | $2 | $3 | **$4** | $5 | $6 |" >> "$RESULTS_FILE"; }

# ── Clean slate ───────────────────────────────────────────────
pkill -f $MACOS_SCHEME 2>/dev/null || true
xcrun simctl terminate $SIMULATOR_ID $BUNDLE_ID 2>/dev/null || true
rm -rf ~/Library/Application\ Support/default.store* 2>/dev/null || true
sleep 2

# ── macOS ─────────────────────────────────────────────────────
cd "$PROJECT_DIR"
swift run $MACOS_SCHEME 2>&1 &
APP_PID=$!
sleep 8

if ps -p $APP_PID > /dev/null 2>&1; then
    osascript -e "tell application \"$APP_PROCESS_NAME\" to activate"
    sleep 1
    screenshot_mac "launch"
    record "TC-001" "macOS" "App launches" "PASS" "mac-launch-*.png" ""
else
    record "TC-001" "macOS" "App launches" "FAIL" "" "Process exited"
fi

# <insert generated use case commands here>

pkill -f $MACOS_SCHEME 2>/dev/null || true

# ── iOS ───────────────────────────────────────────────────────
xcrun simctl boot $SIMULATOR_ID 2>/dev/null || true
open -a Simulator; sleep 5

BUILD_OUT=$(xcodebuild -scheme $IOS_SCHEME \
    -destination "platform=iOS Simulator,id=$SIMULATOR_ID" \
    build 2>&1 | tail -1)

if echo "$BUILD_OUT" | grep -q "BUILD SUCCEEDED"; then
    record "TC-B01" "iOS" "Build succeeds" "PASS" "" ""
else
    record "TC-B01" "iOS" "Build succeeds" "FAIL" "" "$BUILD_OUT"
fi

# (bundle assembly + install — see launching-apps.md Section 3-4)

LAUNCH_OUT=$(xcrun simctl launch $SIMULATOR_ID $BUNDLE_ID 2>&1)
sleep 5

if echo "$LAUNCH_OUT" | grep -q "$BUNDLE_ID"; then
    screenshot_ios "launch"
    record "TC-002" "iOS" "App launches" "PASS" "ios-launch-*.png" ""
else
    record "TC-002" "iOS" "App launches" "FAIL" "" "$LAUNCH_OUT"
fi

# <insert generated use case commands here>

xcrun simctl terminate $SIMULATOR_ID $BUNDLE_ID 2>/dev/null || true
log "Validation complete"
```

## QA plan output template

The full generated QA plan file should follow this structure:

```markdown
# QA Plan: <Project Name>

**Generated:** <date>
**Source:** <path to plan markdown>

## Targets

- macOS scheme: <name>
- iOS scheme: <name>
- Bundle ID: <id>
- Simulator: <uuid>

## Use Cases

<generated UC blocks>

## Test Matrix

<generated platform x feature table>
<state matrix>

## Automation Script

<generated bash script>
```
