#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/env.sh"
cd "$IOS_ROOT"
mkdir -p .tools

if [[ ! -x .tools/dotnet/dotnet ]]; then
  curl -fL --retry 3 -o .tools/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
  bash .tools/dotnet-install.sh --version 10.0.401 --install-dir "$IOS_ROOT/.tools/dotnet"
fi
source "$IOS_ROOT/scripts/ios/env.sh"
dotnet workload install ios --skip-manifest-update

download() {
  local filename="$1" checksum="$2" url="$3"
  if [[ ! -f "$filename" ]]; then
    curl -fL --retry 3 -o "$filename.download" "$url"
    mv "$filename.download" "$filename"
  fi
  echo "$checksum  $filename" | shasum -a 256 -c -
}
base=https://github.com/godotengine/godot-builds/releases/download/4.7-stable
download .tools/godot-mono.zip 85f677dceb0fd9a8a9e8d64ec656060af5cdca36712959622c9ee6287f2583d2 \
  "$base/Godot_v4.7-stable_mono_macos.universal.zip"
download .tools/godot-templates.tpz 4c02a0b99ad9c5bc243c2e79468628db3df89350d9db8fc995988f69d126e069 \
  "$base/Godot_v4.7-stable_mono_export_templates.tpz"
if [[ ! -x "$GODOT" ]]; then
  unzip -q .tools/godot-mono.zip -d .tools/godot
fi
templates="$HOME/Library/Application Support/Godot/export_templates/4.7.stable.mono"
mkdir -p "$templates"
unzip -joq .tools/godot-templates.tpz templates/ios.zip templates/version.txt -d "$templates"

# Godot 4.7 on macOS hardcodes /usr/local/share/dotnet ahead of PATH. Patch only
# this downloaded editor's managed CLI resolver so exports use the local SDK.
# The original assembly is retained, and the machine's .NET install is untouched.
dotnet run --project scripts/ios/godot-cli-fix -- \
  "$IOS_ROOT/.tools/godot/Godot_mono.app/Contents/Resources/GodotSharp/Tools/GodotTools.dll" \
  "$DOTNET_ROOT/dotnet"
"$GODOT" --version
