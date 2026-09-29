# Shared helpers for the DSH-App test scripts.  (ASCII only -- AGENTS.md rule 1)
#
# Every test script dot-sources this file:
#     . "$PSScriptRoot\_lib.ps1"
#
# It exists because the same handful of mistakes kept being repeated by hand during
# development, each time costing a full verification round:
#
#   * reading the WHOLE app.log and matching a phrase that a previous run left behind
#     (a passing check that proves nothing) -- see New-LogOffset / Wait-LogLine
#   * killing processes with "taskkill /T", which takes the harness-managed shell that
#     ran the command down with it -- see Stop-AppAll
#   * leaving a stale harness holding the port, so the next test's result is nonsense
#   * leaving sandboxes, fake harnesses or config mutations behind

# Continue, deliberately: several helpers call cmdlets that may legitimately fail (log
# reads while the app holds the file, port probes for a port nobody owns) and handle it
# with -ErrorAction SilentlyContinue. Making everything terminating would change the
# semantics of all nine tests for no gain -- Complete-Test already refuses to report a
# pass when a helper blew up.
$ErrorActionPreference = 'Continue'

# Set when a helper throws something the test body does not catch, so Complete-Test can
# refuse to report a pass over a test that died halfway through.
$script:Fatal = $null

trap {
    # Deliberately does NOT exit: the test script's own "exit (Complete-Test ...)" still
    # has to run, because leaving the trap to unwind past it would skip the cleanup in the
    # test's finally block and strand sandboxes, processes and a modified config.
    $script:Fatal = $_.Exception.Message
    Write-Host ("    [FATAL] " + $_.Exception.Message) -ForegroundColor Red
    Write-Host ("            at " + $_.InvocationInfo.PositionMessage.Trim()) -ForegroundColor DarkGray
}

$script:RepoRoot   = Split-Path -Parent $PSScriptRoot
$script:DistExe    = Join-Path $script:RepoRoot 'dist\DSH-App\DSH-App.exe'
$script:DataDir    = Join-Path $env:LOCALAPPDATA 'DSH-App'
$script:AppLog     = Join-Path $script:DataDir 'logs\app.log'
$script:ConfigIni  = Join-Path $script:DataDir 'config.ini'
$script:RealDsh    = Join-Path $env:USERPROFILE '.dsh'
$script:Results    = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------- ports
#
# Every port the suite uses is set here and overridable from the environment, so the suite
# can run even when the default 3099 is already taken. That is not hypothetical: 3099 is
# DSH-App's own default, so the moment this suite is run from inside a running DSH-App, the
# app under test is the app hosting the session -- and 3099 belongs to the very harness
# that is running the tests.
#
#   $env:DSH_TEST_PORT      base port, for the tests that drive the real config (3099)
#   $env:DSH_TEST_PORT2     second port, for tests that must not share the base one (3105)
#   $env:DSH_CONFLICT_PORT  port the foreign holder occupies (3098)
#   $env:DSH_TEST_DEBUG     DevTools port for the app under test (9333)
$script:TestPort     = if ($env:DSH_TEST_PORT)     { [int]$env:DSH_TEST_PORT }     else { 3099 }
$script:TestPort2    = if ($env:DSH_TEST_PORT2)    { [int]$env:DSH_TEST_PORT2 }    else { 3105 }
$script:ConflictPort = if ($env:DSH_CONFLICT_PORT) { [int]$env:DSH_CONFLICT_PORT } else { 3098 }
$script:DebugPort    = if ($env:DSH_TEST_DEBUG)    { [int]$env:DSH_TEST_DEBUG }    else { 9333 }

# ---------------------------------------------------------------- logging / results

function Write-Head($text) {
    Write-Host ""
    Write-Host ("=== " + $text + " ===") -ForegroundColor Cyan
}

function Write-Info($text) { Write-Host ("    " + $text) }

