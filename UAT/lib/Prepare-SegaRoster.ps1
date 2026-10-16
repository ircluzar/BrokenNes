<#
.SYNOPSIS
  Gathers the 20 Master System and 20 Game Gear roster games (the file names in Workshop\Sega\mesen\roster-scripts.tsv) from the user's libraries into one folder with the layout the roster suites read:
  <Out>\sms\<name>.sms and <Out>\gg\<name>.gg. Idempotent; a game the library does not have is reported, not an error.
.DESCRIPTION
  The Master System library keeps one .7z per title (the roster file is inside the archive named for its title, e.g. "Astro Warrior.7z" holds "Astro Warrior (USA).sms"); the Game Gear library holds the .gg files themselves.
  The folder is what `Workshop --smstest --roms <folder>` and UAT\sega-gate.ps1 -Roms take: the suites `swap` (real games), `rosterstate` and `golden` run on it, and `--smstest --prove` can then prove them. ROMs are never copied into
  the repository: the default folder is under %LOCALAPPDATA%\VRUN_Nes_Dev_work. Uses 7-Zip (C:\Program Files\7-Zip\7z.exe). Prints one line per missing game and a count; exit 0 always.
.EXAMPLE
  pwsh -File UAT\lib\Prepare-SegaRoster.ps1
#>
param(
    [string]$Out = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\sega_roster'),
    [string]$SmsLibrary = 'X:\EMULATION\Sega Master System',
    [string]$GgLibrary = 'X:\EMULATION\Sega Game Gear',
    [string]$List = (Join-Path $PSScriptRoot '..\..\Workshop\Sega\mesen\roster-scripts.tsv')
)
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $List)) { Write-Host "roster list not found ($List)"; exit 0 }
$names = @(Get-Content -LiteralPath $List | Where-Object { $_ -notmatch '^#' -and $_.Trim().Length -gt 0 } | ForEach-Object { ($_ -split "`t")[0] })
New-Item -ItemType Directory -Force (Join-Path $Out 'sms'), (Join-Path $Out 'gg') | Out-Null
$have = 0; $made = 0; $missing = New-Object System.Collections.Generic.List[string]
$archives = if (Test-Path -LiteralPath $SmsLibrary) { @(Get-ChildItem -LiteralPath $SmsLibrary -Filter *.7z | ForEach-Object Name) } else { @() }
foreach ($n in $names) {
    $gg = $n.EndsWith('.gg', [StringComparison]::OrdinalIgnoreCase)
    $dest = Join-Path $Out ($(if ($gg) { 'gg' } else { 'sms' }) + '\' + $n)
    if (Test-Path -LiteralPath $dest) { $have++; continue }
    if ($gg) {
        $src = Join-Path $GgLibrary $n
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src $dest; $made++ } else { $missing.Add($n) }
        continue
    }
    if (-not (Test-Path -LiteralPath $sevenZip) -or $archives.Count -eq 0) { $missing.Add($n); continue }
    $base = [IO.Path]::GetFileNameWithoutExtension($n); $title = ($base -split ' \(')[0]
    $cand = @($archives | Where-Object { $_ -eq "$base.7z" -or $_ -eq "$title.7z" -or $_ -like "$title (*" -or $_ -like "$title *.7z" })
    $done = $false
    foreach ($c in $cand) {
        $arc = Join-Path $SmsLibrary $c
        if (((& $sevenZip l -ba -- $arc 2>$null) -join "`n") -notmatch [regex]::Escape($n)) { continue }       # (the listing is an array of lines: join it, or -notmatch tests each line)
        $tmp = Join-Path $Out ('_tmp_' + [guid]::NewGuid().ToString('N'))
        & $sevenZip x -y -r "-o$tmp" $arc $n 2>&1 | Out-Null       # -r: the file may sit in a folder inside the archive
        $hit = Get-ChildItem -LiteralPath $tmp -Recurse -File -Filter $n -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { Copy-Item -LiteralPath $hit.FullName $dest; $made++; $done = $true }
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        if ($done) { break }
    }
    if (-not $done) { $missing.Add($n) }
}
foreach ($m in $missing) { Write-Host "missing  $m" }
Write-Host "roster folder $Out : $($have + $made) of $($names.Count) games ($made gathered now, $have already there, $($missing.Count) not in the libraries)"
exit 0
