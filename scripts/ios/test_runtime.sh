#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/env.sh"

dotnet build "$IOS_ROOT/Project/Sonic Remake Project.csproj" -p:GodotTargetPlatform=macos -v minimal \
  > "$IOS_BUILD_DIR/gameplay-build.log" 2>&1
"$GODOT" --path "$IOS_ROOT/Project" --rendering-method mobile --resolution 1280x720 --windowed \
  res://tests/ios/IosSmokeTest.tscn -- --ios-smoke-test --touch-controls \
  "--smoke-output=$IOS_BUILD_DIR" > "$IOS_BUILD_DIR/gameplay-smoke.log" 2>&1

if ! grep -q 'IOS_SMOKE_PASS:' "$IOS_BUILD_DIR/gameplay-smoke.log"; then
  cat "$IOS_BUILD_DIR/gameplay-smoke.log"
  exit 1
fi
if grep -q '^ERROR:' "$IOS_BUILD_DIR/gameplay-smoke.log"; then
  echo "Gameplay assertions completed, but runtime errors need review: $IOS_BUILD_DIR/gameplay-smoke.log" >&2
  exit 1
fi
cat "$IOS_BUILD_DIR/ios-smoke-result.txt"
