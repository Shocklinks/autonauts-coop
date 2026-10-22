#!/bin/bash
# AutonautsCoop QA: two game instances under Wine/Xvfb, separate prefixes (separate saves).
# Version 0.0.2 - 2026-10-20 - Created with Claude
# Usage: test/run.sh <base game dir> <bepinex dir> <work dir> [timeout s]
set -u
BASE="$1"; BEP="$2"; WORK="$3"; TMO="${4:-420}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
bash "$HERE/build.sh" "$BASE" "$BEP" "$WORK/out" || exit 1
ROLES="host client"; [ "${COOP_MULTI:-}" = "1" ] && ROLES="host client client2"   # COOP_MULTI=1: host + 2 joiners
pkill -x Autonauts.exe 2>/dev/null; sleep 1
for r in host client; do
  T="$WORK/$r"; rm -rf "$T"; cp -al "$BASE" "$T"; cp -r "$BEP/." "$T/"
  mkdir -p "$T/BepInEx/plugins"; cp "$WORK/out/AutonautsCoop.dll" "$T/BepInEx/plugins/"
  echo 979120 > "$T/steam_appid.txt"   # test only: lets the game start without the Steam client
  P="$WORK/prefix_$r"; [ -d "$P" ] || WINEPREFIX="$P" WINEDEBUG=-all wineboot -i >/dev/null 2>&1
done
pgrep -f "Xvfb :91" >/dev/null || (Xvfb :91 -screen 0 1280x720x24 >/dev/null 2>&1 &)
pgrep -f "Xvfb :92" >/dev/null || (Xvfb :92 -screen 0 1280x720x24 >/dev/null 2>&1 &)
sleep 1
W=1280; H=720
launch() { # role display
  local T="$WORK/$1"
  (cd "$T" && COOP_TEST=$1 DISPLAY=$2 WINEPREFIX="$WORK/prefix_$1" WINEDEBUG=-all WINEDLLOVERRIDES="winhttp=n,b" \
    timeout "$TMO" wine Autonauts.exe -logFile "Z:$(echo $T/player.log | tr / '\\')" -screen-fullscreen 0 -screen-width $W -screen-height $H >/dev/null 2>&1) &
}
launch host :91; sleep 5; launch client :92
# third player: a protocol-level joiner (three game instances don't fit in the test machine's memory)
if [ "${COOP_MULTI:-}" = "1" ]; then
  VER=$(grep -o 'Version = "[^"]*"' "$HERE/src/Plugin.cs" | cut -d'"' -f2)
  mkdir -p "$WORK/client2/BepInEx"
  (sleep 60; timeout "$TMO" python3 "$HERE/test/protobot.py" 127.0.0.1 27015 "$VER" "$WORK/client2/BepInEx/coop_test_client2.txt" > "$WORK/client2/protobot.log" 2>&1) &
fi
end=$((SECONDS+TMO))
while [ $SECONDS -lt $end ]; do
  done=1
  for r in $ROLES; do [ "$r" = host ] && continue; grep -q DONE "$WORK/$r/BepInEx/coop_test_$r.txt" 2>/dev/null || done=0; done
  [ $done = 1 ] && break
  sleep 5
done
sleep 3; pkill -x Autonauts.exe 2>/dev/null
for r in $ROLES; do echo "== $r"; cat "$WORK/$r/BepInEx/coop_test_$r.txt" 2>/dev/null; done
FILES=""; for r in $ROLES; do FILES="$FILES $WORK/$r/BepInEx/coop_test_$r.txt"; done
P=$(cat $FILES 2>/dev/null | grep -c '^PASS'); F=$(cat $FILES 2>/dev/null | grep -c '^FAIL')
echo "SUMMARY pass=$P fail=$F"
