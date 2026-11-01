-- ============================================================================
--  $2007 PAIRED WRITE LOG - MESEN side
-- ============================================================================
--  Purpose: capture, for every CPU write to $2007, BOTH what the CPU asked for and what the PPU
--  actually did with it. That pairing is the whole point. A $2007 write issued while the render
--  pipeline owns the VRAM address bus does not store the CPU's byte at the CPU's address - it
--  stores something else, somewhere else - and no amount of reasoning about the CPU side alone can
--  tell you which. Only the landing address and the landed byte can.
--
--  HOW THE PAIRING WORKS
--    Two callbacks fire back to back for one $2007 write:
--      1. emu.callbackType.write on the CPU bus at $2007  -> the CPU's intent, plus PPU state
--         (scanline, dot, loopy-v, mask, ctrl) sampled at the exact write cycle.
--      2. emu.callbackType.write on emu.memType.nesPpuMemory -> the address/value that reached the
--         PPU bus. Measured (ppu2007_probe.lua): in 12 frames of vrun_shoptest the two counts are
--         identical (4815 each), i.e. nothing else writes the PPU bus, so a simple "the next PPU
--         bus write belongs to the pending CPU write" pairing is exact.
--    v AFTER the write is deliberately NOT sampled. There is no callback positioned after the
--    increment, and faking one by peeking at the next CPU access would read a v the render pipeline
--    had already moved. The SEQUENCE OF LANDING ADDRESSES is the better evidence anyway: it shows
--    how v actually advanced, without depending on where a sample point happens to sit.
--
--  OUTPUT (one row per $2007 write, '|' separated, LF endings)
--    frame|scanline|cycle|vBefore|cpuValue|landAddr|landValue|mask|ctrl|rendering
--      frame      decimal, 0-based, incremented at endFrame like state_trace.lua
--      scanline   -1..260 as Mesen numbers them (-1 is the pre-render line; BrokenNes calls it 261)
--      cycle      0..340 dot within the scanline
--      vBefore    loopy-v at the write cycle, 4 hex, masked 0x7fff
--      cpuValue   the byte the CPU wrote, 2 hex
--      landAddr   PPU bus address the write reached, 4 hex (or '----' if no PPU bus write fired,
--                 which is itself a finding - it would mean the write was swallowed)
--      landValue  byte that reached the PPU bus, 2 hex (or '--')
--      mask       PPUMASK reconstructed from the flag fields, 2 hex
--      ctrl       PPUCTRL reconstructed from the flag fields, 2 hex
--      rendering  1 if (bg or sprites) enabled at the write cycle, else 0
--
--  ENV
--    P7_ROM_INPUT   input script "frame:Buttons+Buttons,..." (same contract as state_trace.lua)
--    P7_FRAMES      total frames to run (default 250)
--    P7_FROM/P7_TO  frame window to LOG (default: everything). Logging every write of a 900 frame
--                   run is ~350k rows, so the window matters for the big runs.
--    P7_OUT         output path
--    P7_ONLY_GLITCH "1" = only log writes taken while rendering is enabled. The interesting set is
--                   a tiny fraction of all writes; this keeps the file readable.
--    P7_RAMFILL     "fceux" (default) applies the same power-on RAM pattern BrokenNes uses.
-- ============================================================================

local OUT        = os.getenv("P7_OUT")    or "ppu2007_mesen.txt"
local MAXF       = tonumber(os.getenv("P7_FRAMES") or "250")
local FROM       = tonumber(os.getenv("P7_FROM")   or "0")
local TO         = tonumber(os.getenv("P7_TO")     or "99999999")
local ONLY_GLITCH= (os.getenv("P7_ONLY_GLITCH") or "0") == "1"
local RAMFILL    = os.getenv("P7_RAMFILL") or "fceux"
local INPUT      = os.getenv("P7_ROM_INPUT") or ""

-- ---------------------------------------------------------------- power-on RAM
-- BrokenNes fills work RAM with FCEUX's repeating 00 00 00 00 FF FF FF FF at power-on. Mesen
-- defaults to all zeros, so without this the two machines diverge before the first instruction on
-- any game that reads uninitialised RAM (this one does).
--
-- THIS MUST HAPPEN AT SCRIPT LOAD, NOT IN A FRAME CALLBACK. Mesen loads the script right after the
-- 6502's 7-cycle reset, so top-level code here still is power-on. Deferring it to the first
-- endFrame - which is what the earlier ppu2007_log.lua did - overwrites 2 KB of RAM the game has
-- already initialised, and vrun_shoptest then wedges: measured 5430 CPU writes and ZERO $2007
-- writes over 30 frames, against 43039 / 4815 when the fill happens at the right time. That, and
-- not a broken callback registration, is why the old script captured nothing.
local bootCycle = emu.getState()["cpu.cycleCount"] or 0
if RAMFILL == "fceux" then
  if bootCycle > 16 then
    error("script started at cpu.cycleCount=" .. tostring(bootCycle) ..
          "; too late for a power-on RAM fill")
  end
  for i = 0, 2047 do
    emu.write(i, ((i & 4) ~= 0) and 0xFF or 0x00, emu.memType.nesInternalRam)
  end
end
-- Palette RAM too: BrokenNes' --trace defaults to --power-on-palette zeros, and without matching it
-- every pixel differs until the game writes its own palette.
if (os.getenv("P7_PALFILL") or "zeros") == "zeros" then
  for i = 0, 31 do emu.write(i, 0x00, emu.memType.nesPaletteRam) end
end

