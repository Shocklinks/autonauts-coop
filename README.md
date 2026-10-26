# Autonauts Co-op

Online co-op for [Autonauts](https://store.steampowered.com/app/979120/Autonauts/) (Steam, Unity 2018.4.35f1, Mono).
BepInEx 5 plugin. Host-authoritative: the host runs the world; joiners see a mirror and send their clicks to the host.

Status: **alpha 0.12.36**. Not affiliated with Denki or Curve Games. Contains no game files.

## What works (automated two-instance test: 112/112 over TCP, 95/95 over the Steam-style wire, 113/113 with simulated lag, 15/15 three-player, 7/7 big-base stress test)

- Join a running world: the host sends its save in memory; the joiner loads it. Object counts and a whole-world checksum match.
- Joiner walks, picks up, drops, picks up tools and chops trees; the host sees it.
- Host movement, tile changes and new objects reach the joiner.
- Joiner edit mode: place, drag-place (paths, walls, fences), move/rotate and delete buildings and blueprints.
  The request goes to the host; the result comes back through sync. Undo works for the joiner's last edit (place,
  drag-place, move, delete; an undone delete comes back as a blueprint).
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
- Bot server / script library: joiners upload, rename, delete and sort scripts and folders, and link, unlink or
  download scripts to bots and data storages. Library changes sync both ways (last edit wins if two people change
  it at the same moment).
- Catapult: joiners aim it. Spaceport: joiners accept, stop and decline missions (the host rolls new ones).
- Planning mode: joiners draw, move, resize, name and delete planning areas; everyone sees everyone's areas.
  Show/hide toggles stay per player.
- Game speed: the host's speed (and pause) applies to everyone; joiners can't change it and see "Host paused".
- Bot groups: joiners create, rename, recolour, nest and delete groups and move bots between them; group Play, Stop,
  Pause, Drop and Call run on the host (Call brings the bots to the joiner who pressed it).
- "X joined the game" / "X left the game" notices for everyone.
- Three or more players: tested with the host plus two joiners. Joiners with the same name (e.g. "player" on IP
  without Steam) get "player (2)" so their stored characters never mix. Set your name in F8 (empty = Steam name).
- If the host ends the session (quits, goes back to its menu, stops hosting), joiners see "Host has ended the
  lobby." and return to the main menu; if the connection drops it says "(connection lost)".
- Animations on the joiner: farmers' actions (chop, dig, idle...) are mirrored from the host; walking hops are
  reproduced from movement.
- When a joiner leaves (or drops), their character leaves the host's world and is stored: position, hands, pockets,
  upgrades and clothes. Rejoining brings it back where they stood, or next to the host if something was built on
  that spot meanwhile. Matched by SteamID on Steam, by name on IP. Stored characters are saved in the host's world
  save, so this survives the host quitting and loading the world later (the host always gets its own character).
- Joiners don't autosave. Save As writes the joiner's own copy of the world; loading it later in single player they
  play their own character.
- Bug report: F8 > Save bug report writes one zip (BepInEx\bugreports) with logs, versions and sync stats. On a
  joiner it includes the host's log too.
- Version check: host and joiner must run the same AutonautsCoop and Autonauts version. A mismatched joiner is
  refused with a message saying which versions differ.
- Joiner clicks made while their character is mid-action are held for up to 3 s and retried.
- Self-healing: every 10 s the host sends a (uid, hash) digest; the joiner re-requests anything that drifted.
- Big bases: change detection is incremental (moving things every scan, the rest within ~5 s), so scan cost grows
  slowly with base size. Stress test: 8,000 objects with 400 bots and 4,000 items — join in ~70 s, world matches.

## Fixed after the first real Steam test (0.12.0)

