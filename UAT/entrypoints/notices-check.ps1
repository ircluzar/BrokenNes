<#
.SYNOPSIS
  Checks that the third-party notices are accurate for what is in the tree and that they ship with every binary.
.DESCRIPTION
  Two kinds of check; each prints [PASS]/[FAIL]/[SKIP] and the script ends with ONE JSON result line (the shape the other entrypoint scripts use).

  LEDGER (always): every source file in the tree that carries another project's copyright header (BizHawk, ares, ymfm, emu2413) is named by the root ledger
  (THIRD_PARTY_NOTICES.md) or a console family's ledger; the license text of each of those projects is in the root ledger (section 6); no row still says "planned"
  for code that exists; every ledger the root points to exists.

  SHIPPED (when a folder is given): -AppDir (a built or published desktop folder), -LiteDir (a published Lite folder, containing wwwroot), -PluginDir (an
  installed plugin folder next to BrokenNes2_x64.dll, or the fake FL Plugins folder of install-smoke) each hold the notices (root ledger, the three family
  ledgers, LICENSE.txt) and the root ledger is the repository's current one (byte for byte). A folder not given is SKIPPED, not passed.
.EXAMPLE
  pwsh -File UAT\entrypoints\notices-check.ps1
  pwsh -File UAT\entrypoints\notices-check.ps1 -AppDir C:\x\publish -LiteDir C:\y\lite_out -PluginDir "C:\Program Files\Image-Line\FL Studio 2026\Plugins\Fruity\Generators\BrokenNes2"
#>
param(
    [string]$AppDir,
    [string]$LiteDir,
    [string]$PluginDir
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
function Check($name, [bool]$ok, $detail = '') {
    $checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { "  ($detail)" } else { '' }))
}
function Skip($name, $why) { Write-Host "[SKIP] $name  ($why)" }

$root = Join-Path $repo 'THIRD_PARTY_NOTICES.md'
$ledgers = @{
    gb   = Join-Path $repo 'Windows\NesEmulator\gb\THIRD_PARTY_NOTICES.md'
    snes = Join-Path $repo 'Windows\NesEmulator\snes\THIRD_PARTY_NOTICES.md'
    sega = Join-Path $repo 'Windows\NesEmulator\sega\THIRD_PARTY_NOTICES.md'
}
$rootText = Get-Content $root -Raw
$allText = $rootText + ($ledgers.Values | ForEach-Object { Get-Content $_ -Raw }) -join "`n"

# ---- ledger ------------------------------------------------------------------------------------------------------------
Check 'root ledger and the three family ledgers exist' ((Test-Path $root) -and (($ledgers.Values | Where-Object { -not (Test-Path $_) }).Count -eq 0))
Check 'LICENSE.txt exists' (Test-Path (Join-Path $repo 'LICENSE.txt'))
foreach ($lic in @(@{ n = 'BizHawk MIT'; p = 'MIT License, Copyright \(c\) BizHawk team' }, @{ n = 'ares ISC'; p = 'ISC License, Copyright \(c\) 2004-2025 ares team' },
        @{ n = 'ymfm BSD-3'; p = 'BSD 3-Clause License, Copyright \(c\) 2021, Aaron Giles' }, @{ n = 'emu2413 MIT'; p = 'MIT License, Copyright \(c\) 2001-2019 Mitsutaka Okazaki' })) {
    Check "the root ledger carries the $($lic.n) license text" ($rootText -match $lic.p)
}
Check 'no ledger row still says "planned" for code that exists (Z80, YM2412/YM2413, YM2612)' (-not ($allText -match '\| planned \((SMS|Genesis) track\)') -and -not ($rootText -match 'Z80 and others, \*\*planned\*\*'))

# every file with another project's copyright header is named by a ledger
$owners = @('BizHawk', 'ares team', 'Aaron Giles', 'Okazaki', 'Musashi', 'Nuked')
$files = Get-ChildItem (Join-Path $repo 'Windows\NesEmulator'), (Join-Path $repo 'Plugin'), (Join-Path $repo 'Core') -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
$unnamed = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $head = (Get-Content $f.FullName -TotalCount 14) -join "`n"
    if ($head -notmatch 'Copyright \(c\)') { continue }
    if (-not ($owners | Where-Object { $head -match [regex]::Escape($_) })) { continue }
    $name = $f.Name; $stem = [IO.Path]::GetFileNameWithoutExtension($f.Name) -replace '\..*$', ''
    $dir = Split-Path $f.DirectoryName -Leaf
    if (-not ($allText.Contains($name) -or $allText.Contains($stem) -or $allText.Contains("$dir/") -or $allText.Contains("$dir\"))) { [void]$unnamed.Add($f.FullName.Substring($repo.Length + 1)) }
}
Check 'every source file that carries another project''s copyright header is named by a ledger' ($unnamed.Count -eq 0) $(if ($unnamed.Count) { $unnamed -join ', ' } else { "$($files.Count) files scanned" })

# ---- what ships --------------------------------------------------------------------------------------------------------
$needed = @('THIRD_PARTY_NOTICES.md', 'THIRD_PARTY_NOTICES-gb.md', 'THIRD_PARTY_NOTICES-snes.md', 'THIRD_PARTY_NOTICES-sega.md', 'LICENSE.txt')
function Shipped($label, $dir) {
    if (-not $dir) { Skip "$label holds the notices" 'no folder given'; return }
    if (-not (Test-Path $dir)) { Check "$label holds the notices" $false "folder not found: $dir"; return }
    $missing = $needed | Where-Object { -not (Test-Path (Join-Path $dir $_)) -or (Get-Item (Join-Path $dir $_)).Length -eq 0 }
    Check "$label holds the notices" ($missing.Count -eq 0) $(if ($missing) { "missing or empty: $($missing -join ', ')" } else { $dir })
    if (-not $missing) {
        $same = (Get-FileHash (Join-Path $dir 'THIRD_PARTY_NOTICES.md')).Hash -eq (Get-FileHash $root).Hash
        Check "$label root ledger is the repository's current one" $same
    }
}
Shipped 'desktop app' $AppDir
if ($LiteDir) { $w = Join-Path $LiteDir 'wwwroot'; Shipped 'Lite site' $(if (Test-Path $w) { $w } else { $LiteDir }) } else { Skip 'Lite site holds the notices' 'no folder given' }
Shipped 'FL plugin' $PluginDir

$bad = @($checks.GetEnumerator() | Where-Object { -not $_.Value.ok })
$summary = [ordered]@{ entrypoint = 'notices'; pass = ($bad.Count -eq 0); checks = $checks; notes = 'ledger scan + shipped-folder checks; a folder not given is skipped, not passed'; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
$summary | ConvertTo-Json -Compress -Depth 5
exit $(if ($bad.Count) { 1 } else { 0 })
