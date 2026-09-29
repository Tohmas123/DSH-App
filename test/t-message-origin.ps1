# The page -> host bridge must obey the app's own control panel, and nothing else.
#
# chrome.webview.postMessage belongs to the WINDOW, not to one page: every document the
# window loads can call it, including the harness page the app navigates into. The host acts
# on those messages -- save the API key, change the port, restart the service, quit, open a
# URL in the system browser -- so a page that is not the app's own interface must have no say
# at all. Without the check, a page the user reached from a link in a chat answer could post
# {"type":"saveKey"} and route every later request through somebody else's API key.
#
# This test does both halves:
#   1. the control panel's own message is acted on (otherwise a guard that blocked
#      everything would look identical to a working one);
#   2. a privileged message from the harness page is ignored, and the app says so in the log.
#
# It uses {"type":"quit"} as the foreign message on purpose: it is the loudest possible
# thing to obey. If the guard ever regresses, this test fails by the app exiting.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-message-origin.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:TestPort
$DEBUG_PORT = $script:DebugPort

Write-Head "page -> host: only the app's own page is obeyed"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

Stop-AppAll -Ports @($PORT)
Set-TestConfig -Port $PORT

$proc = $null
try {
    $offset = New-LogOffset
    $proc = Start-App -DebugPort $DEBUG_PORT
    Write-Info ("pid " + $proc.Id)

    Assert-That (Wait-LogLine -Offset $offset -Pattern 'harness ready' -TimeoutSec 180) "harness became ready"

    Write-Head "positive control: the panel's own message is acted on"
    Start-Sleep -Seconds 2
    $posted = Invoke-CdpEval -Port $DEBUG_PORT -Expression "(function(){ if(!window.chrome||!window.chrome.webview) return 'no bridge'; window.chrome.webview.postMessage(JSON.stringify({type:'recheck'})); return 'posted'; })()"
    Write-Info ("panel post -> " + $posted)
    Assert-That ($posted -eq 'posted') "the panel page can post to the host"
    Assert-That (Wait-LogLine -Offset $offset -Pattern 'ui -> host: recheck' -TimeoutSec 20) `
        "the host acted on the panel's message"

    Write-Head "navigate the window into the harness, which is a DIFFERENT origin"
    $click = Invoke-CdpEval -Port $DEBUG_PORT -Expression "(function(){var b=document.getElementById('btn-open-embedded'); if(!b) return 'missing'; b.click(); return 'clicked';})()"
    Assert-That ($click -eq 'clicked') "the embedded-window button was clicked"
    Assert-That (Wait-LogLine -Offset $offset -Pattern 'harness UI loaded successfully' -TimeoutSec 60) `
        "the harness page is loaded in the window"

    Write-Head "the harness page posts a message the panel is allowed to send"
    # Same runtime as the panel, different origin and a document this program did not write.
    $foreign = Invoke-CdpEval -Port $DEBUG_PORT -TargetLike ('*127.0.0.1:' + $PORT + '*') `
                    -Expression "(function(){ if(!window.chrome||!window.chrome.webview) return 'no bridge'; window.chrome.webview.postMessage(JSON.stringify({type:'quit'})); return 'posted'; })()"
    Write-Info ("harness post -> " + $foreign)

    if ($foreign -eq 'posted') {
        # The bridge really is reachable from a foreign page, which is why the guard exists.
        Assert-That (Wait-LogLine -Offset $offset -Pattern 'ignored a page message' -TimeoutSec 20) `
            "the host refused the foreign page's message"

        $logged = Get-NewLogText -Offset $offset
        Assert-That ($logged -match 'ignored a page message.*type=quit') "the refusal names the message it dropped"
        Assert-That ($logged -notmatch 'ui -> host: quit') "the foreign message was never treated as a panel message"

        Start-Sleep -Seconds 3
        $alive = [bool](Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)
        Assert-That $alive "the app did NOT quit on the foreign page's request"
        Assert-That (Test-PortBusy -Port $PORT) ("the service is still up on " + $PORT)
    } else {
        # WebView2 in this version does not hand the bridge to the harness page at all. The
        # guard is then belt-and-braces for that particular page -- but it is still the only
        # thing standing between the host and, say, a frame inside it, so it stays. Say so
        # rather than pretending the message was sent.
        Write-Info "this WebView2 build does not expose chrome.webview to the harness page"
        Assert-That ($foreign -eq 'no bridge') "the result was a refusal by the page, not a timeout"
        Assert-That (Test-PortBusy -Port $PORT) "the service is still up"
    }
} finally {
    Write-Head "cleanup"
    if ($proc) { Stop-AppTree -ProcessId $proc.Id }
    Start-Sleep -Seconds 2
    Stop-AppAll -Ports @($PORT)
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    Restore-Config
}

exit (Complete-Test -Name 'message-origin')