- Joiner camera: WASD/arrow pan, keyboard rotate and zoom work (they counted time with the joiner's frozen game clock).
- Joiner bot programming: the editor's "stop the bot first" cover no longer stays up forever; selection rings,
  flashes and other UI timers run.
- Joiners' characters no longer turn to follow the host's mouse (and the other way round): each player's character
  faces their own cursor.
- Smoother movement: the joiner plays movement back slightly behind the host and interpolates between updates, so
  bursty internet packets don't make characters and bots stutter. Farmer, folk and pet animations keep playing.
- Bots and players picking things up or putting them down are reloaded in place instead of rebuilt, so they stay
  clickable and selected on the joiner (many busy bots were hard to click before).
- A joiner who lost the host can host their own game afterwards (the old Steam lobby is now forgotten).

## 0.12.36: script library merges, fewer rebuilds, smaller join hitch

- **Script library merges by script instead of locking.** Players can change different scripts and folders at the
  same time; each push keeps only what that player changed. If two players change the same script, the first change
  stays and the other player is told theirs wasn't kept. Planning areas and bot groups still lock.
- **Blueprints update in place** on the joiner (they keep their building-to-be); a moved or turned one is rebuilt.
- **Items falling out of a bot's or player's hands** are detached in place instead of rebuilt.
- **Hut folk keep their state** (working, sleeping...) on the joiner after an update.
- Fixed: the joiner's own player could be left wearing a hat (or holding an item) that no longer existed after the
  item moved to someone else.
- **Smaller host hitch on the first join:** the host prepares its change records a few ms at a time before anyone
  joins, instead of all at once when the first player connects.
- **Less host garbage:** rarely changing world sections (quests, script library, bot groups) are checked less often
  while they stay the same, and something that just changed is re-saved at most once a second unless a cheap look
  sees it change again.

## 0.12.32-0.12.35: mods, joining, huts and blueprints, editor locks

- **Workshop (Lua) mods don't change the world on the joiner** (spawn, destroy, move, drop, start/stop bot scripts,
  clear tiles, rotate or fuel buildings, feed converters). The host runs the same mod; its results arrive as normal.
- **Joining hitches the host less:** no full re-save of the world before the snapshot, and the snapshot is
  compressed on another thread; what changes meanwhile is sent to the new player right after it.
- **Huts update in place** on the joiner when folk move in or out (most of the time; otherwise the old rebuild).
- A moved or turned building with contents is rebuilt (in-place loading doesn't move it on the map).
- **Editor locks follow changes, not open editors:** several players can look at the script library at once; the
  first to change it holds it until 5 s after their last change, and a change anyone else makes meanwhile is undone
  with a message (their editor stays open).
- **Joiner re-checks unverified updates within ~2 s** instead of waiting for the 10 s check.
- Fixed: a hut on the joiner could list a folk that was already gone (errors at every quit; its status indicator
  could be left behind).
- Test kit: `action=cleanzips`.

## 0.12.25-0.12.31: one editor at a time, faster updates, less garbage

- **Shared editors lock.** The script library, planning areas and bot groups can be edited by one player at a
  time. Whoever opens one first holds it; anyone else gets a message ("Host is editing the script library") and
  their editor closes (library, planning) or their changes are put back (bot groups). The host refuses a change
  from anyone who doesn't hold the lock and sends its copy back.
- **Changes nobody acts on reach the joiner within seconds** (growth, machine steps): the host takes a cheap look
  at every still object every 4 s and only saves the ones that changed, plus a full round every 60 s. Fewer saves
  overall: less garbage on the host.
- **Bots' current program line** goes out in the small movement stream whenever it changes, instead of waiting for
  a full re-save of the bot, so an open program highlights the right line. Bots' walking position no longer makes
  them re-save on every step.
- **Joiner applies the host's text as sent:** no parse of the whole batch and no re-printing each object, and an
  object it already matches is skipped before parsing. Joiner apply garbage roughly halved.
- On the test save: host pauses about every 30 s (was ~27 s; the game alone ~40 s), joiner about every 35 s.
- Test kit: "play it yourself" (menu 5/6), and the save test reports how many objects lag behind without a flush.

## 0.12.6-0.12.24: smoother joiner, sync fixes (found with the test kit on a big save)

- **Joiner error storm fixed.** A building blueprint being removed mid-build stayed half-removed and threw ~150
  errors a second (35,000 per test), each a log line with a stack trace.
- **Buildings with contents update in place** (storage stacks, workbenches, pots) instead of being destroyed and
  rebuilt on every change. So do emptied machines, bots that put things down, and growing plants. Joiner rebuilds on
  a 10,800-object base: ~380 per 3 minutes before, ~50 now.
- **Bot programs no longer change on the joiner.** Rebuilt buildings and storage stacks made the game rewrite and
  recompile every bot's program (53 bots, for each rebuild).
- **Steam Workshop (Lua) mods no longer change storage on the joiner.** They run on both machines; one on the test
  base balanced two storages every few seconds, fighting the host's counts (the host's results still arrive).
