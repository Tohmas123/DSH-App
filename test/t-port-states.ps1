# Port classification: the three states a port can be in.
#
# Why this exists. TestPortOpen answers True for two completely different situations: a
# real listener, and a port Windows has reserved (the kernel completes the handshake for a
# reserved port even though nothing is bound, and a bind there fails with "access
# permissions"). The app used to collapse both into "already in use (pid N)", which for a
# reserved port means telling the user to go find a process that does not exist.
#
# The measurement behind the classification, taken on this machine:
#
#     port   answers on connect   netstat pid   real bind
#     8884   yes                 4 (System)    fails: access permissions   <- reserved
#     3099   yes                 the real pid  in use                      <- listening
#     31500  no                  0             succeeds                    <- free
#
# The driver calls the REAL EnvProbe. The busy port is passed in (never assumed), because
# this suite must not start a listener of its own just to test the probe.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-port-states.ps1
#   # with a port known to be in use:  $env:DSH_TEST_BUSY_PORT='3099'

. "$PSScriptRoot\_lib.ps1"

# A port Windows reserves on this machine (from `netsh int ipv4 show excludedportrange`).
# Fixed rather than discovered, so the test states its own precondition instead of quietly
# skipping when the machine has no reserved range.
$RESERVED_PORT = if ($env:DSH_TEST_RESERVED_PORT) { [int]$env:DSH_TEST_RESERVED_PORT } else { 8884 }
$FREE_PORT     = 31500
$BUSY_PORT     = if ($env:DSH_TEST_BUSY_PORT) { [int]$env:DSH_TEST_BUSY_PORT } else { 0 }

# The Listening branch needs a port that really is being served, and there is no port this
# suite may assume. An earlier version took it from the environment and simply failed on the
# day nothing happened to be listening on the port it was told to use -- a test that depends
# on the machine's mood. Find one instead, excluding anything this suite starts or anything
# whose owner is the System process (a reserved port would be a false witness here).
if ($BUSY_PORT -le 0) {
    $candidate = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -ne $RESERVED_PORT -and
                       $_.LocalPort -ne $FREE_PORT -and
                       $script:TestPorts -notcontains $_.LocalPort -and
                       $_.OwningProcess -gt 4 } |
        Sort-Object LocalPort -Unique |
        Select-Object -First 1
    if ($candidate) { $BUSY_PORT = [int]$candidate.LocalPort }
}

Write-Head "port classification (reserved / listening / free)"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

$sandbox = New-Sandbox 'port-states'
$driverSrc = Join-Path $sandbox 'PortStateProbe.cs'
$driver = @'
using System;
using System.IO;

internal static class PortStateProbe
{
    private static void Show(string tag, int port)
    {
        int owner;
        var state = EnvProbe.ClassifyPort(port, out owner);
        // Also report the raw probe, so the classification can be checked against the
        // primitive it is built on rather than taken on faith.
        Console.WriteLine(tag + ".port=" + port);
        Console.WriteLine(tag + ".state=" + state);
        Console.WriteLine(tag + ".owner=" + owner);
        Console.WriteLine(tag + ".open=" + EnvProbe.TestPortOpen(port, 250));
    }

    private static int Main(string[] args)
    {
        string logDir = args[0];
        int reserved = int.Parse(args[1]);
        int free = int.Parse(args[2]);
        int busy = int.Parse(args[3]);

        Directory.CreateDirectory(logDir);
        AppLogger.Init(logDir);

        Show("reserved", reserved);
        Show("free", free);
        if (busy > 0) Show("busy", busy);

        return 0;
    }
}
'@
Write-TextNoBom -Path $driverSrc -Text $driver

$driverExe = $null
try {
    Write-Head "compile driver"
    $driverExe = Compile-TestDriver -SourceFile $driverSrc -OutName 'PortStateProbe'
    if (-not $driverExe) {
        Write-Info "driver did not compile"
        Assert-That $false "the port-state driver compiled"
    } else {
        Write-Info ("driver: " + $driverExe)

        Write-Head "the precondition this test depends on"
        # Asserted explicitly: if this machine ever stops reserving the port, the test must
        # say so rather than pass for the wrong reason.
        $holder = Get-PortOwner -Port $RESERVED_PORT
        Write-Info ("reserved port " + $RESERVED_PORT + ": netstat owner pid = " + $holder)
        Write-Info ("free port " + $FREE_PORT + ": busy = " + (Test-PortBusy -Port $FREE_PORT))
        if ($BUSY_PORT -gt 0) { Write-Info ("busy port " + $BUSY_PORT + ": owner pid = " + (Get-PortOwner -Port $BUSY_PORT)) }

        Write-Head "run (the driver calls the REAL EnvProbe.ClassifyPort)"
        $logDir = Join-Path $sandbox 'run'
        $out = & $driverExe $logDir $RESERVED_PORT $FREE_PORT $BUSY_PORT 2>&1
        $exit = $LASTEXITCODE
        $outText = ($out | Out-String)
        foreach ($l in $out) { Write-Info $l }

        Assert-That ($exit -eq 0) "driver exited 0"

        Write-Head "checks"
        # The reserved port: answers on connect, but nothing is listening.
        Assert-That ($outText -match 'reserved\.open=True') "a Windows-reserved port does answer on connect (the trap)"
        Assert-That ($outText -match 'reserved\.state=Reserved') "it is classified as Reserved, not Listening"
        Assert-That ($outText -match 'reserved\.owner=4') "its netstat owner is the System process"

        # The free port: the control, proving the classification is not just returning
        # Reserved for everything.
        Assert-That ($outText -match 'free\.open=False') "a free port does not answer"
        Assert-That ($outText -match 'free\.state=Free') "it is classified as Free"

        # A genuinely busy port, when one was supplied or found.
        if ($BUSY_PORT -gt 0) {
            Assert-That ($outText -match 'busy\.open=True') "the chosen busy port answers"
            Assert-That ($outText -match 'busy\.state=Listening') "a real listener is classified as Listening"
            Assert-That ($outText -notmatch 'busy\.state=Reserved') "and never as Reserved"
            Assert-That ($outText -match 'busy\.owner=[1-9]') "and it reports a real owner pid"
        } else {
            Assert-That $false "a port with a real listener was available to test against"
        }
    }
} finally {
    Write-Head "cleanup"
    if ($driverExe) { Remove-Item $driverExe -Force -ErrorAction SilentlyContinue }
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'port-states')
