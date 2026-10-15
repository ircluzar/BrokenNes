<#
.SYNOPSIS
  Unpacks the Master System games the UAT cases name from the user's library (one game per .7z in X:\EMULATION\Sega Master System) into
  %LOCALAPPDATA%\VRUN_Nes_Dev_work\sega_roms\sms under stable file names. Idempotent; a game that is not in the library is reported, not an error.
.DESCRIPTION
  The Game Gear (.gg) and Genesis (.bin) libraries are plain files and the cases read them where they are; only the Master System's archives need unpacking
  (the suite's Workshop reads zips, not 7z). ROMs are never copied into the repository: the folder is outside it, and each case SKIPs when its ROM is not there.
  Uses 7-Zip (C:\Program Files\7-Zip\7z.exe). Prints one line per game; exit 0 always (a missing game shows up as a SKIP in the suite, which is the honest outcome).
.EXAMPLE
  pwsh -File UAT\lib\Prepare-SegaRoms.ps1
#>
param(
    [string]$Library = 'X:\EMULATION\Sega Master System',
    [string]$Out = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\sega_roms\sms')
)
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path $sevenZip)) { Write-Host "7-Zip not found ($sevenZip): the Master System cases will SKIP"; exit 0 }
if (-not (Test-Path $Library)) { Write-Host "library not found ($Library): the Master System cases will SKIP"; exit 0 }
New-Item -ItemType Directory -Force $Out | Out-Null

# stable name, archive, the file inside to take (a pattern: the archives hold several dumps of some games)
$games = @(
    @{ Name = 'alexkidd.sms';     Archive = 'Alex Kidd in Miracle World.7z';  Inner = '*(USA, Europe) (v1.1)*.sms' },
    @{ Name = 'sonic.sms';        Archive = 'Sonic The Hedgehog (USA, Europe).7z'; Inner = '*.sms' },
    @{ Name = 'fantasyzone.sms';  Archive = 'Fantasy Zone.7z';                Inner = '*(World) (v1.2)*.sms' },
    @{ Name = 'psychofox.sms';    Archive = 'Psycho Fox.7z';                  Inner = '*.sms' }
)
foreach ($g in $games) {
    $dest = Join-Path $Out $g.Name
    if (Test-Path $dest) { Write-Host "have     $($g.Name)"; continue }
    $arc = Join-Path $Library $g.Archive
    if (-not (Test-Path -LiteralPath $arc)) { Write-Host "missing  $($g.Name)  (no $($g.Archive) in the library)"; continue }
    $tmp = Join-Path $Out ('_tmp_' + [guid]::NewGuid().ToString('N'))
    & $sevenZip x -y "-o$tmp" -- $arc | Out-Null
    $hit = Get-ChildItem -LiteralPath $tmp -Recurse -File -Filter $g.Inner | Select-Object -First 1
    if (-not $hit) { $hit = Get-ChildItem -LiteralPath $tmp -Recurse -File -Include *.sms | Select-Object -First 1 }
    if ($hit) { Copy-Item -LiteralPath $hit.FullName $dest; Write-Host "unpacked $($g.Name)  <- $($hit.Name)" } else { Write-Host "missing  $($g.Name)  (nothing matching $($g.Inner) inside)" }
    Remove-Item -LiteralPath $tmp -Recurse -Force
}
exit 0
