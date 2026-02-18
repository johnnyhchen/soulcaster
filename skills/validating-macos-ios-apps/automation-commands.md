# Automation Commands

## macOS — AppleScript via osascript

### Activate app
```bash
osascript -e "tell application \"$APP_PROCESS_NAME\" to activate"
```

### Click toolbar button
```bash
osascript <<EOF
tell application "System Events"
    tell process "$APP_PROCESS_NAME"
        click button 1 of toolbar 1 of window 1
    end tell
end tell
EOF
```

### Click element by text
```bash
osascript <<EOF
tell application "System Events"
    tell process "$APP_PROCESS_NAME"
        click static text "$ELEMENT_TEXT" of window 1
    end tell
end tell
EOF
```

### Type text
```bash
osascript <<EOF
tell application "System Events"
    tell process "$APP_PROCESS_NAME"
        keystroke "$TEXT"
        keystroke tab
    end tell
end tell
EOF
```

### Dump UI hierarchy (debugging)
```bash
osascript <<EOF
tell application "System Events"
    tell process "$APP_PROCESS_NAME"
        entire contents of window 1
    end tell
end tell
EOF
```

Use Accessibility Inspector (Xcode > Open Developer Tool > Accessibility Inspector) for visual hierarchy exploration.

## iOS — simctl

### Tap
```bash
xcrun simctl io $SIMULATOR_ID tap $X $Y
```

### Type
```bash
xcrun simctl io $SIMULATOR_ID type "$TEXT"
```

### Swipe
```bash
xcrun simctl io $SIMULATOR_ID swipe $X1 $Y1 $X2 $Y2 $DURATION
```

### Pull to refresh
```bash
xcrun simctl io $SIMULATOR_ID swipe 200 300 200 600 0.3
```

### Hardware button
```bash
xcrun simctl io $SIMULATOR_ID pressButton home
```

### Deep link
```bash
xcrun simctl openurl $SIMULATOR_ID "$URL"
```

## Improving reliability

Add accessibility identifiers to SwiftUI views for stable element targeting:

```swift
Button("Action") { ... }
    .accessibilityIdentifier("toolbar-action")

Text("Section")
    .accessibilityIdentifier("sidebar-section")
```

For CI-level reliability, use XCUITest instead of AppleScript:

```swift
import XCTest

final class AppUITests: XCTestCase {
    func testLaunchShowsEmptyState() {
        let app = XCUIApplication()
        app.launch()
        XCTAssertTrue(app.staticTexts["placeholder-text"].exists)
    }
}
```

Run: `xcodebuild test -scheme $SCHEME -destination "platform=iOS Simulator,id=$SIMULATOR_ID"`
