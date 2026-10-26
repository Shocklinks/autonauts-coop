# AutonautsCoop test kit - kit.ps1
# Version 0.12.37 - 2026-10-26 - Created with Claude
#
# Runs two copies of Autonauts on this PC (a host and a joiner), both driven by the mod's built-in test bots
# (mode four: a host and three joiners, four copies),
# and collects the results in kit\results\<date>-<mode>\.
#   - Your Steam install is only read, never changed. The kit plays from its own copies (in -Work).
#   - Your saves are only read. A save you pick is copied; the test plays the copy.
#   - Steam is not started by the copies (no achievements, stats or playtime); they connect over 127.0.0.1.
# Usually started from RunTests.bat (menu). Direct use:
#   kit.ps1 -Mode full|save|play|four|ui [-Save "<save name>"] [-Lag 150] [-Jitter 50] [-Game <dir>] [-Work <dir>] [-Minutes 45]
param(
    [string]$Mode = "",
    [string]$Save = "",
    [int]$Lag = 0,
    [int]$Jitter = 0,
    [string]$Game = "",
    [string]$Work = "",
    [int]$Minutes = 45,
    [int]$WatchSeconds = 180,
    [int]$GcDivisor = 0          # >0: start both copies with GC_FREE_SPACE_DIVISOR (Unity's collector; read at start)
)
$ErrorActionPreference = "Stop"
$Kit = $PSScriptRoot
$Port = 27115
$SavesRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\Denki Ltd\Autonauts\Saves"

function Say([string]$s, [string]$color = "Gray") { Write-Host $s -ForegroundColor $color }
function Fail([string]$s) { Say ""; Say "STOPPED: $s" Red; exit 1 }

# ---------------- the game ----------------
function Find-Game {
    $c = New-Object System.Collections.Generic.List[string]
    try { $sp = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction Stop).SteamPath } catch { $sp = $null }
    if ($sp) {
        $c.Add((Join-Path $sp "steamapps\common\Autonauts"))
        $vdf = Join-Path $sp "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $c.Add((Join-Path ($m.Groups[1].Value -replace '\\\\', '\') "steamapps\common\Autonauts"))
            }
        }
    }
    $c.Add("B:\SteamLibrary\steamapps\common\Autonauts")
    foreach ($p in $c) { if (Test-Path (Join-Path $p "Autonauts.exe")) { return $p } }
    return $null
}

if (-not $Game) { $Game = Find-Game }
if (-not $Game -or -not (Test-Path (Join-Path $Game "Autonauts.exe"))) {
    $Game = Read-Host "Autonauts folder not found. Paste the folder that has Autonauts.exe"
    if (-not (Test-Path (Join-Path $Game "Autonauts.exe"))) { Fail "No Autonauts.exe in $Game" }
}
if (-not $Work) { $Work = Join-Path $env:LOCALAPPDATA "AutonautsCoopTest" }

# ---------------- the mod ----------------
$zip = Get-ChildItem -Path $Kit -Filter "AutonautsCoop-*.zip" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { Fail "Put the AutonautsCoop-<version>.zip release file in the kit folder ($Kit)." }
$ver = ($zip.BaseName -replace "^AutonautsCoop-", "")

