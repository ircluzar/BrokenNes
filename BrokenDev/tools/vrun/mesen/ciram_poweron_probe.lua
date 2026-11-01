-- DIAGNOSTIC ONLY. Tests an alternative explanation for the "12 bytes that equal their own address
-- low byte" found in Mesen's CIRAM (0x388->88, 0x3AC->AC, 0x3CA->CA, 0x3DD->DD, 0x3FA->FA,
-- 0x3FF->FF). That pattern was read as proof of a $2007-during-rendering glitch storing the address
-- LSB. But a $2007 write log over the same ROM and the same 900 frames finds ZERO writes issued
-- while rendering is enabled, so something else has to be putting those bytes there.
--
-- The obvious other candidate: Mesen's POWER-ON fill for nametable RAM. If CIRAM powers on holding
-- (index & 0xFF), then every byte the game never writes still reads back as its own address low
-- byte, and the "evidence" is an artefact of comparing two emulators' untouched VRAM.
--
-- This dumps CIRAM at frame 0 (before the game has written anything) and again later, plus a
-- direct count of how many of the 2048 bytes equal their own index low byte at each point.
local OUTDIR = os.getenv("PROBE_DIR") or "."
local LATE   = tonumber(os.getenv("PROBE_LATE") or "244")

local function snapshot(tag, frameNo)
  local bytes = {}
  local selfMatch = 0
  for i = 0, 2047 do
    local b = emu.read(i, emu.memType.nesNametableRam)
    bytes[i + 1] = string.char(b)
    if b == (i & 0xFF) then selfMatch = i == 0 and selfMatch or selfMatch + 1 end
  end
  local fh = io.open(OUTDIR .. "/ciram_" .. tag .. ".bin", "wb")
  fh:write(table.concat(bytes))
  fh:close()
  local probes = { 0x388, 0x3AC, 0x3CA, 0x3DD, 0x3FA, 0x3FF }
  local parts = {}
  for _, a in ipairs(probes) do
    parts[#parts + 1] = string.format("%03x->%02x", a, emu.read(a, emu.memType.nesNametableRam))
  end
  return string.format("%s (frame %d): selfMatchBytes=%d/2048   %s",
    tag, frameNo, selfMatch, table.concat(parts, " "))
end

local lines = {}
local frame = 0
local grabbedZero = false

emu.addEventCallback(function()
  if not grabbedZero then
    grabbedZero = true
    lines[#lines + 1] = snapshot("f0", frame)
  end
  frame = frame + 1
  if frame == LATE then lines[#lines + 1] = snapshot("late", frame) end
  if frame >= LATE + 2 then
    local fh = io.open(OUTDIR .. "/ciram_probe.txt", "w")
    fh:write(table.concat(lines, "\n"), "\n")
    fh:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
