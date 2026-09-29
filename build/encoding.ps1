# Encoding discipline for the text files that go into the product.
#   (ASCII only on purpose -- see AGENTS.md, encoding rule 1)
#
# Shared by build.ps1 and pack.ps1 so the rule has exactly one implementation and cannot
# drift into two slightly different ones.
#
# THE RULE: every source file that reaches the compiler or the browser is UTF-8 with a BOM.
#
# WHY IT IS A HARD FAILURE AND NOT A HELPFUL AUTO-FIX
#
# The dangerous case is a file that holds non-ASCII text and has no BOM. Neither tool
# involved falls back to UTF-8 in that situation, and -- this is the part that makes it
# worth a build error -- the compile still SUCCEEDS. csc only falls back to the ANSI code
# page when the bytes are not valid UTF-8; valid UTF-8 Chinese without a BOM is read as
# ANSI and silently turned into mojibake. Every user-facing string in this app is Chinese,
# so that failure mode is "the product ships with gibberish" and nothing anywhere says so.
#
# This exact trap was hit three times while writing the test suite (a Chinese literal in a
# BOM-less .ps1 was decoded as GBK and ate the closing quote), which is why it is checked
# rather than trusted.
#
# A pure-ASCII file with no BOM is harmless, so it is reported and fixed quietly. Only the
# combination that can actually corrupt text stops the build.
#
# Returns the number of files that were missing a BOM, for reporting.

# The two encodings the build scripts use throughout. Defined here rather than in each
# script so there is one place that decides "with BOM" versus "without BOM" -- and so
# dot-sourcing this file is enough to get them. (build.ps1 lost its own copy of these when
# this check replaced its normalization block, which broke the compiler response file.)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$utf8Raw = New-Object System.Text.UTF8Encoding($false)

function Get-TextFilesNeedingBom {
    param([string[]]$Directories)

    $found = @()
    foreach ($d in $Directories) {
        if (-not (Test-Path $d)) { continue }
        $found += Get-ChildItem -Path $d -Recurse -File -Include '*.cs', '*.html' -ErrorAction SilentlyContinue
    }
    return $found
}

function Invoke-EncodingCheck {
    param([Parameter(Mandatory=$true)][string[]]$Directories)

    $fixedAscii  = 0
    $fixedDanger = @()
    $unreadable  = @()

    # Strict UTF-8: throws on bytes that are not valid UTF-8, which is the whole point.
    # Reading with $utf8Raw would instead substitute U+FFFD for every bad byte -- and then
    # writing the result back turns a GBK (ANSI) or BOM-less UTF-16 file into "valid UTF-8
    # with a BOM": the Chinese text is destroyed, the file LOOKS correct to the next run, and
    # the build passes. That is the exact failure this whole file exists to prevent, caused
    # by the fixer itself. Measured 2026-09-22 while reviewing it.
    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)

    foreach ($f in (Get-TextFilesNeedingBom -Directories $Directories)) {
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        if ($hasBom) { continue }

        # Non-ASCII is what makes the missing BOM dangerous.
        $hasNonAscii = $false
        foreach ($b in $bytes) { if ($b -gt 127) { $hasNonAscii = $true; break } }

        $text = $null
        try {
            $text = $utf8Strict.GetString($bytes)
        } catch {
            # Not UTF-8 at all. Do NOT touch it: rewriting would replace every non-ASCII
            # character with U+FFFD and lose the source text for good.
            $rel = $f.FullName
            if ($rel.StartsWith($PSScriptRoot + '\..\')) {
                $rel = $rel.Substring(($PSScriptRoot + '\..\').Length)
            }
            $unreadable += $rel
            continue
        }

        if ($hasNonAscii) {
            $rel = $f.FullName
            if ($rel.StartsWith($PSScriptRoot + '\..\')) {
                $rel = $rel.Substring(($PSScriptRoot + '\..\').Length)
            }
            $fixedDanger += $rel
        } else {
            $fixedAscii++
        }

        [System.IO.File]::WriteAllText($f.FullName, $text, $utf8Bom)
    }

    if ($unreadable.Count -gt 0) {
        Write-Host ""
        Write-Host "================================================================" -ForegroundColor Red
        Write-Host " NOT VALID UTF-8: file(s) LEFT UNTOUCHED ON PURPOSE" -ForegroundColor Red
        Write-Host "================================================================" -ForegroundColor Red
        foreach ($p in $unreadable) { Write-Host ("   " + $p) -ForegroundColor Red }
        Write-Host ""
        Write-Host " These bytes are not UTF-8 (probably GBK/ANSI, or UTF-16 without a BOM)." -ForegroundColor Red
        Write-Host " Adding a BOM to them would NOT fix them -- it would decode every" -ForegroundColor Red
        Write-Host " non-ASCII character into a replacement character and write that back," -ForegroundColor Red
        Write-Host " destroying the text in the file. Nothing was written." -ForegroundColor Red
        Write-Host ""
        Write-Host " Convert the file to UTF-8 (with or without a BOM) in an editor, or" -ForegroundColor Yellow
        Write-Host " restore it from a backup, then build again." -ForegroundColor Yellow
        Write-Host "================================================================" -ForegroundColor Red
        return 2
    }

    if ($fixedAscii -gt 0) {
        Write-Host ("  " + $fixedAscii + " ASCII-only file(s) were missing a BOM; added (harmless)")
    }

    if ($fixedDanger.Count -gt 0) {
        Write-Host ""
        Write-Host "================================================================" -ForegroundColor Red
        Write-Host " ENCODING RULE VIOLATED: non-ASCII WITHOUT A UTF-8 BOM" -ForegroundColor Red
        Write-Host "================================================================" -ForegroundColor Red
        foreach ($p in $fixedDanger) { Write-Host ("   " + $p) -ForegroundColor Red }
        Write-Host ""
        Write-Host " These held non-ASCII text and no BOM, so a compiler or browser would" -ForegroundColor Red
        Write-Host " have read them as ANSI: the text becomes mojibake and NOTHING reports" -ForegroundColor Red
        Write-Host " an error. The BOM has been restored above, so this build is safe --" -ForegroundColor Red
        Write-Host " but the edit that removed it must not be repeated." -ForegroundColor Red
        Write-Host ""
        Write-Host " Usual cause: an editor or tool that drops the BOM on save." -ForegroundColor Yellow
        Write-Host " Re-run to build; nothing else is required." -ForegroundColor Yellow
        Write-Host "================================================================" -ForegroundColor Red
        return 1
    }

    return 0
}
