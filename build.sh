#!/bin/bash
# Builds AutonautsCoop.dll against the game's own assemblies (no NuGet).
# Usage: build.sh <game dir> <bepinex dir> <out dir>
set -e
GAME="$1"; BEP="$2"; OUT="$3"; M="$GAME/Autonauts_Data/Managed"
mkdir -p "$OUT"
REFS=""
for f in mscorlib System System.Core netstandard UnityEngine UnityEngine.CoreModule UnityEngine.IMGUIModule UnityEngine.TextRenderingModule UnityEngine.InputModule UnityEngine.PhysicsModule UnityEngine.UI UnityEngine.AnimationModule Assembly-CSharp Assembly-CSharp-firstpass; do
  [ -f "$M/$f.dll" ] && REFS="$REFS -r:$M/$f.dll"
done
REFS="$REFS -r:$BEP/BepInEx/core/BepInEx.dll -r:$BEP/BepInEx/core/0Harmony.dll"
dotnet /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll -nologo -nostdlib -noconfig -target:library -langversion:9 -optimize -nowarn:CS1701,CS1702 \
  $REFS -out:"$OUT/AutonautsCoop.dll" $(find "$(dirname "$0")/src" -name '*.cs')
echo "built $OUT/AutonautsCoop.dll"
