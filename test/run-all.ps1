# Runs every test and prints one summary table.
#
#   powershell -ExecutionPolicy Bypass -File test\run-all.ps1
#
# Exits non-zero when any test fails, so it can be wired into automation later.
#
# Each test is self-contained: it creates its own sandbox, cleans up after itself, and
# restores whatever it changed in config.ini. They run one after another on purpose --
# several of them need the same ports and the same app data directory.

$ErrorActionPreference = 'Continue'

$tests = @(
    @{ Name = 'happy-path';      Script = 't-happy-path.ps1';      What = 'cold start -> control panel -> open the chat' },
    @{ Name = 'slow-banner';     Script = 't-slow-banner.ps1';     What = 'a slow harness banner must be waited for, not failed on' },
    @{ Name = 'download-progress'; Script = 't-download-progress.ps1'; What = 'first-run progress is measured, not guessed' },
    @{ Name = 'port-conflict';   Script = 't-port-conflict.ps1';   What = 'foreign port holder: ask the user, never touch their process' },
    @{ Name = 'port-states';     Script = 't-port-states.ps1';     What = 'reserved vs listening vs free: the message must match the truth' },
    @{ Name = 'version';         Script = 't-version.ps1';         What = 'launcher and harness versions are visible' },
    @{ Name = 'no-node';         Script = 't-no-node.ps1';         What = 'Node detection: PATH search and the conventional-dir fallback' },
    @{ Name = 'cli';             Script = 't-cli.ps1';             What = 'command line: --port really moves the service, bad values refused' },
    @{ Name = 'single-instance'; Script = 't-single-instance.ps1'; What = 'a second launch must not fight the first' },
    @{ Name = 'watchdog';        Script = 't-watchdog.ps1';        What = 'a killed service comes back on its own' },
    @{ Name = 'upgrade';         Script = 't-upgrade.ps1';         What = 'upgrade over a running install, data untouched' },
    @{ Name = 'uninstall-safety'; Script = 't-uninstall-safety.ps1'; What = 'the uninstaller removes its own files and nothing else' },
    @{ Name = 'message-origin';  Script = 't-message-origin.ps1';  What = 'the page -> host bridge only obeys this program control panel' }
)

# Tests that are NOT part of the default run. They are still addressable by name:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\run-all.ps1 harness-bootstrap
#
# harness-bootstrap is opt-in because it needs the network and takes ~2 minutes -- about
# half as long again as the whole default suite, which runs offline.
$optInTests = @(
    @{ Name = 'harness-bootstrap'; Script = 't-harness-bootstrap.ps1'; What = 'a fresh download produces a complete, runnable harness' }
)

$allTests = $tests + $optInTests

Write-Host ""
Write-Host "DSH-App test suite" -ForegroundColor Cyan
Write-Host ("repo: " + (Split-Path -Parent $PSScriptRoot)) -ForegroundColor Cyan

# Resolved here as well as in _lib.ps1 (this runner does not dot-source the library, it
# only launches the test scripts), so the banner shows the ports that will really be used.
$basePort     = if ($env:DSH_TEST_PORT)     { $env:DSH_TEST_PORT }     else { '3099' }
$secondPort   = if ($env:DSH_TEST_PORT2)    { $env:DSH_TEST_PORT2 }    else { '3105' }
$conflictPort = if ($env:DSH_CONFLICT_PORT) { $env:DSH_CONFLICT_PORT } else { '3098' }
Write-Host ("ports: base=" + $basePort + " second=" + $secondPort + " conflict=" + $conflictPort) -ForegroundColor Cyan

# Also write everything to a file.
#
# Not a nicety: this suite ends DSH-App processes, and DSH-App is often what launches the
# session that launched the suite -- so a run can destroy its own output stream. With the
# log on disk the result survives that and can be read back afterwards.
$logPath = Join-Path $PSScriptRoot 'last-run.log'
try { Start-Transcript -Path $logPath -Force | Out-Null } catch { }
Write-Host ("transcript: " + $logPath) -ForegroundColor DarkGray

$only = $args
$optInNames = @($optInTests | ForEach-Object { $_.Name })
$results = New-Object System.Collections.ArrayList
$started = Get-Date

foreach ($t in $allTests) {
    # Named on the command line -> run exactly that, including opt-in ones.
    if ($only.Count -gt 0) {
        if ($only -notcontains $t.Name) { continue }
    }
    # No arguments -> the default suite, which excludes the opt-in tests.
    #
    # Without this, "run everything" silently included the network test that takes longer
    # than all twelve default tests combined. It ran, and nobody asked for it.
    elseif ($optInNames -contains $t.Name) { continue }

    Write-Host ""
    Write-Host ("#" * 70) -ForegroundColor DarkGray
    Write-Host ("# " + $t.Name + " -- " + $t.What) -ForegroundColor DarkGray
    Write-Host ("#" * 70) -ForegroundColor DarkGray

    $script = Join-Path $PSScriptRoot $t.Script
    if (-not (Test-Path $script)) {
        Write-Host ("    MISSING: " + $t.Script) -ForegroundColor Red
        [void]$results.Add([pscustomobject]@{ Name = $t.Name; Ok = $false; Seconds = 0 })
        continue
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # Each test's own output goes to its own file as well as to the console.
    #
    # Not decoration: run-all's transcript records only what run-all itself writes, so a test
    # that fails inside its own process leaves NOTHING behind -- the summary says "FAILED:
    # upgrade" and the reason is gone. That happened on 2026-09-22 and the failure could not be
    # diagnosed afterwards. With one file per test, "which assertion?" is one grep away, and
    # the file survives even when the run is interrupted.
    $testLog = Join-Path $PSScriptRoot ('last-' + $t.Name + '.log')
    $testErr = Join-Path $PSScriptRoot ('last-' + $t.Name + '.err.log')
    $p = Start-Process -FilePath 'powershell' `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script) `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $testLog -RedirectStandardError $testErr
    $code = $p.ExitCode
    $sw.Stop()

    $ok = ($code -eq 0)
    if (-not $ok) {
        Write-Host ("    " + $t.Name + " FAILED (exit " + $code + ") -- its output is in " + $testLog) -ForegroundColor Red
        Get-Content $testLog -Encoding UTF8 -ErrorAction SilentlyContinue |
            Where-Object { $_ -match '\[FAIL\]|FAILED|Assert' } |
            ForEach-Object { Write-Host ("      " + $_) -ForegroundColor Red }
    }

    [void]$results.Add([pscustomobject]@{
        Name = $t.Name
        Ok = $ok
        Seconds = [int]$sw.Elapsed.TotalSeconds
    })
}

$total = [int]((Get-Date) - $started).TotalSeconds
$failed = @($results | Where-Object { -not $_.Ok })

$code = 0
try {
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host "SUMMARY" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
    foreach ($r in $results) {
        $tag = if ($r.Ok) { 'PASS' } else { 'FAIL' }
        $color = if ($r.Ok) { 'Green' } else { 'Red' }
        Write-Host ("    [{0}] {1,-16} {2,4}s" -f $tag, $r.Name, $r.Seconds) -ForegroundColor $color
    }
    Write-Host ""
    if ($failed.Count -eq 0) {
        Write-Host ("    ALL TESTS PASSED (" + $results.Count + " tests, " + $total + "s)") -ForegroundColor Green
    } else {
        Write-Host ("    FAILED: " + ($failed.Name -join ', ') + "  (" + $total + "s)") -ForegroundColor Red
        $code = 1
    }
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}

exit $code
