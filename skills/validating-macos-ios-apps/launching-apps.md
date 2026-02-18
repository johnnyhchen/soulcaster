# Launching Apps

## macOS — SPM executable

```bash
cd $PROJECT_DIR
swift run $MACOS_SCHEME 2>&1 &
APP_PID=$!
sleep 8
ps -p $APP_PID > /dev/null 2>&1 && echo "RUNNING" || echo "CRASHED"
```

Kill: `pkill -f $MACOS_SCHEME`

## macOS — Xcode project

```bash
xcodebuild -scheme $MACOS_SCHEME -configuration Debug build 2>&1 | tail -3
open "$DERIVED_DATA_PATH/$MACOS_SCHEME.app"
```

## iOS — full pipeline

### 1. Boot simulator

```bash
xcrun simctl list devices available | grep "iPhone"
SIMULATOR_ID="<uuid>"
xcrun simctl boot $SIMULATOR_ID
open -a Simulator
```

### 2. Build

```bash
xcodebuild -scheme $IOS_SCHEME \
  -destination "platform=iOS Simulator,id=$SIMULATOR_ID" \
  build 2>&1 | tail -5
```

### 3. Create .app bundle (SPM only)

SPM produces bare Mach-O binaries. The simulator requires a `.app` bundle:

```bash
DERIVED=$(find ~/Library/Developer/Xcode/DerivedData/$PROJECT_NAME-*/Build/Products/Debug-iphonesimulator -maxdepth 0)

mkdir -p "$DERIVED/$IOS_SCHEME.app"
cp "$DERIVED/$IOS_SCHEME" "$DERIVED/$IOS_SCHEME.app/$IOS_SCHEME"
cp -R "$DERIVED/PackageFrameworks" "$DERIVED/$IOS_SCHEME.app/Frameworks" 2>/dev/null

cat > "$DERIVED/$IOS_SCHEME.app/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>    <string>$IOS_SCHEME</string>
    <key>CFBundleIdentifier</key>    <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>          <string>$APP_NAME</string>
    <key>CFBundleVersion</key>       <string>1</string>
    <key>CFBundleShortVersionString</key> <string>1.0</string>
    <key>CFBundlePackageType</key>   <string>APPL</string>
    <key>UILaunchScreen</key>        <dict/>
    <key>LSRequiresIPhoneOS</key>    <true/>
    <key>CFBundleSupportedPlatforms</key>
    <array><string>iPhoneSimulator</string></array>
    <key>MinimumOSVersion</key>      <string>18.0</string>
    <key>DTPlatformName</key>        <string>iphonesimulator</string>
</dict>
</plist>
EOF
```

### 4. Install and launch

```bash
xcrun simctl install $SIMULATOR_ID "$DERIVED/$IOS_SCHEME.app"
xcrun simctl launch $SIMULATOR_ID $BUNDLE_ID
```

Kill: `xcrun simctl terminate $SIMULATOR_ID $BUNDLE_ID`
