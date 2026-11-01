# build the Workshop into bn_sega_b2a; prints errors only; exit 0 on success
$wt = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path   # the repository (or worktree) this folder sits in
$art = if ($env:BN_ARTIFACTS) { $env:BN_ARTIFACTS } else { "$env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_sega_b2a" }
$out = & dotnet build "$wt\Workshop\BrokenNes.Workshop.csproj" -c Release --artifacts-path $art 2>&1 | Out-String
$code = $LASTEXITCODE
($out -split "`n" | Where-Object { $_ -match ": error " } | Sort-Object -Unique | Select-Object -First 25) -join "`n"
"build exit $code"
