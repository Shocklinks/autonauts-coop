# HANDOFF: how to continue this project (for the next Claude session)

Version 0.11.0 - 2026-10-21 - Created with Claude

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
| Menu.cs | F8 window, toasts, "Host has ended the lobby." dialog |
| TestBot.cs, TestScript.cs, BigBase.cs, Multi.cs | Automated QA scenario (host/client bots), big-base stress test, three-player test |
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
./tools/package.sh "<BepInEx>" out/AutonautsCoop.dll 0.10.0 AutonautsCoop-0.10.0.zip   # player download
```

Running the tests:
- The runs take 10 to 25 minutes. Start them in the background (`nohup ... &`) and poll the log for `SUMMARY`; tool calls time out after 10 minutes.
- Don't edit `src/` while a run is about to start its build.
- Results are in `<work>/{host,client}/BepInEx/coop_test_*.txt` and `coop.log`.

**Last results (0.11.0):** 102/102 TCP, 85/85 UDP, 15/15 three-player; big-base 7/7 (0.10.0).

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

## Open work (priority order)

1. A real Steam session between two PCs. Collect the F8 bug report zip from both sides.
2. Real medium/large late-game saves: join time, host frame cost.
3. Three or more real game clients at once (tested: host + one game joiner + the protocol bot).
4. Rough edge: shared editors (library, planning, groups) are last-write-wins.
