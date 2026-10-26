<#
.SYNOPSIS
  Writes a copy of a source file with every debug-port call taken out (BrokenDev milestone D7): the "build without any debug calls" that a
  release build must be byte-identical to.
.DESCRIPTION
  Removes every line that calls the debug-port library (NESFab dbg_*, C DBG_*, the WLA-DX / ca65 DBG_* macros, the library's #include /
  .include), then any block left empty by it: a NESFab "if" whose indented body is gone, a C "if (...) {" ... "}" whose body is gone.
  It is a text tool for the proof, not a refactoring tool: a call spread over several lines must keep its continuation lines indented.
#>
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $Source)
$call = '^\s*(dbg_\w+\s*\(|DBG_\w+\s*\(|DBG_\w+\s*;|\s*DBG_\w+\s*$|#\s*include\s*[<"](brokendev_dbg|dbgport)|\.include\s+"(brokendev_dbg|dbgport))'
$out = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -notmatch $call) { $out.Add($lines[$i]) } }
# NESFab: an if / else whose body is now empty (the next non-blank line is not indented deeper)
function Indent([string]$s) { ($s -replace '^(\s*).*$', '$1').Length }
$changed = $true
while ($changed) {
    $changed = $false
    for ($i = 0; $i -lt $out.Count; $i++) {
        if ($out[$i] -match '^\s*(if |else\b|elif )' -and $out[$i] -notmatch '[{;]\s*$' -and $Source -like '*.fab') {
            $j = $i + 1
            while ($j -lt $out.Count -and $out[$j].Trim() -eq '') { $j++ }
            if ($j -ge $out.Count -or (Indent $out[$j]) -le (Indent $out[$i])) { $out.RemoveAt($i); $changed = $true; break }
        }
        # C: "if (...) {" followed directly by "}"
        if ($i + 1 -lt $out.Count -and $out[$i] -match '^\s*if\s*\(.*\)\s*\{\s*$' -and $out[$i + 1] -match '^\s*\}\s*$') { $out.RemoveAt($i + 1); $out.RemoveAt($i); $changed = $true; break }
    }
}
[System.IO.File]::WriteAllLines($Destination, $out)
