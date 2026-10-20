# Autonauts Co-op

Online co-op for [Autonauts](https://store.steampowered.com/app/979120/Autonauts/) (Steam, Unity 2018.4.35f1, Mono).
BepInEx 5 plugin. Host-authoritative: the host runs the world; joiners see a mirror and send their clicks to the host.

Status: **alpha 0.4.0**. Not affiliated with Denki or Curve Games. Contains no game files.

## What works (automated two-instance test: 27/27 over TCP, 29/29 over the Steam-style wire)

- Join a running world: the host sends its save in memory; the joiner loads it. Object counts and a whole-world checksum match.
- Joiner walks, picks up, drops, picks up tools and chops trees; the host sees it.
- Host movement, tile changes and new objects reach the joiner.
- Joiner edit mode: place, drag-place (paths, walls, fences), move/rotate and delete buildings and blueprints.
  The request goes to the host; the result comes back through sync. No undo for joiners.
- Joiners teach bots: select a bot, Record, do the work, Play. The host's copy of the joiner records the actions,
  streams them back to the joiner's script editor, and runs the finished script on the real bot. Stop, pause, step
  and whistle calls are forwarded too. The bot being taught is left alone by sync until the editor closes.
- Joiner clicks made while their character is mid-action are held for up to 3 s and retried.
- Self-healing: every 10 s the host sends a (uid, hash) digest; the joiner re-requests anything that drifted.

## Not done / known issues

- Bot extras not synced yet: saved/linked scripts library, renaming bots, bot inventory/upgrade screens.
- Building unlocks/research done after joining aren't pushed to joiners yet (their build palette may lag).
- Joiner inventory, research and wardrobe screens are not synced.
- Joiner side: bots and animals slide without walk animations (simulation is frozen on the joiner).
- Steam invites are written but **not yet tested on real Steam** (needs two PCs, two accounts that each own the game).
- A joiner who disconnects leaves their character standing in the host's world.
- Not tested over the internet or on large late-game bases.

## Connecting

- **Steam (recommended):** host loads a world, F8 > Host this world. The Steam invite dialog opens. Friends accept the
  invite, or right-click the host in their friends list > Join Game. No IPs, no port forwarding (Valve relay).
  Friends-only lobby, up to 8 players. Every player needs their own copy of Autonauts.
- **IP:** host forwards TCP 27015 (or use a virtual LAN). Joiner: main menu, F8, type the IP, Join.

The Steam path uses the game's own Steamworks.NET (15.0.1, SDK 1.51) and app id, ISteamNetworkingMessages (reliable,
channel 0), 60000-byte pieces with a 1-byte header, a 1 s keep-alive and a 60 s timeout. The test suite runs the same
framing/peer code over a UDP stand-in (`COOP_TRANSPORT=udp`); only lobbies and invites are Steam-only.

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
Results: `<work dir>/{host,client}/BepInEx/coop_test_*.txt`. Add `COOP_TRANSPORT=udp` to test the Steam framing path.

The test copies get a `steam_appid.txt` so they start without the Steam client. Test use only; never ship it.

## How it works

See `src/Sync.cs` header and the write-up in [coop-anything](https://github.com/cranebeaker/coop-anything) (`games/autonauts`).

## License

MIT. Created with Claude.
