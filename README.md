# Autonauts Co-op

Online co-op for [Autonauts](https://store.steampowered.com/app/979120/Autonauts/) (Steam, Unity 2018.4.35f1, Mono).
BepInEx 5 plugin. Host-authoritative: the host runs the world; joiners see a mirror and send their clicks to the host.

Status: **alpha 0.8.0**. Not affiliated with Denki or Curve Games. Contains no game files.

## What works (automated two-instance test: 59/59 over TCP, 49/49 over the Steam-style wire)

- Join a running world: the host sends its save in memory; the joiner loads it. Object counts and a whole-world checksum match.
- Joiner walks, picks up, drops, picks up tools and chops trees; the host sees it.
- Host movement, tile changes and new objects reach the joiner.
- Joiner edit mode: place, drag-place (paths, walls, fences), move/rotate and delete buildings and blueprints.
  The request goes to the host; the result comes back through sync. No undo for joiners.
- Joiners teach bots: select a bot, Record, do the work, Play. The host's copy of the joiner records the actions,
  streams them back to the joiner's script editor, and runs the finished script on the real bot. Stop, pause, step
  and whistle calls are forwarded too. The bot being taught is left alone by sync until the editor closes.
- Bot edit locks: while someone records or edits a bot, everyone else sees "Being programmed by NAME" over it and
  can't record or play it until they're done (host and joiners, enforced on the host).
- Joiners rename bots, and move items between their hands, pockets, upgrade slots, bots, wardrobes and aquariums
  in the inventory screen (drag, click, ctrl-click). Inventory keys (stow, recall, cycle, swap, use) work.
- Building menus open on the joiner who engaged the building (not on the host): recipes (workbenches and other
  converters), research choice/cancel, storage lock, animal stations, upgrades, cancel build, sign text and area.
- World state follows the host live: quests/research progress, building and object unlocks (build palette),
  resources, time of day, the saved script library, bot groups, offworld missions and the wardrobe's stored clothes.
- Wardrobe and aquarium screens open on the joiner; taking or storing clothes/fish goes through the host.
  Catapult and spaceport screens are host-only (joiners get a notice).
- If the host leaves (quits, crashes, times out or goes back to its menu), joiners get a message and return to the
  main menu. If the host leaves the world, hosting stops.
- Animations on the joiner: farmers' actions (chop, dig, idle...) are mirrored from the host; walking hops are
  reproduced from movement.
- A joiner who leaves keeps their character; rejoining gets it (and its pockets) back, matched by SteamID on Steam
  and by name on IP. This survives the host saving, quitting and loading the world again (the save records who
  owns which character; the host always gets its own back).
- Version check: host and joiner must run the same AutonautsCoop and Autonauts version. A mismatched joiner is
  refused with a message saying which versions differ.
- Joiner clicks made while their character is mid-action are held for up to 3 s and retried.
- Self-healing: every 10 s the host sends a (uid, hash) digest; the joiner re-requests anything that drifted.

## Not done / known issues

- Joiners editing the script library (saving/renaming stored scripts) isn't forwarded yet; they see the host's.
- Bot server menu, catapult, spaceport, planning mode and game speed are host-only for now.
- An unlock that lands while a joiner has edit mode open shows after they reopen edit mode.
- Steam invites are written but **not yet tested on real Steam** (needs two PCs, two accounts that each own the game).
- A joiner who disconnects leaves their character standing until they rejoin.
- Joiners on 0.7.0 or older can't get the version message from a 0.8.0 host (they just get disconnected).
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
