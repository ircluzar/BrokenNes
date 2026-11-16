# Builds DHRY16.EXE (8086 code) and DHRY386.EXE (386 code, 16-bit real mode, small model) from the netlib Dhrystone 2.1 source with Open Watcom.
# The source was unpacked from the dhry-c shar file by unshar.py (python -I; the shar's shell text is never run).
# Two adaptations, both as on other 16-bit DOS ports: <sys/times.h> comes from .\inc (times() = the BIOS tick counter, HZ 18.2065), and the
# run counter is long (Number_Of_Runs, Run_Index, the scanf/printf), because a 16-bit int caps it at 32767 runs = well under the
# 120-tick minimum measurement on any 386-class machine. The patched copy goes to $Out\src; the downloaded files are not changed.
param([string]$Src = "C:\BrokenNes-tools\x86\tests\dhrystone\src", [string]$Out = "C:\BrokenNes-tools\x86\tests\dhrystone\build")
. C:\BrokenNes-tools\dos\env.ps1
New-Item -ItemType Directory -Force "$Out\src" | Out-Null
Copy-Item "$Src\dhry.h", "$Src\dhry_2.c" "$Out\src\"
$c = Get-Content -Raw "$Src\dhry_1.c"
$c = $c.Replace("REG   int             Run_Index;", "REG   long            Run_Index;").Replace("REG   int             Number_Of_Runs;", "REG   long            Number_Of_Runs;")
$c = $c.Replace("    int n;`n    scanf (`"%d`", &n);", "    long n;`n    scanf (`"%ld`", &n);").Replace("printf (`"Execution starts, %d runs", "printf (`"Execution starts, %ld runs")
[IO.File]::WriteAllText("$Out\src\dhry_1.c", $c)
$inc = Join-Path $PSScriptRoot "inc"
foreach ($v in @(@{n="DHRY16"; c="-0"}, @{n="DHRY386"; c="-3"})) {
  Push-Location $Out
  & wcl -q -bt=dos -ms $($v.c) -ox -DTIMES "-DHZ=18.2065" "-i=$inc" "-fe=$($v.n).EXE" "$Out\src\dhry_1.c" "$Out\src\dhry_2.c" 2>&1 | Select-String -NotMatch "Warning"
  Pop-Location
}
