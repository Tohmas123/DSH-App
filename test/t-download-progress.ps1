# Real download progress on the first run.
#
# The problem this covers: the first run downloads ~224 MB / ~25,400 files and used to be
# described only by elapsed seconds in front of a decorative bar. The user's question is
# "is it still working, and how much longer?", and elapsed time alone cannot answer it.
#
# Why the progress is measured off the disk instead of parsed from npm's output: npm only
# paints its progress bar on a TTY, and this child writes to log files (that redirect is
# deliberate -- it is what makes the banner appear at all). So there is no "added 37/208"
# line to read. What is reliably observable is the directory filling up.
#
# The test drives the REAL DshServer.Start against a fake launcher, and the fake launcher
# writes files into node_modules while it starts. That is the same path a real first run
# takes: the tree grows while Start() polls. Nothing is mocked, so the counting, the
# wording and the "only report when a number moves" rule are all exercised for real.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-download-progress.ps1

. "$PSScriptRoot\_lib.ps1"

$PORT = $script:TestPort2 + 1     # its own port: the fake launcher binds it

Write-Head "first-run download progress"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

$sandbox = New-Sandbox 'download-progress'

# The tree that grows, laid out EXACTLY like a real npx cache entry:
#
#     <entry>\node_modules\@deepseek-ai\dsh\lib\bin.js
#     <entry>\node_modules\<every installed package>
#
# The launcher has to sit INSIDE node_modules, because that is how the code locates the
# directory to watch -- it walks up from the launcher path looking for the "node_modules"
# component. A first attempt put bin.js in a sibling folder, and the result was an honest
# "no node_modules in the launcher path" with no progress ever reported: the shape of the
# tree IS the contract here, not an incidental detail.
$entry = Join-Path $sandbox 'entry'
$modules = Join-Path $entry 'node_modules'
$binJs = Join-Path $modules '@deepseek-ai\dsh\lib\bin.js'
foreach ($d in @((Split-Path $binJs -Parent), (Join-Path $sandbox 'logs'))) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

# One package already present, so the tree exists before Start begins sampling. The tree
# being absent is a legitimate state (nothing downloaded yet) but it is not the state this
# test is about.
Set-Content -Path (Join-Path $modules 'first-package.txt') -Value 'x' -Encoding ASCII

# The fake launcher: grow the tree, then bind the port and print a banner, exactly like a
# first run downloading then starting.
$stub = @'
const net = require("net");
const fs = require("fs");
const path = require("path");

const PORT = Number(process.argv[2]);
const MODULES = process.argv[3];
const COUNT = Number(process.argv[4]);
const BYTES_EACH = Number(process.argv[5]);

function grow() {
  // Staggered on purpose: written all at once it would finish before the first sample, and
  // then nothing could show that the count grows while the service is still starting.
  for (let i = 0; i < COUNT; i++) {
    setTimeout(function () {
      const dir = path.join(MODULES, "pkg-" + i);
      try { fs.mkdirSync(dir, { recursive: true }); } catch (e) {}
      try { fs.writeFileSync(path.join(dir, "data.bin"), Buffer.alloc(BYTES_EACH, 65)); } catch (e) {}
    }, i * 250);
  }
}
grow();

const srv = net.createServer(function (s) { s.on("error", function () {}); s.end("ok"); });
srv.on("error", function (e) { process.stderr.write("srv error: " + e.message + "\n"); });
srv.listen(PORT, "127.0.0.1", function () {
  process.stderr.write("fake harness: bound port " + PORT + "\n");
  // The banner is deliberately late (growth finishes around 6s at 150ms/package). If the
  // launcher printed it too early, Start() would return while the tree was still filling
  // and the final count would be whatever happened to have landed -- a racy expected value
  // rather than a fact.
  setTimeout(function () {
    process.stdout.write("dsh web: http://127.0.0.1:" + PORT + "/?token=LABTOKEN_progress_test\n");
    process.stderr.write("fake harness: banner printed\n");
  }, 15000);
});
setInterval(function () {}, 1000);
'@
Write-TextNoBom -Path $binJs -Text $stub

$driverSrc = Join-Path $sandbox 'ProgressDriver.cs'
$driver = @'
using System;
using System.IO;

