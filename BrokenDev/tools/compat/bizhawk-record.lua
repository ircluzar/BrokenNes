-- BrokenDev: make EmuHawk RECORD a .bk2 by itself (the import half of the .bk2 check, tests/bk2-import.ps1).
-- EmuHawk 2.11.1's Lua has no "new movie" call, so the test starts EmuHawk playing an empty movie of the right length (--movie, a BrokenNes
-- export with no input), turns the movie to record mode at power-on (movie.setreadonly(false)), presses the spec's pad script frame by frame
-- (joypad.set with BizHawk's own button names, e.g. "P1 B1"), then saves the movie BizHawk recorded (movie.save) and exits.
-- The spec (BDR_SPEC): "frames N", "out PATH", "done PATH", then "i FRAME NAME+NAME|-" lines (held from FRAME on, '-' = nothing).
local specPath = os.getenv("BDR_SPEC")
local frames, outPath, donePath = 0, nil, nil
local inputs = {}
for line in io.lines(specPath) do
  local k, rest = line:match("^(%S+)%s+(.*)$")
  if k == "frames" then frames = tonumber(rest)
  elseif k == "out" then outPath = rest
  elseif k == "done" then donePath = rest
  elseif k == "i" then
    local fr, b = rest:match("^(%d+)%s+(%S+)$")
    local names = {}
    if b ~= "-" then for n in b:gmatch("[^+]+") do names[#names + 1] = n:gsub("_SP_", " ") end end
    inputs[#inputs + 1] = { frame = tonumber(fr), names = names }
  end
end
local function finish(msg)
  local f = io.open(donePath, "w"); f:write(msg .. "\n"); f:close()
  client.exit()
end
if not movie.isloaded() then finish("error: EmuHawk loaded no movie") return end
if emu.framecount() ~= 0 then finish("error: the script started at frame " .. emu.framecount() .. ", not at power-on") return end
movie.setreadonly(false)
if client.ispaused() then client.unpause() end
local all = {}
for _, s in ipairs(inputs) do for _, n in ipairs(s.names) do all[n] = true end end
local cur = {}
local idx = 1
while emu.framecount() < frames do
  local f = emu.framecount()
  while inputs[idx] and inputs[idx].frame <= f do
    cur = {}; for _, n in ipairs(inputs[idx].names) do cur[n] = true end
    idx = idx + 1
  end
  local held = {}
  for n in pairs(all) do held[n] = cur[n] == true end
  joypad.set(held)
  emu.frameadvance()
end
movie.save(outPath)
finish("done " .. movie.mode() .. " " .. movie.length())
