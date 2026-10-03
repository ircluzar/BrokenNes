-- Mesen 2.1.1 capture script for the Master System / Game Gear oracle comparison (black box: run Mesen, take its screenshots and state at fixed frame counts).
-- Run by mesen-capture.ps1:  Mesen.exe --testRunner <rom> capture.lua   (environment variables below)
--
--   MESEN_OUT     directory the files go to (must exist)
--   MESEN_TAG     file name prefix
--   MESEN_FRAMES  comma list of frame counts: after that many completed frames, take a screenshot  <tag>_f<N>.png
--   MESEN_INPUT   "frame:Btn+Btn,frame:"  held buttons per frame (frames counted from 0, an entry holds until the next).
--                 Names are the Workshop's PadButtons names: Up Down Left Right A B Start Select  (see keyOf below)
--   MESEN_GG      "1" when the ROM is a Game Gear game (Start is the pause button there)
--   MESEN_DUMP    "1" also writes <tag>_f<N>.state.txt (cpu.* and vdp.* keys) and <tag>_f<N>.vram.bin / .cram.bin / .ram.bin (16 KB VRAM, 64 B colour RAM, 8 KB work RAM)
--   MESEN_STATE   "1" also writes <tag>_f<N>.state.txt (the whole emu.getState table, sorted)
--   MESEN_WRITES  "1" logs every VDP register write as "frame line cycle reg value" to <tag>.vdpwrites.txt (needs MESEN_DUMP)
--   MESEN_PORTS   "1" logs every Z80 port read/write of the VDP, joypad and I/O control ports to <tag>.ports.txt
--
-- The screenshot is whatever Mesen shows (256 x 192 for the Master System, 160 x 144 for the Game Gear with the settings mesen-capture.ps1 writes).

local outDir = os.getenv("MESEN_OUT") or "."
local tag = os.getenv("MESEN_TAG") or "shot"
local wanted, last = {}, 0
for n in (os.getenv("MESEN_FRAMES") or ""):gmatch("%d+") do
  n = tonumber(n)
  wanted[n] = true
  if n > last then last = n end
end

-- Mesen's SMS / Game Gear controller buttons are one, two, pause (the Pause button; the Game Gear's Start is the same button) and the pad.
-- The Workshop's names map as the board does: A = button 1, B = button 2, Select = Pause on the Master System, Start = Start on the Game Gear.
local isGg = os.getenv("MESEN_GG") == "1"
local keyOf = { up = "up", down = "down", left = "left", right = "right", a = "one", b = "two" }
if isGg then keyOf.start = "pause" else keyOf.select = "pause" end
local script = {}
for part in (os.getenv("MESEN_INPUT") or ""):gmatch("[^,]+") do
  local f, buttons = part:match("^(%d+):(.*)$")
  if f then
    local held = {}
    for name in buttons:gmatch("[^+]+") do
      local k = keyOf[name:lower()]
      if k then held[k] = true end
    end
    script[#script + 1] = { frame = tonumber(f), held = held }
  end
end
table.sort(script, function(p, q) return p.frame < q.frame end)

local held = {}
local started = 0   -- frames started so far; the frame running has index started-1
local done = 0      -- frames completed

local function applyScript(index)
  for _, e in ipairs(script) do
    if e.frame <= index then held = e.held end
  end
end

local function writeFile(path, bytes)
  local f = assert(io.open(path, "wb"))
  f:write(bytes)
  f:close()
end

local function dumpState(n)
  local st = emu.getState()
  local keys = {}
  for k, _ in pairs(st) do keys[#keys + 1] = k end
  table.sort(keys)
  local lines = {}
  for _, k in ipairs(keys) do
    if os.getenv("MESEN_STATE") == "1" or k:match("^vdp%.") or k:match("^cpu%.") then
      lines[#lines + 1] = k .. " = " .. tostring(st[k])
    end
  end
  writeFile(outDir .. "/" .. tag .. "_f" .. n .. ".state.txt", table.concat(lines, "\n") .. "\n")
end

local function dumpVdp(n)
  local vram = {}
  for i = 0, 0x3FFF do vram[#vram + 1] = string.char(emu.read(i, emu.memType.smsVideoRam)) end
  writeFile(outDir .. "/" .. tag .. "_f" .. n .. ".vram.bin", table.concat(vram))
  local cram = {}
  for i = 0, 63 do cram[#cram + 1] = string.char(emu.read(i, emu.memType.smsPaletteRam)) end
  writeFile(outDir .. "/" .. tag .. "_f" .. n .. ".cram.bin", table.concat(cram))
  local ram = {}
  for i = 0, 0x1FFF do ram[#ram + 1] = string.char(emu.read(i, emu.memType.smsWorkRam)) end
  writeFile(outDir .. "/" .. tag .. "_f" .. n .. ".ram.bin", table.concat(ram))
end

-- MESEN_PORTS=1: every Z80 port write as "frame port value" (frame = the 1-based number of the frame running), to compare with the Workshop's --trace-io
local portLog = nil
if os.getenv("MESEN_PORTS") == "1" then
  portLog = assert(io.open(outDir .. "/" .. tag .. ".ports.txt", "wb"))
  emu.addMemoryCallback(function(address, value)
    portLog:write(string.format("%d %02X %02X\n", started, address & 0xFF, value & 0xFF))
  end, emu.callbackType.write, 0x00, 0xFF, emu.cpuType.sms, emu.memType.smsPort)
end

emu.addEventCallback(function()
  started = started + 1
  applyScript(started - 1)
end, emu.eventType.startFrame)

emu.addEventCallback(function()
  emu.setInput(held, 0)
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
  done = done + 1
  if wanted[done] then
    writeFile(outDir .. "/" .. tag .. "_f" .. done .. ".png", emu.takeScreenshot())
    if os.getenv("MESEN_DUMP") == "1" or os.getenv("MESEN_STATE") == "1" then dumpState(done) end
    if os.getenv("MESEN_DUMP") == "1" then dumpVdp(done) end
  end
  if done >= last then
    if portLog then portLog:close() end
    emu.stop(0)
  end
end, emu.eventType.endFrame)