-- ---------------------------------------------------------------- input script
local BUTTONS = { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" }
local steps = {}
for step in string.gmatch(INPUT, "[^,]+") do
  local f, btns = string.match(step, "^(%d+):(.*)$")
  if f then
    local set = {}
    for b in string.gmatch(btns or "", "[^%+]+") do set[b] = true end
    steps[#steps + 1] = { frame = tonumber(f), set = set }
  end
end
local held = {}
for _, b in ipairs(BUTTONS) do held[b] = false end

-- ---------------------------------------------------------------- state helpers
-- Mesen exposes PPUCTRL/PPUMASK only as individual boolean fields, never as the register byte, so
-- rebuild the bytes here. Getting these back into register form is what lets the row diff
-- digit-for-digit against BrokenNes' log, which logs the real registers.
local function maskByte(s)
  local m = 0
  if s["ppu.mask.grayscale"]      then m = m | 0x01 end
  if s["ppu.mask.backgroundMask"] then m = m | 0x02 end
  if s["ppu.mask.spriteMask"]     then m = m | 0x04 end
  if s["ppu.mask.backgroundEnabled"] then m = m | 0x08 end
  if s["ppu.mask.spritesEnabled"]    then m = m | 0x10 end
  if s["ppu.mask.intensifyRed"]   then m = m | 0x20 end
  if s["ppu.mask.intensifyGreen"] then m = m | 0x40 end
  if s["ppu.mask.intensifyBlue"]  then m = m | 0x80 end
  return m
end
local function ctrlByte(s)
  local c = 0
  -- Nametable select lives in t bits 10-11, not in a control field; recover it from t so the
  -- logged ctrl byte matches the register the game actually wrote.
  c = c | ((s["ppu.tmpVideoRamAddr"] >> 10) & 0x03)
  if s["ppu.control.verticalWrite"]        then c = c | 0x04 end
  if s["ppu.control.spritePatternAddr"]     and s["ppu.control.spritePatternAddr"] ~= 0     then c = c | 0x08 end
  if s["ppu.control.backgroundPatternAddr"] and s["ppu.control.backgroundPatternAddr"] ~= 0 then c = c | 0x10 end
  if s["ppu.control.largeSprites"]         then c = c | 0x20 end
  if s["ppu.control.nmiOnVerticalBlank"]   then c = c | 0x80 end
  return c
end

-- ---------------------------------------------------------------- the log
local frame = 0
local rows = {}
local pending = nil      -- CPU-side half of a $2007 write, waiting for its PPU bus write
local nWrites, nLanded = 0, 0

local function flushPending()
  -- Called when a CPU $2007 write produced no PPU bus write at all, or at end of run.
  if pending == nil then return end
  rows[#rows + 1] = pending.prefix .. "|----|--|" .. pending.suffix
  pending = nil
end

emu.addMemoryCallback(function(addr, value)
  local s = emu.getState()
  local sl = s["ppu.scanline"]
  local cy = s["ppu.cycle"]
  local v  = (s["ppu.videoRamAddr"] or 0) & 0x7FFF
  local m  = maskByte(s)
  local c  = ctrlByte(s)
  local rendering = ((m & 0x18) ~= 0) and 1 or 0
  nWrites = nWrites + 1

  flushPending()   -- previous write never reached the bus; emit it as such

  if frame < FROM or frame > TO then return end
  if ONLY_GLITCH and rendering == 0 then return end

  pending = {
    prefix = string.format("%d|%d|%d|%04x|%02x", frame, sl, cy, v, value & 0xFF),
    suffix = string.format("%02x|%02x|%d", m, c, rendering),
  }
end, emu.callbackType.write, 0x2007, 0x2007, emu.cpuType.nes, emu.memType.nesMemory)

emu.addMemoryCallback(function(addr, value)
  if pending == nil then return end
  nLanded = nLanded + 1
  rows[#rows + 1] = pending.prefix ..
    string.format("|%04x|%02x|", addr & 0x3FFF, value & 0xFF) .. pending.suffix
  pending = nil
end, emu.callbackType.write, 0x0000, 0x3FFF, emu.cpuType.nes, emu.memType.nesPpuMemory)

-- Input MUST be applied on inputPolled, not endFrame. Measured: with setInput called from the
-- endFrame handler the pad is already latched for the frame and the write is discarded - Mesen ran
-- the shop ROM's whole 900 frame script and never saw Start, which showed up as "Mesen makes no
-- $2007 writes after frame 11" and would have been read as a real behavioural difference. Applying
-- it here reproduces state_trace.lua's semantics exactly, including the "latest step at or before
-- this frame" rule (<=, not ==) so a step never gets skipped.
local stepIdx = 0
emu.addEventCallback(function()
  while stepIdx < #steps and steps[stepIdx + 1].frame <= frame do
    stepIdx = stepIdx + 1
    held = {}
    for _, b in ipairs(BUTTONS) do held[b] = steps[stepIdx].set[b] and true or false end
  end
  emu.setInput(held, 0)
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
  frame = frame + 1
  if frame >= MAXF then
    flushPending()
    local fh = io.open(OUT, "w")
    fh:write("# ppu2007 paired log (Mesen)\n")
    fh:write("# frame|scanline|cycle|vBefore|cpuValue|landAddr|landValue|mask|ctrl|rendering\n")
    fh:write(string.format("# totals: cpu2007writes=%d ppuBusLanded=%d loggedRows=%d frames=%d\n",
      nWrites, nLanded, #rows, frame))
    fh:write(table.concat(rows, "\n"), "\n")
    fh:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
