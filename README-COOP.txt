Autonauts Co-op 0.5.0 (alpha) - 2026-10-20 - Created with Claude
Mod loader: BepInEx 5.4.23.5 (included). Game: Autonauts (Steam).

INSTALL (every player, same version)
1. Steam > Autonauts > Manage > Browse local files.
2. Copy everything in this folder into that game folder (next to Autonauts.exe).
3. Start the game. BepInEx\coop.log appears = mod loaded.
Uninstall: delete winhttp.dll, doorstop_config.ini, .doorstop_version and the BepInEx folder.

HOST
1. Load or start a world. Press F8 > Host this world.
2. The Steam invite window opens. Invite friends (F8 > Invite Steam friends reopens it).

JOIN
1. Start Autonauts and stay on the main menu.
2. Accept the Steam invite, or right-click the host in Steam > Join Game.
Fallback: F8 > type the host's IP > Join (host must forward TCP 27015).

WHAT JOINERS CAN DO
- Walk, pick up, drop, use tools, chop, inventory keys (stow/recall/cycle/swap).
- Build: place, drag-place, move and delete buildings/blueprints (no undo for joiners).
- Teach bots: select a bot > Record > do the work > Play. Stop/pause/step/whistle work.
- Rename bots. Inventory screen: move items between you, bots, upgrade slots, wardrobes.
- While anyone records/edits a bot, others see "Being programmed by NAME" and can't record it.
- Leave and rejoin (same Steam name): you get your character and pockets back.

STATUS
- Tested automatically (2 game copies, one machine): everything above + whole-world checksum.
- Steam invites: NOT YET TESTED on real Steam.
- Not synced yet: saved/linked script library.
- Unlocks gained after joining may not show in the joiner's build menu until they rejoin.
- On the joiner, bots/animals slide without walk animations.
Bug reports: send BepInEx\coop.log from both PCs.
