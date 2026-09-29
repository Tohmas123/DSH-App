# Old-Node guard: an outdated Node must be reported, not worked around.
#
# The app must recognise a too-old Node, say so, and NOT try to start the service on it.
# This script proves that by putting a fake node.exe (reporting v18.20.4) first on PATH and
# watching what the launcher does.
#
# It is a standalone dev probe, NOT part of test\run-all.ps1 (the suite has no equivalent
# because a fake Node cannot be swapped in from inside a test that also needs the real one).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\test-oldnode.ps1
#
# ASCII only on purpose: PowerShell 5.1 decodes a .ps1 without a BOM as ANSI, which turns
# Chinese text in a script into mojibake and can eat a quote (see AGENTS.md, rule 21).

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sandbox  = Join-Path $repoRoot '.cache\vercheck'
$exe      = Join-Path $repoRoot 'dist\DSH-App\DSH-App.exe'

if (-not (Test-Path $exe)) {
    Write-Host "dist\DSH-App\DSH-App.exe is missing -- run build\build.ps1 first"
    exit 1
}

if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
foreach ($d in @('path', 'pf\nodejs', 'pfx86\nodejs', 'lad')) {
    New-Item -ItemType Directory -Path (Join-Path $sandbox $d) -Force | Out-Null
}

# Stub that reports an old version. A real executable, so the app's probe accepts it.
$cs = Join-Path $sandbox 'stub.cs'
@'
public class Stub {
  public static int Main(string[] a) {
    if (a.Length > 0 && a[0] == "--version") { System.Console.WriteLine("v18.20.4"); return 0; }
    System.Console.Error.WriteLine("stub: not a real node");
    return 1;
  }
}
'@ | Set-Content -Path $cs -Encoding ASCII
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe "/out:$(Join-Path $sandbox 'path\node.exe')" $cs | Out-Null
if (-not (Test-Path (Join-Path $sandbox 'path\node.exe'))) {
    Write-Host "could not build the stub node.exe"
    exit 1
}
Write-Host "stub node.exe (reports v18.20.4) built"

# Where the app actually writes its log.
#
# NOT the sandbox. The app resolves its data directory with
# Environment.GetFolderPath(SpecialFolder.LocalApplicationData), which reads the SHELL FOLDER
# from the registry and IGNORES the LOCALAPPDATA environment variable -- so redirecting that
# variable for the child (which this script tried first) moves nothing, and the log stays
# here. That is also why the assertions below only look at the part of the log written by
# THIS run: the file is shared with every other run of the app and already contains
# "starting harness" lines from history.
$log = Join-Path $env:LOCALAPPDATA 'DSH-App\logs\app.log'

$offset = 0
if (Test-Path $log) { $offset = (Get-Item $log).Length }

Get-Process -Name 'DSH-App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
foreach ($p in 3099, 31700) {
    $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "=== start with a too-old node on PATH ==="
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $exe -Parent
$psi.EnvironmentVariables['PATH'] = (Join-Path $sandbox 'path')
$psi.EnvironmentVariables['ProgramFiles'] = (Join-Path $sandbox 'pf')
$psi.EnvironmentVariables['ProgramFiles(x86)'] = (Join-Path $sandbox 'pfx86')
$psi.EnvironmentVariables['ProgramW6432'] = (Join-Path $sandbox 'pf')
# Optional: uncomment to inspect the window with DevTools while it sits on the guard page.
# $psi.EnvironmentVariables['DSH_APP_DEBUG_PORT'] = '9333'
$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host "  pid = $($proc.Id)"
Start-Sleep -Seconds 15

# Only what this run appended, decoded from the recorded byte offset.
$new = ''
if (Test-Path $log) {
    $fs = New-Object System.IO.FileStream($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $len = $fs.Length
        if ($len -gt $offset) {
            $fs.Seek($offset, [System.IO.SeekOrigin]::Begin) | Out-Null
            $buf = New-Object byte[] ($len - $offset)
            $fs.Read($buf, 0, $buf.Length) | Out-Null
            $new = [System.Text.Encoding]::UTF8.GetString($buf)
        }
    } finally { $fs.Close() }
}

Write-Host ""
Write-Host "=== app.log, this run only ==="
($new -split "`r?`n") | Where-Object { $_ -match 'node|check|starting harness' } | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "=== assertions ==="
$a1 = $new -match 'older than the required major'
$a2 = $new -match 'v18\.20\.4'
$a3 = -not ($new -match 'starting harness')
$ok = $a1 -and $a2 -and $a3
Write-Host ("  detected the old node            : {0}" -f $a1)
Write-Host ("  ...and named its version         : {0}" -f $a2)
Write-Host ("  did NOT try to start the service : {0}" -f $a3)

Write-Host ""
Write-Host "=== cleanup ==="
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
foreach ($p in 3099, 31700) {
    $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue }
}
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  done"

if ($ok) { Write-Host "RESULT: PASS" } else { Write-Host "RESULT: FAIL" }
exit $(if ($ok) { 0 } else { 1 })
