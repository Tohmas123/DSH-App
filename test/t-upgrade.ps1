# Upgrading while the old build is running.
#
# This is the path every existing user takes when a new version is handed out, and it has
# one hard requirement: Windows cannot replace a running executable, so the installer has
# to stop the old launcher first. It also has one promise to keep: the user's chats and
# settings must not be touched.
#
# Runs entirely in a TEMP install directory; the real installed copy is never involved.
#
#   powershell -ExecutionPolicy Bypass -File test\t-upgrade.ps1

. "$PSScriptRoot\_lib.ps1"

$SILENT = Join-Path $script:RepoRoot 'dist\package\DSH-App-standalone.exe'
$PORT = $script:TestPort

Write-Head "upgrade over a running install"

if (-not (Test-Path $SILENT)) {
    Write-Info "dist\package\DSH-App-standalone.exe missing -- run build\pack.ps1 first"
    exit 1
}

Stop-AppAll -Ports @($PORT)
$sandbox = New-Sandbox 'upgrade'
$target = Join-Path $sandbox 'app'

# Captured before anything runs, so the assertion at the end can prove this test did not
# touch it. See the /noreg comment below.
$realRegKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App'
$realBefore = (@(& reg.exe query $realRegKey 2>&1 | Out-String)).Trim()

try {
    Write-Head "install (silent) into a sandbox directory"
    # /noreg is not optional here. Without it this install rewrites the machine's real
    # "Apps & features" entry so that it points at THIS sandbox -- and the sandbox is
    # deleted when the test ends, leaving the developer with an uninstall entry aimed at a
    # directory that no longer exists. The entry is only visible in Windows Settings, so
    # nothing ever reported it: it was found by accident while auditing this test.
    $p = Start-Process -FilePath $SILENT -ArgumentList @('/S', ('/D=' + $target), '/nodesktop', '/noreg') -Wait -PassThru
    Write-Info ("installer exit = " + $p.ExitCode)
    Assert-That ($p.ExitCode -eq 0) "the installer exited 0"
    Assert-That (Test-Path (Join-Path $target 'DSH-App.exe')) "DSH-App.exe was installed"
    Assert-That (Test-Path (Join-Path $target 'Uninstaller.exe')) "Uninstaller.exe came with it"

    $installedExe = Join-Path $target 'DSH-App.exe'
    $versionBefore = (Get-Item $installedExe).VersionInfo.FileVersion
    Write-Info ("installed version = " + $versionBefore)

    Write-Head "record the user's data fingerprints"
    $cfg = $script:ConfigIni
    $sessions = Join-Path $script:RealDsh 'sessions'
    $sessBefore0 = @(Get-ChildItem $sessions -Recurse -File -Force -ErrorAction SilentlyContinue)
    Write-Info ("sessions before the app starts = " + $sessBefore0.Count + " files")

    Write-Head "start it, so the old copy is genuinely running"
    $offset = New-LogOffset
    $running = Start-App -Exe $installedExe
    Write-Info ("pid " + $running.Id)
    # Wait for it to be properly up, not merely started.
    $up = Wait-LogLine -Offset $offset -Pattern 'harness ready|port conflict|FAILED' -TimeoutSec 180
    $aliveBefore = [bool](Get-Process -Id $running.Id -ErrorAction SilentlyContinue)
    Write-Info ("still running before the upgrade = " + $aliveBefore)
    Assert-That $aliveBefore "the installed copy is running before the upgrade"

    # The fingerprint has to be taken HERE, not earlier: the app rewrites config.ini on
    # startup (it records lastRunVersion), and comparing against a pre-start hash only
    # measures that startup write, not the upgrade. That mistake made this test fail the
    # first time it ran.
    $cfgHashBefore = (Get-FileHash $cfg -Algorithm MD5).Hash
    $cfgTextBefore = Get-Content $cfg -Encoding UTF8 -Raw
    $sessBefore = @(Get-ChildItem $sessions -Recurse -File -Force -ErrorAction SilentlyContinue)
    $sessBytesBefore = ($sessBefore | Measure-Object Length -Sum).Sum
    Write-Info ("config.ini md5 (post-start) = " + $cfgHashBefore)
    Write-Info ("sessions = " + $sessBefore.Count + " files / " + $sessBytesBefore + " bytes")

    Write-Head "upgrade WHILE it is running (this used to fail: the exe is locked)"
    $p2 = Start-Process -FilePath $SILENT -ArgumentList @('/S', ('/D=' + $target), '/nodesktop', '/noreg') -Wait -PassThru
    Write-Info ("upgrade exit = " + $p2.ExitCode)
    Assert-That ($p2.ExitCode -eq 0) "the upgrade exited 0 despite the running instance"

    Start-Sleep -Seconds 3
    Assert-That (-not [bool](Get-Process -Id $running.Id -ErrorAction SilentlyContinue)) "the running instance was stopped for the upgrade"
    Assert-That ((Get-Item $installedExe).VersionInfo.FileVersion -eq (Get-Item $script:DistExe).VersionInfo.FileVersion) "the installed exe now matches the built one"

    $files = @(Get-ChildItem $target -Recurse -File)
    Write-Info ("files in the install directory = " + $files.Count)
    Assert-That ($files.Count -ge 8) "the program files (plus the uninstall trio) are all present"
    Assert-That (Test-Path (Join-Path $target 'ui\app.html')) "the UI page is present after the upgrade"

    Write-Head "the user's data must be untouched"
    $cfgHashAfter = (Get-FileHash $cfg -Algorithm MD5).Hash
    $cfgTextAfter = Get-Content $cfg -Encoding UTF8 -Raw
    $sessAfter = @(Get-ChildItem $sessions -Recurse -File -Force -ErrorAction SilentlyContinue)
    $sessBytesAfter = ($sessAfter | Measure-Object Length -Sum).Sum
    Write-Info ("config.ini md5 (after upgrade) = " + $cfgHashAfter)
    Write-Info ("sessions = " + $sessAfter.Count + " files / " + $sessBytesAfter + " bytes")
    Assert-That ($cfgHashAfter -eq $cfgHashBefore) "config.ini is byte-for-byte unchanged"
    # Not just the hash: the settings that matter must still be readable and equal.
    Assert-That ($cfgTextAfter -match 'port=\d+') "the configured port survived"
    Assert-That ($cfgTextAfter -match '(?s)workspace=.+') "the workspace setting survived"
    Assert-That ($cfgTextAfter.Contains($cfgTextBefore.Trim())) "the previous settings are still all present"
    Assert-That (($sessBefore.Count -eq $sessAfter.Count) -and ($sessBytesBefore -eq $sessBytesAfter)) "the session files are unchanged"

    Write-Head "and the machine's own uninstall entry must be untouched"
    # The reason /noreg is passed above, asserted rather than assumed. See AGENTS.md,
    # 铁律二十五. On a machine with no DSH-App entry both reads are the same "not found"
    # text, so the check is meaningful either way.
    $realAfter = (@(& reg.exe query $realRegKey 2>&1 | Out-String)).Trim()
    Assert-That ($realAfter -eq $realBefore) "the real Apps and features entry is byte-for-byte what it was"
} finally {
    Write-Head "cleanup"
    Stop-AppAll -Ports @($PORT)
    Assert-That (Wait-PortFree -Port $PORT -TimeoutSec 20) ("port " + $PORT + " released")
    Remove-Sandbox $sandbox
}

exit (Complete-Test -Name 'upgrade')
