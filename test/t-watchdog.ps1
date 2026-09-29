# The service watchdog.
#
# The launcher's promise is that the window keeps working. If the harness process dies on
# its own -- a crash, a task manager kill, an antivirus hiccup -- the app is supposed to
# notice and bring it back, not leave the user staring at a dead page with no explanation.
#
# EVERY CHECK HERE IS MADE AGAINST THE PORT, NOT THE LOG.
#
# The first version of this test read the new pid out of the app's log, and that was a
# mistake twice over. A loose pattern ('server pid \d+', where \d+ can cross a newline)
# reported a "replacement" that never existed, and the log line came back empty on other
# runs, so the test killed pid 0 and then graded itself on noise. Which process is holding
# the port is the only fact that matters, and it cannot be misread: it is whatever the OS
# says, before and after.
#
# The one log check kept is deliberately weak on purpose (did it say it was restarting),
# because that text is useful support evidence but is not what the user experiences.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-watchdog.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT       = $script:TestPort
$DEBUG_PORT = $script:DebugPort

Write-Head "service watchdog"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

$log = Join-Path $script:DataDir 'logs\app.log'

Stop-AppAll -Ports @($PORT, 31700)
Set-TestConfig -Port $PORT

$proc = $null
try {
    $offset = New-LogOffset
    $proc = Start-App -DebugPort $DEBUG_PORT
    Write-Info ("launcher pid " + $proc.Id)

    Write-Head "let the service come up"
    $served = Wait-PortBusy -Port $PORT -TimeoutSec 120
    Assert-That $served "the service is answering on the port"

    # Let the app settle before breaking anything. Killing the server while the launcher
    # is still finishing its own start would test a different situation -- and the app has
    # its own recovery for that, which would make this test pass for the wrong reason.
    Start-Sleep -Seconds 5
    $oldPid = Get-PortOwner -Port $PORT
    Assert-That ($oldPid -gt 0) ("captured the pid serving the port (" + $oldPid + ")")

    Write-Head "kill the service process"
    if ($oldPid -le 0) {
        Assert-That $false "no process to test with"
    } else {
        Write-Info ("killing pid " + $oldPid)
        Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Assert-That (-not (Test-PortBusy -Port $PORT)) "the port stopped being served"

        Write-Head "wait for the launcher to bring it back"
        # The watchdog polls on a 10s interval and needs three misses before it acts, so
        # this legitimately takes the better part of a minute; the timeout is generous on
        # purpose because a slow recovery is still a recovery.
        $back = Wait-PortBusy -Port $PORT -TimeoutSec 180
        Assert-That $back "the port is being served again"

        if ($back) {
            $newPid = Get-PortOwner -Port $PORT
            Write-Info ("port is now served by pid " + $newPid)
            Assert-That ($newPid -gt 0) "the port has an owner"
            # The decisive check: a NEW process. The old one was killed, so if the same
            # pid came back the port was never really released and nothing was restarted.
            Assert-That ($newPid -ne $oldPid) ("a different process is serving it (" + $oldPid + " -> " + $newPid + ")")
            Assert-That ($null -eq (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) "the killed process is still gone"
        }

        # Support evidence, not the assertion: the log should say what happened.
        $noticed = Get-NewLogText -Offset $offset | Select-String -Pattern 'watchdog: harness is gone' -Quiet
        Assert-That ([bool]$noticed) "the log records that the watchdog restarted the service"

        # The launcher itself must have survived all of this.
        Assert-That ($proc -and -not $proc.HasExited) "the launcher window is still running"
    }
} finally {
    Write-Head "cleanup"
    if ($proc -and -not $proc.HasExited) { Stop-AppTree -ProcessId $proc.Id }
    Start-Sleep -Seconds 2
    Stop-AppAll -Ports @($PORT)
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    Restore-Config
}

exit (Complete-Test -Name 'watchdog')
