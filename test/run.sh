#!/bin/bash
# AutonautsCoop QA: two game instances under Wine/Xvfb, separate prefixes (separate saves).
# Version 0.0.2 - 2026-10-20 - Created with Claude
# Usage: test/run.sh <base game dir> <bepinex dir> <work dir> [timeout s]
set -u
BASE="$1"; BEP="$2"; WORK="$3"; TMO="${4:-420}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
"$HERE/build.sh" "$BASE" "$BEP" "$WORK/out" || exit 1
pkill -f Autonauts.exe 2>/dev/null; sleep 1
for r in host client; do
  T="$WORK/$r"; rm -rf "$T"; cp -al "$BASE" "$T"; cp -r "$BEP/." "$T/"
  mkdir -p "$T/BepInEx/plugins"; cp "$WORK/out/AutonautsCoop.dll" "$T/BepInEx/plugins/"
  echo 979120 > "$T/steam_appid.txt"   # test only: lets the game start without the Steam client
  P="$WORK/prefix_$r"; [ -d "$P" ] || WINEPREFIX="$P" WINEDEBUG=-all wineboot -i >/dev/null 2>&1
done
pgrep -f "Xvfb :91" >/dev/null || (Xvfb :91 -screen 0 1280x720x24 >/dev/null 2>&1 &)
pgrep -f "Xvfb :92" >/dev/null || (Xvfb :92 -screen 0 1280x720x24 >/dev/null 2>&1 &)
sleep 1
launch() { # role display
  local T="$WORK/$1"
  (cd "$T" && COOP_TEST=$1 DISPLAY=$2 WINEPREFIX="$WORK/prefix_$1" WINEDEBUG=-all WINEDLLOVERRIDES="winhttp=n,b" \
    timeout "$TMO" wine Autonauts.exe -logFile "Z:$(echo $T/player.log | tr / '\\')" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 >/dev/null 2>&1) &
}
launch host :91; sleep 5; launch client :92
end=$((SECONDS+TMO))
while [ $SECONDS -lt $end ]; do
  grep -q DONE "$WORK/client/BepInEx/coop_test_client.txt" 2>/dev/null && break
  sleep 5
done
sleep 3; pkill -f Autonauts.exe 2>/dev/null
echo "== host";   cat "$WORK/host/BepInEx/coop_test_host.txt" 2>/dev/null
echo "== client"; cat "$WORK/client/BepInEx/coop_test_client.txt" 2>/dev/null
P=$(cat "$WORK"/*/BepInEx/coop_test_*.txt 2>/dev/null | grep -c '^PASS'); F=$(cat "$WORK"/*/BepInEx/coop_test_*.txt 2>/dev/null | grep -c '^FAIL')
echo "SUMMARY pass=$P fail=$F"
