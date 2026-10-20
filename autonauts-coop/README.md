# Autonauts Co-op

Online co-op for [Autonauts](https://store.steampowered.com/app/979120/Autonauts/) (Steam, Unity 2018.4.35f1, Mono).
BepInEx 5 plugin. Host-authoritative: the host runs the world; joiners see a mirror and send their clicks to the host.

Status: **alpha 0.1.0**. Not affiliated with Denki or Curve Games. Contains no game files.

## What works (automated two-instance test, 15/15)

- Join a running world: the host sends its save in memory; the joiner loads it. Object counts and a whole-world checksum match.
- Joiner walks, picks up, drops, picks up tools and chops trees; the host sees it.
- Host movement, tile changes and new objects reach the joiner.
- Self-healing: every 10 s the host sends a (uid, hash) digest; the joiner re-requests anything that drifted.

## Not done / known issues

- Joiners can't place buildings/blueprints or teach bots yet.
- Joiner inventory, research and wardrobe screens are not synced.
- Joiner side: bots and animals slide without walk animations (simulation is frozen on the joiner).
- Transport is TCP/IP only (port 27015). Steam invites are next.
- Not tested over the internet or on large late-game bases.

## Play

1. Install [BepInEx 5.4.23.5 win x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) into the game folder.
2. Put `AutonautsCoop.dll` in `BepInEx\plugins`.
3. Host: load a world, F8 -> Host this world. Joiner: main menu, F8 -> IP -> Join.

## Build

No NuGet needed. Compiles with Roslyn from the .NET 8 SDK against the game's own assemblies:

```
./build.sh "<game dir>" "<extracted BepInEx dir>" out
```

## Test

`test/run.sh "<game dir>" "<BepInEx dir>" <work dir>` copies the game into two folders, runs both under Wine + Xvfb
with separate prefixes, and drives them with in-game bots (`COOP_TEST=host|client`) through the real input path.
Results: `<work dir>/{host,client}/BepInEx/coop_test_*.txt`.

The test copies get a `steam_appid.txt` so they start without the Steam client. Test use only; never ship it.

## How it works

See `src/Sync.cs` header and the write-up in [coop-anything](https://github.com/cranebeaker/coop-anything) (`games/autonauts`).

## License

MIT. Created with Claude.
