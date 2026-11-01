-- Mesen 2 SNES register-write timing log: the counterpart of BrokenNes.Workshop --snesrun --rom X --reg-log "2100,420B:out.txt"
-- (which writes "<vblankCount> <scanline> <lineClock> <addr> <value>"). Comparing where in the frame a game writes INIDISP ($2100) or
-- starts a DMA ($420B) is how the HDMA overhead defect was found (docs/projects/vrun-resume.md).
-- ENV: RL_OUT (file), RL_REGS ("2100,420B"), RL_FROM / RL_TO (frame counts to log), RL_INPUT (same format as
-- BrokenDev/tools/mesen-snes-capture.lua), RL_SHIFT (input shift). Frame count = completed frames (endFrame events), as in
-- mesen-snes-capture.lua. Line: "<frame> <scanline> <hclock> <addr> <value> <bank+pc>". The first line lists the state keys found.
-- Writes are watched in every system-bank mirror ($00-$3F and $80-$BF). Run it in a private Mesen copy (diff-snes.ps1 makes one).
local out = assert(io.open(os.getenv("RL_OUT") or "reglog.txt", "w"))
local from = tonumber(os.getenv("RL_FROM") or "0")
local to = tonumber(os.getenv("RL_TO") or "600")
local shift = tonumber(os.getenv("RL_SHIFT") or "0")
local keyOf = { a = "a", b = "b", x = "x", y = "y", l = "l", r = "r", start = "start", select = "select", up = "up", down = "down", left = "left", right = "right" }
local script = {}
for part in (os.getenv("RL_INPUT") or ""):gmatch("[^,]+") do
  local f, buttons = part:match("^(%d+):(.*)$")
  if f then
    local held = {}
    for name in buttons:gmatch("[^+]+") do local k = keyOf[name:lower()]; if k then held[k] = true end end
    script[#script + 1] = { frame = tonumber(f) + shift, held = held }
  end
end
local held, done, pos = {}, 0, 0
local keysDumped = false
local function st()
  local s = emu.getState()
  if not keysDumped then
    keysDumped = true
    local names = {}
    for k, _ in pairs(s) do if k:find("scanline") or k:find("Clock") or k:find("cycle") or k:find("pc") then names[#names + 1] = k end end
    table.sort(names)
    out:write("# keys: " .. table.concat(names, " ") .. "\n")
  end
  return s
end
local function onWrite(address, value)
  if done < from or done > to then return end
  local s = st()
  local sl = s["snes.ppu.scanline"] or s["ppu.scanline"] or -1
  local hc = s["memoryManager.hClock"] or s["ppu.hClock"] or s["snes.ppu.cycle"] or s["ppu.cycle"] or -1
  local pc = s["snes.cpu.pc"] or s["cpu.pc"] or -1
  local k = s["snes.cpu.k"] or s["cpu.k"] or 0
  out:write(string.format("%d %s %s %04X %02X %s\n", done, tostring(sl), tostring(hc), address & 0xFFFF, value, string.format("%02X%04X", k, pc)))
  out:flush()
end
for reg in (os.getenv("RL_REGS") or "2100,420B"):gmatch("[^,]+") do
  local r = tonumber(reg, 16)
  for _, base in ipairs({ 0x00, 0x80 }) do
    for bank = base, base + 0x3F do
      local a = bank * 0x10000 + r
      emu.addMemoryCallback(onWrite, emu.callbackType.write, a, a, emu.cpuType.snes, emu.memType.snesMemory)
    end
  end
end
emu.addEventCallback(function()
  while pos < #script and script[pos + 1].frame <= done do pos = pos + 1; held = script[pos].held end
  emu.setInput(held, 0)
end, emu.eventType.inputPolled)
emu.addEventCallback(function()
  done = done + 1
  if done > to then out:close(); emu.stop(0) end
end, emu.eventType.endFrame)
