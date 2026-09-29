# The uninstaller must remove what it installed, and nothing else.
#
# Two things are checked here, and the second one is the reason this test exists:
#
#   1. It removes the program files (and, when the folder is then empty, the folder).
#   2. A file that was ALREADY in the install folder when the product was installed is
#      still there afterwards.
#
# (2) used to fail. The uninstaller deleted the entire folder it found itself in, with
# "rmdir /s /q". For the normal case -- a dedicated folder -- that is correct and even
# desirable. It stops being correct the moment someone unpacks the portable build into a
# folder that already holds their own files: the installer writes its marker into that
# folder, and uninstalling then destroys everything in it. That is data loss, and no test
# covered it because every test installed into an empty sandbox.
#
# The install folder is a TEMP sandbox and nothing outside it is touched: the data and
# .dsh directories are redirected with the uninstaller's own test hooks, and so are the two
# shortcuts and the "Apps & features" registry entry. Without those three hooks this test
# deletes the developer's own desktop shortcut and their real uninstall entry -- which is
# exactly what happened the first time it was run, and why the hooks exist.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\t-uninstall-safety.ps1

. "$PSScriptRoot\_lib.ps1"

$SILENT = Join-Path $script:RepoRoot 'dist\package\DSH-App-standalone.exe'

Write-Head "uninstaller: removes its own files, keeps the user's"

if (-not (Test-Path $SILENT)) {
    Write-Info "dist\package\DSH-App-standalone.exe missing -- run build\pack.ps1 first"
    exit 1
}

$sandbox = Join-Path $script:RepoRoot '.cache\t-uninstall-safety'
# Deliberately NOT under %TEMP%, unlike every other test's sandbox: the uninstaller refuses
# to run when it finds itself inside a temp directory (a sensible guard against a
# self-extracting copy deleting a scratch folder), so an install there cannot be
# uninstalled at all. That refusal is tested separately by the exit code seen here.
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
$target  = Join-Path $sandbox 'shared folder'  # ALSO holds user files, and has a space in its
                                               # name on purpose: the cleanup helper is handed
                                               # this path as a cmd.exe argument, so a quoting
                                               # mistake here would show up as files left behind.
$fakeData = Join-Path $sandbox 'appdata'
$fakeDsh  = Join-Path $sandbox 'dshhome'
$fakeDesktop = Join-Path $sandbox 'desktop\DSH-App.lnk'
$fakeStartMenu = Join-Path $sandbox 'startmenu\DSH-App.lnk'
New-Item -ItemType Directory -Path $target, $fakeData, $fakeDsh -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $fakeDesktop -Parent), (Split-Path $fakeStartMenu -Parent) -Force | Out-Null
Set-Content -Path $fakeDesktop -Value 'pretend shortcut' -Encoding ASCII
Set-Content -Path $fakeStartMenu -Value 'pretend shortcut' -Encoding ASCII

# The uninstaller's registry key, redirected to a throwaway one. The real key is captured
# first so the test can prove it came out unchanged.
$realRegKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App'
$testRegKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App-Test'
$testRegSub = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App-Test'
$testRegPsPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-App-Test'
$realBefore = (@(& reg.exe query $realRegKey 2>&1 | Out-String)).Trim()
& reg.exe delete $testRegKey /f 2>&1 | Out-Null

# User files, created BEFORE installing, at both depths: one beside the program files and
# one in a subfolder that the product also uses (ui).
$keep1 = Join-Path $target 'my-notes.txt'
$keep2 = Join-Path $target 'important-project\data.bin'
$keep3 = Join-Path $target 'ui\my-custom-page.html'
New-Item -ItemType Directory -Path (Split-Path $keep2 -Parent) -Force | Out-Null
Set-Content -Path $keep1 -Value 'do not delete me' -Encoding ASCII
Set-Content -Path $keep2 -Value 'nor me' -Encoding ASCII

