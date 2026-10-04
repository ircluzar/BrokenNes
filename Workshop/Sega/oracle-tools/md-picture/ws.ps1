[CmdletBinding(PositionalBinding=$false)] param([string]$OutFile = "", [int]$TimeoutSec = 300, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
# Run the b2a Workshop exe with arguments (quoted properly), capture stdout to a file (or print it), kill on timeout.
$art = if ($env:BN_ARTIFACTS) { $env:BN_ARTIFACTS } else { "$env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_sega_b2a" }   # the folder build.ps1 builds into
$exe = "$art\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"
$q = ($Rest | ForEach-Object { if ($_ -match '[\s\[\]]' -and $_ -notmatch '^".*"$') { '"' + $_ + '"' } else { $_ } }) -join ' '
$o = if ($OutFile) { $OutFile } else { Join-Path $env:TEMP ("ws_" + [guid]::NewGuid().ToString("N") + ".txt") }
$p = Start-Process $exe -ArgumentList $q -RedirectStandardOutput $o -RedirectStandardError "$o.err" -NoNewWindow -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); "TIMEOUT after $TimeoutSec s" }
"exit " + $p.ExitCode
if (-not $OutFile) { Get-Content $o; if ((Get-Item "$o.err").Length -gt 0) { "--stderr"; Get-Content "$o.err" }; Remove-Item $o, "$o.err" -ErrorAction SilentlyContinue }
