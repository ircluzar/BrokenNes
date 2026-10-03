<#
.SYNOPSIS
  Fetches the oracles, test sets and port sources the Sega work uses, into a folder OUTSIDE the repository, and records where every file came from.
.DESCRIPTION
  The manifest below is the list the owner approved on 2026-10-03 (Tier P0 and Tier T1 of docs/projects/sega-cores-plan.md, verified sizes from the
  research manifest). It is the only thing this script can fetch. Re-running it skips what is already there and verifies it.
    P0  what the Phase 0 gate needs (Z80 vectors, z80test, zexdoc/zexall, SN76489 TestRom, emu76489 reference, SMSTestSuite, a durable Mesen 2.1.1)
    T1  what the tracks need when they start (BlastEm, the 68000 vector sets, port sources, Nuked sources, VDP/FM/Genesis test ROMs)
  Rules it keeps: nothing is written inside the repository; GPL / LGPL / non-commercial material is oracle-only and never committed; everything with a license that
  is not permissive stays local. Each destination folder gets a SOURCE.txt (URL, version, license, size, SHA-256, date), and MANIFEST.json lists them all.
  Mesen 2.1.1 is COPIED from an existing local install (no download).
.EXAMPLE
  pwsh -File Workshop\Sega\fetch-tools.ps1                  # everything approved (P0 + T1)
  pwsh -File Workshop\Sega\fetch-tools.ps1 -Tier P0         # just the Phase 0 gate
  pwsh -File Workshop\Sega\fetch-tools.ps1 -VerifyOnly      # check what is on disk against MANIFEST.json, download nothing
