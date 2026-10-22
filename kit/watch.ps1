# AutonautsCoop test kit - watch.ps1 (started by Watch.bat)
# Version 0.12.5 - 2026-10-22 - Created with Claude
#
# Waits for test requests and runs the kit for each one, so tests can be started without anyone at the PC.
#   - A request is a small text file in kit\requests\ named <anything>.req, for example:
#         mode=save
#         save=BRAVO
#         lag=0
#         jitter=0
#   - Only these settings are read, and only the test kit is ever run. Anything else in the file is ignored.
#   - While a request runs it is renamed .running; when done, <name>.done says where the results are and the
#     kit's console output is in <name>.log.
#   - kit\watcher-status.txt shows that the watcher is alive (updated every few seconds).
# Close this window to stop watching.
$ErrorActionPreference = "Stop"
$Kit = $PSScriptRoot
$Req = Join-Path $Kit "requests"
$Status = Join-Path $Kit "watcher-status.txt"
$SavesRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\Denki Ltd\Autonauts\Saves"
New-Item -ItemType Directory -Force -Path $Req | Out-Null

function Set-Status([string]$s) {
    try { Set-Content -Path $Status -Value ("{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $s) -Encoding UTF8 } catch {}
}

function Read-Request([string]$path) {
    $r = @{ mode = ""; save = ""; lag = 0; jitter = 0; minutes = 45; watch = 180 }
    foreach ($line in Get-Content $path) {
        if ($line -notmatch '^\s*([a-z]+)\s*=\s*(.*?)\s*$') { continue }
        $k = $Matches[1]; $v = $Matches[2]
        switch ($k) {
            "mode"    { if ($v -in @("full", "save")) { $r.mode = $v } }
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
while ($true) {
    $next = Get-ChildItem -Path $Req -Filter "*.req" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -First 1
    if (-not $next) { Set-Status "idle"; Start-Sleep -Seconds 5; continue }

    $name = $next.BaseName
    $running = Join-Path $Req "$name.running"
    $done = Join-Path $Req "$name.done"
    $log = Join-Path $Req "$name.log"
    Move-Item -Path $next.FullName -Destination $running -Force
    $r = Read-Request $running
    $problem = $null
    if (-not $r.mode) { $problem = "mode must be full or save" }
    elseif ($r.mode -eq "save") {
        $names = @(Get-ChildItem -Path $SavesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        if ($names -notcontains $r.save) { $problem = "no save named '$($r.save)'" }
    }
    if ($problem) {
        Set-Content -Path $done -Value "REFUSED: $problem" -Encoding UTF8
        Remove-Item $running -Force -ErrorAction SilentlyContinue
        Write-Host ("{0}  {1}: refused ({2})" -f (Get-Date -Format "HH:mm"), $name, $problem) -ForegroundColor Yellow
        continue
    }

    $desc = "$($r.mode)" + $(if ($r.mode -eq "save") { " '$($r.save)'" } else { "" }) + " lag $($r.lag)+$($r.jitter)"
    Write-Host ("{0}  {1}: running {2}" -f (Get-Date -Format "HH:mm"), $name, $desc) -ForegroundColor Cyan
    Set-Status "running $name ($desc)"
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $Kit "kit.ps1"), "-Mode", $r.mode,
              "-Lag", "$($r.lag)", "-Jitter", "$($r.jitter)", "-Minutes", "$($r.minutes)", "-WatchSeconds", "$($r.watch)")
    if ($r.mode -eq "save") { $argList += @("-Save", $r.save) }
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
    if (Test-Path $summary) { $lines += (Get-Content $summary | Select-Object -First 8) }
    Set-Content -Path $done -Value $lines -Encoding UTF8
    Remove-Item $running -Force -ErrorAction SilentlyContinue
    Write-Host ("{0}  {1}: done ({2})" -f (Get-Date -Format "HH:mm"), $name, (($lines | Where-Object { $_ -like "RESULT*" }) -join "")) -ForegroundColor Green
}