internal static class ProgressDriver
{
    private static int Main(string[] args)
    {
        // args: binJs logDir port modules packages bytesEach
        string binJs = args[0];
        string logDir = args[1];
        int port = int.Parse(args[2]);
        string modules = args[3];
        int packages = int.Parse(args[4]);
        int bytesEach = int.Parse(args[5]);

        Directory.CreateDirectory(logDir);
        AppLogger.Init(logDir);

        var node = EnvProbe.ProbeNode();
        var dsh = new EnvProbe.DshInfo();
        dsh.How = "npx-cache";
        dsh.Ready = true;
        dsh.LauncherExe = node.Found && node.Path.Length > 0 ? node.Path : "node";
        // The argv index the fake launcher reads its tree from is the same shape the
        // product uses, and Start() parses this same string to find the directory to watch.
        dsh.LauncherArgs = "\"" + binJs + "\" " + port + " \"" + modules + "\" "
            + packages + " " + bytesEach;
        dsh.TreeRoot = Path.GetDirectoryName(Path.GetDirectoryName(binJs));

        // Reported so a failure says which directory was watched, instead of leaving the
        // next person to guess (a wrong root silently counts the wrong files).
        Console.WriteLine("modules=" + modules);

        var server = new DshServer();
        int reports = 0;
        var first = new DshServer.Progress();
        var last = new DshServer.Progress();

        string url = server.Start(dsh, node, port, Path.GetTempPath(), "sk-progress", logDir, null,
            delegate(DshServer.Progress p)
            {
                reports++;
                if (reports == 1) { first = p; }
                last = p;
                Console.WriteLine("progress#" + reports + " packets=" + p.Packets
                    + " bytes=" + p.Bytes + " elapsed=" + p.ElapsedSec);
                Console.WriteLine("TEXT#" + reports + " " + server.Describe(p));
            });

        Console.WriteLine("url=" + (url == null ? "(null)" : url));
        Console.WriteLine("reports=" + reports);
        Console.WriteLine("firstPackets=" + first.Packets);
        Console.WriteLine("lastPackets=" + last.Packets);
        Console.WriteLine("lastBytes=" + last.Bytes);

        // Describe() with nothing measured yet must not invent numbers.
        var empty = new DshServer.Progress();
        empty.ElapsedSec = 7;
        empty.Phase = "starting";   // ASCII placeholder: the phase is not what EMPTY tests
        Console.WriteLine("EMPTY " + server.Describe(empty));

        try { server.Stop(); } catch { }
        return url == null ? 1 : 0;
    }
}
'@
Write-TextNoBom -Path $driverSrc -Text $driver

$PACKAGES = 40
$BYTES_EACH = 1024 * 1024      # 1 MB per package -> a byte figure worth printing

$driverExe = $null
try {
    Write-Head "compile driver"
    $driverExe = Compile-TestDriver -SourceFile $driverSrc -OutName 'ProgressDriver'
    if (-not $driverExe) {
        Write-Info "driver did not compile"
        Assert-That $false "the progress driver compiled"
    } else {
        Write-Info ("driver: " + $driverExe)

        Write-Head "run (real DshServer + a launcher that fills the tree while starting)"
        $logDir = Join-Path $sandbox 'logs'
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $out = & $driverExe $binJs $logDir $PORT $modules $PACKAGES $BYTES_EACH 2>&1
        $sw.Stop()
        $exit = $LASTEXITCODE
        $outText = ($out | Out-String)
        foreach ($l in $out) { Write-Info $l }

        Write-Head "checks"
        Assert-That ($exit -eq 0) "driver exited 0 (the service came up)"
        Assert-That ($outText -match ('url=http://127\.0\.0\.1:' + $PORT + '/\?token=LABTOKEN')) "it still captured the banner"

        # The progress must actually report the files on disk, not a guess.
        $reports = 0
        if ($outText -match 'reports=(\d+)') { $reports = [int]$Matches[1] }
        Write-Info ("progress reports = " + $reports)
        Assert-That ($reports -ge 1) "progress was reported at all"

        $first = 0; $last = 0
        if ($outText -match 'firstPackets=(\d+)') { $first = [int]$Matches[1] }
        if ($outText -match 'lastPackets=(\d+)')  { $last  = [int]$Matches[1] }
        Write-Info ("packages seen: first=" + $first + " last=" + $last)

        # The count must equal the real directory contents, not a plausible-looking number.
        # Top-level entries are: the "@deepseek-ai" scope folder (created with the launcher
        # path), the pre-existing first-package.txt, and the packages the launcher wrote.
        $expected = 2 + $PACKAGES
        Assert-That ($last -eq $expected) ("the final count matches the files on disk (" + $last + " = " + $expected + ")")
        Assert-That ($last -gt $first) "the count grew while it was starting"

        $lastBytes = 0
        if ($outText -match 'lastBytes=(\d+)') { $lastBytes = [long]$Matches[1] }
        Write-Info ("bytes reported = " + $lastBytes)
        Assert-That ($lastBytes -gt 0) "a byte total was reported"

        # The wording must mention the measured numbers. The Chinese is written as \uXXXX
        # escapes because this script has to stay pure ASCII: PowerShell 5.1 decodes a
        # BOM-less script as ANSI, which mangles the literals and eats the closing quote.
        $pkgWord = '\u4E2A\u4F9D\u8D56\u5305'                  # "ge yi lai bao"
        $waitedWord = '\u5DF2\u7B49\u5F85'                     # "yi deng dai"
        Assert-That ($outText -match ('\u5DF2\u4E0B\u8F7D ' + $last + ' ' + $pkgWord)) "the text names the measured package count"
        Assert-That ($outText -match 'MB') "the text names a size"

        # With nothing measured, it must fall back to elapsed time and invent no numbers.
        Assert-That ($outText -match ('EMPTY .*' + $waitedWord + ' 7 \u79D2')) "an empty measurement falls back to elapsed seconds"
        Assert-That ($outText -notmatch ('EMPTY .*' + $pkgWord)) "and invents no package count"
    }
} finally {
    Write-Head "cleanup"
    # Dump the driver's own log BEFORE the sandbox goes away: when the progress root is
    # resolved wrongly, these lines are the only place that says so.
    $driverLog = Join-Path $sandbox 'logs\app.log'
    if (Test-Path $driverLog) {
        Write-Info "driver app.log (progress + start lines):"
        Get-Content $driverLog -Encoding UTF8 |
            Where-Object { $_ -match 'progress:|starting harness|harness ready' } |
            ForEach-Object { Write-Info ("  " + $_) }
    } else { Write-Info "no driver app.log found" }
    if ($driverExe) { Remove-Item $driverExe -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like ('*' + $binJs + '*') } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'download-progress')
