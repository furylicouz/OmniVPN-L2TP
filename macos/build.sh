#!/bin/bash
# Сборка OmniVPN Connect для macOS: universal (Apple Silicon + Intel), .app и .dmg.
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Info.plist)
APP="build/OmniVPN Connect.app"
SDK=$(xcrun --show-sdk-path --sdk macosx)
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

for ARCH in arm64 x86_64; do
  echo "[swiftc] $ARCH"
  xcrun swiftc -O -sdk "$SDK" -target "$ARCH-apple-macos12.0" -module-name L2TPConnect \
    -o "build/L2TPConnect-$ARCH" Sources/*.swift
done
lipo -create -output "$APP/Contents/MacOS/OmniVPN Connect" build/L2TPConnect-arm64 build/L2TPConnect-x86_64
cp Info.plist "$APP/Contents/Info.plist"
iconutil -c icns -o "$APP/Contents/Resources/AppIcon.icns" AppIcon.iconset
codesign --force --sign "${SIGN_IDENTITY:--}" --timestamp=none "$APP"
codesign --verify --verbose=2 "$APP"
lipo -info "$APP/Contents/MacOS/OmniVPN Connect"

STAGE=build/dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -quiet -volname "OmniVPN Connect" -srcfolder "$STAGE" -ov -format UDZO "build/OmniVPNConnect-$VERSION.dmg"
echo "BUILD OK: build/OmniVPNConnect-$VERSION.dmg"
