# AutonautsCoop test kit - kit.ps1
# Version 0.12.2 - 2026-10-22 - Created with Claude
#
# Runs two copies of Autonauts on this PC (a host and a joiner), both driven by the mod's built-in test bots,
# and collects the results in kit\results\<date>-<mode>\.
#   - Your Steam install is only read, never changed. The kit plays from its own copies (in -Work).
#   - Your saves are only read. A save you pick is copied; the test plays the copy.
#   - Steam is not started by the copies (no achievements, stats or playtime); they connect over 127.0.0.1.
# Usually started from RunTests.bat (menu). Direct use:
#   kit.ps1 -Mode full|save [-Save "<save name>"] [-Lag 150] [-Jitter 50] [-Game <dir>] [-Work <dir>] [-Minutes 45]
param(
    [string]$Mode = "",
    [string]$Save = "",
    [int]$Lag = 0,
    [int]$Jitter = 0,
    [string]$Game = "",
    [string]$Work = "",
    [int]$Minutes = 45,
    [int]$WatchSeconds = 180
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
    $choice = Read-Host "Choose 1-4"
    switch ($choice) {
        "1" { $Mode = "full" }
        "2" { $Mode = "full"; $Lag = 150; $Jitter = 50 }
        "3" { $Mode = "save" }
        "4" { $Mode = "save"; $Lag = 150; $Jitter = 50 }
        default { Fail "Choose 1, 2, 3 or 4." }
    }
}
if ($Mode -eq "save" -and -not $Save) { $Save = Pick-Save }
if ($Mode -eq "save" -and -not (Test-Path (Join-Path $SavesRoot $Save))) { Fail "Save not found: $Save" }

# ---------------- space ----------------
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$gameMB = [Math]::Round(((Get-ChildItem $Game -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB), 0)
$drive = (Get-Item $Work).PSDrive
$freeMB = [Math]::Round($drive.Free / 1MB, 0)
$haveMB = 0; if (Test-Path (Join-Path $Work "host\Autonauts.exe")) { $haveMB = $gameMB * 2 }
if ($freeMB + $haveMB -lt $gameMB * 2 + 1500) { Fail "Need about $([Math]::Round(($gameMB * 2 + 1500) / 1024, 1)) GB free on $($drive.Name): for the two game copies (have $([Math]::Round($freeMB / 1024, 1)) GB). Use -Work <folder> on another drive." }
$ramGB = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 0)
if ($ramGB -lt 8) { Say "Warning: $ramGB GB RAM. Two copies of the game need about 6-8 GB." Yellow }