# ---------------- menu ----------------
function Pick-Save {
    if (-not (Test-Path $SavesRoot)) { Fail "No saves folder at $SavesRoot" }
    $saves = @(Get-ChildItem -Path $SavesRoot -Directory | Sort-Object LastWriteTime -Descending)
    if ($saves.Count -eq 0) { Fail "No saves in $SavesRoot" }
    Say ""
    Say "Your saves (newest first). The test uses a copy; the original is not touched." White
    for ($i = 0; $i -lt [Math]::Min($saves.Count, 20); $i++) {
        $mb = [Math]::Round(((Get-ChildItem $saves[$i].FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
        Say ("  {0,2}) {1}   ({2:yyyy-MM-dd HH:mm}, {3} MB)" -f ($i + 1), $saves[$i].Name, $saves[$i].LastWriteTime, $mb)
    }
    $n = Read-Host "Number"
    $k = 0; if (-not [int]::TryParse($n, [ref]$k) -or $k -lt 1 -or $k -gt $saves.Count) { Fail "Not a number from the list." }
    return $saves[$k - 1].Name
}

if (-not $Mode) {
    Say ""
    Say "AutonautsCoop test kit (mod $ver)" White
    Say "  1) Full test: new world, every feature (about 20-25 min)"
    Say "  2) Full test with internet-like lag (150 ms each way + 50 ms jitter)"
    Say "  3) Your save: join it, walk, pick bots, open a program, watch, compare worlds (about 10 min)"
    Say "  4) Your save with internet-like lag"
    Say "  5) Play it yourself: host and joiner windows, no test bots (you control both)"
    Say "  6) Play it yourself with internet-like lag"
    Say "  7) Four players: host + 3 joiners, new world (four game windows, about 15 min)"
    Say "  8) Four players on your save"
    Say "  9) In-game multiplayer buttons (menus, join panel, notices; takes screenshots)"
    $choice = Read-Host "Choose 1-9"
    switch ($choice) {
        "1" { $Mode = "full" }
        "2" { $Mode = "full"; $Lag = 150; $Jitter = 50 }
        "3" { $Mode = "save" }
        "4" { $Mode = "save"; $Lag = 150; $Jitter = 50 }
        "5" { $Mode = "play" }
        "6" { $Mode = "play"; $Lag = 150; $Jitter = 50 }
        "7" { $Mode = "four" }
        "8" { $Mode = "four"; $Save = Pick-Save }
        "9" { $Mode = "ui" }
        default { Fail "Choose 1 to 9." }
    }
}
if ($Mode -eq "play" -and -not $Save) {
    $yn = Read-Host "Put one of your saves in the host's copy so you can load it? (y/n)"
    if ($yn -match '^[yY]') { $Save = Pick-Save }
}
if ($Mode -eq "save" -and -not $Save) { $Save = Pick-Save }
if ($Mode -eq "save" -and -not (Test-Path (Join-Path $SavesRoot $Save))) { Fail "Save not found: $Save" }
if ($Mode -eq "four" -and $Save -and -not (Test-Path (Join-Path $SavesRoot $Save))) { Fail "Save not found: $Save" }
# the copies of the game this mode runs (each its own folder, logs and save folder)
$Roles = if ($Mode -eq "four") { @("host", "client1", "client2", "client3") } else { @("host", "client") }
$Joiners = @($Roles | Where-Object { $_ -ne "host" })

# ---------------- space ----------------
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$gameMB = [Math]::Round(((Get-ChildItem $Game -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB), 0)
$drive = (Get-Item $Work).PSDrive
$freeMB = [Math]::Round($drive.Free / 1MB, 0)
$nc = $Roles.Count
$haveMB = 0; foreach ($r in $Roles) { if (Test-Path (Join-Path $Work "$r\Autonauts.exe")) { $haveMB += $gameMB } }
if ($freeMB + $haveMB -lt $gameMB * $nc + 1500) { Fail "Need about $([Math]::Round(($gameMB * $nc + 1500) / 1024, 1)) GB free on $($drive.Name): for the $nc game copies (have $([Math]::Round($freeMB / 1024, 1)) GB). Use -Work <folder> on another drive." }
$ramGB = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 0)
if ($ramGB -lt 4 * $nc) { Say "Warning: $ramGB GB RAM. $nc copies of the game need about $(3 * $nc)-$(4 * $nc) GB." Yellow }

# ---------------- copies ----------------
Say ""
Say "Game:  $Game" White
Say "Copies: $Work (first run copies about $([Math]::Round($gameMB / 1024, 1)) GB $nc times; later runs only update)" White
foreach ($p in Get-Process -Name "Autonauts" -ErrorAction SilentlyContinue) {
    if ($p.Path -and $p.Path.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $p.Id -Force }
}
$modDir = Join-Path $Work "mod-$ver"
if (-not (Test-Path (Join-Path $modDir "BepInEx\plugins\AutonautsCoop.dll"))) {
    if (Test-Path $modDir) { Remove-Item $modDir -Recurse -Force }
    Expand-Archive -Path $zip.FullName -DestinationPath $modDir -Force
}
foreach ($r in $Roles) {
    $d = Join-Path $Work $r
    Say "  copying the game for the $r..."
    & robocopy $Game $d /MIR /XD BepInEx /XF winhttp.dll doorstop_config.ini .doorstop_version changelog.txt steam_appid.txt /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying the game failed (robocopy $LASTEXITCODE)" }
    if (Test-Path (Join-Path $d "BepInEx")) { Remove-Item (Join-Path $d "BepInEx") -Recurse -Force }
    & robocopy $modDir $d /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Installing the mod failed (robocopy $LASTEXITCODE)" }
    Set-Content -Path (Join-Path $d "steam_appid.txt") -Value "979120" -Encoding ASCII
    $data = Join-Path $Work "data_$r"
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $data "Saves") | Out-Null
    Remove-Item (Join-Path $d "player.log") -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $d "BepInEx") -Filter "coop_ui_*.png" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
if ($Save -and ($Mode -eq "save" -or $Mode -eq "play" -or $Mode -eq "four")) {
    Say "  copying your save `"$Save`"..."
    & robocopy (Join-Path $SavesRoot $Save) (Join-Path $Work "data_host\Saves\$Save") /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying the save failed (robocopy $LASTEXITCODE)" }
}

# ---------------- collect (also during the run) ----------------
function Collect {
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($r in $Roles) {
        $b = Join-Path $Work "$r\BepInEx"
        foreach ($f in @("coop_test_$r.txt", "coop.log", "coop_diag.log", "LogOutput.log")) {
            $src = Join-Path $b $f
            if (Test-Path $src) { try { Copy-Item $src (Join-Path $out "$r-$f") -Force } catch {} }
        }
        $pl = Join-Path $Work "$r\player.log"
        if (Test-Path $pl) { try { Copy-Item $pl (Join-Path $out "$r-player.log") -Force } catch {} }
        foreach ($png in @(Get-ChildItem -Path $b -Filter "coop_ui_*.png" -ErrorAction SilentlyContinue)) { try { Copy-Item $png.FullName (Join-Path $out $png.Name) -Force } catch {} }
    }
}

# ---------------- digest: the few lines that matter, in one small file ----------------
function Cut([string]$s, [int]$n) { if ($s.Length -le $n) { return $s } return $s.Substring(0, $n) + "..." }
function Write-Digest([string]$dir, $summary) {
    $d = New-Object System.Collections.Generic.List[string]
    foreach ($l in $summary) { if ($l -ne "") { $d.Add($l) } }
    foreach ($r in $Roles) {
        $d.Add(""); $d.Add("== $r")
        $pl = Join-Path $dir "$r-player.log"
        if (Test-Path $pl) {
            $lines = Get-Content $pl
            $ex = 0; $where = @{}
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match 'Exception') {
                    $ex++
                    if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s+at (\S+)') { $k = ($lines[$i] -replace ':.*$', '') + " at " + $Matches[1]; $where[$k] = 1 + [int]$where[$k] }
                }
            }
            $d.Add("errors in player.log: $ex")
            foreach ($kv in ($where.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 4)) { $d.Add("  $($kv.Value)x $($kv.Key)") }
        }
        $cl = Join-Path $dir "$r-coop.log"
        if (Test-Path $cl) {
            $fails = @(Select-String -Path $cl -Pattern 'failed' -SimpleMatch | Where-Object { $_.Line -notmatch '^\S+ \[test\] (PASS|FAIL)' })
            $d.Add("'failed' lines in coop.log: $($fails.Count)")
            foreach ($f in ($fails | Select-Object -First 3)) { $d.Add("  " + (Cut $f.Line 220)) }
        }
        $dl = Join-Path $dir "$r-coop_diag.log"
        if (Test-Path $dl) {
            $stats = @(Select-String -Path $dl -Pattern ' stats 10s: ' -SimpleMatch)
            foreach ($st in ($stats | Select-Object -Last 2)) { $d.Add("  " + (Cut ($st.Line -replace '^.*stats 10s: ', 'stats: ') 700)) }
            if ($r -ne "host") {
                $rb = @(Select-String -Path $dl -Pattern ' rebuilt (\S+?)#' | ForEach-Object { $_.Matches[0].Groups[1].Value })
                $d.Add("rebuilds (could not update in place): $($rb.Count)")
                foreach ($g in ($rb | Group-Object | Sort-Object Count -Descending | Select-Object -First 8)) { $d.Add("  $($g.Count)x $($g.Name)") }
            }
        }
    }
    # objects the world check found different: where the two sides' JSON first differs
    $cc = Join-Path $dir "client-coop.log"; $hc = Join-Path $dir "host-coop.log"
    if ((Test-Path $cc) -and (Test-Path $hc)) {
        $mine = @{}; $theirs = @{}
        foreach ($m in (Select-String -Path $cc -Pattern '\[test\] stable here (\d+): (.*)$')) { $mine[$m.Matches[0].Groups[1].Value] = $m.Matches[0].Groups[2].Value }
        foreach ($m in (Select-String -Path $hc -Pattern '\[test\] stable host (\d+): (.*)$')) { $theirs[$m.Matches[0].Groups[1].Value] = $m.Matches[0].Groups[2].Value }
        if ($mine.Count -gt 0) { $d.Add(""); $d.Add("== world check differences (joiner J vs host H, around the first difference)") }
        foreach ($u in $mine.Keys) {
            $a = $mine[$u]; $b = if ($theirs.ContainsKey($u)) { $theirs[$u] } else { "" }
            $i = 0; $n = [Math]::Min($a.Length, $b.Length)
            while ($i -lt $n -and $a[$i] -eq $b[$i]) { $i++ }
            $s0 = [Math]::Max(0, $i - 120)
            $type = if ($a -match '^\{"ID":"([^"]+)"') { $Matches[1] } else { "?" }
            $d.Add("$type $u (joiner $($a.Length) chars, host $($b.Length))")
            $d.Add("  J: " + $a.Substring($s0, [Math]::Min(240, $a.Length - $s0)))
            if ($b.Length -gt $s0) { $d.Add("  H: " + $b.Substring($s0, [Math]::Min(240, $b.Length - $s0))) } else { $d.Add("  H: (not logged)") }
        }
    }
    $d | Set-Content -Path (Join-Path $dir "digest.txt") -Encoding UTF8
}

# ---------------- run ----------------
function Start-Role([string]$r) {
    if ($Mode -eq "play") { Remove-Item Env:COOP_TEST -ErrorAction SilentlyContinue; $env:COOP_NAME = $(if ($r -eq "host") { "Host" } else { "Joiner" }) }
    else { $env:COOP_TEST = $r; Remove-Item Env:COOP_NAME -ErrorAction SilentlyContinue }
    if ($Mode -eq "four") { $env:COOP_FOUR = "1"; $env:COOP_FOUR_SAVE = $Save; $env:COOP_LOWTEX = "1" }
    else { Remove-Item Env:COOP_FOUR, Env:COOP_FOUR_SAVE, Env:COOP_LOWTEX -ErrorAction SilentlyContinue }
    if ($Mode -eq "ui") { $env:COOP_UITEST = "1" } else { Remove-Item Env:COOP_UITEST -ErrorAction SilentlyContinue }
    $env:COOP_DATA = Join-Path $Work "data_$r"
    $env:COOP_NOSTEAM = "1"; $env:COOP_BIND = "127.0.0.1"; $env:COOP_IP = "127.0.0.1"; $env:COOP_PORT = "$Port"
    $env:COOP_LAG = "$Lag"; $env:COOP_JITTER = "$Jitter"
    if ($GcDivisor -gt 0) { $env:GC_FREE_SPACE_DIVISOR = "$GcDivisor" } else { Remove-Item Env:GC_FREE_SPACE_DIVISOR -ErrorAction SilentlyContinue }
    if ($Mode -eq "save") { $env:COOP_SOAK = "1"; $env:COOP_SAVE = $Save; $env:COOP_SOAK_SECONDS = "$WatchSeconds" }
    else { Remove-Item Env:COOP_SOAK, Env:COOP_SAVE, Env:COOP_SOAK_SECONDS -ErrorAction SilentlyContinue }
    $d = Join-Path $Work $r
    $log = Join-Path $d "player.log"
    return Start-Process -FilePath (Join-Path $d "Autonauts.exe") -WorkingDirectory $d -PassThru `
        -ArgumentList @("-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720", "-logFile", "`"$log`"")
}

$label = $Mode + $(if ($Mode -eq "four" -and $Save) { "-save" } else { "" }) + $(if ($Lag -gt 0 -or $Jitter -gt 0) { "-lag$Lag" } else { "" })
if ($GcDivisor -gt 0) { $label += "-gc$GcDivisor" }
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $Kit "results\$stamp-$label"
$resFiles = @($Roles | ForEach-Object { Join-Path $Work "$_\BepInEx\coop_test_$_.txt" })
$resJoiners = @($Joiners | ForEach-Object { Join-Path $Work "$_\BepInEx\coop_test_$_.txt" })
Remove-Item $resFiles -ErrorAction SilentlyContinue
function All-Done { foreach ($f in $resJoiners) { if (-not ((Test-Path $f) -and (Select-String -Path $f -Pattern "^DONE" -Quiet))) { return $false } } return $true }

if ($Mode -eq "play") {
    Say ""
    Say "Starting the host and the joiner ($label). Two game windows open; no test bots." White
    Say "  Host window:   start a new game or load (your save is under Load), then F8 > Host." White
    Say "  Joiner window: F8 > join by IP 127.0.0.1, port 27015 > Join." White
    Say "  Steam is off in both copies: names are Host and Joiner. F7 marks a moment in both diagnostics logs." White
    Say "  Close both game windows when you're done; the logs are then saved to kit\results." White
    $started = Get-Date
    $ph = Start-Role "host"
    Start-Sleep -Seconds 5
    $pc = Start-Role "client"
    $tick = 0
    while (-not ($ph.HasExited -and $pc.HasExited)) {
        Start-Sleep -Seconds 5
        $tick++; if ($tick % 12 -eq 0) { Collect }
    }
    Collect
    $summary = @(
        "AutonautsCoop test kit - $label (played by hand) - $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
        "Mod $ver   Game $Game",
        $(if ($Save) { "Save copied for the host: $Save" } else { "No save copied" }),
        "Lag: $Lag ms + $Jitter ms jitter each way",
        "Took $([Math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min",
        ""
    )
    $summary | Set-Content -Path (Join-Path $out "summary.txt") -Encoding UTF8
    try { Write-Digest $out $summary } catch {}
    Set-Content -Path (Join-Path $Kit "results\latest.txt") -Value $out -Encoding UTF8
    Say ""
    Say "Saved: $out (digest.txt first). Tell Claude what you saw; it reads the logs from there." White
    exit 0
}

Say ""
Say "Starting the host and $($Joiners.Count) joiner(s) ($label). $($Roles.Count) game windows open." White
Say "Leave them alone (don't click into them or minimize them). Close this window to stop." White
$started = Get-Date
$procs = @()
try {
    $procs += Start-Role "host"
    foreach ($j in $Joiners) { Start-Sleep -Seconds $(if ($Joiners.Count -gt 1) { 3 } else { 5 }); $procs += Start-Role $j }
    $lastShown = ""; $tick = 0
    $deadline = (Get-Date).AddMinutes($Minutes)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $lines = @()
        foreach ($f in $resFiles) { if (Test-Path $f) { $lines += @(Get-Content $f) } }
        $pass = @($lines | Where-Object { $_ -like "PASS*" }).Count
        $fail = @($lines | Where-Object { $_ -like "FAIL*" }).Count
        $last = $lines | Select-Object -Last 1
        $mins = [Math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        $status = "  $mins min: $pass passed, $fail failed"
        if ($status -ne $lastShown) { Write-Host "`r$status   " -NoNewline; $lastShown = $status }
        $tick++; if ($tick % 6 -eq 0) { Collect }   # partial results survive closing this window
        # stop early: asked to (kit\requests\cancel), or the mod failed to load (its patches didn't apply)
        $cancel = Join-Path $Kit "requests\cancel"
        if (Test-Path $cancel) { Remove-Item $cancel -Force -ErrorAction SilentlyContinue; Say ""; Say "Cancelled." Yellow; break }
        if ($tick % 6 -eq 3) {
            $broken = $false
            foreach ($r in $Roles) { $pl = Join-Path $Work "$r\player.log"; if ((Test-Path $pl) -and (Select-String -Path $pl -Pattern "HarmonyException" -SimpleMatch -Quiet)) { $broken = $true } }
            if ($broken) { Say ""; Say "The mod failed to load (HarmonyException in player.log): stopping." Red; break }
        }
        if (All-Done) { Start-Sleep -Seconds 5; break }
        if (@($procs | Where-Object { -not $_.HasExited }).Count -eq 0) { Say ""; Say "All games closed." Yellow; break }
    }
    Say ""
}
finally {
    foreach ($p in $procs) { if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch {} } }
}

Collect
$all = @()
foreach ($f in $resFiles) { if (Test-Path $f) { $all += @(Get-Content $f) } }
$pass = @($all | Where-Object { $_ -like "PASS*" }).Count
$fail = @($all | Where-Object { $_ -like "FAIL*" }).Count
$done = All-Done
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$gpu = (Get-CimInstance Win32_VideoController | Select-Object -First 1).Name
$summary = @(
    "AutonautsCoop test kit - $label - $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    "Mod $ver   Game $Game",
    ($(if ($Save -and $Mode -ne "full") { "Save: $Save" } else { "New world" }) + $(if ($Mode -eq "four") { " - four players (host + 3 joiners)" } else { "" })),
    "Lag: $Lag ms + $Jitter ms jitter each way",
    $(if ($GcDivisor -gt 0) { "GC_FREE_SPACE_DIVISOR=$GcDivisor" } else { "GC: default" }),
    "PC: $cpu, $ramGB GB RAM, $gpu",
    "Took $([Math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min. Finished: $done",
    "RESULT: $pass passed, $fail failed",
    ""
) + ($all | Where-Object { $_ -like "FAIL*" -or $_ -like "PASS info:*" })
$summary | Set-Content -Path (Join-Path $out "summary.txt") -Encoding UTF8
try { Write-Digest $out $summary } catch { Set-Content -Path (Join-Path $out "digest.txt") -Value "digest failed: $($_.Exception.Message)" -Encoding UTF8 }
Set-Content -Path (Join-Path $Kit "results\latest.txt") -Value $out -Encoding UTF8

# Started by an older watcher (it doesn't set COOP_WATCHER)? Swap it for the current one: start Watch.bat and stop
# the old watcher process (this run's results are already saved; the new watcher marks the request interrupted).
if (-not $env:COOP_WATCHER) {
    try {
        $me = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($me.ParentProcessId)"
        if ($parent -and $parent.CommandLine -match 'watch\.ps1') {
            Start-Process -FilePath (Join-Path $Kit "Watch.bat") -WorkingDirectory $Kit -WindowStyle Minimized
            $gp = Get-CimInstance Win32_Process -Filter "ProcessId=$($parent.ParentProcessId)"
            if ($gp -and $gp.CommandLine -match 'Watch\.bat') { Stop-Process -Id $gp.ProcessId -Force }   # its window (else it waits at "pause")
            Stop-Process -Id $parent.ProcessId -Force
        }
    } catch {}
}

Say ""
if ($fail -eq 0 -and $done) { Say "RESULT: all $pass passed." Green } else { Say "RESULT: $pass passed, $fail failed$(if (-not $done) { ', did not finish' })." Red }
foreach ($l in ($all | Where-Object { $_ -like "FAIL*" })) { Say "  $l" Red }
foreach ($l in ($all | Where-Object { $_ -like "PASS info:*" })) { Say "  $($l.Substring(5))" }
Say ""
Say "Results: $out" White
Say "Tell Claude the test finished; it reads the results from this folder."
