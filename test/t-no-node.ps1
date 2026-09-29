# Node.js detection -- the branch that decides whether the user sees the install guide.
#
# This is the most likely first-run failure for the people this launcher is built for: a
# fresh Windows box with no Node. When Node really cannot be found, ProbeNode must report
# Found=false, because that single flag is what makes the app show "install Node.js" and
# stop instead of trying to launch a service that cannot run.
#
# WHAT THIS TEST CAN AND CANNOT DO
#
# It cannot reproduce "no Node on the machine" on a machine that has Node, and that is by
# design rather than by accident. ProbeNode deliberately does not trust PATH: after the
# PATH search fails it falls back to the conventional install directories via
# Environment.GetFolderPath(SpecialFolder...), which reads the Windows registry / shell
# folders and ignores the environment, so it still finds C:\Program Files\nodejs\node.exe.
# Forcing the "not found" branch would mean renaming the user's Node installation or
# editing the registry, which this suite never does.
#
# So the test pins down the contract that actually exists on such a machine:
#   * with a normal PATH, Node is found through PATH
#   * with PATH emptied, the PATH search yields nothing (FindOnPath returns empty)
#   * and the app STILL finds Node anyway, through the fallback -- which is the behaviour
#     that keeps non-technical users working when the installer did not add Node to PATH
#   * the log says which of the two routes was taken, so a support question can be answered
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-no-node.ps1

. "$PSScriptRoot\_lib.ps1"

Write-Head "Node.js detection"

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

$sandbox = New-Sandbox 'no-node'
$empty   = Join-Path $sandbox 'empty-path'
New-Item -ItemType Directory -Path $empty -Force | Out-Null

$driverSrc = Join-Path $sandbox 'NodeProbe.cs'
$driver = @'
using System;
using System.IO;

internal static class NodeProbe
{
    private static void Show(string tag, EnvProbe.NodeInfo n)
    {
        Console.WriteLine(tag + ".Found=" + n.Found);
        Console.WriteLine(tag + ".Version=" + n.Version);
        Console.WriteLine(tag + ".Path=" + n.Path);
        Console.WriteLine(tag + ".Detail=" + n.Detail);
        Console.WriteLine(tag + ".OnPath=" + (EnvProbe.FindOnPath("node.exe") ?? ""));
    }

    private static int Main(string[] args)
    {
        string emptyPath = args[0];
        string logDir = args[1];
        Directory.CreateDirectory(logDir);
        AppLogger.Init(logDir);

        // 1) Normal machine.
        Show("with", EnvProbe.ProbeNode());

        // 2) Nothing on PATH. The PATH search must come back empty, and the probe must
        //    fall back rather than give up.
        Environment.SetEnvironmentVariable("PATH", emptyPath);
        Show("without", EnvProbe.ProbeNode());

        return 0;
    }
}
'@
Write-TextNoBom -Path $driverSrc -Text $driver

try {
    Write-Head "compile driver"
    $driverExe = Compile-TestDriver -SourceFile $driverSrc -OutName 'NodeProbe'
    if (-not $driverExe) {
        Write-Info "driver did not compile"
        Assert-That $false "the probe driver compiled"
    } else {
        Write-Info ("driver: " + $driverExe)

        Write-Head "run (the driver calls the REAL EnvProbe.ProbeNode)"
        $logDir = Join-Path $sandbox 'run'
        $out = & $driverExe $empty $logDir 2>&1
        $exit = $LASTEXITCODE
        # Join into one string: matching an array with -match returns the matching members,
        # not a boolean, which reads like a pass and is not one.
        $outText = ($out | Out-String)
        foreach ($l in $out) { Write-Info $l }

        Assert-That ($exit -eq 0) "driver exited 0"

        Write-Head "checks"
        Assert-That ($outText -match 'with\.Found=True') "Node is found on a normal machine"
        Assert-That ($outText -match 'with\.Detail=PATH') "and it was found through PATH"
        Assert-That ($outText -match 'with\.OnPath=\S') "FindOnPath returned its location"

        # The PATH search itself, which is the branch a scrubbed environment exercises.
        Assert-That ($outText -match 'without\.OnPath=\r?\n') "with PATH emptied, FindOnPath returns empty"

        # And the more important half: the app still finds Node. This is why a user whose
        # Node is not on PATH is not sent to the download page for something they have.
        Assert-That ($outText -match 'without\.Found=True') "Node is STILL found via the conventional install directory"
        Assert-That ($outText -match 'without\.Detail=outside PATH') "and it says it came from outside PATH"

        # The log must distinguish the two routes; "which node did it use?" is the first
        # question when a machine misbehaves.
        $driverLog = Join-Path $logDir 'app.log'
        $logText = if (Test-Path $driverLog) { Get-Content $driverLog -Encoding UTF8 -Raw } else { "" }
        Assert-That ($logText -match 'node found: v') "the log records the in-PATH find"
        Assert-That ($logText -match 'node found outside PATH: v') "the log records the fallback find"

        Write-Info ""
        Write-Info "NOT COVERED HERE: the window showing the install-Node guide when Node truly is"
        Write-Info "                 absent. That needs a Node-free machine or registry edits, and"
        Write-Info "                 this suite does not modify the host to test itself."
    }
} finally {
    Write-Head "cleanup"
    # The driver was staged next to the product so its DLL references resolve; take it back
    # out, or the next build/pack step could sweep a test binary into the deliverable.
    if ($driverExe) { Remove-Item $driverExe -Force -ErrorAction SilentlyContinue }
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'no-node')
