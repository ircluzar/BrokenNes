# 'instrument extract': instrument extraction for batch work. The headless Workshop's --extract-instrument runs the game (NES on the FIX cores; Game Boy, SNES, Master System, Game Gear and Genesis on their stock cores), feeds the shared
# capture core (Plugin/BrokenNes2.Plugin/Extract) every frame, picks the note of a channel at a frame and writes a .bn2preset the plugin loads. The capture is defined in
# docs/projects/flp-music-pipeline.md ("Instrument extraction"). ROMs are read in place, never copied.

function Invoke-BdInstrumentExtract {
    <#
      Args (from the verb's remaining arguments): <rom> --frame N --channel C [--out file.bn2preset] [--input "40:Start,44:"] [--name text] [--console Kind] [--apu core] [--json].
      Channels: NES / Game Boy 0-3; SNES 0-7 (the S-DSP voices); Sega 0-2 PSG tones, 3 PSG noise, 4.. FM (YM2413 with --apu SMS-FM, YM2612 4-9), 10 the Genesis DAC.
      -Frame / -Out given the PowerShell way work too. Returns the Workshop's exit code (0 written, 1 no note, 2 usage).
    #>
    param([string[]]$Rest = @(), [long]$Frame = -1, [string]$Out = '', [string]$Exe = '', [switch]$Json)
    $rom = $null; $chan = $null; $inputScript = ''; $name = ''; $console = ''; $apu = ''
    for ($i = 0; $i -lt $Rest.Count; $i++) {
        $a = [string]$Rest[$i]
        switch -Regex ($a) {
            '^--?frame$'   { $Frame = [long]$Rest[++$i]; continue }
            '^--?channel$' { $chan = [int]$Rest[++$i]; continue }
            '^--?out$'     { $Out = [string]$Rest[++$i]; continue }
            '^--?input$'   { $inputScript = [string]$Rest[++$i]; continue }
            '^--?name$'    { $name = [string]$Rest[++$i]; continue }
            '^--?json$'    { $Json = $true; continue }
            '^--?console$' { $console = [string]$Rest[++$i]; continue }
            '^--?apu$'     { $apu = [string]$Rest[++$i]; continue }
            default        { if (-not $rom) { $rom = $a } else { throw "instrument extract: unexpected argument '$a'" } }
        }
    }
    if (-not $rom -or $Frame -lt 0 -or $null -eq $chan) { throw 'usage: brokendev.ps1 instrument extract <rom> --frame N --channel C [--out file.bn2preset] [--input "40:Start,44:"] [--name text] [--console Kind] [--apu core] [--json]' }
    if (-not (Test-Path -LiteralPath $rom)) { throw "no ROM at $rom" }
    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe) { throw "the Workshop is not built: run 'brokendev.ps1 build-workshop'" }
    $a = @('--extract-instrument', '--rom', (Resolve-Path -LiteralPath $rom).Path, '--frame', "$Frame", '--channel', "$chan")
    if ($Out) { $a += @('--out', [System.IO.Path]::GetFullPath($Out)) }
    if ($inputScript) { $a += @('--input', $inputScript) }
    if ($name) { $a += @('--name', $name) }
    if ($console) { $a += @('--console', $console) }
    if ($apu) { $a += @('--apu', $apu) }
    if ($Json) { $a += '--json' }
    $r = Invoke-BdProcess -FilePath $Exe -Arguments $a -TimeoutSec 600 -Env (Get-BdChildEnv) -Quiet
    Write-Host ($r.Output.TrimEnd())
    $r.ExitCode
}