function Assert-That {
    param(
        [Parameter(Mandatory=$true)][bool]$Condition,
        [Parameter(Mandatory=$true)][string]$What
    )
    $tag = if ($Condition) { 'PASS' } else { 'FAIL' }
    $color = if ($Condition) { 'Green' } else { 'Red' }
    Write-Host ("    [" + $tag + "] " + $What) -ForegroundColor $color
    [void]$script:Results.Add([pscustomobject]@{ Ok = $Condition; What = $What })
}

# Prints the summary and returns the process exit code (0 only when all passed).
function Complete-Test {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        # Floor on the number of assertions. A test that dies partway can end up reporting
        # a handful of passing checks as a pass; every real test in this suite makes more
        # than this, so a smaller count means the run was cut short.
        [int]$MinimumChecks = 3
    )
    if ($script:Fatal) {
        # A helper blew up mid-test. Without this the run would report the checks that
        # happened to execute before the exception as "ALL PASS" -- a green light over a
        # test that never finished. It happened: a bad log read threw once and the suite
        # printed "ALL PASS (1 checks)", which is worse than no test at all.
        Write-Host ("    ABORTED: " + $script:Fatal) -ForegroundColor Red
        return 1
    }
    $failed = @($script:Results | Where-Object { -not $_.Ok })
    Write-Head ("result: " + $Name)
    if ($script:Results.Count -lt $MinimumChecks) {
        # The message is built first: a parenthesised Write-Host argument cannot be split
        # across lines with a leading "+", because the closing ")" ends the command and the
        # next line starts an invalid expression.
        $msg = "    ABORTED: only " + $script:Results.Count + " check(s) ran, expected at least " + $MinimumChecks + " -- the test stopped early"
        Write-Host $msg -ForegroundColor Red
        return 1
    }
    if ($failed.Count -eq 0) {
        Write-Host ("    ALL PASS (" + $script:Results.Count + " checks)") -ForegroundColor Green
        return 0
    }
    Write-Host ("    FAILED: " + $failed.Count + " of " + $script:Results.Count) -ForegroundColor Red
    foreach ($f in $failed) { Write-Host ("      - " + $f.What) -ForegroundColor Red }
    return 1
}

# ---------------------------------------------------------------- app log, the safe way

# Records how long app.log is right now. Always take one BEFORE starting an app, then
# hand it to Wait-LogLine: matching the whole file would happily "prove" success using
# a line from a previous run.
function New-LogOffset {
    param([string]$LogPath = "")
    if (-not $LogPath) { $LogPath = $script:AppLog }
    if (-not (Test-Path $LogPath)) { return 0 }
    return (Get-Item $LogPath).Length
}

function Get-NewLogText {
    param(
        [Parameter(Mandatory=$true)][long]$Offset,
        [string]$LogPath = ""
    )
    if (-not $LogPath) { $LogPath = $script:AppLog }
    if (-not (Test-Path $LogPath)) { return "" }
    $length = (Get-Item $LogPath).Length

    # Everything here is in BYTES, matching New-LogOffset. Mixing byte offsets with
    # Substring()'s character indexes would quietly slice the wrong place as soon as a
    # non-ASCII line appeared in the log.
    #
    # Rotation. AppLogger moves app.log to app.prev.log once it passes 1 MB and starts a
    # fresh file, so an offset taken before that points past the END of the new file and
    # every later read returns "". That is not hypothetical: it silently produced an empty
    # log line in the watchdog test, which sent the investigation chasing a product bug
    # that did not exist. When the file shrank, the tail we want is split across the two
    # files, so read each part separately instead of assuming one file.
    if ($length -lt $Offset) {
        $prev = Join-Path (Split-Path $LogPath -Parent) 'app.prev.log'
        $head = ""
        if ((Test-Path $prev) -and (Get-Item $prev).Length -gt $Offset) {
            $head = Read-LogFile -Path $prev -From $Offset
        }
        return $head + (Read-LogFile -Path $LogPath -From 0)
    }

    return (Read-LogFile -Path $LogPath -From $Offset)
}

