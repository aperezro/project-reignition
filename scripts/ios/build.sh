#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/env.sh"

mode="${1:-signed}"
case "$mode" in
  export|signed|unsigned|simulator) ;;
  *) echo "Usage: $0 [export|signed|unsigned|simulator]" >&2; exit 2 ;;
esac
[[ -x "$GODOT" ]] || { echo "Set GODOT to the Godot 4.7 .NET executable." >&2; exit 1; }
command -v dotnet >/dev/null
xcodebuild -version

if [[ "${SKIP_EXPORT:-0}" != 1 ]]; then
  python3 "$IOS_ROOT/scripts/ios/convert_videos.py" --check
  dotnet build "$IOS_ROOT/Project/Sonic Remake Project.csproj" --nologo \
    > "$IOS_BUILD_DIR/dotnet-build.log" 2>&1
  "$GODOT" --headless --path "$IOS_ROOT/Project" --editor --import \
    > "$IOS_BUILD_DIR/import.log" 2>&1
  mkdir -p "$IOS_BUILD_DIR/xcode"
  "$GODOT" --headless --path "$IOS_ROOT/Project" --export-release iOS \
    "$IOS_BUILD_DIR/xcode/Reignition.zip" > "$IOS_BUILD_DIR/export.log" 2>&1
  # Godot can finish its native export even when its .NET export plugin failed.
  if rg -q 'Export .NET Project:|Failed to build project|Export failed' "$IOS_BUILD_DIR/export.log"; then
    echo "The managed iOS export failed; inspect $IOS_BUILD_DIR/export.log" >&2
    exit 1
  fi
fi
[[ "$mode" == export ]] && exit 0

project="$IOS_BUILD_DIR/xcode/Reignition.xcodeproj"
common=(-project "$project" -scheme Reignition -configuration Release)
if [[ "$mode" == simulator ]]; then
  # Godot 4.7's .NET template advertises a universal simulator slice, but its
  # static engine library contains x86_64 only. Inspect the binary itself.
  simulator_lib="$IOS_BUILD_DIR/xcode/Reignition.xcframework/ios-arm64_x86_64-simulator/libgodot.a"
  simulator_arch="${SIMULATOR_ARCH:-$(uname -m)}"
  available_archs="$(lipo -archs "$simulator_lib")"
  if [[ " $available_archs " != *" $simulator_arch "* ]]; then
    simulator_arch=x86_64
  fi
  xcodebuild "${common[@]}" -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
    -derivedDataPath "$IOS_BUILD_DIR/DerivedData-Simulator" CODE_SIGNING_ALLOWED=NO \
    "ARCHS=$simulator_arch" ONLY_ACTIVE_ARCH=YES build \
    > "$IOS_BUILD_DIR/xcode-simulator.log" 2>&1
  simulator_app="$IOS_BUILD_DIR/DerivedData-Simulator/Build/Products/Release-iphonesimulator/Reignition.app"
  # Godot disables Metal/Vulkan in its simulator engine. Set the project value
  # too: a CLI renderer override alone does not initialize its iOS GL context.
  cp "$IOS_ROOT/scripts/ios/simulator-override.cfg" "$simulator_app/override.cfg"
  codesign --force --deep --sign - "$simulator_app"
  codesign --verify --deep --strict "$simulator_app"
  echo "$simulator_app"
elif [[ "$mode" == unsigned ]]; then
  xcodebuild "${common[@]}" -sdk iphoneos -destination 'generic/platform=iOS' \
    -derivedDataPath "$IOS_BUILD_DIR/DerivedData-Unsigned" CODE_SIGNING_ALLOWED=NO build \
    > "$IOS_BUILD_DIR/xcode-unsigned.log" 2>&1
  stage="$(mktemp -d "$IOS_BUILD_DIR/ipa-staging.XXXXXX")"
  ipa_temp="$(mktemp "$IOS_BUILD_DIR/ipa.XXXXXX")"
  trap 'rm -rf "$stage"; rm -f "$ipa_temp"' EXIT
  mkdir -p "$stage/Payload"
  ditto "$IOS_BUILD_DIR/DerivedData-Unsigned/Build/Products/Release-iphoneos/Reignition.app" \
    "$stage/Payload/Reignition.app"
  ditto --norsrc --noextattr --noqtn -c -k "$stage" "$ipa_temp"
  mv "$ipa_temp" "$IOS_BUILD_DIR/Project-Reignition-unsigned.ipa"
  echo "$IOS_BUILD_DIR/Project-Reignition-unsigned.ipa (must be signed before installation)"
else
  xcodebuild "${common[@]}" -sdk iphoneos -destination 'generic/platform=iOS' \
    -archivePath "$IOS_BUILD_DIR/Reignition.xcarchive" \
    -allowProvisioningUpdates -allowProvisioningDeviceRegistration archive \
    > "$IOS_BUILD_DIR/xcode-archive.log" 2>&1
  python3 - "$IOS_ROOT/Project/export_presets.cfg" "$IOS_BUILD_DIR/ExportOptions.plist" <<'PY'
import plistlib, re, sys
with open(sys.argv[1]) as source:
    team = re.search(r'application/app_store_team_id="([^"]+)"', source.read()).group(1)
with open(sys.argv[2], 'wb') as target:
    plistlib.dump(dict(method='debugging', teamID=team, signingStyle='automatic',
        stripSwiftSymbols=True, manageAppVersionAndBuildNumber=False), target)
PY
  xcodebuild -exportArchive -archivePath "$IOS_BUILD_DIR/Reignition.xcarchive" \
    -exportPath "$IOS_BUILD_DIR/signed" -exportOptionsPlist "$IOS_BUILD_DIR/ExportOptions.plist" \
    -allowProvisioningUpdates > "$IOS_BUILD_DIR/xcode-export.log" 2>&1
  echo "$IOS_BUILD_DIR/signed/Reignition.ipa"
fi
