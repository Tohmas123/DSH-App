# The command line.
#
# Only what can actually be proven from outside is asserted here:
#
#   * --port N puts the service on port N. This is checked against the PORT, not against
#     config.ini alone -- writing the number down and then ignoring it would still look
#     like a pass if the file were all we looked at, and the whole point is that the
#     setting is really used.
#   * the setting lands in config.ini, so the interface and the command line share one
#     source of truth instead of the app having two.
#   * a bad value is refused without a window and without touching the settings.
#
# Every launch uses -WindowStyle Hidden. A message box is a window: without this the
# "refused a bad value" case would sit on a modal dialog forever, because nobody is there
# to click OK.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-cli.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT     = $script:TestPort
$CLI_PORT = $script:TestPort + 2      # distinct from every port the suite otherwise uses
$DEBUG    = $script:DebugPort

Write-Head "command line options"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

# Reads config.ini as text, so a test can compare it before and after.
function Read-ConfigText {
    if (-not (Test-Path $script:ConfigIni)) { return "" }
    return (Get-Content $script:ConfigIni -Encoding UTF8 -Raw)
}

# Launches the app through Start-Process with -WindowStyle Hidden.
#
# Hidden is not cosmetic: refusing a bad value shows a MessageBox, and a message box IS a
# window, so without this the process would sit on a modal dialog that nobody can dismiss
# and the test would hang. Nothing the app does is visible to the assertions anyway -- they
# read the port and config.ini.
function Start-AppWithArgs {
    param([string[]]$AppArgs)
    # A single already-quoted string: passing an array makes PowerShell join and requote
    # it, which turns the embedded quotes into literal ones and the app sees them.
    $argLine = ($AppArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    return Start-Process -FilePath $script:DistExe -ArgumentList $argLine `
        -WorkingDirectory (Split-Path $script:DistExe -Parent) `
        -WindowStyle Hidden -PassThru
}

function Stop-Quietly($proc) {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}

Stop-AppAll -Ports @($PORT, $CLI_PORT, 31700)
if (-not (Test-PortBusy -Port $CLI_PORT)) { Assert-That $true "the CLI port starts out free" }
else { Assert-That $false ("the CLI port " + $CLI_PORT + " was already in use before the test") }

$proc = $null
try {
    Write-Head ("--port " + $CLI_PORT + " must really move the service")

    # Start from a config that says something else, so "it ended up on CLI_PORT" cannot be
    # explained by it having been there already.
    Set-TestConfig -Port $PORT
    $before = Read-ConfigText

    $offset = New-LogOffset
    $proc = Start-AppWithArgs @('--port', "$CLI_PORT")
    Write-Info ("pid " + $proc.Id)

    $moved = Wait-PortBusy -Port $CLI_PORT -TimeoutSec 120
    Assert-That $moved ("the service is answering on the port given on the command line")

    if ($moved) {
        $owner = Get-PortOwner -Port $CLI_PORT
        Write-Info ("port " + $CLI_PORT + " served by pid " + $owner)
        Assert-That ($owner -gt 0) "that port has a real owner"
    }

    $logText = Get-NewLogText -Offset $offset
    Assert-That ($logText -match ('command line: port set to ' + $CLI_PORT)) "the log records the command line setting"

    $after = Read-ConfigText
    Assert-That ($after -ne $before) "config.ini was updated"
    # \s*$ rather than $: the file is CRLF, so a "$" straight after the digits never
    # matches -- there is a "\r" in the way. (Cost a false failure once already.)
    Assert-That ($after -match ('(?m)^port=' + $CLI_PORT + '\s*$')) "config.ini now holds the command line port"

    Stop-Quietly $proc
    $proc = $null
    [void](Wait-PortFree -Port $CLI_PORT -TimeoutSec 20)

    Write-Head "a bad value must be refused, not half-applied"

    Set-TestConfig -Port $PORT
    $good = Read-ConfigText
    $offset2 = New-LogOffset

    $proc = Start-AppWithArgs @('--port', '999')
    Start-Sleep -Seconds 6

    $logText2 = Get-NewLogText -Offset $offset2
    Assert-That ($logText2 -match 'bad command line') "the log says the command line was refused"
    Assert-That ($logText2 -notmatch ('command line: port set to')) "no setting was applied"
    Assert-That ((Read-ConfigText) -eq $good) "config.ini was left alone"
    Assert-That (-not (Test-PortBusy -Port 999)) "nothing was started on the rejected port"

    Stop-Quietly $proc
    $proc = $null

    Write-Head "an unknown argument must be refused too"

    $offset3 = New-LogOffset
    $proc = Start-AppWithArgs @('--nonsense')
    Start-Sleep -Seconds 6
    $logText3 = Get-NewLogText -Offset $offset3
    Assert-That ($logText3 -match 'bad command line') "an unknown argument is refused"
    Assert-That ((Read-ConfigText) -eq $good) "config.ini is still untouched"
    Stop-Quietly $proc
    $proc = $null
} finally {
    Write-Head "cleanup"
    Stop-Quietly $proc
    Stop-AppAll -Ports @($CLI_PORT, 31700)
    Assert-That (Wait-PortFree -Port $CLI_PORT -TimeoutSec 20) ("port " + $CLI_PORT + " released")
    Restore-Config
}

exit (Complete-Test -Name 'cli')
