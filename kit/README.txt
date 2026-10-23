AutonautsCoop test kit
Version 0.12.15 - 2026-10-23 - Created with Claude

What it does
  Runs two copies of Autonauts on this PC, a host and a joiner. The mod's built-in test bots play both
  sides and check that everything works and matches. The results land in kit\results\.

What it doesn't touch
  - Your Steam install: only read. The kit plays from its own copies in
    %LOCALAPPDATA%\AutonautsCoopTest (about 2 GB for the two copies).
  - Your saves: only read. When you test one of your saves, the kit copies it and plays the copy.
  - Steam: the copies don't start Steam (no achievements, stats or playtime). They connect over 127.0.0.1.

How to run
  1. Put the release zip (AutonautsCoop-<version>.zip) in this folder, next to RunTests.bat.
  2. Double-click RunTests.bat and pick a test:
       1) Full test: new world, every feature (about 20-25 min)
       2) Full test with internet-like lag (150 ms each way + 50 ms jitter)
       3) Your save: join it, walk, pick bots with the whistle, open a bot's program, watch, compare worlds
       4) Your save with internet-like lag
  3. Two game windows open. Leave them alone until the kit says RESULT.
  4. Tell Claude it finished. The results are in kit\results\ (newest folder; summary.txt first).

Needs: about 8 GB RAM and 2-3 GB free disk. Windows PowerShell (built into Windows 10/11).

Options (from a command prompt in this folder)
  RunTests.bat -Mode full
  RunTests.bat -Mode save -Save "My base" -Lag 150 -Jitter 50
  RunTests.bat -Mode full -Work D:\AutonautsCoopTest      (put the game copies on another drive)
  RunTests.bat -Mode save -WatchSeconds 600               (watch your save for 10 minutes)

Running tests without anyone at the PC (watcher)
  1. Double-click Watch.bat and leave its window open (or run WatchAtLogin.bat once so it starts when you log in;
     "WatchAtLogin.bat remove" undoes that).
  2. Claude drops a request file into kit\requests\ (for example mode=save, save=BRAVO). The watcher runs the kit
     for it and writes <name>.done with where the results are.
  3. The watcher only ever runs this test kit, with only these settings: mode (full/save), save (must be one of your
     saves), lag, jitter, minutes, watch. Close its window to stop it.
  The PC must stay logged in (locking the screen is fine; signing out or sleep stops it).
  4. A request with "action=shutdown" shuts the PC down after the requests before it (60 s warning; cancel with
     "shutdown /a" in a command prompt). Claude only sends it when you ask.
  5. Each results folder has digest.txt: the result, errors, frame/garbage stats, rebuilds and world-check
     differences in one short file. The .done file carries it too.
  6. When watch.ps1 is updated, the watcher restarts itself with the new version.
