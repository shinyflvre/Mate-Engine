#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT_DIR/Assets/MATE ENGINE - Scripts/Plugins/macOS/MateDesktopWindowMac.mm"
BUNDLE_DIR="$ROOT_DIR/Assets/MATE ENGINE - Scripts/Plugins/macOS/MateDesktopWindowMac.bundle"
OUT_DIR="$BUNDLE_DIR/Contents/MacOS"
OUT="$OUT_DIR/MateDesktopWindowMac"
TMP_DIR="$(mktemp -d)"

cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

SDK_PATH="$(xcrun --sdk macosx --show-sdk-path)"
COMMON_FLAGS=(
  -std=c++17
  -fobjc-arc
  -fblocks
  -bundle
  -isysroot "$SDK_PATH"
  -mmacosx-version-min=12.0
  -framework Cocoa
  -framework CoreGraphics
)

mkdir -p "$OUT_DIR"

xcrun clang++ "${COMMON_FLAGS[@]}" -arch x86_64 "$SRC" -o "$TMP_DIR/MateDesktopWindowMac.x86_64"
xcrun clang++ "${COMMON_FLAGS[@]}" -arch arm64 "$SRC" -o "$TMP_DIR/MateDesktopWindowMac.arm64"
lipo -create "$TMP_DIR/MateDesktopWindowMac.x86_64" "$TMP_DIR/MateDesktopWindowMac.arm64" -output "$OUT"
codesign --force --sign - "$OUT"

lipo -info "$OUT"
otool -L "$OUT"
