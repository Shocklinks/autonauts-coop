Autonauts Co-op @VERSION@ (alpha) - @DATE@ - Created with Claude
Mod loader: BepInEx 5.4.23.5 (included). Game: Autonauts (Steam).

INSTALL (every player, same version)
1. Steam > Autonauts > Manage > Browse local files.
2. Copy everything in this folder into that game folder (next to Autonauts.exe).
3. Start the game. BepInEx\coop.log appears = mod loaded.
Uninstall: delete winhttp.dll, doorstop_config.ini, .doorstop_version and the BepInEx folder.

HOST
1. Load or start a world. Press F8 > Host this world.
2. The Steam invite window opens. Invite friends (F8 > Invite Steam friends reopens it).
Going back to the main menu ends the session for everyone.

JOIN
1. Start Autonauts and stay on the main menu.
2. Accept the Steam invite, or right-click the host in Steam > Join Game.
Fallback: F8 > type the host's IP > Join (host must forward TCP 27015).

WHAT JOINERS CAN DO
- Walk, pick up, drop, use tools, chop, inventory keys (stow/recall/cycle/swap).
- Build: place, drag-place, move and delete buildings/blueprints. Undo works for your last edit.
- Building menus: pick recipes, research, storage lock, upgrades, cancel, signs.
- Teach bots: select a bot > Record > do the work > Play. Stop/pause/step/whistle work.
- Rename bots. Inventory screen: move items between you, bots, upgrade slots, wardrobes.
- While anyone records/edits a bot, others see "Being programmed by NAME" and can't record it.
- Leave and rejoin: your character (position, hands, pockets, upgrades, clothes) is stored while you are away
  and comes back when you rejoin - next to the host if something was built where you stood. Survives host save/reload.
- Bot groups: create, rename, recolour, delete; group Play/Stop/Pause/Drop/Call.
- Notices when players join or leave. "Host has ended the lobby." when the host stops.
- More than 2 players work. Joining by IP without Steam? Set your name in F8 first.
- Joiners don't autosave. Save As keeps your own copy of the world.
- Both players need the same AutonautsCoop version; a mismatch is refused with a message.
- Research, building unlocks, resources, time of day and wardrobe contents follow the host live.
- Wardrobe and aquarium screens open for joiners.
- Bot server / script library, catapult aiming, spaceport missions and planning mode work for joiners.
- Game speed and pause: the host decides; joiners follow.

STATUS
- Tested automatically (2 game copies, one machine): everything above + whole-world checksum.
- Steam invites: NOT YET TESTED on real Steam.
- Host-only: changing game speed.
Bug reports: F8 > Save bug report, then send the zip from BepInEx\bugreports (a joiner's zip includes the host's log).
When something goes wrong, press F7 right away: it marks the moment in both players' diagnostics logs
(BepInEx\coop_diag.log, included in the bug report zip).
