# HANDOFF: how to continue this project (for the next Claude session)

Version 0.12.38 - 2026-10-26 - Created with Claude

Read this first. The owner (GitHub: Shocklinks) is not familiar with GitHub: do the Git and GitHub work yourself,
explain results in short, plain sentences, and ask before anything public (releases, hub PR changes).

## The project

- **What:** AutonautsCoop, a BepInEx 5 + HarmonyX plugin that adds online co-op to Autonauts (Steam, Unity 2018.4.35f1, Mono).
- **Model:** the host is authoritative. A joiner loads the host's save from memory and mirrors it; its own simulation is frozen.
  - The joiner's input goes to the host as small JSON messages and is replayed there.
  - Changes come back as object save JSON, removals, movement, tile changes, animations and "world sections".
- **Transports:** TCP (port 27015) and Steam ISteamNetworkingMessages with friends-only lobbies.
- **Status:** alpha. Automated suites pass (see QA). Never tested yet on real Steam between two PCs, over the internet,
  or on real medium/large late-game saves.

## How updates reach GitHub (owner's setup)

- The owner uses **GitHub Desktop** with both repos cloned on their PC in `D:\Documents\GitHub\autonauts-coop` and
  `D:\Documents\GitHub\coop-anything`. Sessions are linked to the PC, not to GitHub (they can't have both).
- Ask for access to `D:\Documents\GitHub` and write changed files into those clones.
- Then tell the owner the commit message to type. In GitHub Desktop they click **Commit to main** and **Push origin**.
  For the hub, they switch to branch `game/autonauts` first, after **Fetch origin** and a sync with upstream.
- Releases: the owner creates them on github.com and attaches the zip `tools/package.sh` built.
- Don't overwrite files the owner changed: compare with the clone first.

## Repos

| Repo | Role |
|---|---|
| `Shocklinks/autonauts-coop` | This repo: code, tests, releases. MIT. Code only, never game files. |
| `Shocklinks/coop-anything` | Owner's fork of the community hub `cranebeaker/coop-anything` |

The hub entry and write-up are in PR **cranebeaker/coop-anything#112** (branch `game/autonauts`):
`data/directory.yaml`, regenerated `DIRECTORY.md`, `games/autonauts/README.md`, and `games/autonauts/NOTES.md`.
NOTES.md is the detailed per-version list of engine pitfalls: **read it before changing sync code.**

Hub rules (its AGENTS.md):
- No game files and no personal info (real names, emails, local paths with usernames).
- One game per PR, opened as a **draft**; the owner reviews the diff first.
- Run `python tools/build_directory.py` after editing `data/directory.yaml`.
- Sync the fork with upstream before changing the branch, or a stale `directory.yaml` deletes other people's entries.

Commit as `Shocklinks <Shocklinks@users.noreply.github.com>`. Never use a real name or email.

## Source map (`src/`)

| File | What it does |
|---|---|
| Plugin.cs | BepInEx entry, `Version` constant, log file `BepInEx/coop.log` |
| Coop.cs | Session controller: host/client update loops, hello/version handshake, snapshot join, Info message dispatch, in-memory save loading (`MyFile` patches) |
| Net.cs | Peer, TCP peer (graceful close), NetHost/NetClient, gzip |
| Steam.cs | Steam wire, UDP stand-in wire (tests), WirePeer framing (60000-byte pieces), lobbies/invites |
| Sync.cs | Core sync: host scan/broadcast, client apply (in-place load, recreate), digest, input dispatch (`HostApplyInput`), player order |
| Scan.cs | Incremental change detection (movers every scan, hot/moved objects, a ring slice; round about 5 s) |
| Build.cs | Edit mode forwarding and per-joiner undo |
| Teach.cs | Bot teaching forwarding |
| Bots.cs | Edit locks, bot rename, inventory screen ops, inventory keys, UI rebind after rebuilds |
| Menus.cs | Building menus redirected to the engaging joiner: recipes, research, storage, signs, wardrobe, aquarium, catapult, spaceport |
| WorldState.cs | World sections (quests, unlocks, resources, day/night, scripts, groups, missions, wardrobe, planning, time), Session (leave / lobby ended), animations |
| Shared.cs | Sections both sides edit (script library, planning, groups): push whole section; bot-server bot ops; game speed |
| Groups.cs | Bot groups: replace with UI rebuild, group commands run on host |
| Persist.cs | Seats + stored ("parked") joiner characters in the host save; restore on rejoin |
| VersionCheck.cs | Refuse a joiner on a different mod/game version, with a reason |
| Extras.cs | Joiner autosave/quicksave block; bug report zip |
| Menu.cs | F8 window (settings, status, bug report); Menu.Toast -> NativeUI.Notify (plain box only without a HUD) |
| NativeUI.cs | Game-native UI: pause-screen Host/Invite/Stop buttons, main-menu Multiplayer button + join panel (RenameSign prefab), notices (WarningRollover prefab), ended box (GameStateConfirm) |
| NativeUITest.cs | UI test (COOP_UITEST=1, kit mode=ui): clicks through the buttons, screenshots BepInEx\coop_ui_*.png |
| Feel.cs | Joiner feel: real frame time for UI/camera (the sim clock is frozen), look-at-own-cursor, animators, buffered movement interpolation, whistle bot picking (bots held still, drawn bot under cursor) |
| Diag.cs | Diagnostics log `coop_diag.log` (clicks, actions and host outcomes, screens, 10 s stats, RTT via Msg.Echo, F7 mark); fake mouse for tests |
| Kit.cs, Soak.cs | Windows test kit support: save dir redirect, no Steam, bind address; scenario on an existing save |
| TestBot.cs, TestScript.cs, BigBase.cs, Multi.cs | Automated QA scenario (host/client bots), big-base stress test, three-player test |
| Four.cs | Four-player test: host + 3 game joiners (COOP_FOUR=1, COOP_TEST=client1..3, optional COOP_FOUR_SAVE); kit `mode=four` |
| kit/ | Windows test kit: RunTests.bat + kit.ps1 (two copies on the owner's PC, full/lag/own-save tests, results in kit\results) |
| test/protobot.py | A joiner that speaks the TCP protocol without the game (third player in COOP_MULTI; three game instances don't fit in sandbox memory) |

## Environment setup (cloud sandbox, Linux)

1. **Tools:**
   - Install: `wine` (9.x), `xvfb`, `dotnet-sdk-8.0`, `zip`. Roslyn comes with the SDK; `build.sh` calls `/usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll`. No NuGet needed.
   - Download BepInEx 5.4.23.5 win x64 (GitHub BepInEx releases, `BepInEx_win_x64_5.4.23.5.zip`) and extract it to a folder. SHA-256 seen before: `82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4`.
2. **Game copy:**
   - The owner's install is `B:\SteamLibrary\steamapps\common\Autonauts`. Read-only: never modify it; copy it into the sandbox through the linked-computer file tools.
   - Only the game files are needed, about 1 GB. Copy `Autonauts_Data/` and the top-level `.exe`/`.dll` files.
   - Never commit or upload game files anywhere.
3. **Decompiled source (optional, for reading the game's code):** ILSpy 9.1 `ICSharpCode.Decompiler` on
   `Autonauts_Data/Managed/Assembly-CSharp.dll`. Keep it private (sandbox only).

## Build, test, package

```bash
./build.sh "<game copy>" "<extracted BepInEx>" out                 # -> out/AutonautsCoop.dll
./test/run.sh "<game copy>" "<BepInEx>" <work dir> 2400              # TCP suite
COOP_TRANSPORT=udp ./test/run.sh "<game copy>" "<BepInEx>" <work> 2400  # Steam framing over UDP stand-in
COOP_BIG=1 ./test/run.sh "<game copy>" "<BepInEx>" <work> 1800      # big-base stress test (small map)
COOP_MULTI=1 ./test/run.sh "<game copy>" "<BepInEx>" <work> 1500    # 3 players: host + game joiner + test/protobot.py
COOP_LAG=150 COOP_JITTER=50 COOP_NOSTEAM=1 COOP_BIND=127.0.0.1 COOP_DATA_BASE=<work> ./test/run.sh ... 3000   # lag + kit switches
./tools/package.sh "<BepInEx>" out/AutonautsCoop.dll 0.12.1 AutonautsCoop-0.12.1.zip   # player download
```

Running the tests:
- The runs take 10 to 25 minutes. Start them in the background (`nohup ... &`) and poll the log for `SUMMARY`; tool calls time out after 10 minutes.
- Don't edit `src/` while a run is about to start its build.
- Results are in `<work>/{host,client}/BepInEx/coop_test_*.txt` and `coop.log`.

**Last kit results (0.12.36d, owner's PC):** full 116/116, BRAVO save 18/18 (7 joiner rebuilds). 0.12.37 not yet run;
four-player test not yet run.
**Last cloud results (0.12.1):** 112/112 TCP, 95/95 UDP, 113/113 TCP with 150+50 ms simulated lag and the kit switches, 11/11+5/5 own-save (soak) scenario, 15/15 three-player (0.11), 7/7 big-base.

Sandbox limits:
- Two medium-map game instances (about 3 GB each) get OOM-killed, so the big test uses a small map.
- When stopping runs, kill with `pkill -x Autonauts.exe`. Never `pkill -f` with a pattern that matches your own command line.

## Releasing a new version

1. Bump `Version` in `src/Plugin.cs` and its header comment.
2. Update `README.md`: the status line, the test counts, and the "what works" / "known issues" lists. Update `dist/README-COOP.txt` (the player readme) if features changed.
3. Build, then run TCP + UDP (and the big test if sync or performance code changed) until clean.
4. Commit and push to `main`. Tag `vX.Y.Z-Alpha` (the existing tag style).
5. Create a GitHub release for that tag with the zip from `tools/package.sh` attached; players download it from there.
6. Hub: no change needed for normal releases (the entry links to this repo). Update PR #112, or open a new hub PR
   after it merges, only for milestones: a status change, a real Steam test result, or a major feature or known issue.
   For that, also update `games/autonauts/README.md`/`NOTES.md` and the entry's `notes`/`checked` fields.

## Testing on the owner's PC (test kit)

- **Default workflow (owner's choice):** build in the sandbox, test on the owner's PC ("PC-Shock", left on). Cloud
  test runs only for what the kit doesn't cover yet (UDP/Steam framing, three-player).
- The owner runs `kit\Watch.bat` (or set it to start at login with `WatchAtLogin.bat`). To start a test, write a
  file `kit\requests\<name>.req` (lines `mode=full|save`, `save=<save name>`, `lag=`, `jitter=`) with the
  device commit tool; read `kit\requests\<name>.done`, then the results folder it names. `kit\watcher-status.txt`
  shows the watcher is alive. Put the new release zip in `kit\` first (the kit uses the newest one).
- Read `kit\requests\<name>.done` first: it carries the run's `digest.txt` (result, errors, last stats lines,
  joiner rebuilds by type, world-check differences with both sides' JSON at the first difference). Open the full
  logs only when the digest isn't enough.
- `build.sh` also runs `tools/PatchCheck.cs` (mono): every Harmony patch must resolve to exactly one game method.
  One missing or ambiguous target stops PatchAll and the mod silently does nothing in the game (happened in 0.12.19:
  an overloaded method). A file `kit\requests\cancel` stops a running test; the kit also stops by itself when
  player.log shows a HarmonyException.
- `action=shutdown` in a request shuts the PC down after the requests before it (only when the owner asks).
  The watcher restarts itself when watch.ps1 changes; kit.ps1 is re-read every run.

- The owner's linked computer has only a Linux VM shell: it can't run the Windows game. The kit does instead.
- Put the release zip in `kit\` (gitignored) and have the owner double-click `kit\RunTests.bat`.
- Results land in `kit\results\<date>-<mode>\` (gitignored): `summary.txt`, both sides' `coop_test_*.txt`, `coop.log`,
  `coop_diag.log`, Unity `player.log`. Read them through the linked folder.
- The sandbox runs ~2 fps under Wine: tests that need N frames must count frames, not seconds.
- Real saves can use Steam Workshop (Lua) mods; the kit's copies still load them. Settlement mode has Folk inside
  huts (nested JSON) and a Transmitter that makes local, non-savable rewards: watch for objects the joiner creates
  on its own taking host UIDs.
- The sandbox can't hold two large-map copies for long (the joiner gets OOM-killed after ~5 min); use the kit.
- `coop_diag.log` is the first place to look in a real-session bug report: "send"/"from" lines pair each joiner
  action with the host's outcome, "rebuilt"/"didn't match" lines show sync churn, "stats 10s" lines show fps/RTT.

## Open work (priority order)

- Shared sections (Shared.cs): SectionLocks, one editor per section; HostIncoming refuses a push from a non-holder
  and answers {"srevert"}. Pushes are allowed while nobody holds the section (upload + link in one frame).
- Host scan (Scan.cs): movers by fingerprint (no tile; full every 8 s, every scan while locked/open); still objects
  by StaticFp (reflection over a field whitelist) every 4 s, full every 60 s. Stable() strips movers' own TX/TY and
  workers' "Instruction"; current lines go in "ip" next to "mv".
- Joiner apply: SplitUp() takes "up" elements as raw text; UpsertRaw skips matches before parsing.
- Section locks are change-based (SharedSection.LockTick: hash per tick while open; hold 5 s after the last change).
  Scripts has no lock (0.12.36): SharedSection.Merge = Library.Merge. The joiner's push carries "b" (the host copy it
  edited from); the host 3-way merges per script/folder UID (only entries the pusher changed; both changed -> host's
  kept and the pusher gets a "wasn't kept" note; a new entry with a taken UID gets a fresh one).
- Host first join: Scan.Warm fills the scan cache a few ms per frame while nobody is connected (the first join's
  flush was a full save of every object, ~175 ms on BRAVO). WorldState sections in Backoff slow to 4 s while unchanged.
- Scan hot objects (changed in the last 3 s): re-saved only when StaticFp changes or once a second.
- Join snapshot (Coop.SendSnapshot): flush without Scan.Full, capture on the main thread, gzip on a thread;
  Peer.Hold + Peer.Deferred keep the broadcasts made meanwhile and send them right after the snapshot.
- Joiner in-place reloads (0.12.37): converters are put back to Idle before Load (Sync.QuietIdle; a Converting ->
  Converting SetState ran EndConverting on the copy: ingredients destroyed, stage counted, mod callback fired).
  Blueprint Stage is set after Load (the game caps it at stages-1). Carry lists drop items no longer under the farmer.
  Sync.No() logs why a building fell back to a rebuild. Diag.TestWork() keeps test checksums out of frame stats.
- Joiner: P_ClientNoModWorldWrites blocks world-changing Mod* calls; LateCheck re-saves unverified in-place updates
  1.5 s later.
0. Garbage (GC pauses, ~200 ms each on a big save): host mod garbage ~8 MB/s (mostly re-saving objects to check
   them), joiner apply ~14 MB/s (parsing host JSON and re-serializing each object). Next ideas: per-type cheap
   fingerprints for the background round; apply host JSON without the parse/ToString round trip. Blueprints and huts
   update in place since 0.12.35/36 (blueprints keep their building-to-be; P_ClientKeepNewBuilding).
   Workshop (Lua) mods run on the joiner too: ModStorage writes are blocked there (0.12.24); other mod APIs that
   change the world (spawn objects, move bots) may still act locally (the digest removes extras).

0a. Native UI notes (0.12.38): layouts were checked from kit screenshots at 1280x720 (pause buttons in the free strip
   right of the Planet Scan panel; other aspect ratios not checked). "Invite Friends" needs Steam, so the kit (no
   Steam) never shows it. Bot lock labels ("Being programmed by NAME", Locks.OnGUI) are still IMGUI. Starting a new
   world ~2 s after the title screen appears fails in world generation (map preview in progress): tests start at once.
0b. Joiner hitch (noted 0.12.37, not yet investigated): ~200 ms frame about every 100 s on BRAVO ("apply max ~190-211 ms"
   in the joiner's 10 s stats; also in 0.12.36). Not a GC frame. Likely one large batch (periodic full round/digest).
0c. Rejoin duplicate-UID bug (0.12.37, BRAVO four-player run): right after a rejoin snapshot, a converter update whose
   ingredient was just taken from a bot hit "same key" in Plot.AddObject; the ingredient got a local UID (1e9+), the
   building was rebuilt, the extra removed by the digest ~3 s later. Self-heals; root cause open.
1. A real Steam session between two PCs. Collect the F8 bug report zip from both sides.
2. Real medium/large late-game saves: join time, host frame cost.
3. Three or more real game clients at once (tested: host + one game joiner + the protocol bot).
4. Rough edge: shared editors (library, planning, groups) are last-write-wins.