- **Storage counts follow the host**, including stacks that sit in another building and stacks over their normal
  capacity. The joiner re-checks the whole world once, 5 s after joining, so anything that settled after the load
  is repaired.
- **Host:** anything acted on (added to, taken from, picked up) is checked at the next scan; the slow background
  round is now 40 s. Bots are fully re-saved every 8 s (every scan while someone has one open), otherwise only when
  a cheap look sees a change. Unchanged objects skip the normalizing pass.
- **Joiner:** updates that match what it already has are skipped without saving its copy to compare. A kind of
  update that has applied cleanly 20 times in a row is double-checked one time in 10 (the 10 s digest still
  re-checks everything that changed, and what it flags is always checked). Held items are found directly instead of
  saving the whole bot.
- Build: `build.sh` checks that every patch finds its game method (an overloaded method made 0.12.19 not load).
- Note: each garbage-collection pause is ~200 ms on a ~1 GB save, set by Unity 2018's collector. The game alone
  pauses about every 40 s on that save; with a joiner both sides now pause about every 20-25 s (was 13-21 s).
- Test kit: `digest.txt` in each results folder (result, errors, stats, rebuilds, world-check differences); the
  watcher can shut the PC down when asked and restarts itself when updated. Diagnostics: garbage per part of the
  mod and per game update (soak runs).

## 0.12.5: host stutter

- World sections (quests, scripts, planning, ...) are checked one per frame on their own timers instead of all at
  once every second; a section that is slow to save is checked less often (at most every 6 s).
- The "full round" of re-checking still objects is spread over frames with a time budget.
- Changed objects go out as their saved text (no parse and re-serialize), and the anti-entropy digest is built as
  text: less garbage, so fewer garbage-collection pauses.
- A joiner's snapshot no longer comes with a second copy of the whole world right before it.
- Fixed while testing: an item moved from a bot to a player in the same update could vanish on the joiner.
- Diagnostics: the host's stats line now says where its time goes (scan, changes, sections, digest...) and how
  long garbage-collection frames took, so the next kit run shows what is left.

## 0.12.2-0.12.4: real late-game saves (found with the test kit)

The first test-kit run on a real save (Settlement mode, 420x240, ~10,800 objects, 53 bots, mods) broke the joiner
within seconds. Fixed:

- **Settlement Transmitter rewards took the host's next UIDs on the joiner.** The joiner's own character and new items
  then landed on those local objects (parented to the Transmitter), and sync errors cascaded. The joiner now gives
  those rewards UIDs from its own range.
- **Leftovers of objects destroyed with their parent** (a bot's load, a hut's folk) stayed in the game's UID registry,
  plot lists and collections. New copies then got the wrong UID or failed with "same key", and the whistle screen
  stopped at a dead bot (so no bot could be picked). The joiner now clears such leftovers.
- **Huts rebuilt every update** with their folk (folk energy and timers change constantly). Huts and their folk are
  now updated in place.
- The whistle screen no longer breaks when the hovered bot is rebuilt.
- 0.12.3 (second kit run on the same save):
  - Updates that change nothing are skipped. The host re-sends everything right after a join; ~700 blueprints and
    bots were rebuilt for nothing (a second-long stall, and the joiner's character briefly vanished).
  - A joiner's character using a building is reloaded in place (the "engaged" link is kept) instead of rebuilt,
    which closed their building screen.
  - Host: bots, animals and vehicles are re-saved only when something visible changed (tile, facing, state,
    hands, energy) or once a second. That was most of the host's scan time on a 53-bot base (host fell from
    ~230 to ~60 fps with a joiner).
  - The save test lists which objects differ when the worlds don't match.
- 0.12.4 (third kit run):
  - **Bot programs no longer delete buildings on the joiner.** A program names the workbench or storage it uses by
    UID; those were taken for things the bot holds, so every bot update destroyed them on the joiner (~700 objects
    missing after a join, ~12,000 re-requests in 4 minutes). Only real saved objects count as held now.
  - Held items' own tile and a modded item's "Used":0 no longer make objects look different.
  - The joiner's player list always has their own character first (it could be empty for a moment).
- Test kit: partial results are saved during the run, so closing the window early still leaves logs.

## 0.12.1: picking bots, diagnostics, test kit

