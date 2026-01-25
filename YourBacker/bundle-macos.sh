#!/bin/bash

# Build and bundle YourBacker as a macOS .app
# Usage: ./bundle-macos.sh [Release|Debug]

set -e

CONFIG="${1:-Release}"
APP_NAME="YourBacker"
BUNDLE_NAME="$APP_NAME.app"
OUTPUT_DIR="./publish/macos"
BUNDLE_PATH="$OUTPUT_DIR/$BUNDLE_NAME"

echo "Building $APP_NAME for macOS ($CONFIG)..."

# Clean previous bundle
rm -rf "$BUNDLE_PATH"

# Publish the app
dotnet publish -c "$CONFIG" -r osx-arm64 --self-contained true -o "$OUTPUT_DIR/temp"

# Create app bundle structure
mkdir -p "$BUNDLE_PATH/Contents/MacOS"
mkdir -p "$BUNDLE_PATH/Contents/Resources"

# Copy the published files to MacOS folder
cp -R "$OUTPUT_DIR/temp/"* "$BUNDLE_PATH/Contents/MacOS/"

# Copy Info.plist
cp Info.plist "$BUNDLE_PATH/Contents/"

# Copy the icon
if [ -f "Assets/YourBacker.icns" ]; then
    cp "Assets/YourBacker.icns" "$BUNDLE_PATH/Contents/Resources/"
    echo "✓ Icon copied"
else
    echo "⚠ Warning: Assets/YourBacker.icns not found"
fi

# Create PkgInfo file
echo -n "APPL????" > "$BUNDLE_PATH/Contents/PkgInfo"

# Make the executable... executable
chmod +x "$BUNDLE_PATH/Contents/MacOS/$APP_NAME"

# Clean up temp folder
rm -rf "$OUTPUT_DIR/temp"

echo ""
echo "✓ App bundle created: $BUNDLE_PATH"
echo ""
echo "To run:    open $BUNDLE_PATH"
echo "To install: cp -R $BUNDLE_PATH /Applications/"
