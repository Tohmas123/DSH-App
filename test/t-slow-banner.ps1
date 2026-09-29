# Regression test: a harness that binds its port early and prints its banner late must
# still be waited for.
#
# 2026-09-20: the app used to give up after 10 seconds of "listening but no URL" and
# report a failure while the banner was already sitting in the log. A plugin doing LAN
# setup (remote-web-ui) stretched that gap past the limit, so a healthy start looked
# broken. DshServer now only ends the wait at its 300-second deadline.
#
#   powershell -ExecutionPolicy Bypass -File test\t-slow-banner.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:TestPort2
$BANNER_DELAY_MS = 15000     # comfortably past the old 10-second rule

Write-Head "slow-banner regression (port $PORT, banner after $($BANNER_DELAY_MS/1000)s)"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}
if (Test-PortBusy -Port $PORT) {
    Write-Info "port $PORT is already busy; cannot run this test"
    exit 1
}

$sandbox = New-Sandbox 'slow-banner'
$fakeBin = Join-Path $sandbox 'fake-bin.js'
$driverSrc = Join-Path $sandbox 'SlowDriver.cs'
$driverExe = $null

try {
    # Fake harness: bind immediately, print the tokenised banner late. The socket error
    # handler is not optional -- the app probes the port by connecting and closing, and
    # without it that arrives as an unhandled error event and kills the fake harness,
    # which makes the test fail for the wrong reason.
    $stub = @"
const net = require("net");
const PORT = $PORT;
const TOKEN = "LABTOKEN_slow_banner_test";
const srv = net.createServer(function (s) { s.on("error", function () {}); s.end("ok"); });
srv.on("error", function (e) { process.stderr.write("srv error: " + e.message + "\n"); });
srv.listen(PORT, "127.0.0.1", function () {
  process.stderr.write("fake harness: bound port " + PORT + "\n");
  setTimeout(function () {
    process.stdout.write("dsh web: http://127.0.0.1:" + PORT + "/?token=" + TOKEN + "\n");
    process.stderr.write("fake harness: banner printed\n");
  }, $BANNER_DELAY_MS);
});
setInterval(function () {}, 1000);
"@
    Set-Content -Path $fakeBin -Value $stub -Encoding UTF8

    $driver = @'
using System;
using System.IO;

internal static class SlowDriver
{
    private static int Main(string[] args)
    {
        string binJs = args[0];
        string logDir = args[1];
        int port = int.Parse(args[2]);
        Directory.CreateDirectory(logDir);
        AppLogger.Init(logDir);

        var node = EnvProbe.ProbeNode();
        var dsh = new EnvProbe.DshInfo();
        dsh.How = "npx-cache";
        dsh.Ready = true;
        dsh.LauncherExe = node.Found && node.Path.Length > 0 ? node.Path : "node";
        // Same shape the app builds: the script path is quoted, the subcommand follows.
        dsh.LauncherArgs = "\"" + binJs + "\" web --no-open --port {port}";
        dsh.TreeRoot = Path.GetDirectoryName(Path.GetDirectoryName(binJs));

        var server = new DshServer();
        var started = DateTime.UtcNow;
        string url = server.Start(dsh, node, port, Path.GetTempPath(), "sk-lab", logDir, null);

        Console.WriteLine("url=" + (url == null ? "(null)" : url));
        Console.WriteLine("elapsed=" + (int)(DateTime.UtcNow - started).TotalSeconds);
        Console.WriteLine("error=" + server.LastError);
        try { server.Stop(); } catch { }
        return url == null ? 1 : 0;
    }
}
'@
    Set-Content -Path $driverSrc -Value $driver -Encoding UTF8

    Write-Head "compile driver"
    $driverExe = Compile-TestDriver -SourceFile $driverSrc -OutName 'SlowBannerDriver'
    if (-not $driverExe) {
        Write-Info "driver did not compile"
        exit 1
    }
    Write-Info ("driver: " + $driverExe)

    Write-Head "run (the driver drives the REAL DshServer against the fake harness)"
    $logDir = Join-Path $sandbox 'run'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & $driverExe $fakeBin $logDir $PORT 2>&1
    $sw.Stop()
    $exit = $LASTEXITCODE
    foreach ($l in $out) { Write-Info $l }

    $driverLog = Join-Path $logDir 'app.log'
    $logText = if (Test-Path $driverLog) { Get-Content $driverLog -Encoding UTF8 -Raw } else { "" }
    # Join the captured output: matching an ARRAY with -match returns the members that
    # matched, not a boolean, which is easy to mistake for a pass.
    $outText = ($out | Out-String)

    Write-Head "checks"
    Assert-That ($exit -eq 0) "driver exited 0"
    Assert-That ($outText -match ('url=http://127\.0\.0\.1:' + $PORT + '/\?token=LABTOKEN')) "captured the late token URL"
    Assert-That ($logText -match 'is listening; waiting for the access URL') "logged that it was listening and waiting"
    Assert-That ($logText -match 'harness ready') "reached harness ready"
    # The whole point: it waited longer than the old 10-second rule instead of giving up.
    Assert-That ($sw.Elapsed.TotalSeconds -ge 12) ("waited past the old 10s limit (took " + [int]$sw.Elapsed.TotalSeconds + "s)")
    Assert-That ($logText -notmatch 'never printed its access URL') "did not report the old failure"

    Write-Head "cleanup"
    if (-not (Wait-PortFree -Port $PORT -TimeoutSec 15)) {
        Write-Info "port $PORT still busy after the driver stopped"
    }
} finally {
    if ($driverExe) { Remove-Item $driverExe -Force -ErrorAction SilentlyContinue }
    # Any fake harness left behind would hold the port and break the next run.
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like '*fake-bin.js*' -and $_.CommandLine -notlike '*Win32_Process*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'slow-banner')
