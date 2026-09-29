# The normal cold start: environment check -> service up -> control panel -> open the
# chat in the embedded window.
#
# This is the path a user walks every day, so it is the one worth protecting. It also
# pins the deliberate behaviour that the window STOPS on the panel instead of jumping
# straight into the chat.
#
#   powershell -ExecutionPolicy Bypass -File test\t-happy-path.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:TestPort
$DEBUG_PORT = $script:DebugPort

Write-Head "happy path"

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

    Write-Head "cold start"
    Assert-That (Wait-LogLine -Offset $offset -Pattern 'harness ready' -TimeoutSec 180) "harness became ready"
    Assert-That (Test-PortBusy -Port $PORT) ("service is listening on " + $PORT)

    $logText = Get-NewLogText -Offset $offset
    Assert-That ($logText -match 'webview ready') "the embedded browser started"
    Assert-That ($logText -match 'ui server listening') "the local UI server started"
    Assert-That ($logText -match 'node found') "node was found"
    Assert-That ($logText -notmatch 'FAILED:') "no failure was logged"

    Write-Head "the window stops on the control panel"
    # Give the page a moment to receive the panel message.
    Start-Sleep -Seconds 3
    $json = Invoke-CdpEval -Port $DEBUG_PORT -Expression "JSON.stringify({s:(Array.from(document.querySelectorAll('.step')).find(function(x){return x.classList.contains('on')})||{}).id, actions:(function(){var e=document.getElementById('panel-actions');return e?e.style.display!=='none':null;})(), url:location.href})"
    Write-Info ("panel payload: " + $json)
    Assert-That ($null -ne $json) "read the panel through DevTools"
    if ($json) {
        $o = $json | ConvertFrom-Json
        Assert-That ($o.s -eq 's-panel') "the control panel is showing (not the chat yet)"
        Assert-That ($o.actions -eq $true) "both open buttons are offered"
        Assert-That ($o.url -like '*app.html*') "the window is still on the app's own page"
    }

    Write-Head "open the chat in the embedded window"
    $click = Invoke-CdpEval -Port $DEBUG_PORT -Expression "(function(){var b=document.getElementById('btn-open-embedded'); if(!b) return 'missing'; b.click(); return 'clicked';})()"
    Write-Info ("click -> " + $click)
    Assert-That ($click -eq 'clicked') "the embedded-window button exists and was clicked"

    Assert-That (Wait-LogLine -Offset $offset -Pattern 'user chose the embedded window' -TimeoutSec 30) "the host received the choice"
    Assert-That (Wait-LogLine -Offset $offset -Pattern 'harness UI loaded successfully' -TimeoutSec 60) "the chat UI loaded in the window"

    # Once the window navigates to the chat, the DevTools target is no longer the app's
    # page -- so "still on app.html" must be false, and something else must be there.
    # (An empty result would also satisfy a lazy "not like app.html" check, which is why
    # the panel page is checked for explicitly.)
    Start-Sleep -Seconds 2
    $pageTargets = $null
    try { $pageTargets = Invoke-RestMethod ("http://127.0.0.1:" + $DEBUG_PORT + "/json/list") -TimeoutSec 5 } catch { }
    $urls = @()
    if ($pageTargets) { $urls = @($pageTargets | Where-Object { $_.type -eq 'page' } | ForEach-Object { $_.url }) }
    Write-Info ("open pages: " + ($urls -join ' ; '))
    Assert-That ($urls.Count -gt 0) "there is still a page open after the navigation"
    Assert-That (-not (@($urls | Where-Object { $_ -like '*app.html*' }).Count -gt 0)) "the panel page is gone (the window really navigated)"
    Assert-That (@($urls | Where-Object { $_ -like '*127.0.0.1:' + $PORT + '*' }).Count -gt 0) ("the window is now on the harness at port " + $PORT)
} finally {
    Write-Head "cleanup"
    if ($proc) { Stop-AppTree -ProcessId $proc.Id }
    Start-Sleep -Seconds 2
    Stop-AppAll -Ports @($PORT)
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    Restore-Config
}

exit (Complete-Test -Name 'happy-path')
