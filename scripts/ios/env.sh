#!/usr/bin/env bash
# Source from build/test scripts; dependencies stay local to this checkout.
IOS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ -x "$IOS_ROOT/.tools/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$IOS_ROOT/.tools/dotnet"
  export DOTNET_CLI_HOME="$IOS_ROOT/.tools/dotnet-home"
  export PATH="$DOTNET_ROOT:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
GODOT="${GODOT:-$IOS_ROOT/.tools/godot/Godot_mono.app/Contents/MacOS/Godot}"
IOS_BUILD_DIR="${IOS_BUILD_DIR:-$IOS_ROOT/build/ios}"
mkdir -p "$IOS_BUILD_DIR"
