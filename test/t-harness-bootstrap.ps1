# Does the harness bootstrap actually produce something usable?
#
# WHY THIS TEST EXISTS
#
# The other twelve tests all run on a machine that already has the harness in its npm
# cache. Every one of them therefore starts from "the download already happened". The path
# a brand new user takes -- npx fetches ~360 MB, resolves ~25,000 files, and the result has
# to actually RUN -- was the one path nothing covered. It is also the path a
# non-technical user is most likely to hit, and the one that historically produced a real
# field failure (the sharp image module missing from an incomplete download).
#
# WHY IT DOES NOT DRIVE THE APP
#
# The obvious version of this test would launch the app with no cache and watch it
# download. That is not safely reproducible: the app finds its cache through
# Environment.GetFolderPath(SpecialFolder.LocalApplicationData), which reads the Windows
# known-folder API and ignores environment variables, so no environment change can point it
# somewhere else. Forcing the app down the download path would mean temporarily moving the
# user's real 214 MB cache directory aside -- real risk to a working installation, for a
# layer of the product that the other tests already cover.
#
# So this drives the same mechanism the app drives (npx, same package spec) into its own
# cache, and then checks the part that actually matters: is what came out complete, and
# does it start?
#
# ISOLATION: npm_config_cache and DSH_HOME both point into the sandbox, so nothing is
# written to the real npm cache or the real ~/.dsh. The real cache is only ever read for
# its package tarball cache, so by default this is fast and safe.
#
# WHO RUNS THIS, AND WHEN
#
# This one is deliberately NOT part of the default suite. It needs the network and takes
# about two minutes, while the other twelve tests take about four minutes in total and run
# offline. Put it in the release checklist, not in the loop you run after every edit.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-harness-bootstrap.ps1
#   powershell ... -File test\t-harness-bootstrap.ps1 -Cold      # empty cache first
#   powershell ... -File test\t-harness-bootstrap.ps1 -SkipStart # download checks only
#
# WHAT IS MEASURED (so nobody has to guess again): a run takes ~120 s; the sandbox ends
# holding ~27,500 files / ~360 MB, of which the harness tree is ~190 packages / ~25,400
# files / ~214 MB and the package cache is ~147 MB.
#
# -Cold and the default differ only in whether the package cache starts empty. Both fetch
# their own tarballs either way -- see the note by the cache setup for why borrowing the
# machine's cache was tried and removed.
#
# ISOLATION IS THE POINT, AND IT IS CHECKED: npm_config_cache and DSH_HOME both point into
# the sandbox, and this test writes nothing outside it. That claim was broken once by a
# junction to the machine's real cache and is now verified rather than assumed.

param(
    [switch]$Cold,
    # Skipping the start makes this a pure download test; useful when npm or the network is
    # slow and only the artifact matters.
    [switch]$SkipStart
)

. "$PSScriptRoot\_lib.ps1"

$CLI_PORT = $script:TestPort + 3

Write-Head ("harness bootstrap" + $(if ($Cold) { " (COLD: real download)" } else { " (warm package cache)" }))

if (-not (Test-Path $script:DistExe)) {
    Write-Info "dist exe missing -- run build\build.ps1 first"
    exit 1
}

$sandbox = New-Sandbox 'harness-bootstrap'
$cache   = Join-Path $sandbox 'npmcache'
$dshHome = Join-Path $sandbox 'dshhome'
New-Item -ItemType Directory -Path $cache, $dshHome -Force | Out-Null

# The sandbox cache is REAL, not a link to the machine's cache.
#
# A junction was tried here to avoid re-fetching the ~147 MB of package tarballs, and it was
# removed: pointing npm at the machine's cache meant every write npm makes under the cache
# ROOT (including the _npx entry itself) landed inside the machine's real cache directory.
# A test whose stated contract is "the real environment is untouched" cannot quietly write
# 1.3 MB into it -- the contract is worth more than the two minutes saved.
#
# The cost of dropping it is honest and bounded: this test fetches its own tarballs, so it
# needs the network and takes ~120 s.
Write-Info "package cache: private to this test (nothing outside the sandbox is read or written)"

# The node and npm this machine actually has; the app would use the same ones.
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
$npx  = (Get-Command npx.cmd  -ErrorAction SilentlyContinue).Source
if (-not $node -or -not $npx) {
    Write-Info "no node/npx on PATH; cannot run this test"
    exit 1
}
Write-Info ("node = " + $node)
Write-Info ("npx  = " + $npx)

$harness = $null
# Fingerprint the machine's real cache BEFORE anything runs, so the isolation claim can be
# asserted at the end rather than trusted. This was added after a junction to the real cache
# quietly wrote ~1.3 MB into it while the test still described itself as read-only.
$realCacheDir = Join-Path $env:LOCALAPPDATA 'npm-cache\_cacache'
$realNpxDir   = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
$realBefore   = (Get-ChildItem $realCacheDir -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object).Count
$realNpxBefore = @(Get-ChildItem $realNpxDir -Directory -ErrorAction SilentlyContinue).Count

