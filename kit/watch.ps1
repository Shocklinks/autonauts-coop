# AutonautsCoop test kit - watch.ps1 (started by Watch.bat)
# Version 0.12.37 - 2026-10-26 - Created with Claude
#
# Waits for test requests and runs the kit for each one, so tests can be started without anyone at the PC.
#   - A request is a small text file in kit\requests\ named <anything>.req, for example:
#         mode=save
#         save=BRAVO
#         lag=0
#         jitter=0
#   - "mode=ui" tests the in-game multiplayer buttons and saves screenshots in the results folder.
#   - "mode=four" runs the four-player test (host + 3 joiners); add "save=<name>" to run it on a save.
#   - Only these settings are read, and only the test kit is ever run. Anything else in the file is ignored.
#   - While a request runs it is renamed .running; when done, <name>.done says where the results are and the
#     kit's console output is in <name>.log.
#   - kit\watcher-status.txt shows that the watcher is alive (updated every few seconds).
#   - "action=cleanzips" removes old AutonautsCoop-*.zip files from kit\ (keeps the newest).
#   - "gc=<1-9>" runs the copies with GC_FREE_SPACE_DIVISOR (Unity's garbage collector setting) for a comparison.
#   - A request with only "action=shutdown" shuts this PC down (60 s warning; "shutdown /a" in a command prompt
#     cancels). It waits its turn: requests before it run first.
#   - A file named "cancel" in kit\requests stops the test that is running (kit.ps1 checks for it).
#   - When watch.ps1 itself is updated, the watcher restarts itself with the new version (between requests).
# Close this window to stop watching.
$ErrorActionPreference = "Stop"
$Kit = $PSScriptRoot
$env:COOP_WATCHER = "2"   # tells kit.ps1 this watcher is current
$Self = $MyInvocation.MyCommand.Path
$SelfTime = (Get-Item $Self).LastWriteTimeUtc
$Req = Join-Path $Kit "requests"
$Status = Join-Path $Kit "watcher-status.txt"
$SavesRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\Denki Ltd\Autonauts\Saves"
New-Item -ItemType Directory -Force -Path $Req | Out-Null