try {
    Write-Head "install into a folder that already holds the user's files"
    # /noreg keeps this install out of the machine's real "Apps & features" list.
    #
    # The arguments are ONE pre-quoted string, not an array. PowerShell 5.1's -ArgumentList
    # joins an array with spaces and does NOT quote the elements, so "/D=<dir with space>"
    # was silently split into two arguments and the install landed in a folder named "dir"
    # (the first word only), while the installer still reported success. Measured here.
    $p = Start-Process -FilePath $SILENT -ArgumentList ('/S /D="' + $target + '" /nodesktop /noreg') -Wait -PassThru
    Write-Info ("installer exit = " + $p.ExitCode)
    Assert-That ($p.ExitCode -eq 0) "the installer exited 0"
    Assert-That (Test-Path (Join-Path $target 'DSH-App.exe')) "the program landed in that folder"
    Assert-That (Test-Path (Join-Path $target 'install.marker')) "so did the install marker"

    # The pre-existing page is written after the install, so the product overwriting
    # same-named files is not part of this test; only our own extra file is.
    Set-Content -Path $keep3 -Value 'a page of my own' -Encoding ASCII

    Write-Head "the manifest must list what was installed, and only that"
    $manifest = Join-Path $target 'install.files'
    Assert-That (Test-Path $manifest) "an install manifest was written"
    $listed = @()
    if (Test-Path $manifest) {
        $listed = @(Get-Content $manifest -Encoding UTF8 |
                    Where-Object { $_.Trim().Length -gt 0 -and -not $_.Trim().StartsWith('#') })
        Write-Info ("manifest lists " + $listed.Count + " file(s)")
        Assert-That ($listed -contains 'DSH-App.exe') "it names the program"
        Assert-That ($listed -contains 'Uninstaller.exe') "it names the uninstaller"
        # The user's own files must NOT be in it, or they would be deleted.
        Assert-That (($listed | Where-Object { $_ -like '*my-notes.txt*' }).Count -eq 0) "it does not name the user's file"
        Assert-That (($listed | Where-Object { $_ -like '*important-project*' }).Count -eq 0) "nor the user's folder"
        Assert-That (($listed | Where-Object { $_ -like '*my-custom-page*' }).Count -eq 0) "nor the user's page"
    }

    Write-Head "uninstall (silent), everything outside the install folder redirected"
    # The sandbox registry entry stands in for the one the installer would have written.
    # Existence is asked of the registry provider, not parsed out of reg.exe's output: a
    # missing key makes reg.exe write "ERROR: ..." to stderr, and PowerShell turns that into
    # an error record whose text starts with "reg.exe :", so a "starts with ERROR" test
    # silently never matches. That mistake is the only reason this file has a Test-Path.
    & reg.exe add $testRegKey /v DisplayName /t REG_SZ /d 'DSH-App' /f 2>&1 | Out-Null
    Assert-That (Test-Path $testRegPsPath) "the sandbox Apps and features entry exists before uninstalling"

    $un = Join-Path $target 'Uninstaller.exe'
    Assert-That (Test-Path $un) "the uninstaller is present"

    Write-Head "the location overrides must be refused without the test-hook variable"
    # The shipped uninstaller must not be usable as "recursively delete the directory I name".
    # The overrides therefore only work when DSH_APP_TEST_HOOKS=1 is in the environment, and a
    # hook without it is refused (exit 4) rather than ignored -- ignoring it would point a
    # forgetful test at the REAL profile directories.
    if (Test-Path $un) {
        $refused = Start-Process -FilePath $un `
                -ArgumentList @('/S', ('/data=' + $fakeData), ('/dshhome=' + $fakeDsh)) `
                -Wait -PassThru
        Write-Info ("exit code without the hook variable = " + $refused.ExitCode)
        Assert-That ($refused.ExitCode -eq 4) "it refused the override instead of acting on it"
        Assert-That (Test-Path (Join-Path $target 'DSH-App.exe')) "and deleted nothing (the program is still there)"
        Assert-That (Test-Path (Join-Path $target 'install.files')) "and the manifest is still there too"
        Assert-That (Test-Path $keep1) "and the user's file is untouched"
    }

    if (Test-Path $un) {
        $env:DSH_APP_TEST_HOOKS = '1'   # inherited by the child; the only thing that unlocks them
        $u = Start-Process -FilePath $un `
                -ArgumentList @('/S', ('/data=' + $fakeData), ('/dshhome=' + $fakeDsh),
                                ('/desktop=' + $fakeDesktop), ('/startmenu=' + $fakeStartMenu),
                                ('/regkey=' + $testRegSub)) `
                -Wait -PassThru
        Remove-Item Env:\DSH_APP_TEST_HOOKS -ErrorAction SilentlyContinue
        Write-Info ("uninstaller exit = " + $u.ExitCode)
        Assert-That ($u.ExitCode -eq 0) "the uninstaller exited 0"
    }

    Write-Head "wait for the detached cleanup helper to finish"
    # The uninstaller cannot delete the folder it is running from, so it hands that to a
    # detached cmd script. That script starts with a two-second wait, so "the program files
    # are gone" happens well before "everything listed is gone" -- wait for the second one,
    # otherwise the assertions below race the helper.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path (Join-Path $target 'install.files'))) { break }
        Start-Sleep -Milliseconds 500
    }
    Assert-That (-not (Test-Path (Join-Path $target 'DSH-App.exe'))) "the program files were removed"
    Assert-That (-not (Test-Path (Join-Path $target 'Uninstaller.exe'))) "including the uninstaller itself"

    Write-Head "the user's files must still be there"
    Assert-That (Test-Path $keep1) ("the user's own file survived: " + (Split-Path $keep1 -Leaf))
    Assert-That (Test-Path $keep2) ("the user's own folder survived: important-project\data.bin")
    Assert-That (Test-Path $keep3) ("the user's page in ui\ survived: my-custom-page.html")

    if (Test-Path $keep1) {
        $text = (Get-Content $keep1 -Raw).Trim()
        Assert-That ($text -eq 'do not delete me') "and its contents are untouched"
    }
    if (Test-Path $keep2) {
        Assert-That ((Get-Content $keep2 -Raw).Trim() -eq 'nor me') "and so are the folder's contents"
    }

    Write-Head "what else is left behind"
    $left = @(Get-ChildItem $target -Recurse -Force -ErrorAction SilentlyContinue)
    Write-Info ("still in the folder: " + $left.Count + " item(s)")
    foreach ($i in $left) { Write-Info ("  " + $i.FullName.Substring($target.Length).TrimStart('\')) }

    # Nothing else should remain: the program files are gone, so the leftovers are exactly
    # the user's three files (plus their folders).
    Assert-That (($left | Where-Object { -not $_.PSIsContainer }).Count -eq 3) "only the user's 3 files remain"
    Assert-That (-not (Test-Path (Join-Path $target 'install.marker'))) "the install marker was removed"
    Assert-That (-not (Test-Path (Join-Path $target 'install.files'))) "and so was the manifest"

    Write-Head "nothing outside the install folder"
    Assert-That (-not (Test-Path $fakeDesktop)) "the shortcut it was pointed at was removed"
    Assert-That (-not (Test-Path $fakeStartMenu)) "and so was the Start menu one"
    Assert-That (-not (Test-Path $testRegPsPath)) "and so was the Apps and features entry"
    $realAfter = (@(& reg.exe query $realRegKey 2>&1 | Out-String)).Trim()
    Assert-That ($realAfter -eq $realBefore) "the machine's own entry was never touched"
} finally {
    Write-Head "cleanup"
    Get-Process -Name 'DSH-App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    & reg.exe delete $testRegKey /f 2>&1 | Out-Null
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

exit (Complete-Test -Name 'uninstall-safety')
