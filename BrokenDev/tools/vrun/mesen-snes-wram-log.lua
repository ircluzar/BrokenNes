-- Mesen 2 (SNES): log one work-RAM byte after every completed frame, to compare a game's own counters with BrokenNes
-- (Workshop --snesrun --rom X --frames N --dump-wram <hexaddr>:1 prints the same byte after N frames).
--   ENV  WL_OUT  output file ("<frame> <hex byte>" per line)
--        WL_ADDR work-RAM address in hex (Nested games keep NES RAM at $0000-$07FF: Nested VRUN's map_slot is the .mlb's R:0396)
--        WL_TO   last frame (default 300)
-- Used in docs/projects/vrun-resume.md for the enhanced build's title-start lag frame (the map slot rotates once per title frame).
local out = assert(io.open(os.getenv("WL_OUT"), "w"))
local addr = tonumber(os.getenv("WL_ADDR"), 16)
local to = tonumber(os.getenv("WL_TO") or "300")
local done = 0
emu.addEventCallback(function()
  done = done + 1
  out:write(string.format("%d %02X\n", done, emu.read(addr, emu.memType.snesWorkRam)))
  if done >= to then out:close(); emu.stop(0) end
end, emu.eventType.endFrame)
