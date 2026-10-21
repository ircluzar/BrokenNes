-- DIAGNOSTIC ONLY. Mesen's ntbhash changes at frame 60 of the shop ROM, but a callback on CPU
-- writes to $2007 sees nothing after frame 11. One of the two observations must be incomplete.
-- This watches CIRAM itself, plus the WHOLE PPU register window rather than the bare $2007
-- address, so a write reaching PPUDATA through one of its $2000-$3FFF mirrors cannot hide.
local OUT   = os.getenv("PROBE_OUT")    or "ciram_write_probe.txt"
local MAXF  = tonumber(os.getenv("PROBE_FRAMES") or "300")
local INPUT = os.getenv("PROBE_INPUT")  or ""

for i = 0, 2047 do emu.write(i, ((i & 4) ~= 0) and 0xFF or 0x00, emu.memType.nesInternalRam) end
for i = 0, 31   do emu.write(i, 0x00, emu.memType.nesPaletteRam) end

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

local frame = 0
local ntPerFrame, regPerFrame = {}, {}
local regAddrs = {}      -- which CPU addresses in $2000-$3FFF the game actually uses

emu.addMemoryCallback(function(addr, value)
  ntPerFrame[frame] = (ntPerFrame[frame] or 0) + 1
end, emu.callbackType.write, 0x0000, 0x07FF, emu.cpuType.nes, emu.memType.nesNametableRam)

emu.addMemoryCallback(function(addr, value)
  -- Bucket by the register the mirror resolves to, and remember the raw address seen.
  local reg = addr & 0x0007
  regPerFrame[frame] = regPerFrame[frame] or {}
  regPerFrame[frame][reg] = (regPerFrame[frame][reg] or 0) + 1
  regAddrs[addr] = (regAddrs[addr] or 0) + 1
end, emu.callbackType.write, 0x2000, 0x3FFF, emu.cpuType.nes, emu.memType.nesMemory)

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
  if frame < MAXF then return end
  local f = io.open(OUT, "w")
  f:write("== CIRAM writes per frame (only frames with any) ==\n")
  local ks = {}
  for k in pairs(ntPerFrame) do ks[#ks + 1] = k end
  table.sort(ks)
  for _, k in ipairs(ks) do f:write(string.format("  f%d: %d\n", k, ntPerFrame[k])) end

  f:write("\n== PPU register writes per frame, bucketed by addr&7 (only frames with any) ==\n")
  ks = {}
  for k in pairs(regPerFrame) do ks[#ks + 1] = k end
  table.sort(ks)
  for _, k in ipairs(ks) do
    local parts = {}
    for r = 0, 7 do
      if regPerFrame[k][r] then parts[#parts + 1] = string.format("$200%d=%d", r, regPerFrame[k][r]) end
    end
    f:write(string.format("  f%d: %s\n", k, table.concat(parts, " ")))
  end

  f:write("\n== distinct CPU addresses used in $2000-$3FFF ==\n")
  ks = {}
  for k in pairs(regAddrs) do ks[#ks + 1] = k end
  table.sort(ks)
  for _, k in ipairs(ks) do f:write(string.format("  %04x: %d\n", k, regAddrs[k])) end
  f:close()
  emu.stop(0)
end, emu.eventType.endFrame)
