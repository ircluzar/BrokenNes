-- BrokenDev compat adapter for Mesen 2 (NES, SNES and the Game Boy: gb, gbc). Run by lib\adapters\mesen.ps1:
--   Mesen.exe <rom> --luaScriptsToLoad mesen.lua --testRunner --timeout=<s>      with BDC_SPEC = the spec file
-- The spec and the result formats are described at the top of BrokenDev\lib\Compat.ps1.
--
-- Frames: 'done' counts endFrame events (frames Mesen completed since power-on). The pad script is applied on the inputPolled event of the
-- frame being run, the same convention as BrokenDev's Mesen diff scripts: input line 'input f X' is held while frame f+1 runs.
-- Pictures: emu.takeScreenshot() in the endFrame event (a PNG of what Mesen shows; on the SNES it may lag the frame just completed by a
-- frame or two, which BrokenDev's diff measured: the compat checks never compare pictures to the frame).

-- Mesen's sandbox has io and os (AllowIoOsAccess) but not the 'package' library: paths use '/' (Windows accepts it).
local specPath = os.getenv("BDC_SPEC")
local spec = { reads = {}, inputs = {}, shots = {}, frames = 0 }
local function fail(msg)
  -- a setup error goes beside the spec (the result file may not exist yet) and ends the test runner with exit code 3
  pcall(function() local f = io.open(specPath .. ".error.txt", "w"); f:write(msg .. "\n"); f:close() end)
  emu.log("bdcompat: " .. msg)
  emu.stop(3)
end
if not specPath then emu.stop(3) return end

local specFile = io.open(specPath, "r")
if not specFile then fail("cannot open the spec " .. specPath) return end
local specText = specFile:read("a")
specFile:close()
for line in specText:gmatch("[^\r\n]+") do
  line = line:gsub("%s+$", "")
  if line ~= "" and line:sub(1, 1) ~= "#" then
    local key, rest = line:match("^(%S+)%s*(.*)$")
    if key == "console" then spec.console = rest
    elseif key == "rom" then spec.rom = rest
    elseif key == "out" then spec.out = rest
    elseif key == "savedir" then spec.savedir = rest
    elseif key == "frames" then spec.frames = tonumber(rest)
    elseif key == "read" then
      local name, domain, addr = rest:match("^(%S+)%s+(%S+)%s+(%x+)$")
      spec.reads[#spec.reads + 1] = { name = name, domain = domain, addr = tonumber(addr, 16) }
    elseif key == "input" then
      local f, buttons = rest:match("^(%d+)%s+(%S+)$")
      local held = {}
      if buttons ~= "-" then for b in buttons:gmatch("[^+]+") do held[b:lower()] = true end end
      spec.inputs[#spec.inputs + 1] = { frame = tonumber(f), held = held }
    elseif key == "shot" then spec.shots[tonumber(rest)] = true
    else fail("unknown spec directive '" .. key .. "'") return end
  end
end
table.sort(spec.inputs, function(a, b) return a.frame < b.frame end)

local isNes = spec.console == "nes"
-- the Game Boy (gb, gbc): work RAM, high RAM and the cartridge's RAM are Mesen's gbWorkRam, gbHighRam and gbCartRam, the same offsets
-- as BrokenDev's domains (wram 0 = $C000, bank n at n x $1000; hram 0 = $FF80; cartram bank n at n x $2000)
local isGb = spec.console == "gb" or spec.console == "gbc"
local memType = isNes and emu.memType.nesInternalRam or (isGb and emu.memType.gbWorkRam or emu.memType.snesWorkRam)
local gbTypes = isGb and { wram = emu.memType.gbWorkRam, hram = emu.memType.gbHighRam, cartram = emu.memType.gbCartRam } or nil
local sep = "/"
local out = io.open(spec.out .. sep .. "result.txt", "w")
if not out then fail("cannot write " .. spec.out .. sep .. "result.txt") return end
out:write("# bdcompat-result 1\n")
local names = {}
for i, r in ipairs(spec.reads) do
  names[i] = r.name
  if isGb then
    r.memType = gbTypes[r.domain]
    if not r.memType then fail("read " .. r.name .. ": domain " .. r.domain .. " does not belong to the " .. spec.console) return end
  else
    r.memType = memType
    if (isNes and r.domain ~= "ram") or ((not isNes) and r.domain ~= "wram") then fail("read " .. r.name .. ": domain " .. r.domain .. " does not belong to the " .. spec.console) return end
  end
end
local rom = emu.getRomInfo() or {}
out:write("emu Mesen 2.1.1 " .. (isNes and "NES" or (isGb and "Game Boy" or "SNES")) .. "\n")
out:write("reads " .. table.concat(names, " ") .. "\n")
out:write("note memory: " .. (isNes and "emu.memType.nesInternalRam (2 KB system RAM)" or (isGb and "emu.memType.gbWorkRam / gbHighRam / gbCartRam" or "emu.memType.snesWorkRam (128 KB work RAM)")) .. "\n")
out:write("note rom: " .. tostring(rom.name) .. "\n")
-- Mesen's power-on RAM fill as this run got it (the script loads before the first frame runs): a census of the first 2 KB
do
  local counts, distinct = {}, 0
  for a = 0, 0x7FF do
    local v = emu.read(a, memType)
    if not counts[v] then counts[v] = 0; distinct = distinct + 1 end
    counts[v] = counts[v] + 1
  end
  local top = {}
  for v, c in pairs(counts) do top[#top + 1] = { v = v, c = c } end
  table.sort(top, function(a, b) return a.c > b.c end)
  local parts = {}
  for i = 1, math.min(3, #top) do parts[#parts + 1] = string.format("%d x $%02X", top[i].c, top[i].v) end
  out:write("note power-on RAM (first 2 KB, read before the first frame): " .. distinct .. " distinct values; " .. table.concat(parts, ", ") .. "\n")
end
out:flush()

local held, pos, done = {}, 0, 0
local finished = false

emu.addEventCallback(function()
  while pos < #spec.inputs and spec.inputs[pos + 1].frame <= done do
    pos = pos + 1
    held = spec.inputs[pos].held
  end
  emu.setInput(held, 0)
end, emu.eventType.inputPolled)

-- NES sound activity: the APU status register's length-counter bits ($4015 read, debug access: no side effect) say which channel is
-- playing. An approximation (a channel at volume 0 with a running length counter counts as active); the SNES has no such summary.
local audioActive = false
local function sampleAudio()
  if not isNes then return end
  local ok, v = pcall(emu.read, 0x4015, emu.memType.nesDebug)
  if ok and v and (v & 0x1F) ~= 0 then audioActive = true end
end

emu.addEventCallback(function()
  if finished then return end
  done = done + 1
  local ok, err = pcall(function()
    local vals = {}
    for i, r in ipairs(spec.reads) do vals[i] = emu.read(r.addr, r.memType) end
    out:write("t " .. done .. (#vals > 0 and (" " .. table.concat(vals, " ")) or "") .. "\n")
    sampleAudio()
    if isNes and done % 30 == 0 then out:write("audio " .. done .. " " .. (audioActive and 1 or 0) .. "\n"); audioActive = false end
    if spec.shots[done] then
      local file = string.format("shot_%05d.png", done)
      local f = assert(io.open(spec.out .. sep .. file, "wb"))
      f:write(emu.takeScreenshot())
      f:close()
      out:write("shot " .. done .. " " .. file .. "\n")
    end
    if done % 60 == 0 then out:flush() end
  end)
  if not ok then
    finished = true
    out:write("end crashed " .. done .. " lua: " .. tostring(err):gsub("\n", " ") .. "\n")
    out:close()
    emu.stop(4)
    return
  end
  if done >= spec.frames then
    finished = true
    out:write("end done " .. done .. "\n")
    out:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
