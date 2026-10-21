-- DEPRECATED - DO NOT USE. Superseded by ppu2007_pair.lua in this directory.
--
-- This script writes an EMPTY log and exits 0, which reads as "the ROM never writes $2007" when in
-- fact it writes thousands of times. Two defects, both measured:
--   1. It applies the power-on RAM fill from the endFrame callback, a whole frame after boot. That
--      overwrites RAM the game has already initialised and wedges the game: 5430 CPU writes and
--      ZERO $2007 writes over 30 frames, against 43039 / 4815 when the fill is applied at script
--      load, which is where state_trace.lua correctly puts it.
--   2. It applies input from endFrame too, where the pad is already latched and setInput is
--      discarded. Input must be applied on emu.eventType.inputPolled.
-- It also records only the CPU's intent, never the address and byte that actually reached the PPU
-- bus - the one thing a $2007-during-rendering investigation needs. ppu2007_pair.lua pairs each CPU
-- write with the nesPpuMemory write it produces and reports both.
--
-- Original description follows.
--
-- Logs every CPU write to $2007 together with the PPU state at that instant, so the address the
-- write LANDS on can be compared against another emulator write-for-write.
--
-- The question this exists to answer: when a game's vblank VRAM flush overruns into the visible
-- frame, $2007 collides with the render pipeline's use of the VRAM address bus. Hardware then
-- writes the address low byte instead of the CPU's data, and advances the address by a coarse-X
-- AND a Y increment rather than PPUCTRL bit 2's +1/+32. Reproducing that needs to know, per write,
-- the scanline/dot it happened on and the v it used.
--
-- Env: VRUN_TRACE_ROM, VRUN_TRACE_INPUT ("frame:Buttons,..."), VRUN_TRACE_FRAMES, VRUN_TRACE_OUT,
--      VRUN_TRACE_FROM / VRUN_TRACE_TO (frame window to log, default all).

local outPath   = os.getenv("VRUN_TRACE_OUT")   or "ppu2007_mesen.txt"
local maxFrames = tonumber(os.getenv("VRUN_TRACE_FRAMES") or "250")
local fromF     = tonumber(os.getenv("VRUN_TRACE_FROM")   or "0")
local toF       = tonumber(os.getenv("VRUN_TRACE_TO")     or "999999")
local inputSpec = os.getenv("VRUN_TRACE_INPUT") or ""

-- Same power-on RAM fill BrokenNes uses (FCEUX's repeating 00 00 00 00 FF FF FF FF), forced after
-- the 7-cycle reset so both machines start from identical memory.
local ramFilled = false
local function fillRam()
  if ramFilled then return end
  if (emu.getState()["cpu.cycleCount"] or 0) < 7 then return end
  for i = 0, 2047 do
    emu.write(i, ((i & 4) ~= 0) and 0xFF or 0x00, emu.memType.nesInternalRam)
  end
  ramFilled = true
end

local BUTTONS = { A=false, B=false, Select=false, Start=false, Up=false, Down=false, Left=false, Right=false }
local steps = {}
for step in string.gmatch(inputSpec, "[^,]+") do
  local f, btns = string.match(step, "^(%d+):(.*)$")
  if f then
    local set = {}
    for b in string.gmatch(btns or "", "[^%+]+") do set[b] = true end
    table.insert(steps, { frame = tonumber(f), set = set })
  end
end
local held = {}
for k, v in pairs(BUTTONS) do held[k] = v end

local frame = 0
local lines = {}
table.insert(lines, "# ppu2007 log (Mesen side)")
table.insert(lines, "# columns: frame|scanline|cycle|v|value|mask")

emu.addMemoryCallback(function(addr, value)
  if frame < fromF or frame > toF then return end
  local s = emu.getState()
  
  -- Mesen exposes the loopy-v as videoRamAddr; fall back to addressBus if a build renames it.
  local v = s["ppu.videoRamAddr"] or 0
  table.insert(lines, string.format("%d|%d|%d|%04x|%02x|%02x",
      frame, s["ppu.scanline"], s["ppu.cycle"], v & 0x7FFF, value & 0xFF, 0))
end, emu.callbackType.write, 0x2007, 0x2007)

emu.addEventCallback(function()
  fillRam()
  for _, s in ipairs(steps) do
    if s.frame == frame then
      held = {}
      for k in pairs(BUTTONS) do held[k] = s.set[k] and true or false end
    end
  end
  emu.setInput(held, 0)
  frame = frame + 1
  if frame >= maxFrames then
    local fh = io.open(outPath, "w")
    fh:write(table.concat(lines, "\n"), "\n")
    fh:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
