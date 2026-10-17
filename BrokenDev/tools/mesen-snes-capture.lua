-- Mesen 2.1.1 capture script for the SNES differential replay (black box: run Mesen, take its screenshots at fixed frame counts).
-- Run by diff-snes.ps1:  Mesen.exe <rom> --luaScriptsToLoad mesen-snes-capture.lua --testRunner     (environment variables below)
--
--   MESEN_OUT     directory the files go to (must exist)
--   MESEN_TAG     file name prefix
--   MESEN_FRAMES  comma list of frame counts (ranges "a-b" allowed) to capture around
--   MESEN_WINDOW  "lo:hi" (default -1:4): see below
--   MESEN_INPUT   "frame:Btn+Btn,frame:"  held buttons per frame (frames counted from 0, an entry holds until the next; applied when the game polls).
--                 Names: A B X Y L R Start Select Up Down Left Right (case-insensitive), the same as BrokenNes.Workshop --console-run --input.
--   MESEN_POKES   "frame:addr=value,..."  work RAM pokes (addr hex, the SNES work RAM offset 0..1FFFF) applied when that frame starts; a forced state, a probe.
--
-- The run stops after the last wanted frame. Mesen's test runner ends a script after about 100 s of wall clock, which is far more frames than a typical check.

local outDir = os.getenv("MESEN_OUT") or "."
local tag = os.getenv("MESEN_TAG") or "shot"
local wanted, last = {}, 0
for token in (os.getenv("MESEN_FRAMES") or ""):gmatch("[^,]+") do
  local a, b = token:match("^(%d+)%-(%d+)$")
  if not a then a = token:match("^(%d+)$"); b = a end
  a, b = tonumber(a), tonumber(b)
  for n = a, b do wanted[n] = true end
  if b > last then last = b end
end
-- MESEN_WINDOW "lo:hi": around every wanted frame N, the picture is saved at every completed-frame count N+lo .. N+hi, as <tag>_d<count>.png. Mesen's screenshot call
-- returns an earlier frame than the one just completed, and how much earlier depends on the build and on how its frame counter started, so the driver does not guess:
-- it compares BrokenNes' picture after N frames with each of these and reports which one matches (the same offset for every frame is a harness property; a drifting one is not).
local lo, hi = (os.getenv("MESEN_WINDOW") or "-1:4"):match("^(-?%d+):(-?%d+)$")
lo, hi = tonumber(lo), tonumber(hi)
local capture, lastCapture = {}, 0
for n, _ in pairs(wanted) do
  for k = lo, hi do
    if n + k >= 1 then capture[n + k] = true; if n + k > lastCapture then lastCapture = n + k end end
  end
end

-- MESEN_INPUT_SHIFT: added to every input frame number (the game reads the pad in the vblank AFTER Mesen's end-of-frame event, so the harness's frame counting and the
-- emulator's can disagree by one; the driver tries shifts and reports which one makes the pictures agree).
local shift = tonumber(os.getenv("MESEN_INPUT_SHIFT") or "0")
local keyOf = { a = "a", b = "b", x = "x", y = "y", l = "l", r = "r", start = "start", select = "select", up = "up", down = "down", left = "left", right = "right" }
local script = {}
for part in (os.getenv("MESEN_INPUT") or ""):gmatch("[^,]+") do
  local f, buttons = part:match("^(%d+):(.*)$")
  if f then
    local held = {}
    for name in buttons:gmatch("[^+]+") do
      local k = keyOf[name:lower()]
      if k then held[k] = true end
    end
    script[#script + 1] = { frame = tonumber(f) + shift, held = held }
  end
end
table.sort(script, function(p, q) return p.frame < q.frame end)

local pokes = {}
for part in (os.getenv("MESEN_POKES") or ""):gmatch("[^,]+") do
  local f, a, v = part:match("^(%d+):(%x+)=(%x+)$")
  if f then pokes[#pokes + 1] = { frame = tonumber(f), addr = tonumber(a, 16), value = tonumber(v, 16) } end
end

local function writeFile(path, bytes)
  local f = assert(io.open(path, "wb"))
  f:write(bytes)
  f:close()
end

local held = {}
local done = 0          -- frames completed
local scriptPos = 0
local function applyScript(index)
  while scriptPos < #script and script[scriptPos + 1].frame <= index do
    scriptPos = scriptPos + 1
    held = script[scriptPos].held
  end
end

emu.addEventCallback(function()
  applyScript(done)
  emu.setInput(held, 0)
  for _, p in ipairs(pokes) do
    if p.frame == done then emu.write(p.addr, p.value, emu.memType.snesWorkRam) end
  end
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
  done = done + 1
  if capture[done] then
    writeFile(outDir .. "/" .. tag .. "_d" .. done .. ".png", emu.takeScreenshot())
  end
  if done >= lastCapture then emu.stop(0) end
end, emu.eventType.endFrame)