try {
    # Both modes are stated up front, so what a run covers is known rather than inferred
    # from how long it took. (Time is a poor proxy: unpacking ~25,000 files dominates and
    # costs about the same either way.)
    if ($Cold) {
        Remove-Item $cache -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        Assert-That ((Get-ChildItem $cache -Recurse -File -Force -EA SilentlyContinue).Count -eq 0) "cold mode: the package cache really is empty"
        Write-Info "package cache: empty (every byte comes from the network)"
    } else {
        Write-Info "package cache: private sandbox cache (first run fetches ~147 MB)"
    }

    Write-Head "run the same bootstrap the app runs (npx -y @deepseek-ai/dsh)"
    $env:npm_config_cache = $cache
    $env:DSH_HOME = $dshHome
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & cmd.exe /c "npx -y @deepseek-ai/dsh --version" 2>&1
    $sw.Stop()
    $outText = ($out | Out-String)
    foreach ($l in $out) { Write-Info $l }
    Write-Info ("elapsed = " + [int]$sw.Elapsed.TotalSeconds + " s")

    Assert-That ($outText -match '\d+\.\d+\.\d+') "the harness reported a version"
    # The version is printed on its own, but npm also warns about unrelated packages
    # ("node-domexception@1.0.0"), so taking the first match anywhere picks up npm's noise.
    # The harness answers on the last non-empty line.
    $lastLine = ($out | Where-Object { "$_".Trim().Length -gt 0 } | Select-Object -Last 1)
    $reported = [regex]::Match("$lastLine", '^\s*(\d+\.\d+\.\d+(?:-[\w.]+)?)\s*$').Groups[1].Value
    if ($reported.Length -eq 0) {
        $reported = [regex]::Match($outText, '(?m)^\s*(\d+\.\d+\.\d+(?:-[\w.]+)?)\s*$').Groups[1].Value
    }
    Write-Info ("reported version = '" + $reported + "'  (last output line)")

    # ---- the artifact ---------------------------------------------------------
    Write-Head "the downloaded tree"

    # Found rather than predicted: npx hashes the package spec into a directory name, and
    # that name is an npm implementation detail we should not hard-code.
    $binJs = Get-ChildItem $cache -Recurse -File -Filter 'bin.js' -Force -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -like '*@deepseek-ai\dsh\lib\bin.js' } |
             Select-Object -First 1
    Assert-That ($null -ne $binJs) "the harness launcher (bin.js) is present"

    if ($binJs) {
        $harness = $binJs.FullName
        Write-Info ("bin.js = " + $harness)

        # Walk up to the directory actually named "dsh", the same way the product finds the
        # node_modules it watches. Counting parent hops got this wrong on the first attempt
        # -- three hops from lib\bin.js lands on the "@deepseek-ai" scope folder, not the
        # package root, so every file check below looked in the wrong place.
        $root = Split-Path $harness -Parent
        for ($i = 0; $i -lt 8 -and $root; $i++) {
            if ((Split-Path $root -Leaf) -eq 'dsh') { break }
            $root = Split-Path $root -Parent
        }
        Write-Info ("tree root = " + $root)
        Assert-That ((Split-Path $root -Leaf) -eq 'dsh') "the package root was located"

        # The version on disk must be the one that was reported, or the cache would hold
        # two different builds and whichever is found first becomes a coin toss.
        $pkg = Join-Path $root 'package.json'
        Assert-That (Test-Path $pkg) "package.json is next to it"
        if (Test-Path $pkg) {
            $v = [regex]::Match([System.IO.File]::ReadAllText($pkg), '"version"\s*:\s*"([^"]+)"').Groups[1].Value
            Write-Info ("version on disk = " + $v)
            Assert-That ($v.Length -gt 0) "the manifest declares a version"
            if ($reported.Length -gt 0 -and $v.Length -gt 0) {
                Assert-That ($v -eq $reported) "the version on disk matches what the harness reported"
            }
        }

        # Dependencies are NOT inside the dsh package: npm installs them flat, as siblings.
        # The dsh package itself is about ten files -- so the tree to inspect is node_modules,
        # which sits two levels above the package root (@deepseek-ai\dsh).
        $modules = Split-Path (Split-Path $root -Parent) -Parent
        Write-Info ("node_modules = " + $modules)

        # The field failure this guards against: a download that "succeeded" but came down
        # without the platform binary, so the harness dies on startup. sharp is the one
        # that has actually bitten, and it is optional at the manifest level -- which is
        # exactly why an incomplete install can look like a success.
        $sharp = Join-Path $modules 'sharp'
        Assert-That (Test-Path $sharp) "the sharp module came down"

        $sharpBin = Get-ChildItem (Join-Path $modules '@img') -Directory -Force -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like 'sharp-win32-*' } | Select-Object -First 1
        Assert-That ($null -ne $sharpBin) "and its Windows platform binary is present"
        if ($sharpBin) { Write-Info ("sharp binary = @img\" + $sharpBin.Name) }

        # Measured baselines for a complete tree. A stub that merely resolved the manifest
        # would be far smaller; a partial download would be short on one of these.
        $files = Get-ChildItem $modules -Recurse -File -Force -ErrorAction SilentlyContinue
        $mb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
        $topLevel = @(Get-ChildItem $modules -Directory -Force).Count
        Write-Info ("node_modules = " + $topLevel + " packages / " + $files.Count + " files / " + $mb + " MB")
        Write-Info ("  (a complete tree measures ~190 packages / ~25,400 files / ~214 MB)")
        Assert-That ($topLevel -gt 100) "the dependency tree is fully populated, not a stub"
        Assert-That ($files.Count -gt 10000) "with a real number of files"
        Assert-That ($mb -gt 100) "and a real amount of data"
    }

    # ---- does it actually start ----------------------------------------------
    if ($SkipStart) {
        Write-Info "start skipped (-SkipStart)"
    } elseif (-not $harness) {
        Assert-That $false "cannot try to start a harness that was not downloaded"
    } else {
        Write-Head "start the freshly downloaded harness"
        $port = $CLI_PORT
        $logDir = Join-Path $sandbox 'run'
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $outLog = Join-Path $logDir 'out.log'
        $errLog = Join-Path $logDir 'err.log'

        # Started directly, the way the app starts it: node + bin.js, output to files. The
        # tokens in the banner can only be read from a file (a piped child prints none),
        # which is the same reason the product redirects to files.
        $args = '"' + $harness + '" web --no-open --port ' + $port
        $p = Start-Process -FilePath $node -ArgumentList $args -PassThru -NoNewWindow `
                -RedirectStandardOutput $outLog -RedirectStandardError $errLog `
                -WorkingDirectory (Split-Path $harness -Parent)

        Assert-That ($p.Id -gt 0) "the harness process started"
        try {
            $ready = Wait-PortBusy -Port $port -TimeoutSec 120
            Assert-That $ready "it bound its port, so the download produced a runnable harness"

            if (-not $ready) {
                Write-Info "--- err.log tail ---"
                if (Test-Path $errLog) { Get-Content $errLog -Encoding UTF8 -Tail 20 | ForEach-Object { Write-Info ("  " + $_) } }
            }
        } finally {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            # The harness may leave a child holding the port.
            $holder = Get-PortOwner -Port $port
            if ($holder -gt 0) { Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
            [void](Wait-PortFree -Port $port -TimeoutSec 20)
        }
    }

    # ---- what it cost ---------------------------------------------------------
    Write-Head "what the sandbox holds (this is the real cost of a first run)"

    # Measured per directory rather than by recursing the sandbox: in warm mode the cache
    # is a junction, and Get-ChildItem does not follow reparse points -- so a recursive
    # count reports "7 files / 0 MB" and hides the 214 MB that actually landed.
    $npxDir = Get-ChildItem $cache -Directory -Force -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -eq '_npx' } | Select-Object -First 1
    if ($npxDir) {
        $npxFiles = Get-ChildItem $npxDir.FullName -Recurse -File -Force -ErrorAction SilentlyContinue
        Write-Info ("harness tree   = " + $npxFiles.Count + " files / " + [math]::Round((($npxFiles | Measure-Object Length -Sum).Sum) / 1MB, 1) + " MB")
    }
    $cc = Join-Path $cache '_cacache'
    if (Test-Path $cc) {
        $ccFiles = Get-ChildItem $cc -Recurse -File -Force -ErrorAction SilentlyContinue
        Write-Info ("package cache  = " + $ccFiles.Count + " files / " + [math]::Round((($ccFiles | Measure-Object Length -Sum).Sum) / 1MB, 1) + " MB")
    }
    Write-Info "  (the app tells users ~200 MB of files, ~360 MB on disk including the cache)"

    # ---- the isolation claim, verified instead of asserted in a comment ---------
    Write-Head "nothing outside the sandbox was touched"
    $realAfter = (Get-ChildItem $realCacheDir -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object).Count
    $realNpxAfter = @(Get-ChildItem $realNpxDir -Directory -ErrorAction SilentlyContinue).Count
    Write-Info ("machine package cache: " + $realBefore + " -> " + $realAfter + " files")
    Write-Info ("machine _npx entries:  " + $realNpxBefore + " -> " + $realNpxAfter)
    Assert-That ($realAfter -eq $realBefore) "the machine's package cache was not written to"
    Assert-That ($realNpxAfter -eq $realNpxBefore) "and no _npx entry was added there"
} finally {
    Write-Head "cleanup"
    Remove-Item Env:\npm_config_cache -ErrorAction SilentlyContinue
    Remove-Item Env:\DSH_HOME -ErrorAction SilentlyContinue
    Stop-AppAll -Ports @($CLI_PORT) -SkipApps
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'harness-bootstrap')