#>
param(
    [string]$Root = 'C:\BrokenNes-tools\sega',
    [ValidateSet('P0', 'T1', 'All')][string]$Tier = 'All',
    [switch]$VerifyOnly,
    [string]$MesenSource = 'C:\Users\philt\OneDrive\Documents\PROJECTS\sys0\simulations\VRUN\tools\mesen'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($Root.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to write inside the repository: $Root" }

# kind: zip (download, extract to dest, strip the top folder) | file (download one file) | copy (local files) | sparse (git sparse checkout)
$items = @(
    # ---------------- Tier P0 ----------------
    @{ Id = 'z80-vectors'; Tier = 'P0'; Kind = 'zip'; Url = 'https://codeload.github.com/SingleStepTests/z80/zip/ebe1875d48f374bcfd4b505d8eb8ee751568b5f7'; Dest = 'test-sets\singlesteptests-z80'; Strip = 1;
       Version = 'v1.0-beta.2 (ebe1875, 2026-04-10)'; License = 'MIT'; Local = 'local only (1.4 GB)'; Expect = 'about 1.375 GB extracted: v1/*.json'; Min = 150MB }
    @{ Id = 'z80test'; Tier = 'P0'; Kind = 'zip'; Url = 'https://github.com/raxoft/z80test/releases/download/v1.2a/z80test-1.2a.zip'; Dest = 'test-sets\z80test-1.2a'; Strip = 0;
       Version = 'v1.2a (2023-12-02)'; License = 'MIT (Copyright 2012-2023 Patrik Rak)'; Local = 'could be committed later with LICENSE + SOURCE.txt'; Expect = '61,595 bytes'; Min = 50KB }
    @{ Id = 'zexdoc'; Tier = 'P0'; Kind = 'file'; Url = 'https://raw.githubusercontent.com/agn453/ZEXALL/main/zexdoc.com'; Dest = 'test-sets\zexall\zexdoc.com'; Version = 'agn453/ZEXALL main (pin 8f71d418bae6, 2025-06-15)'; License = 'GPL-2.0 (F. D. Cringle, 1994)'; Local = 'LOCAL ONLY, never committed'; Expect = '8,704 bytes'; Min = 8KB }
    @{ Id = 'zexall'; Tier = 'P0'; Kind = 'file'; Url = 'https://raw.githubusercontent.com/agn453/ZEXALL/main/zexall.com'; Dest = 'test-sets\zexall\zexall.com'; Version = 'agn453/ZEXALL main (pin 8f71d418bae6, 2025-06-15)'; License = 'GPL-2.0 (F. D. Cringle, 1994)'; Local = 'LOCAL ONLY, never committed'; Expect = '8,704 bytes'; Min = 8KB }
    @{ Id = 'zexall-sms'; Tier = 'P0'; Kind = 'zip'; Url = 'https://github.com/maxim-zhao/zexall-sms/releases/download/v0.21/ZEXALL-SMS-0.21.zip'; Dest = 'test-sets\zexall-sms-0.21'; Strip = 0; Version = 'v0.21 (2025-10-02)'; License = 'GPL-2.0'; Local = 'LOCAL ONLY, never committed'; Expect = '52,991 bytes'; Min = 40KB }
    @{ Id = 'sn76489-testrom'; Tier = 'P0'; Kind = 'zip'; Url = 'https://www.smspower.org/uploads/Homebrew/SN76489TestRom-SMS-1.00.zip'; Dest = 'test-sets\sms-roms\sn76489-testrom-1.00'; Strip = 0; Version = 'v1.00 (2024-03-27)'; License = 'MIT per github.com/JoppyFurr/SN76489-TestRom (smspower page: not stated)'; Local = 'local (committing is an owner decision)'; Expect = '121,965 bytes'; Min = 100KB }
    @{ Id = 'sms-test-suite'; Tier = 'P0'; Kind = 'file'; Url = 'https://github.com/sverx/SMSTestSuite/releases/download/v0.38/SMSTestSuite.sms'; Dest = 'test-sets\sms-roms\SMSTestSuite-0.38.sms'; Version = 'v0.38 (2026-08-31, 985bc04)'; License = 'none found'; Local = 'LOCAL ONLY'; Expect = '32,768 bytes'; Min = 30KB }
    @{ Id = 'emu76489-c'; Tier = 'P0'; Kind = 'file'; Url = 'https://raw.githubusercontent.com/digital-sound-antiques/emu76489/main/emu76489.c'; Dest = 'oracles\psg-ref\emu76489\emu76489.c'; Version = 'main (pin c0fa09706, 2026-07-08)'; License = 'MIT (Copyright 2001-2016 Okazaki)'; Local = 'oracle only: reference render, not linked into BrokenNes'; Expect = '5,760 bytes'; Min = 5KB }
    @{ Id = 'emu76489-h'; Tier = 'P0'; Kind = 'file'; Url = 'https://raw.githubusercontent.com/digital-sound-antiques/emu76489/main/emu76489.h'; Dest = 'oracles\psg-ref\emu76489\emu76489.h'; Version = 'main (pin c0fa09706, 2026-07-08)'; License = 'MIT (Copyright 2001-2016 Okazaki)'; Local = 'oracle only'; Expect = '1,118 bytes'; Min = 1KB }
    @{ Id = 'mesen-2.1.1'; Tier = 'P0'; Kind = 'copy'; Url = $MesenSource; Dest = 'oracles\mesen-2.1.1'; Files = @('Mesen.exe', 'MesenCore.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'MesenNesDB.txt');
       Version = '2.1.1 (tag 137ae7c, 2025-07-06; the repository is archived, successor MesenCE)'; License = 'GPL-3.0'; Local = 'oracle only, never committed'; Expect = 'about 85 MB copied, not downloaded'; Min = 50MB }

    # ---------------- Tier T1 ----------------
    @{ Id = 'blastem'; Tier = 'T1'; Kind = 'zip'; Url = 'https://www.retrodev.com/blastem/blastem-win64-1.0.0.zip'; Dest = 'oracles\blastem-1.0.0-win64'; Strip = 0; Version = '1.0.0 (2026-09-11)'; License = 'GPL-3'; Local = 'oracle only'; Expect = '3,038,866 bytes'; Min = 2MB }
    @{ Id = 'sst-m68000'; Tier = 'T1'; Kind = 'zip'; Url = 'https://codeload.github.com/SingleStepTests/m68000/zip/refs/heads/main'; Dest = 'test-sets\singlesteptests-m68000'; Strip = 1; Version = 'main (last push 2024-08-01)'; License = 'MIT'; Local = 'local only (138 MB extracted)'; Expect = 'a 51 MB archive, 138 MB extracted: v1/*.json.bin + decode.py'; Min = 40MB }
    @{ Id = 'sst-680x0'; Tier = 'T1'; Kind = 'zip'; Url = 'https://codeload.github.com/SingleStepTests/680x0/zip/refs/heads/main'; Dest = 'test-sets\singlesteptests-680x0'; Strip = 1; Version = 'main (last push 2024-05-14)'; License = 'none found'; Local = 'LOCAL ONLY'; Expect = 'about 204 MB (68000/v1/*.json.gz)'; Min = 150MB }
    @{ Id = 'ymfm'; Tier = 'T1'; Kind = 'zip'; Url = 'https://github.com/aaronsgiles/ymfm/archive/81aec25ccbb9.zip'; Dest = 'port-sources\ymfm'; Strip = 1; Version = '81aec25 (2026-07-27)'; License = 'BSD-3-Clause (Copyright 2021 Aaron Giles)'; Local = 'port source: a port needs its notice in the ledger'; Expect = 'about 150-200 KB zip'; Min = 100KB }
    @{ Id = 'emu2413'; Tier = 'T1'; Kind = 'zip'; Url = 'https://github.com/digital-sound-antiques/emu2413/archive/refs/tags/1.6.2.zip'; Dest = 'port-sources\emu2413'; Strip = 1; Version = '1.6.2 (2e4f6f9)'; License = 'MIT (Copyright 2001-2019 Okazaki)'; Local = 'port source'; Expect = 'about 60 KB tree'; Min = 20KB }
    @{ Id = 'musashi'; Tier = 'T1'; Kind = 'zip'; Url = 'https://github.com/kstenerud/Musashi/archive/313ebf1bd9f4.zip'; Dest = 'port-sources\musashi'; Strip = 1; Version = '313ebf1 (2026-03-08)'; License = 'MIT (license text in readme.txt)'; Local = 'port source (fallback 68000)'; Expect = 'about 1.3 MB tree (example/ is symlinks, which Windows cannot create and the port does not need)'; Min = 200KB; TolerateSymlinks = $true }
    @{ Id = 'ares-sparse'; Tier = 'T1'; Kind = 'sparse'; Url = 'https://github.com/ares-emulator/ares'; Dest = 'port-sources\ares-sparse'; Paths = @('ares/component/processor/m68000', 'ares/component/processor/z80', 'ares/component/audio/sn76489', 'ares/component/audio/ym2612', 'ares/component/audio/ym2413', 'ares/md/vdp', 'ares/ms/vdp');
       Version = 'master (4cb8d92 at manifest time; the resolved commit is recorded)'; License = 'ISC (ares team; bundled third-party notices in LICENSE)'; Local = 'port source (md/ms vdp: read-only reference per the plan, we write the VDPs clean-room)'; Expect = 'about 0.3-0.4 MB'; Min = 100KB }
    @{ Id = 'nuked-opn2'; Tier = 'T1'; Kind = 'zip'; Url = 'https://codeload.github.com/nukeykt/Nuked-OPN2/zip/refs/heads/master'; Dest = 'oracles\nuked\opn2'; Strip = 1; Version = 'master (335747d, 2023-08-10)'; License = 'LGPL-2.1'; Local = 'ORACLE ONLY: built outside the repo, never copied'; Expect = 'about 0.9 MB'; Min = 100KB }
    @{ Id = 'nuked-opll'; Tier = 'T1'; Kind = 'zip'; Url = 'https://codeload.github.com/nukeykt/Nuked-OPLL/zip/refs/heads/master'; Dest = 'oracles\nuked\opll'; Strip = 1; Version = 'master (1269cf5, 2023-01-19)'; License = 'GPL-2.0'; Local = 'ORACLE ONLY'; Expect = 'about 18 KB archive: opll.c, opll.h, LICENSE'; Min = 10KB }
    @{ Id = 'nuked-psg'; Tier = 'T1'; Kind = 'zip'; Url = 'https://codeload.github.com/nukeykt/Nuked-PSG/zip/refs/heads/master'; Dest = 'oracles\nuked\psg'; Strip = 1; Version = 'master (d15a168, 2023-04-15)'; License = 'GPL-2.0'; Local = 'ORACLE ONLY'; Expect = 'about 36 KB'; Min = 10KB }
    @{ Id = 'nuked-md'; Tier = 'T1'; Kind = 'zip'; Url = 'https://codeload.github.com/nukeykt/Nuked-MD/zip/refs/heads/main'; Dest = 'oracles\nuked\md'; Strip = 1; Version = 'main (594ca2a, 2026-07-11)'; License = 'GPL-2.0'; Local = 'ORACLE ONLY (gate-level, ~300x slower than hardware)'; Expect = 'about 1.9 MB'; Min = 500KB }
    @{ Id = 'sms-vdp-test'; Tier = 'T1'; Kind = 'zip'; Url = 'https://www.smspower.org/uploads/Homebrew/SMSVDPTest-SMS-1.31.zip'; Dest = 'test-sets\sms-roms\smsvdptest-1.31'; Strip = 0; Version = 'v1.31 (2009-11-04)'; License = 'none stated'; Local = 'LOCAL ONLY'; Expect = '33,456 bytes'; Min = 30KB }
    @{ Id = 'ym2413-testrom'; Tier = 'T1'; Kind = 'zip'; Url = 'https://www.smspower.org/uploads/Homebrew/YM2413TestRom-SMS-1.00.zip'; Dest = 'test-sets\sms-roms\ym2413-testrom-1.00'; Strip = 0; Version = 'v1.00 (2024-03-27)'; License = 'MIT per github.com/JoppyFurr/YM2413-TestRom (smspower page: not stated)'; Local = 'local'; Expect = '122,540 bytes'; Min = 100KB }
)

# Genesis test ROMs (Exodus techdocs list, Google Drive ids; sizes are Content-Length from HEAD requests; license: none stated for all)
$drive = @(
    @('19vQuo8diG5OQMD5ythejtJByFzOwIl4i', 'VDPFIFOTesting.zip', 13121), @('1nHZoGNWpAuSbgJVxkE0y6xst3d8wgApD', 'VDPFIFOTesting-src.zip', 212882),
    @('14qAO4_EKKN2bcumExkv-RgTcl9H_Auzq', 'SpriteMaskingTestRom.zip', 3628), @('1xfZ5TwrpE--bHoLtEoPXjRu_PxrdWG0v', 'SpriteMaskingTestRom-src.zip', 4420),
    @('1-FZqLceTxBnzJv8AR4bBTAUZqS_pyjT-', 'cram-flicker.zip', 3151), @('1OULXRZwJd11D4Y5vVtEX11EugzuUELqf', 'vctest.zip', 2499),
    @('1nDITsbkmo3BYRve6NX35Aqt7jo7FWtqP', 'window-distortion-bug.zip', 1573), @('10lrPUQq9gvBVooOZM75jeuu49Crsn7C1', 'wbug-src.zip', 5701),
    @('1NKagoNVmUEZB9DojKcmXSsSlJY96xS8-', 'itest.zip', 21406), @('10Nmad6V4rKJYNLdyd3jdfhERld6K53k9', 'itest-src.zip', 52463),
    @('1btAFafip50yCpAV74R6hh0QOjN0XBv-L', 'memtest_68k.zip', 2056), @('169ZUeEMrfYFLiKz1jpb53zJQJg1Wba00', 'window-test-by-fonzie.zip', 3547),
    @('1YdzJFkB4IrMxCwFfmunIb_hIHPvNZA_Y', 'Direct-Color-DMA.zip', 76002), @('1Zz_-_s4p54XLnQk_G6dwLenDzq29RFXP', 'dmacolor7-src.zip', 676866),
    @('1EgwX-T4g9bUcsGc6FywO_-4Irxjo1Vgn', 'bcd-verifier-u1.zip', 37685), @('1Ks5qpVXaEqphEJgT7PkAcMXrfsymlzVo', 'bcd-verifier-src.zip', 120006),
    @('1VGTj6ZTLS7eVzqXuiCXmkdBBVnZ6KfJQ', 'm68k_opcode_sizes.zip', 1367), @('1e_UipO5OY40cMHhw5dAR1QFcR2uxkEA3', 'smd_emu_tests-src.zip', 36356),
    @('1RAHRinL6gWFgD-RmmxAZJAc1UCcAqOHU', 'titan-overdrive2.zip', 4010970))
foreach ($d in $drive) {
    $items += @{ Id = 'md-' + [IO.Path]::GetFileNameWithoutExtension($d[1]); Tier = 'T1'; Kind = 'file'; Url = "https://drive.usercontent.google.com/download?id=$($d[0])&export=download"; Dest = "test-sets\md-roms\$($d[1])";
                 Version = 'Exodus techdocs list (Google Drive, unversioned)'; License = 'none stated'; Local = 'LOCAL ONLY'; Expect = "$($d[2]) bytes"; Min = [int]($d[2] * 0.9); Exact = $d[2] }
}
$items += @{ Id = 'md-titan-overdrive1'; Tier = 'T1'; Kind = 'file'; Url = 'https://archive.org/download/demo_titan_overdrive_rev1.1-106-final/Titan_Overdrive_Rev1.1-106-Final.bin'; Dest = 'test-sets\md-roms\Titan_Overdrive_Rev1.1-106-Final.bin';
             Version = 'Rev 1.1 (Evoke 2013), archive.org'; License = 'none stated'; Local = 'LOCAL ONLY'; Expect = '3,946,282 bytes'; Min = 3500000; Exact = 3946282 }

$manifestPath = Join-Path $Root 'MANIFEST.json'
$done = [ordered]@{}
if (Test-Path $manifestPath) { (Get-Content $manifestPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $done[$_.Name] = $_.Value } }

function Sha256($p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }
function Download($url, $out) {
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) { & curl.exe -L --fail --silent --show-error --retry 3 --retry-delay 3 -o $out $url; if ($LASTEXITCODE -ne 0) { throw "curl failed ($LASTEXITCODE): $url" } }
    else { Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing }
}
function LooksLikeHtml($p) { $b = [IO.File]::ReadAllBytes($p) | Select-Object -First 64; ([Text.Encoding]::ASCII.GetString($b)) -match '^\s*<(!DOCTYPE|html|\?xml)' }

$plan = $items | Where-Object { $Tier -eq 'All' -or $_.Tier -eq $Tier }
$results = New-Object System.Collections.Generic.List[object]
foreach ($it in $plan) {
    $dest = Join-Path $Root $it.Dest
    $status = ''; $bytes = 0; $hash = ''
    try {
        if ($VerifyOnly) {
            $rec = $done[$it.Id]
            if (-not $rec) { $status = 'NOT FETCHED' }
            else {
                $probe = if ($it.Kind -eq 'file') { $dest } else { Join-Path $Root ('_archives\' + $it.Id + '.zip') }
                if ($it.Kind -in 'copy', 'sparse') { $status = if (Test-Path $dest) { 'present' } else { 'MISSING' } }
                elseif (-not (Test-Path $probe)) { $status = 'archive gone (extracted copy only)' }
                else { $status = if ((Sha256 $probe) -eq $rec.sha256) { 'verified' } else { 'HASH MISMATCH' } }
            }
        }
        elseif ($done.Contains($it.Id) -and (Test-Path $dest)) { $status = 'already fetched' }
        else {
            switch ($it.Kind) {
                'file' {
                    Download $it.Url $dest
                    if (LooksLikeHtml $dest) { Remove-Item -LiteralPath $dest -Force; throw 'the server returned a web page (a confirmation or error page), not the file' }
                    $bytes = (Get-Item $dest).Length; $hash = Sha256 $dest
                    if ($it.Exact -and $bytes -ne $it.Exact) { throw "size $bytes, expected $($it.Exact)" }
                }
                'zip' {
                    $zip = Join-Path $Root ('_archives\' + $it.Id + '.zip')
                    Download $it.Url $zip
                    $bytes = (Get-Item $zip).Length; $hash = Sha256 $zip
                    if ($bytes -lt $it.Min) { throw "archive is only $bytes bytes (expected at least $($it.Min))" }
                    New-Item -ItemType Directory -Force $dest | Out-Null
                    $targs = @('-xf', $zip, '-C', $dest); if ($it.Strip -gt 0) { $targs += "--strip-components=$($it.Strip)" }
                    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; & tar.exe @targs 2>&1 | Out-Null; $ErrorActionPreference = $eap; if ($LASTEXITCODE -ne 0 -and -not ($it.TolerateSymlinks -and (Get-ChildItem $dest -Recurse -File | Measure-Object).Count -gt 20)) { throw "tar failed ($LASTEXITCODE)" }
                }
                'copy' {
                    New-Item -ItemType Directory -Force $dest | Out-Null
                    foreach ($f in $it.Files) { Copy-Item -LiteralPath (Join-Path $it.Url $f) -Destination (Join-Path $dest $f) -Force }
                    $bytes = ($it.Files | ForEach-Object { (Get-Item (Join-Path $dest $_)).Length } | Measure-Object -Sum).Sum
                    $hash = (Sha256 (Join-Path $dest 'MesenCore.dll'))
                }
                'sparse' {
                    if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
                    & git clone --depth 1 --filter=blob:none --sparse $it.Url $dest 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
                    & git -C $dest sparse-checkout set @($it.Paths) 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'sparse-checkout failed' }
                    $hash = (& git -C $dest rev-parse HEAD).Trim()
                    $bytes = (Get-ChildItem $dest -Recurse -File -Exclude '*.git*' | Where-Object { $_.FullName -notmatch '\\\.git\\' } | Measure-Object Length -Sum).Sum
                }
            }
            $srcDir = if ($it.Kind -eq 'file') { Split-Path $dest } else { $dest }
            $line = "{0}`n  url:     {1}`n  version: {2}`n  license: {3}`n  policy:  {4}`n  size:    {5:N0} bytes (expected {6})`n  sha256:  {7}`n  fetched: {8:yyyy-MM-dd HH:mm}`n" -f $it.Id, $it.Url, $it.Version, $it.License, $it.Local, $bytes, $it.Expect, $hash, (Get-Date)
            Add-Content -LiteralPath (Join-Path $srcDir 'SOURCE.txt') -Value $line
            $done[$it.Id] = [pscustomobject]@{ tier = $it.Tier; url = $it.Url; dest = $it.Dest; bytes = $bytes; sha256 = $hash; license = $it.License; version = $it.Version; fetched = (Get-Date).ToString('s') }
            $status = 'fetched'
        }
    }
    catch { $status = 'FAILED: ' + $_.Exception.Message }
    $results.Add([pscustomobject]@{ Id = $it.Id; Tier = $it.Tier; Status = $status; Bytes = $bytes })
    "{0,-26} {1,-3} {2,14:N0}  {3}" -f $it.Id, $it.Tier, $bytes, $status
}
if (-not $VerifyOnly) {
    New-Item -ItemType Directory -Force $Root | Out-Null
    ($done | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $manifestPath
}
$failed = @($results | Where-Object { $_.Status -like 'FAILED*' -or $_.Status -in 'HASH MISMATCH', 'MISSING' })
"`n{0} items, {1} problems. Root: {2}" -f $results.Count, $failed.Count, $Root
if ($failed.Count) { exit 1 }
