-- Mesen 2.1.1 capture script for the Master System / Game Gear oracle comparison (black box: run Mesen, take its screenshots and state at fixed frame counts).
-- Run by mesen-capture.ps1:  Mesen.exe --testRunner <rom> capture.lua   (environment variables below)
--
--   MESEN_OUT     directory the files go to (must exist)
--   MESEN_TAG     file name prefix
--   MESEN_FRAMES  comma list of frame counts: after that many completed frames, take a screenshot  <tag>_f<N>.png
--   MESEN_INPUT   "frame:Btn+Btn,frame:"  held buttons per frame (frames counted from 0, an entry holds until the next).
--                 Names are the Workshop's PadButtons names: Up Down Left Right A B Start Select
--   MESEN_DUMP    "1" also writes <tag>_f<N>.vdp.txt (VDP registers, counters) and <tag>_f<N>.vram.bin / .cram.bin
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

local keyOf = { up = "up", down = "down", left = "left", right = "right", a = "a", b = "b", start = "start", select = "select", x = "x", y = "y", l = "l", r = "r" }
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
  if done >= last then emu.stop(0) end
end, emu.eventType.endFrame)
