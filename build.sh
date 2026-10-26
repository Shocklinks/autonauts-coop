#!/bin/bash
# Builds AutonautsCoop.dll against the game's own assemblies (no NuGet).
# Usage: build.sh <game dir> <bepinex dir> <out dir>
set -e
GAME="$1"; BEP="$2"; OUT="$3"; M="$GAME/Autonauts_Data/Managed"
mkdir -p "$OUT"
REFS=""
for f in mscorlib System System.Core netstandard UnityEngine UnityEngine.CoreModule UnityEngine.IMGUIModule UnityEngine.TextRenderingModule UnityEngine.InputModule UnityEngine.PhysicsModule UnityEngine.UI UnityEngine.AnimationModule UnityEngine.ScreenCaptureModule UnityEngine.UIModule Assembly-CSharp Assembly-CSharp-firstpass; do
  [ -f "$M/$f.dll" ] && REFS="$REFS -r:$M/$f.dll"
done
REFS="$REFS -r:$BEP/BepInEx/core/BepInEx.dll -r:$BEP/BepInEx/core/0Harmony.dll"
dotnet /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll -nologo -nostdlib -noconfig -target:library -langversion:9 -optimize -nowarn:CS1701,CS1702 \
  $REFS -out:"$OUT/AutonautsCoop.dll" $(find "$(dirname "$0")/src" -name '*.cs')
echo "built $OUT/AutonautsCoop.dll"
# every Harmony patch must find its game method (one bad target stops PatchAll: the mod then does nothing in game)
if command -v mono >/dev/null; then
  PC="$OUT/patchcheck"; mkdir -p "$PC"; cp "$BEP"/BepInEx/core/*.dll "$PC/"
  CR=""; for f in mscorlib System System.Core netstandard; do [ -f "$M/$f.dll" ] && CR="$CR -r:$M/$f.dll"; done
  dotnet /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll -nologo -nostdlib -noconfig -target:exe -langversion:9 -nowarn:CS1701,CS1702 \
    $CR -r:"$BEP/BepInEx/core/0Harmony.dll" -out:"$PC/PatchCheck.exe" "$(dirname "$0")/tools/PatchCheck.cs"
  mono "$PC/PatchCheck.exe" "$OUT/AutonautsCoop.dll" "$M" "$BEP/BepInEx/core" || { echo "PATCH CHECK FAILED"; exit 1; }
fi