function Set-Status([string]$s) {
    try { Set-Content -Path $Status -Value ("{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $s) -Encoding UTF8 } catch {}
}

function Read-Request([string]$path) {
    $r = @{ mode = ""; save = ""; lag = 0; jitter = 0; minutes = 45; watch = 180; action = ""; gc = 0 }
    foreach ($line in Get-Content $path) {
        if ($line -notmatch '^\s*([a-z]+)\s*=\s*(.*?)\s*$') { continue }
        $k = $Matches[1]; $v = $Matches[2]
        switch ($k) {
            "mode"    { if ($v -in @("full", "save", "four", "ui")) { $r.mode = $v } }
            "action"  { if ($v -in @("shutdown", "cleanzips")) { $r.action = $v } }
            "gc"      { $n = 0; if ([int]::TryParse($v, [ref]$n) -and $n -ge 1 -and $n -le 9) { $r.gc = $n } }
            "save"    { $r.save = $v }
            "lag"     { $n = 0; if ([int]::TryParse($v, [ref]$n) -and $n -ge 0 -and $n -le 2000) { $r.lag = $n } }
            "jitter"  { $n = 0; if ([int]::TryParse($v, [ref]$n) -and $n -ge 0 -and $n -le 2000) { $r.jitter = $n } }
            "minutes" { $n = 0; if ([int]::TryParse($v, [ref]$n) -and $n -ge 5 -and $n -le 120) { $r.minutes = $n } }
            "watch"   { $n = 0; if ([int]::TryParse($v, [ref]$n) -and $n -ge 30 -and $n -le 3600) { $r.watch = $n } }
        }
    }
    return $r
}

Write-Host "AutonautsCoop test watcher. Waiting for requests in $Req" -ForegroundColor White
Write-Host "Close this window to stop." -ForegroundColor White
Set-Status "idle"
# a request left half-done by a restart: say so instead of leaving it "running" forever
foreach ($old in @(Get-ChildItem -Path $Req -Filter "*.running" -ErrorAction SilentlyContinue)) {
    $latest = Join-Path $Kit "results\latest.txt"
    $res = if (Test-Path $latest) { (Get-Content $latest -Raw).Trim() } else { "?" }
    Set-Content -Path (Join-Path $Req "$($old.BaseName).done") -Value @("interrupted (watcher restarted); newest results: $res") -Encoding UTF8
    Remove-Item $old.FullName -Force -ErrorAction SilentlyContinue
}
while ($true) {
    # updated? start the new version and stop this one
    if ((Get-Item $Self).LastWriteTimeUtc -ne $SelfTime) {
        Write-Host ("{0}  watch.ps1 changed: restarting" -f (Get-Date -Format "HH:mm")) -ForegroundColor White
        Start-Process -FilePath (Join-Path $Kit "Watch.bat") -WorkingDirectory $Kit -WindowStyle Minimized
        exit 0
    }
    $next = Get-ChildItem -Path $Req -Filter "*.req" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -First 1
    if (-not $next) { Set-Status "idle"; Start-Sleep -Seconds 5; continue }

    $name = $next.BaseName
    $running = Join-Path $Req "$name.running"
    $done = Join-Path $Req "$name.done"
    $log = Join-Path $Req "$name.log"
    Move-Item -Path $next.FullName -Destination $running -Force
    $r = Read-Request $running
    if ($r.action -eq "cleanzips") {
        # keep the newest release zip in kit\ (the one tests use); remove the older AutonautsCoop-*.zip files
        $zips = @(Get-ChildItem -Path $Kit -Filter "AutonautsCoop-*.zip" | Sort-Object LastWriteTime -Descending)
        $gone = @()
        foreach ($z in ($zips | Select-Object -Skip 1)) { try { Remove-Item $z.FullName -Force; $gone += $z.Name } catch {} }
        Set-Content -Path $done -Value (@("kept: " + $(if ($zips.Count) { $zips[0].Name } else { "none" }), "removed $($gone.Count): " + ($gone -join ", "))) -Encoding UTF8
        Remove-Item $running -Force -ErrorAction SilentlyContinue
        Write-Host ("{0}  {1}: removed {2} old zips" -f (Get-Date -Format "HH:mm"), $name, $gone.Count) -ForegroundColor Cyan
        continue
    }
    if ($r.action -eq "shutdown") {
        Set-Content -Path $done -Value "shutting down $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') (60 s; cancel with: shutdown /a)" -Encoding UTF8
        Remove-Item $running -Force -ErrorAction SilentlyContinue
        Write-Host ("{0}  {1}: shutting this PC down in 60 s (cancel: shutdown /a)" -f (Get-Date -Format "HH:mm"), $name) -ForegroundColor Yellow
        Set-Status "shutting down"
        & shutdown.exe /s /t 60 /c "AutonautsCoop test watcher: tests done, shutting down. Cancel: shutdown /a"
        Start-Sleep -Seconds 90
        continue
    }
    $problem = $null
    if (-not $r.mode) { $problem = "mode must be full, save, four or ui" }
    elseif ($r.mode -eq "save" -or ($r.mode -eq "four" -and $r.save)) {
        $names = @(Get-ChildItem -Path $SavesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        if ($names -notcontains $r.save) { $problem = "no save named '$($r.save)'" }
    }
    if ($problem) {
        Set-Content -Path $done -Value "REFUSED: $problem" -Encoding UTF8
        Remove-Item $running -Force -ErrorAction SilentlyContinue
        Write-Host ("{0}  {1}: refused ({2})" -f (Get-Date -Format "HH:mm"), $name, $problem) -ForegroundColor Yellow
        continue
    }

    $desc = "$($r.mode)" + $(if ($r.save -and $r.mode -ne "full") { " '$($r.save)'" } else { "" }) + " lag $($r.lag)+$($r.jitter)"
    Write-Host ("{0}  {1}: running {2}" -f (Get-Date -Format "HH:mm"), $name, $desc) -ForegroundColor Cyan
    Set-Status "running $name ($desc)"
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $Kit "kit.ps1"), "-Mode", $r.mode,
              "-Lag", "$($r.lag)", "-Jitter", "$($r.jitter)", "-Minutes", "$($r.minutes)", "-WatchSeconds", "$($r.watch)", "-GcDivisor", "$($r.gc)")
    if ($r.save -and $r.mode -ne "full") { $argList += @("-Save", $r.save) }
    $latestFile = Join-Path $Kit "results\latest.txt"
    $before = if (Test-Path $latestFile) { (Get-Content $latestFile -Raw).Trim() } else { "" }
    try {
        $argLine = ($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join " "   # quote names with spaces
        $p = Start-Process -FilePath "powershell.exe" -ArgumentList $argLine -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $code = $p.ExitCode
    }
    catch { $code = -1; Add-Content -Path $log -Value "watcher: $($_.Exception.Message)" }
    $after = if (Test-Path $latestFile) { (Get-Content $latestFile -Raw).Trim() } else { "" }
    $result = if ($after -and $after -ne $before) { $after } else { "(no results folder)" }
    $summary = Join-Path $result "summary.txt"
    $lines = @("request: $desc", "exit: $code", "results: $result")
    $digest = Join-Path $result "digest.txt"
    if (Test-Path $digest) { $lines += (Get-Content $digest | Select-Object -First 80) }
    elseif (Test-Path $summary) { $lines += (Get-Content $summary | Select-Object -First 8) }
    Set-Content -Path $done -Value $lines -Encoding UTF8
    Remove-Item $running -Force -ErrorAction SilentlyContinue
    Write-Host ("{0}  {1}: done ({2})" -f (Get-Date -Format "HH:mm"), $name, (($lines | Where-Object { $_ -like "RESULT*" }) -join "")) -ForegroundColor Green
}
