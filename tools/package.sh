#!/bin/bash
# AutonautsCoop - tools/package.sh - build the player download zip
# Version 0.10.0 - 2026-10-21 - Created with Claude
# Usage: tools/package.sh <extracted BepInEx 5.4.23.5 win x64 dir> <AutonautsCoop.dll> <version> <out.zip>
# The zip extracts straight into the game folder: BepInEx loader + plugin + README-COOP.txt.
set -e
BEP="$1"; DLL="$2"; VER="$3"; OUT="$(realpath -m "$4")"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
[ -f "$BEP/winhttp.dll" ] || { echo "not a BepInEx folder: $BEP"; exit 1; }
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
cp -r "$BEP/." "$T/"
mkdir -p "$T/BepInEx/plugins"
cp "$DLL" "$T/BepInEx/plugins/AutonautsCoop.dll"
sed "s/@VERSION@/$VER/; s/@DATE@/$(date +%Y-%m-%d)/" "$HERE/dist/README-COOP.txt" > "$T/README-COOP.txt"
rm -f "$OUT"; (cd "$T" && zip -qr "$OUT" .)
echo "packaged $OUT"
