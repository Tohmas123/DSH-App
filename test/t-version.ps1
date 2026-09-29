# The versions must be visible, because "which build is that machine running?" is the
# first question when something goes wrong -- and before this existed the harness version
# could not be answered without reading files by hand.
#
#   powershell -ExecutionPolicy Bypass -File test\t-version.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:TestPort
$DEBUG_PORT = $script:DebugPort

Write-Head "version visibility"

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

    $ready = Wait-LogLine -Offset $offset -Pattern 'harness ready' -TimeoutSec 120
    Assert-That $ready "harness became ready"

    $logText = Get-NewLogText -Offset $offset

    Write-Head "launcher version"
    $fileVersion = (Get-Item $script:DistExe).VersionInfo.FileVersion
    Write-Info ("exe file version = " + $fileVersion)
    Assert-That ($fileVersion -match '^\d+\.\d+\.\d+\.\d+$') "the exe reports a real file version (not 0.0.0.0)"
    Assert-That ($fileVersion -notmatch '^0\.0\.0\.0$') "file version is not the default 0.0.0.0"
    Assert-That ($logText -match 'version: \d+\.\d+\.\d+') "the startup log records the launcher version"

    Write-Head "harness version"
    $line = Get-LogLine -Offset $offset -Pattern 'dsh resolved from npx cache:.*\(harness [^)]+\)'
    Write-Info ("log line: " + $line)
    Assert-That ($null -ne $line) "the log names the harness build it resolved"

    $expected = Get-HarnessVersionOnDisk -BinJs (Get-DshBinJs)
    Write-Info ("harness on disk = " + $expected)
    Assert-That ($expected.Length -gt 0) "found the harness manifest to compare against"
    if ($line -and $expected.Length -gt 0) {
        Assert-That ($line -match [regex]::Escape($expected)) ("the logged harness version matches the installed one (" + $expected + ")")
    }

    Write-Head "shown on the panel"
    $json = Invoke-CdpEval -Port $DEBUG_PORT -Expression "JSON.stringify({v:(document.getElementById('panel-ver')||{}).textContent,h:(document.getElementById('panel-harness')||{}).textContent})"
    Write-Info ("panel payload: " + $json)
    Assert-That ($null -ne $json) "read the control panel through DevTools"
    if ($json) {
        $o = $json | ConvertFrom-Json
        Assert-That ($o.v -match '^\d+\.\d+\.\d+$') ("panel shows the launcher version (" + $o.v + ")")
        Assert-That ($o.h -match '\d+\.\d+\.\d+') ("panel shows the harness version (" + $o.h + ")")
    }
} finally {
    Write-Head "cleanup"
    if ($proc) { Stop-AppTree -ProcessId $proc.Id }
    Start-Sleep -Seconds 2
    Stop-AppAll -Ports @($PORT)
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    Restore-Config
}

exit (Complete-Test -Name 'version')