# Reads a log file from a byte offset.
#
# Must go through a FileStream with FileShare.ReadWrite: the app keeps app.log open for
# writing, and File.ReadAllBytes asks for exclusive-ish access and throws "being used by
# another process". That is not a theoretical concern -- it tore this test suite down
# mid-run once, leaving a bogus "ALL PASS (1 checks)".
function Read-LogFile {
    param([Parameter(Mandatory=$true)][string]$Path, [long]$From = 0)
    if (-not (Test-Path $Path)) { return "" }
    $fs = New-Object System.IO.FileStream($Path, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $fs.Length
        if ($len -le $From) { return "" }
        $fs.Seek($From, 'Begin') | Out-Null
        $buf = New-Object byte[] ($len - $From)
        $read = 0
        while ($read -lt $buf.Length) {
            $n = $fs.Read($buf, $read, $buf.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    } finally { $fs.Dispose() }
}

# Waits for a regex to show up in the log text written AFTER $Offset.
function Wait-LogLine {
    param(
        [Parameter(Mandatory=$true)][long]$Offset,
        [Parameter(Mandatory=$true)][string]$Pattern,
        [int]$TimeoutSec = 60,
        [string]$LogPath = ""
    )
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) {
        $text = Get-NewLogText -Offset $Offset -LogPath $LogPath
        if ($text -match $Pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-LogLine {
    param(
        [Parameter(Mandatory=$true)][long]$Offset,
        [Parameter(Mandatory=$true)][string]$Pattern,
        [string]$LogPath = ""
    )
    $text = Get-NewLogText -Offset $Offset -LogPath $LogPath
    $m = [regex]::Matches($text, $Pattern)
    if ($m.Count -eq 0) { return $null }
    return $m[$m.Count - 1].Value.Trim()
}

# ---------------------------------------------------------------- processes

# Clears stray harnesses off the given ports, and (unless told not to) ends every DSH-App.
#
# Two deliberate rules:
#   * never "taskkill /T": in this environment the shell running the command belongs to
#     the same job tree, so a tree kill takes the test with it
#   * kill the harness by pid as well, because a killed launcher can leave it behind
#     holding the port -- which silently poisons the next test
#
# SkipApps exists because of how this suite is normally run: from inside DSH-App. Ending
# "every DSH-App" therefore ends the one hosting the session and running these tests, which
# takes the test run down with it. -SkipApps clears only the ports, leaving other apps
# alone; per-test cleanup uses Stop-AppTree to end exactly the app the test started.
function Stop-AppAll {
    param([int[]]$Ports = @(), [switch]$SkipApps)

    if (-not $SkipApps) {
        foreach ($p in @(Get-Process DSH-App -ErrorAction SilentlyContinue)) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 2
    }

    foreach ($port in $Ports) {
        $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
             Select-Object -First 1
        if ($c) {
            $owner = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $c.OwningProcess) -ErrorAction SilentlyContinue
            if ($owner -and $owner.CommandLine -and $owner.CommandLine -match 'bin\.js') {
                Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Start-Sleep -Seconds 1
}

# Ends ONE app that this test started, plus the harness it owns -- and nothing else.
#
# Stop-AppAll above is a blunt instrument: it ends every DSH-App on the machine. That is
# fine when the suite owns the desktop, but the suite is normally launched *from* DSH-App,
# so "every DSH-App" includes the one hosting the session and running these very tests.
# This helper is the surgical version, for cleanup.
function Stop-AppTree {
    param([int]$ProcessId)

    if ($ProcessId -le 0) { return }
    foreach ($child in @(Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $ProcessId) -ErrorAction SilentlyContinue)) {
        # The harness is a node process; the WebView2 children die with their parent.
        if ($child.Name -and $child.Name -match '^node(\.exe)?$') {
            Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

function Test-PortBusy {
    param([Parameter(Mandatory=$true)][int]$Port)
    $c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
         Select-Object -First 1
    return [bool]$c
}

# Which process is actually serving the port, according to the OS.
#
# This is the honest way to ask "is the service up, and is it the same process as before?"
# -- unlike a log line, it cannot match the wrong run or come back empty because of a
# formatting change.
function Get-PortOwner {
    param([Parameter(Mandatory=$true)][int]$Port)
    $c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
         Select-Object -First 1
    if (-not $c) { return 0 }
    return [int]$c.OwningProcess
}

function Wait-PortBusy {
    param([Parameter(Mandatory=$true)][int]$Port, [int]$TimeoutSec = 60)
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) {
        if (Test-PortBusy -Port $Port) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (Test-PortBusy -Port $Port)
}

function Wait-PortFree {
    param([Parameter(Mandatory=$true)][int]$Port, [int]$TimeoutSec = 20)
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) {
        if (-not (Test-PortBusy -Port $Port)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return -not (Test-PortBusy -Port $Port)
}

# ---------------------------------------------------------------- config.ini

# Writes a text file as UTF-8 with NO byte-order mark.
function Write-TextNoBom {
    param([Parameter(Mandatory=$true)][string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# Where the pre-test config is parked so an interrupted run can still be undone.
$script:ConfigGuard = Join-Path $PSScriptRoot '.config-guard.ini'

# The only ports this suite ever writes, read from the configured values above rather than
# hard-coded, so a run on overridden ports is still recognised as "ours" by the recovery
# check. A port that is NOT in this set was chosen by a human and is never overwritten.
$script:TestPorts = @($script:TestPort, $script:TestPort2, $script:ConflictPort)

# Runs before anything else touches config.ini, and undoes a run that was killed midway.
#
# The suite rewrites the user's real %LOCALAPPDATA%\DSH-App\config.ini, which is the one
# thing here that is not sandboxed. Keeping the snapshot only in memory is not enough: if
# the process is killed between the write and the restore (a network drop did exactly that
# on 09-20), the real config stays modified, and the NEXT run then snapshots the modified
# file and faithfully restores that -- so the damage becomes permanent. That is how the
# live config ended up on port=3080 with setupDone=false.
#
# So the original bytes are mirrored to disk BEFORE the real file is touched, and the
# mirror is deleted only after a verified restore. Finding one at startup means a previous
# run died, and the decision to undo is made on CONTENT, not on file timestamps.
#
# Timestamps cannot work here: the app legitimately rewrites config.ini during every test
# (it records lastRunVersion), so "the live file is newer than the snapshot" is always
# true and a timestamp rule would refuse to fix the very case it exists for.
#
# Content decides instead. If the live port is one of ours, the file is test residue and
# the snapshot is restored. If it is anything else, a human chose that port -- so the
# snapshot is reported and dropped rather than silently overwriting their edit.
function Repair-InterruptedConfig {
    if (-not (Test-Path $script:ConfigGuard)) { return }

    Write-Host "    [WARN] found a leftover config snapshot -- a previous test run was interrupted" -ForegroundColor Yellow

    $livePort = $null
    if (Test-Path $script:ConfigIni) {
        $m = Select-String -Path $script:ConfigIni -Pattern '^port=(\d+)' -ErrorAction SilentlyContinue |
             Select-Object -First 1
        if ($m) { $livePort = [int]$m.Matches[0].Groups[1].Value }
    }

    if ($null -eq $livePort -or ($script:TestPorts -notcontains $livePort)) {
        Write-Host ("    [WARN] the live port is " + $livePort + ", which this suite never sets") -ForegroundColor Yellow
        Write-Host "    [WARN] treating it as a deliberate edit; not touching config.ini" -ForegroundColor Yellow
        Remove-Item $script:ConfigGuard -Force -ErrorAction SilentlyContinue
        return
    }

    $text = Get-Content $script:ConfigGuard -Encoding UTF8 -Raw
    if ($text.Length -eq 0) {
        Remove-Item $script:ConfigIni -Force -ErrorAction SilentlyContinue
    } else {
        Write-TextNoBom -Path $script:ConfigIni -Text $text
    }
    Remove-Item $script:ConfigGuard -Force -ErrorAction SilentlyContinue
    Write-Host ("    [FIX ] config.ini had been left on port " + $livePort + " by a dead run; snapshot restored") -ForegroundColor Green
}

# Points the app at a different port (and optionally workspace) by rewriting config.ini.
#
# The original content is held IN MEMORY and mirrored to the guard file on disk, so the
# normal path never depends on the file and the crash path still has something to use.
function Set-TestConfig {
    param(
        [Parameter(Mandatory=$true)][int]$Port,
        [string]$Workspace
    )
    if ($null -eq $script:ConfigOriginal) {
        if (Test-Path $script:ConfigIni) {
            $script:ConfigOriginal = Get-Content $script:ConfigIni -Encoding UTF8 -Raw
            Write-Info ("config.ini snapshot taken (" + $script:ConfigOriginal.Length + " chars)")
        } else {
            $script:ConfigOriginal = ""
            Write-Info "config.ini does not exist; nothing to restore afterwards"
        }
        Write-TextNoBom -Path $script:ConfigGuard -Text $script:ConfigOriginal
    }

    $lines = @()
    if ($script:ConfigOriginal) {
        $lines = @($script:ConfigOriginal -split "`r?`n" |
                   Where-Object { $_ -notmatch '^port=' -and $_ -notmatch '^workspace=' -and $_.Trim().Length -gt 0 })
    } else {
        $lines = @('# test config')
    }
    if (-not ($lines | Where-Object { $_ -match '^setupDone=' })) { $lines += 'setupDone=true' }
    $lines += ('port=' + $Port)
    if ($Workspace) { $lines += ('workspace=' + $Workspace) }
    [System.IO.File]::WriteAllText($script:ConfigIni, (($lines -join "`r`n") + "`r`n"),
        (New-Object System.Text.UTF8Encoding($false)))
}

# Puts the snapshot back and VERIFIES the round trip. A silent failure here is exactly how
# the real config was left on port 3080 once, so this refuses to be quiet about it.
function Restore-Config {
    if ($null -eq $script:ConfigOriginal) { return }
    if ($script:ConfigOriginal.Length -eq 0) {
        Remove-Item $script:ConfigIni -Force -ErrorAction SilentlyContinue
        Remove-Item $script:ConfigGuard -Force -ErrorAction SilentlyContinue
        $script:ConfigOriginal = $null
        return
    }
    Write-TextNoBom -Path $script:ConfigIni -Text $script:ConfigOriginal
    $now = Get-Content $script:ConfigIni -Encoding UTF8 -Raw
    if ($now -ne $script:ConfigOriginal) {
        Write-Host "    [WARN] config.ini restore did not round-trip -- check it by hand" -ForegroundColor Yellow
        Write-Host "    [WARN] the snapshot is kept at $script:ConfigGuard" -ForegroundColor Yellow
    } else {
        Remove-Item $script:ConfigGuard -Force -ErrorAction SilentlyContinue
        Write-Info "config.ini restored"
    }
    $script:ConfigOriginal = $null
}

# ---------------------------------------------------------------- sandboxes

function New-Sandbox {
    param([Parameter(Mandatory=$true)][string]$Name)
    $dir = Join-Path $env:TEMP ('dsh-test-' + $Name)
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Remove-Sandbox {
    param([Parameter(Mandatory=$true)][string]$Dir)
    if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- launching the app

function Start-App {
    param(
        [string]$Exe,
        [int]$DebugPort = 0,
        [hashtable]$ExtraEnv = $null
    )
    if (-not $Exe) { $Exe = $script:DistExe }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path $Exe -Parent
    $psi.UseShellExecute = $false
    if ($DebugPort -gt 0) { $psi.EnvironmentVariables['DSH_APP_DEBUG_PORT'] = "$DebugPort" }
    # Used by the missing-Node test to hide Node without touching the real system.
    if ($ExtraEnv) {
        foreach ($k in $ExtraEnv.Keys) { $psi.EnvironmentVariables[$k] = [string]$ExtraEnv[$k] }
    }
    return [System.Diagnostics.Process]::Start($psi)
}

# ---------------------------------------------------------------- CDP

# Reads the app's own page through the Chromium DevTools endpoint.  Returns the raw
# JSON string the page produced, or $null.
function Invoke-CdpEval {
    param(
        [Parameter(Mandatory=$true)][int]$Port,
        [Parameter(Mandatory=$true)][string]$Expression,
        [int]$TimeoutSec = 20,
        # Which DevTools target to evaluate in. The default is the app's own panel page; a
        # test that cares about a DIFFERENT page -- the harness the window navigated to, say
        # -- passes a pattern such as '*127.0.0.1:3099*'.
        [string]$TargetLike = '*app.html*'
    )
    $list = $null
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) {
        try { $list = Invoke-RestMethod ("http://127.0.0.1:" + $Port + "/json/list") -TimeoutSec 3 } catch { }
        if ($list) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $list) { return $null }

    $target = $list | Where-Object { $_.url -like $TargetLike } | Select-Object -First 1
    if (-not $target) { return $null }

    $js = @'
import fs from 'node:fs';
const t = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const expr = process.argv[3];
const ws = new WebSocket(t.ws);
const opened = await Promise.race([
  new Promise(r => ws.addEventListener('open', () => r(true))),
  new Promise(r => setTimeout(() => r(false), 5000)),
]);
if (!opened) { console.log('WS_TIMEOUT'); process.exit(0); }
let id = 1;
const call = (m, p) => new Promise((res, rej) => { const my = id++;
  const on = ev => { const x = JSON.parse(ev.data); if (x.id !== my) return;
    ws.removeEventListener('message', on); x.error ? rej(new Error(JSON.stringify(x.error))) : res(x.result); };
  ws.addEventListener('message', on); ws.send(JSON.stringify({ id: my, method: m, params: p })); });
try {
  const r = await call('Runtime.evaluate', { expression: expr, returnByValue: true });
  console.log('RESULT:' + (r.result ? r.result.value : ''));
} catch (e) {
  console.log('CDP_ERROR:' + e.message);
}
ws.close();
process.exit(0);
'@
    # The JSON goes through a FILE, not the command line: PowerShell strips the double
    # quotes out of an inline JSON argument, which silently turned {"ws":...} into
    # {ws:...} and made JSON.parse throw -- and the empty output looked like "no target".
    #
    # And it must be written with File.WriteAllText + UTF8Encoding(false): PowerShell's
    # "Set-Content -Encoding UTF8" emits a BOM on 5.1, which makes JSON.parse fail with
    # "Unexpected token". That exact trap is already recorded in the progress log -- it
    # was hit once before when a test rewrote the profile manifest.
    $jsFile = Join-Path $env:TEMP ('dsh-cdp-' + [guid]::NewGuid().ToString('N') + '.mjs')
    $payloadFile = Join-Path $env:TEMP ('dsh-cdp-' + [guid]::NewGuid().ToString('N') + '.json')
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($jsFile, $js, $utf8NoBom)
    [System.IO.File]::WriteAllText($payloadFile,
        (@{ ws = $target.webSocketDebuggerUrl } | ConvertTo-Json -Compress), $utf8NoBom)
    try {
        $out = & node $jsFile $payloadFile $Expression 2>$null
        $line = $out | Where-Object { $_ -like 'RESULT:*' } | Select-Object -Last 1
        if ($line) { return $line.Substring(7) }
        $errLine = $out | Where-Object { $_ -like 'CDP_ERROR:*' } | Select-Object -Last 1
        if ($errLine) { Write-Info ("cdp error: " + $errLine.Substring(10)) }
        return $null
    } finally {
        Remove-Item $jsFile, $payloadFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- misc

function Get-NodeExe {
    $n = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($n) { return $n.Source }
    return "node"
}

# Path of the harness launcher inside the newest npx cache entry.
function Get-DshBinJs {
    $root = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (-not (Test-Path $root)) { return "" }
    $best = ""
    $bestTime = [datetime]::MinValue
    foreach ($d in Get-ChildItem $root -Directory -ErrorAction SilentlyContinue) {
        $cand = Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js'
        if (-not (Test-Path $cand)) { continue }
        $t = (Get-Item $cand).LastWriteTimeUtc
        if ($t -gt $bestTime) { $bestTime = $t; $best = $cand }
    }
    return $best
}

# The harness version as installed, read from the manifest next to a resolved bin.js.
# Anchored on "\node_modules\" rather than counting parent hops: counting hops got the
# path wrong the first time this was written.
function Get-HarnessVersionOnDisk {
    param([string]$BinJs)
    if (-not $BinJs) { return "" }
    $i = $BinJs.IndexOf('\node_modules\', [System.StringComparison]::OrdinalIgnoreCase)
    if ($i -lt 1) { return "" }
    $manifest = Join-Path $BinJs.Substring(0, $i) 'node_modules\@deepseek-ai\dsh\package.json'
    if (-not (Test-Path $manifest)) { return "" }
    $m = [regex]::Match((Get-Content $manifest -Encoding UTF8 -Raw), '"version"\s*:\s*"([^"]+)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

# Compiles a throwaway C# driver against the real product sources, so a test can drive
# DshServer / the form directly without touching the app's own files.  Returns the path
# of the produced exe, placed next to the product so its DLL references resolve.
function Compile-TestDriver {
    param(
        [Parameter(Mandatory=$true)][string]$SourceFile,
        [Parameter(Mandatory=$true)][string]$OutName,
        [string[]]$ExtraSources = @()
    )
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $cache = Join-Path $script:RepoRoot '.cache\wv2\lib\net45'
    $out = Join-Path $env:TEMP ($OutName + '.exe')
    $cscArgs = @(
        '/nologo', '/target:exe', ('/out:' + $out),
        ('/reference:' + (Join-Path $cache 'Microsoft.Web.WebView2.Core.dll')),
        ('/reference:' + (Join-Path $cache 'Microsoft.Web.WebView2.WinForms.dll')),
        $SourceFile
    )
    foreach ($s in $ExtraSources) { $cscArgs += $s }
    $cscArgs += (Join-Path $script:RepoRoot 'src\AppLogger.cs')
    $cscArgs += (Join-Path $script:RepoRoot 'src\EnvProbe.cs')
    $cscArgs += (Join-Path $script:RepoRoot 'src\DshServer.cs')
    $cscArgs += (Join-Path $script:RepoRoot 'src\ProcessUtil.cs')

    & $csc @cscArgs 2>&1 | ForEach-Object { Write-Info $_ }
    if (-not (Test-Path $out)) { return $null }

    # The driver needs the WebView2 DLLs beside it to resolve EnvProbe's references.
    $staged = Join-Path (Split-Path $script:DistExe -Parent) ($OutName + '.exe')
    Copy-Item $out $staged -Force
    return $staged
}

# ---------------------------------------------------------------- startup

# Runs here, at the very end of the dot-source, so every entry point gets it for free
# (run-all.ps1 and each t-*.ps1 individually) and nothing can touch config.ini first.
Repair-InterruptedConfig