- **Picking bots with the whistle works on the joiner.** The whistle screen only picks a bot that stays under the
  cursor for 10 frames and finds it through the tile it is registered on. On the joiner, bots kept moving (the host's
  bots don't stop to listen) and are drawn a moment behind their tile, so busy bots were almost impossible to pick,
  and a click made you walk to the bot instead. Now, while the joiner's whistle screen is open, their bots stand
  still where they are drawn (as listening bots do in single player), all count as listening, and the screen gets the
  bot actually drawn under the cursor. New test: 14 busy bots, hover each, click one; it must open its program.
- **Diagnostics log** (`BepInEx\coop_diag.log`, on by default, included in the F8 bug report from both sides): every
  click and what was under the cursor, every action a joiner sends and what the host did with it (done, target
  missing, held while busy, refused), screen changes, bot selection, script editor target, toasts, objects rebuilt,
  and every 10 s: frame rate, round-trip time, traffic and sync cost. **F7** marks "something went wrong here" in
  both players' logs. Config: `[Debug] Diagnostics`, `MarkKey`.
- **Windows test kit** (`kit\RunTests.bat`): runs a host and a joiner copy of the game on one PC with the test bots:
  the full test, the full test with simulated internet lag, or a test on your own save (join, walk, pick bots, open a
  program, watch, compare worlds). Your install and saves are only read. See `kit\README.txt`.
- **Bots no longer rebuilt over and over on the joiner.** The game saves decimals as whole hundredths and loses one
  on each load/save round trip (81.52 energy loads, then saves as 81.51). The joiner's check after each update saw
  that as a mismatch and rebuilt the bot instead: every working bot, every update (60-90 rebuilds per 10 s with 14
  bots in the test). Values from the host now load so they save back exactly. Also, the whistle screen no longer
  pauses the joiner's copies of the host's bots locally (that changed their state and caused the same rebuilds).
- **Simulated lag** for tests: `COOP_LAG` / `COOP_JITTER` (ms, each way) delay every received packet.

## Not done / known issues

- Planning areas sync when the editor leaves planning mode (and while drawing); two people editing areas at the
  same moment: last one wins.
- Steam invites are written but **not yet tested on real Steam** (needs two PCs, two accounts that each own the game).
- Joiners on 0.7.0 or older can't get the version message from a 0.8.0+ host (they just get disconnected).
- Not tested over the internet. The big-base test ran on a small map (two medium maps didn't fit this test
  machine's memory); real medium/large late-game worlds still need a real-PC test.
- Changes to things that don't move (e.g. storage contents) reach joiners within ~5 s instead of instantly.

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

## Continuing development

See [HANDOFF.md](HANDOFF.md): source map, environment setup, test and release steps.

## Build

No NuGet needed. Compiles with Roslyn from the .NET 8 SDK against the game's own assemblies:

```
./build.sh "<game dir>" "<extracted BepInEx dir>" out
```

## Package

`tools/package.sh "<extracted BepInEx dir>" out/AutonautsCoop.dll <version> AutonautsCoop-<version>.zip` builds the
player download (BepInEx + plugin + README-COOP.txt, extracts into the game folder).

## Test

`test/run.sh "<game dir>" "<BepInEx dir>" <work dir>` copies the game into two folders, runs both under Wine + Xvfb
with separate prefixes, and drives them with in-game bots (`COOP_TEST=host|client`) through the real input path.
Results: `<work dir>/{host,client}/BepInEx/coop_test_*.txt`. Add `COOP_TRANSPORT=udp` to test the Steam framing path,
`COOP_MULTI=1` for the three-player test (host + game joiner + `test/protobot.py`), or `COOP_BIG=1` for the big-base stress test (`COOP_BIG_BOTS`, `COOP_BIG_BUILDINGS`, `COOP_BIG_ITEMS` set its size).

Kit/test switches (environment variables, never set in normal play): `COOP_LAG`/`COOP_JITTER` simulated lag,
`COOP_DATA_BASE` (run.sh) / `COOP_DATA` separate save folders, `COOP_NOSTEAM=1` don't start Steam, `COOP_BIND` host
address, `COOP_SOAK=1 COOP_SAVE=<name>` test on an existing save (`src/Soak.cs`).

On Windows: `kit\RunTests.bat` does the same with two copies on one PC (see `kit\README.txt`).

The test copies get a `steam_appid.txt` so they start without the Steam client. Test use only; never ship it.

## How it works

See `src/Sync.cs` header and the write-up in [coop-anything](https://github.com/cranebeaker/coop-anything) (`games/autonauts`).

## License

MIT. Created with Claude.
