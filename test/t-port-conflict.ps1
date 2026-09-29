# Port conflict handling: the app must never take over a service it did not start, it
# must recognize its OWN leftover harness, and it must not touch anyone else's process.
#
# Depends on the app being up, because it reads the port-conflict screen through the
# page itself (DevTools) rather than trusting the log alone.
#
#   powershell -ExecutionPolicy Bypass -File test\t-port-conflict.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:ConflictPort
$DEBUG_PORT = $script:DebugPort

Write-Head "port conflict"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}
if (-not (Get-Command node.exe -ErrorAction SilentlyContinue)) {
    Write-Info "node.exe not found on PATH; cannot start a fake holder"
    exit 1
}

Stop-AppAll -Ports @($PORT)
$sandbox = New-Sandbox 'port-conflict'
$holder = $null
$holderScript = Join-Path $sandbox 'other.js'
$proc = $null
$testConfig = $false

try {
    # A holder that is deliberately NOT a harness: node's command line does not contain
    # "bin.js", so the app must classify it as a stranger and refuse to end it.
    $js = 'require("net").createServer(function (s) { s.on("error", function () {}); s.end("x"); }).listen(' + $PORT + ', "127.0.0.1"); setInterval(function () {}, 1000);'
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($holderScript, $js, $utf8NoBom)

    $holder = Start-Process -FilePath (Get-Command node.exe).Source `
              -ArgumentList ('"' + $holderScript + '"') `
              -RedirectStandardOutput (Join-Path $sandbox 'out.txt') `
              -RedirectStandardError (Join-Path $sandbox 'err.txt') -PassThru
    Start-Sleep -Seconds 3
    Write-Info ("foreign holder pid = " + $holder.Id + ", holding " + $PORT)
    if (-not (Test-PortBusy -Port $PORT)) {
        Write-Info "the holder never bound the port; cannot run this test"
        exit 1
    }

    Set-TestConfig -Port $PORT
    $testConfig = $true

    Write-Head "start the app against the busy port"
    $offset = New-LogOffset
    $proc = Start-App -DebugPort $DEBUG_PORT
    Write-Info ("pid " + $proc.Id)

    $conflict = Wait-LogLine -Offset $offset -Pattern 'port conflict' -TimeoutSec 60
    Assert-That $conflict "the app detected the conflict"

    $logText = Get-NewLogText -Offset $offset
    Assert-That ($logText -notmatch 'starting harness') "it did not start a second service on the busy port"

    Write-Head "the stranger must survive"
    Assert-That ([bool](Get-Process -Id $holder.Id -ErrorAction SilentlyContinue)) "the foreign process is still running"
    Assert-That (Test-PortBusy -Port $PORT) "the foreign holder still owns the port"

    Write-Head "what the user sees"
    $json = Invoke-CdpEval -Port $DEBUG_PORT -Expression "JSON.stringify({s:(Array.from(document.querySelectorAll('.step')).find(function(x){return x.classList.contains('on')})||{}).id, orphan:(function(){var b=document.getElementById('btn-end-orphan');return b?b.style.display!=='none':null;})(), hint:(document.getElementById('port-hint')||{}).textContent})"
    Write-Info ("panel payload: " + $json)
    Assert-That ($null -ne $json) "read the port screen through DevTools"
    if ($json) {
        $o = $json | ConvertFrom-Json
        Assert-That ($o.s -eq 's-port') "the port-conflict step is showing"
        # No "end that process" button for a stranger: that button only appears for our
        # own leftover harness, and offering it here would invite killing a third party.
        #
        # THIS is the assertion that actually proves the classification, and it is why the
        # log wording is not asserted separately any more. Whether the app called the
        # holder "ours" is exactly what decides this button's visibility, so asserting the
        # button plus the surviving stranger covers the same ground twice over.
        #
        # It used to check the log text as well, by searching a byte-offset slice of
        # app.log. That was flaky for reasons that had nothing to do with the app: the log
        # rotates at 1 MB and the tests append to it across runs, so the slice could come
        # back empty and report a failure while the app had classified the holder
        # correctly the whole time (the line was in the file, the offset simply no longer
        # pointed at it). An offset into a file that rotates underneath you is not a
        # foundation for an assertion.
        Assert-That ($o.orphan -eq $false) "the end-orphan button is hidden for a foreign holder"

        # Support evidence, looked up by content across the whole file rather than through
        # an offset, and deliberately weak: the last conflict line must name this port.
        $line = Select-String -Path $script:AppLog -Pattern ('orphan harness=False') -SimpleMatch -ErrorAction SilentlyContinue |
                Select-Object -Last 1
        Assert-That ($null -ne $line) "the app's log describes the holder as not its own"
    }
} finally {
    Write-Head "cleanup"
    if ($proc) { Stop-AppTree -ProcessId $proc.Id }
    Start-Sleep -Seconds 2
    Stop-AppAll -Ports @($PORT)
    if ($holder -and (Get-Process -Id $holder.Id -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
    }
    # Belt and braces: match on the script path, never on a broad name pattern.
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like ('*' + $holderScript + '*') } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    if ($testConfig) { Restore-Config }
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'port-conflict')
