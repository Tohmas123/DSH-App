# Second launch must notice the first one and exit without touching the service.
#
# Without this, double-clicking the icon a second time starts another launcher that then
# fights the running one over the port, leaving the user on a "port in use" screen while
# the program they wanted is already running.
#
#   powershell -ExecutionPolicy Bypass -File test\t-single-instance.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:TestPort
$DEBUG_PORT = $script:DebugPort

Write-Head "single instance"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

Stop-AppAll -Ports @($PORT)
Set-TestConfig -Port $PORT

$first = $null
$second = $null
try {
    Write-Head "launch the first instance"
    $offset = New-LogOffset
    $first = Start-App -DebugPort $DEBUG_PORT
    Write-Info ("pid " + $first.Id)
    $ready = Wait-LogLine -Offset $offset -Pattern 'harness ready' -TimeoutSec 90
    Assert-That $ready "first instance reached harness ready"
    Assert-That (Test-PortBusy -Port $PORT) ("service is listening on " + $PORT)

    Write-Head "launch a second instance"
    $offset2 = New-LogOffset
    $second = Start-App
    Start-Sleep -Seconds 5

    $text = Get-NewLogText -Offset $offset2
    Assert-That ($text -match 'another instance is already running') "second instance logged that another one is running"
    # It must not have gone on to start a service of its own.
    Assert-That ($text -notmatch 'starting harness') "second instance did NOT start a harness"
    Assert-That (Test-PortBusy -Port $PORT) "the original service is still up"

    $secondAlive = [bool](Get-Process -Id $second.Id -ErrorAction SilentlyContinue)
    Write-Info ("second instance process still alive = " + $secondAlive + " (expected: it is showing a message box)")
} finally {
    Write-Head "cleanup"
    if ($second) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }
    if ($first) { Stop-Process -Id $first.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    Stop-AppAll -Ports @($PORT)
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    Restore-Config
}

exit (Complete-Test -Name 'single-instance')
