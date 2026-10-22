AutonautsCoop test kit
Version 0.12.1 - 2026-10-21 - Created with Claude

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
