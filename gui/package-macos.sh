#!/bin/bash
set -euo pipefail
project="$(cd "$(dirname "$0")" && pwd)"
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo '请选择 osx-arm64 或 osx-x64' >&2; exit 2 ;; esac
output="$project/../Artifacts/LogosDecoder/$rid"
mkdir -p "$output"
stage="$(mktemp -d "$output/.package.XXXXXX")"
trap 'rm -rf "$stage"' EXIT
app="$stage/Logos 解码器.app"
mkdir -p "$app/Contents/MacOS"
dotnet publish "$project/LogosDecoder.csproj" -c Release -r "$rid" --self-contained true -p:DebugType=None -o "$app/Contents/MacOS"
mkdir -p "$app/Contents/Resources"
cp "$project/Assets/Logos.icns" "$app/Contents/Resources/"
cp "$project/Info.plist" "$app/Contents/"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --sequesterRsrc --keepParent "$app" "$stage/LogosDecoder.zip"
# 发布阶段只替换本脚本产生的构建产物。
if [[ -e "$output/Logos 解码器.app" ]]; then
    mv "$output/Logos 解码器.app" "$stage/previous.app"
fi
mv "$app" "$output/Logos 解码器.app"
mv "$stage/LogosDecoder.zip" "$output/LogosDecoder.zip"
