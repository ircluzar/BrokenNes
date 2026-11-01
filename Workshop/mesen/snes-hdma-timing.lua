-- Mesen 2 side of Workshop --snes-timing-test (Workshop/SnesTimingTest.cs): reads the count the HDMA probe ROM stores every frame.
--
--   BrokenNes.Workshop.exe --snes-timing-test --write-roms <dir>          (writes hdma-probe-<name>.sfc)
--   Mesen.exe <dir>\hdma-probe-<name>.sfc --luaScriptsToLoad snes-hdma-timing.lua --testRunner    (env HDMA_OUT = result file)
--
-- Use a private Mesen copy with SNES RAM zeroed and frame skipping off (BrokenDev\tools\diff-snes.ps1 makes one under its work folder).
-- The script writes one line per new count for frames 21..40 ("<frame> <count>") and a last line "mean <value>": that mean is the number
-- SnesTimingTest.Probes records for the probe.
local out = assert(io.open(os.getenv("HDMA_OUT") or "hdma.txt", "w"))
local done, last, sum, n = 0, -1, 0, 0
emu.addEventCallback(function()
  done = done + 1
  local frameNo = emu.read(0x22, emu.memType.snesWorkRam)
  if done > 20 and frameNo ~= last then
    local count = emu.read(0x20, emu.memType.snesWorkRam) | (emu.read(0x21, emu.memType.snesWorkRam) << 8)
    out:write(string.format("%d %d\n", done, count))
    sum = sum + count; n = n + 1
  end
  last = frameNo
  if done >= 40 then
    out:write(string.format("mean %.1f\n", n > 0 and sum / n or -1))
    out:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
