-- Tier-1 reference run on BizHawk (EmuHawk; Genplus-gx for the Genesis): plays the input script BrokenNes played and writes PNG pictures
-- around each checkpoint frame, then quits. One run per game:
--   EmuHawk.exe --config=<home>\config.ini --chromeless --lua=bizhawk_t1.lua <rom>     (config: StartPaused, sound and throttle off)
-- env: BNT1_INPUT "600:Start,610:,..." (from that frame on, hold these buttons; BrokenNes pad names)
--      BNT1_SHOTS "f1,f2,..." frames wanted (frames completed since power-on); BNT1_WIN pictures f-WIN..f+WIN
--      BNT1_OUT   folder: m_<frame>.png
local script = os.getenv("BNT1_INPUT") or ""
-- BNT1_INPUT_FILE: the script in a file (a 40-minute script is ~55k characters; one environment variable holds 32767)
do
  local fn = os.getenv("BNT1_INPUT_FILE")
  if fn and fn ~= "" then local fh = io.open(fn, "r"); if fh then script = fh:read("*a"):gsub("%s+$", ""); fh:close() end end
end
local outDir = os.getenv("BNT1_OUT")
local win = tonumber(os.getenv("BNT1_WIN") or "2")
if emu.framecount() ~= 0 then client.reboot_core() end
if client.ispaused() then client.unpause() end
local steps = {}
for st in script:gmatch("[^,]+") do
  local f, b = st:match("^%s*(%d+)%s*:(.*)$")
  if f then
    local t = {}
    for n in b:gmatch("[^+%s]+") do t[n:lower()] = true end
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
-- BrokenNes pad name -> BizHawk button (MdSession's generic layout: A, B as is, L -> Z, R -> C, Select -> Mode)
local bh = { up = "Up", down = "Down", left = "Left", right = "Right", a = "A", b = "B", start = "Start", l = "Z", r = "C", select = "Mode", x = "X", y = "Y" }
local have = {}
for k, _ in pairs(joypad.get() or {}) do have[tostring(k)] = true end
local function name(b) if have["P1 " .. b] then return "P1 " .. b end if have[b] then return b end return nil end
local all = {}
for k, v in pairs(bh) do local n = name(v); if n then all[k] = n end end
local cur, pad, done = 1, {}, 0
local ok, err = pcall(function()
  while done < last do
    while cur <= #steps and steps[cur].f <= done do pad = steps[cur].pad; cur = cur + 1 end
    local t = {}
    for k, n in pairs(all) do t[n] = pad[k] == true end
    joypad.set(t)
    emu.frameadvance()
    done = done + 1
    if want[done] then client.screenshot(outDir .. "/m_" .. done .. ".png") end
  end
end)
if not ok then local f = io.open(outDir .. "/error.txt", "w"); f:write(tostring(err)); f:close() end
client.exitCode(ok and 0 or 4)
