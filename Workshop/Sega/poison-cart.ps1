[CmdletBinding()]
param([string]$Artifacts = "$env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_sega_b3b_poison")
# The proof that --md-cart-test can fail: each case breaks one rule in the source, rebuilds, runs the test, counts the failing checks and puts the source back (the file is restored from a backup even if
# something throws). Run from the repository root on a tree whose cartridge sources are committed or at least saved: a copy is the only record of the original while a case runs.
$ErrorActionPreference = 'Stop'
$md = "Windows\NesEmulator\md"
$cases = @(
  @{ name = "a stopped 68000 never samples the interrupt level again"; file = "$md\BOARD_MD.cs"; old = "if (Cpu.Stopped) iplSample = Vdp.IplAt(t);"; new = "/* poisoned */" },
  @{ name = "the 24Cxx page write does not wrap inside its page"; file = "$md\MdEeprom.cs"; old = "a = a & ~(pageSize - 1) | a + 1 & pageSize - 1;"; new = "a = a + 1;" },
  @{ name = "a read device byte is not acknowledged"; file = "$md\MdEeprom.cs"; old = "if (rd) Acknowledge(St.Read);"; new = "if (rd) { chipSda = true; state = St.Idle; }" },
  @{ name = "Acclaim's SCL is read as SDA (wiring swapped)"; file = "$md\MdCartridge.cs"; old = "case MdEepromWiring.Acclaim when address == 0x200000: e.Drive(e.MasterSda, (value & 1) != 0); return true;"; new = "case MdEepromWiring.Acclaim when address == 0x200000: e.Drive((value & 1) != 0, e.MasterScl); return true;" },
  @{ name = "SRAM outside the ROM is not mapped at power-on"; file = "$md\MdCartridge.cs"; old = "SramMapped = !LockedOn && (start & ~1u) >= (uint)rom.Length;"; new = "SramMapped = !LockedOn && rom.Length <= 0x200000;" },
  @{ name = "SRAM addresses past the declared range are ROM instead of mirrors"; file = "$md\MdCartridge.cs"; old = "if (address < (start & ~1u)) return -1;"; new = "if (address < (start & ~1u) || HasSramHeader && address > (SramEnd | 1u)) return -1;" },
  @{ name = "a headerless cartridge's RAM is never mapped by a write"; file = "$md\MdCartridge.cs"; old = "if (SramHeaderless && !SramMapped && (address & 1) != 0 && address >= 0x200000 && address <= 0x3FFFFF) SramMapped = true;"; new = "/* poisoned */" },
  @{ name = "Japan + Europe counts as overseas"; file = "$md\MdCartridge.cs"; old = "return u || e && !j;"; new = "return u || e;" },
  @{ name = "the 93C46 programs without a write enable"; file = "$md\MdMicrowire.cs"; old = "pendingProgram = writeEnabled;"; new = "pendingProgram = true;" },
  @{ name = "the 93C46 stores its words little-endian"; file = "$md\MdMicrowire.cs"; old = "private int Word(int a) => Data[a * 2] << 8 | Data[a * 2 + 1];"; new = "private int Word(int a) => Data[a * 2 + 1] << 8 | Data[a * 2];" },
  @{ name = "the 93C46 takes a seven-bit address"; file = "$md\MdMicrowire.cs"; old = "if (++addrBits == (op == 0 ? 4 : 6)) AddressDone();"; new = "if (++addrBits == (op == 0 ? 4 : 7)) AddressDone();" }
)
Set-Location (Split-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path)))
function Build { $log = dotnet build Workshop\BrokenNes.Workshop.csproj -c Release --artifacts-path $Artifacts 2>&1 | Out-String; if ($LASTEXITCODE -ne 0) { throw "build failed: " + (($log -split "`n" | Where-Object { $_ -match ' error ' } | Select-Object -First 3) -join " | ") } }
function Run { $exe = "$Artifacts\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"; $o = Join-Path $env:TEMP ("poison_" + [guid]::NewGuid().ToString("N") + ".txt"); $p = Start-Process $exe -ArgumentList "--md-cart-test" -RedirectStandardOutput $o -NoNewWindow -PassThru; if (-not $p.WaitForExit(180000)) { $p.Kill() }; $t = Get-Content $o; Remove-Item $o; $t }
Build
$base = Run
"baseline: " + ($base | Select-Object -Last 1)
foreach ($c in $cases) {
  $f = $c.file; $backup = Get-Content $f -Raw
  try {
    if (-not $backup.Contains($c.old)) { "case '$($c.name)': the text to poison is not in $f (the source moved on): update the case"; continue }
    Set-Content $f $backup.Replace($c.old, $c.new) -NoNewline
    Build
    $out = Run
    $failing = @($out | Where-Object { $_ -match '^\s+FAIL' })
    "{0,-70} -> {1} failing: {2}" -f $c.name, $failing.Count, (($failing | Select-Object -First 2 | ForEach-Object { $_.Trim().Substring(0, [Math]::Min(70, $_.Trim().Length)) }) -join " ; ")
  }
  finally { Set-Content $f $backup -NoNewline }
}
Build
"restored: " + ((Run) | Select-Object -Last 1)
