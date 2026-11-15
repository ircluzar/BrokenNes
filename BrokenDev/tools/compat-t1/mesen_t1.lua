-- Tier-1 reference run (Great Compatibility Catalog P3/T1): Mesen 2 plays the same input script BrokenNes played and dumps raw
-- pictures around each checkpoint frame, then quits. No socket: one run per game, started as
--   Mesen.exe <rom> --luaScriptsToLoad mesen_t1.lua --testRunner --timeout=<s>
-- env: BNT1_INPUT  the romtest input script ("600:Start,610:,...": from that frame on, hold these buttons)
--      BNT1_SHOTS  "f1,f2,...": frames whose picture is wanted (frames completed since power-on)
--      BNT1_WIN    pictures from f-WIN..f+WIN are written (the two emulators' first frame can sit a frame or two apart)
--      BNT1_OUT    folder: writes m_<frame>.raw ("<w> <h>\n" + w*h little-endian 0xRRGGBB words)
local script = os.getenv("BNT1_INPUT") or ""
local outDir = os.getenv("BNT1_OUT")
local win = tonumber(os.getenv("BNT1_WIN") or "2")
if not outDir then emu.log("t1: BNT1_OUT missing"); emu.stop(3); return end
-- BNT1_MAP "a=one,b=two,start=pause": BrokenNes pad names -> this console's Mesen input names (empty = dropped)
local map = {}
for k, v in (os.getenv("BNT1_MAP") or ""):gmatch("(%w+)=(%w*)") do map[k] = v end
local steps = {}
for st in script:gmatch("[^,]+") do
  local f, b = st:match("^%s*(%d+)%s*:(.*)$")
  if f then
    local t = {}
    for n in b:gmatch("[^+%s]+") do
      local k = n:lower()
      if map[k] ~= nil then k = map[k] end
      if k ~= "" then t[k] = true end
    end
    steps[#steps + 1] = { f = tonumber(f), pad = t }
  end
end
table.sort(steps, function(a, b) return a.f < b.f end)
local want, last = {}, 0
for s in (os.getenv("BNT1_SHOTS") or ""):gmatch("%d+") do
  local f = tonumber(s)
  for k = f - win, f + win do if k >= 0 then want[k] = true end end
  if f + win > last then last = f + win end
end
local done, cur, pad = 0, 1, {}
local function writeShot(path)
  local buf = emu.getScreenBuffer()
  local sz = emu.getScreenSize and emu.getScreenSize() or nil
  local w = sz and sz.width or 256
  local h = sz and sz.height or (#buf // w)
  local parts = {}
  for i = 1, w * h, 256 do
    local k = math.min(256, w * h - i + 1)
    local row = {}
    for j = 0, k - 1 do row[j + 1] = (buf[i + j] or 0) & 0xFFFFFF end
    parts[#parts + 1] = string.pack("<" .. string.rep("I4", k), table.unpack(row, 1, k))
  end
  local f = io.open(path, "wb"); f:write(w .. " " .. h .. "\n"); f:write(table.concat(parts)); f:close()
end
emu.addEventCallback(function()
  while cur <= #steps and steps[cur].f <= done do pad = steps[cur].pad; cur = cur + 1 end
  emu.setInput(pad, 0)
end, emu.eventType.inputPolled)
local function hwFrame()
  local ok, st = pcall(emu.getState)
  if ok and st then return st["ppu.frameCount"] or st["frameCount"] end
  return nil
end
local logged = false
emu.addEventCallback(function()
  done = done + 1
  if not logged then
    logged = true
    local f = io.open(outDir .. "/start.txt", "w"); f:write("done=" .. done .. " hw=" .. tostring(hwFrame()) .. string.char(10)); f:close()
  end
  if want[done] then writeShot(outDir .. "/m_" .. done .. ".raw") end
  if done >= last then emu.stop(0) end
end, emu.eventType.endFrame)