# ---------------- copies ----------------
Say ""
Say "Game:  $Game" White
Say "Copies: $Work (first run copies about $([Math]::Round($gameMB / 1024, 1)) GB twice; later runs only update)" White
foreach ($p in Get-Process -Name "Autonauts" -ErrorAction SilentlyContinue) {
    if ($p.Path -and $p.Path.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $p.Id -Force }
}
$modDir = Join-Path $Work "mod-$ver"
if (-not (Test-Path (Join-Path $modDir "BepInEx\plugins\AutonautsCoop.dll"))) {
    if (Test-Path $modDir) { Remove-Item $modDir -Recurse -Force }
    Expand-Archive -Path $zip.FullName -DestinationPath $modDir -Force
}
foreach ($r in @("host", "client")) {
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
}
if ($Mode -eq "save") {
    Say "  copying your save `"$Save`"..."
    & robocopy (Join-Path $SavesRoot $Save) (Join-Path $Work "data_host\Saves\$Save") /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying the save failed (robocopy $LASTEXITCODE)" }
}

# ---------------- collect (also during the run) ----------------
function Collect {
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($r in @("host", "client")) {
        $b = Join-Path $Work "$r\BepInEx"
        foreach ($f in @("coop_test_$r.txt", "coop.log", "coop_diag.log", "LogOutput.log")) {
            $src = Join-Path $b $f
            if (Test-Path $src) { try { Copy-Item $src (Join-Path $out "$r-$f") -Force } catch {} }
        }
        $pl = Join-Path $Work "$r\player.log"
        if (Test-Path $pl) { try { Copy-Item $pl (Join-Path $out "$r-player.log") -Force } catch {} }
    }
}

# ---------------- run ----------------
function Start-Role([string]$r) {
    $env:COOP_TEST = $r
    $env:COOP_DATA = Join-Path $Work "data_$r"
    $env:COOP_NOSTEAM = "1"; $env:COOP_BIND = "127.0.0.1"; $env:COOP_IP = "127.0.0.1"; $env:COOP_PORT = "$Port"
    $env:COOP_LAG = "$Lag"; $env:COOP_JITTER = "$Jitter"
    if ($Mode -eq "save") { $env:COOP_SOAK = "1"; $env:COOP_SAVE = $Save; $env:COOP_SOAK_SECONDS = "$WatchSeconds" }
    else { Remove-Item Env:COOP_SOAK, Env:COOP_SAVE, Env:COOP_SOAK_SECONDS -ErrorAction SilentlyContinue }
    $d = Join-Path $Work $r
    $log = Join-Path $d "player.log"
    return Start-Process -FilePath (Join-Path $d "Autonauts.exe") -WorkingDirectory $d -PassThru `
        -ArgumentList @("-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720", "-logFile", "`"$log`"")
}

$label = $Mode + $(if ($Lag -gt 0 -or $Jitter -gt 0) { "-lag$Lag" } else { "" })
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $Kit "results\$stamp-$label"
$resHost = Join-Path $Work "host\BepInEx\coop_test_host.txt"
$resClient = Join-Path $Work "client\BepInEx\coop_test_client.txt"
Remove-Item $resHost, $resClient -ErrorAction SilentlyContinue

Say ""
Say "Starting the host and the joiner ($label). Two game windows open." White
Say "Leave them alone (don't click into them or minimize them). Close this window to stop." White
$started = Get-Date
$ph = $null; $pc = $null
try {
    $ph = Start-Role "host"
    Start-Sleep -Seconds 5
    $pc = Start-Role "client"
    $lastShown = ""; $tick = 0
    $deadline = (Get-Date).AddMinutes($Minutes)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $lines = @()
        foreach ($f in @($resHost, $resClient)) { if (Test-Path $f) { $lines += @(Get-Content $f) } }
        $pass = @($lines | Where-Object { $_ -like "PASS*" }).Count
        $fail = @($lines | Where-Object { $_ -like "FAIL*" }).Count
        $last = $lines | Select-Object -Last 1
        $mins = [Math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        $status = "  $mins min: $pass passed, $fail failed"
        if ($status -ne $lastShown) { Write-Host "`r$status   " -NoNewline; $lastShown = $status }
        $tick++; if ($tick % 6 -eq 0) { Collect }   # partial results survive closing this window
        if ((Test-Path $resClient) -and (Select-String -Path $resClient -Pattern "^DONE" -Quiet)) { Start-Sleep -Seconds 5; break }
        if ($ph.HasExited -and $pc.HasExited) { Say ""; Say "Both games closed." Yellow; break }
    }
    Say ""
}
finally {
    foreach ($p in @($ph, $pc)) { if ($p -and -not $p.HasExited) { try { Stop-Process -Id $p.Id -Force } catch {} } }
}

Collect
$all = @()
foreach ($f in @($resHost, $resClient)) { if (Test-Path $f) { $all += @(Get-Content $f) } }
$pass = @($all | Where-Object { $_ -like "PASS*" }).Count
$fail = @($all | Where-Object { $_ -like "FAIL*" }).Count
$done = (Test-Path $resClient) -and (Select-String -Path $resClient -Pattern "^DONE" -Quiet)
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$gpu = (Get-CimInstance Win32_VideoController | Select-Object -First 1).Name
$summary = @(
    "AutonautsCoop test kit - $label - $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    "Mod $ver   Game $Game",
    $(if ($Mode -eq "save") { "Save: $Save" } else { "New world" }),
    "Lag: $Lag ms + $Jitter ms jitter each way",
    "PC: $cpu, $ramGB GB RAM, $gpu",
    "Took $([Math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min. Finished: $done",
    "RESULT: $pass passed, $fail failed",
    ""
) + ($all | Where-Object { $_ -like "FAIL*" -or $_ -like "PASS info:*" })
$summary | Set-Content -Path (Join-Path $out "summary.txt") -Encoding UTF8
Set-Content -Path (Join-Path $Kit "results\latest.txt") -Value $out -Encoding UTF8

Say ""
if ($fail -eq 0 -and $done) { Say "RESULT: all $pass passed." Green } else { Say "RESULT: $pass passed, $fail failed$(if (-not $done) { ', did not finish' })." Red }
foreach ($l in ($all | Where-Object { $_ -like "FAIL*" })) { Say "  $l" Red }
foreach ($l in ($all | Where-Object { $_ -like "PASS info:*" })) { Say "  $($l.Substring(5))" }
Say ""
Say "Results: $out" White
Say "Tell Claude the test finished; it reads the results from this folder."
