-- BrokenDev compat adapter for BizHawk 2.11.1 (EmuHawk): NesHawk for the NES, a bsnes core for the SNES. Run by lib\adapters\bizhawk.ps1:
--   EmuHawk.exe --config=<scratch>\config.ini --chromeless --lua=<this file> <rom>      with BDC_SPEC = the spec file
-- The spec and the result formats are described at the top of BrokenDev\lib\Compat.ps1. The adapter's config.ini pins the core, turns sound
-- and every throttle off and sets frame skipping to 0 (an unthrottled EmuHawk skips rendering 2 frames in 3, and a skipped frame's picture
-- would be an older one).
--
-- Frames: EmuHawk starts a --lua script after it has already run the first frame (emu.framecount() = 1) unless it starts paused. The
-- adapter's config sets StartPaused, so the script starts at emu.framecount() = 0, a real power-on (an input at frame 0 is honoured and the
-- RAM fill noted is the power-on one), and unpauses. Should it ever start later than frame 0, it reboots the core (client.reboot_core():
-- the ROM is loaded again, battery save included, the script keeps running) and says so in a note.
-- Each loop: joypad.set for the frame about to run (input line 'input f X' is held while frame f+1 runs), emu.frameadvance(), then the
-- reads, the 't' line and the picture of the frame just completed; the frame number is the count of frames advanced since power-on (a
-- note says so if emu.framecount() ever disagrees).
-- joypad.set lasts one frame, so every button of pad 1 is set (true or false) before every frame: the host's keyboard and gamepads cannot
-- leak into a run.
-- Memory: memory.read_u8(addr, domain) on the core's own domain, never the system bus: NesHawk "RAM" (2 KB), bsnes "WRAM" (128 KB).
-- Pictures: client.screenshot(path) = the core's frame buffer as PNG (no OSD: the config sets ScreenshotCaptureOsd off). NesHawk shows
-- scanlines 8-231 by default (256x224); BSNESv115+ gives 256x224 (its ShowOverscan is off by default), the legacy BSNES core 256x239
-- when the game turns overscan on. A 'note picture:' line names each size the first time a shot has it.
-- End: 'end done N', then client.exitCode(0): EmuHawk closes the game, which writes the battery save (<game>.SaveRAM in the SaveRAM folder =
-- the spec's savedir; a file it replaces becomes <game>.SaveRAM.bak). Any Lua error is caught: 'end crashed N <message>', exit code 4.

local specPath = os.getenv("BDC_SPEC")
local spec = { reads = {}, inputs = {}, shots = {}, frames = 0 }
local out = nil
local done = 0

local function oneLine(s) return (tostring(s):gsub("[\r\n]+", " ")) end

local function finish(code)
  if out then out:close(); out = nil end
  client.exitCode(code)
end

local function crash(msg)
  if out then out:write("end crashed " .. done .. " " .. oneLine(msg) .. "\n") end
  finish(4)
end

local function safe(f, ...)
  local ok, v = pcall(f, ...)
  if ok then return v end
  return nil
end

-- ------------------------------------------------------------------------------------------------ spec

local function readSpec()
  if not specPath or specPath == "" then error("BDC_SPEC is not set") end
  local f = io.open(specPath, "rb")
  if not f then error("cannot open the spec " .. specPath) end
  local text = f:read("a")
  f:close()
  for raw in text:gmatch("[^\n]+") do
    local line = raw:gsub("%s+$", ""):gsub("^%s+", "")
    if line ~= "" and line:sub(1, 1) ~= "#" then
      local key, rest = line:match("^(%S+)%s*(.*)$")
      if key == "console" then spec.console = rest
      elseif key == "rom" then spec.rom = rest
      elseif key == "out" then spec.out = rest
      elseif key == "savedir" then spec.savedir = rest
      elseif key == "frames" then spec.frames = tonumber(rest)
      elseif key == "read" then
        local name, domain, addr = rest:match("^(%S+)%s+(%S+)%s+(%x+)$")
        if not name then error("bad read line: " .. line) end
        spec.reads[#spec.reads + 1] = { name = name, domain = domain, addr = tonumber(addr, 16) }
      elseif key == "input" then
        local fr, buttons = rest:match("^(%d+)%s+(%S+)$")
        if not fr then error("bad input line: " .. line) end
        local names = {}
        if buttons ~= "-" then for b in buttons:gmatch("[^+]+") do names[#names + 1] = b end end
        spec.inputs[#spec.inputs + 1] = { frame = tonumber(fr), names = names }
      elseif key == "shot" then spec.shots[tonumber(rest)] = true
      elseif key == "movie" then spec.movie = rest
      else error("unknown spec directive '" .. key .. "'") end
    end
  end
  if spec.console ~= "nes" and spec.console ~= "snes" and spec.console ~= "gb" and spec.console ~= "gbc" then error("spec: console must be nes, snes, gb or gbc, not " .. tostring(spec.console)) end
  if not spec.out or spec.out == "" then error("spec: no out folder") end
  if not spec.frames or spec.frames <= 0 then error("spec: frames must be positive") end
  table.sort(spec.inputs, function(a, b) return a.frame < b.frame end)
end

-- ------------------------------------------------------------------------------------------------ the core

local isNes, isGb, domain, domainSize, system, coreName
local prefix = "P1 "      -- the pad's button prefix ("" on the Game Boy cores)
local startedAt, rebooted = nil, false
local padNames = {}       -- BizHawk's names of pad 1's buttons ("P1 A", ...)
local steps = {}          -- spec.inputs, each a full set of pad 1 values

-- The spec's button names -> the pad's names (the same for NesHawk and the SNES cores: "P1 " + the name).
local canon = { a = "A", b = "B", x = "X", y = "Y", l = "L", r = "R", select = "Select", start = "Start", up = "Up", down = "Down", left = "Left", right = "Right" }

local function listOf(t)
  -- NLua hands .NET arrays over as tables indexed from 0
  local r = {}
  if type(t) ~= "table" then return r end
  local i = 0
  while t[i] ~= nil do r[#r + 1] = tostring(t[i]); i = i + 1 end
  if #r == 0 then for _, v in pairs(t) do r[#r + 1] = tostring(v) end end
  return r
end

local function runningCore()
  -- the core EmuHawk loaded: the last entry of the config's RecentCores (a .NET Queue<string>)
  local cfg = safe(client.getconfig)
  if not cfg then return nil end
  local arr = safe(function() return cfg.RecentCores:ToArray() end)
  local n = safe(function() return cfg.RecentCores.Count end)
  if arr and n and n > 0 then return tostring(arr[n - 1]) end
  return nil
end

local function powerOn()
  -- EmuHawk started paused (the config's StartPaused): the script is at frame 0. Otherwise reboot the core for a real frame 0.
  startedAt = emu.framecount()
  if startedAt ~= 0 then
    client.reboot_core()
    rebooted = true
  end
  if emu.framecount() ~= 0 then error("the core did not restart at frame 0 (emu.framecount() = " .. emu.framecount() .. ")") end
  if client.ispaused() then client.unpause() end
end

local function setup()
  system = emu.getsystemid()
  if system == "NULL" then error("no ROM is loaded (EmuHawk could not open " .. tostring(spec.rom) .. " with the pinned core)") end
  isNes = spec.console == "nes"
  isGb = spec.console == "gb" or spec.console == "gbc"
  if isGb then
    -- the Game Boy cores (Gambatte, SameBoy) say GB or GBC (a dual-mode cartridge runs as GBC on them)
    if system ~= "GB" and system ~= "GBC" then error("the spec is for the " .. spec.console .. " but EmuHawk runs system " .. system) end
  elseif (isNes and system ~= "NES") or ((not isNes) and system ~= "SNES") then error("the spec is for the " .. spec.console .. " but EmuHawk runs system " .. system) end
  coreName = runningCore()
  domain = isNes and "RAM" or "WRAM"
  local domains = listOf(memory.getmemorydomainlist())
  local function has(d) for _, x in ipairs(domains) do if x == d then return true end end return false end
  if not has(domain) then error("the core has no memory domain " .. domain .. " (it has: " .. table.concat(domains, ", ") .. ")") end
  domainSize = memory.getmemorydomainsize(domain)
  if isGb then
    -- BrokenDev's Game Boy domains -> the cores' own: WRAM (8 KB, or 32 KB on a Color), HRAM, CartRAM
    local gbNames = { wram = "WRAM", hram = "HRAM", cartram = "CartRAM" }
    for _, r in ipairs(spec.reads) do
      r.bizDomain = gbNames[r.domain]
      if not r.bizDomain then error("read " .. r.name .. ": domain " .. r.domain .. " does not belong to the " .. spec.console) end
      if not has(r.bizDomain) then error("read " .. r.name .. ": the core has no memory domain " .. r.bizDomain .. " (it has: " .. table.concat(domains, ", ") .. ")") end
      local size = memory.getmemorydomainsize(r.bizDomain)
      if r.addr < 0 or r.addr >= size then error(string.format("read %s: address %X is outside %s (%d bytes)", r.name, r.addr, r.bizDomain, size)) end
    end
  else
    local want = isNes and 0x800 or 0x20000
    if domainSize ~= want then error(string.format("memory domain %s is %d bytes, expected %d", domain, domainSize, want)) end
    for _, r in ipairs(spec.reads) do
      r.bizDomain = domain
      if (isNes and r.domain ~= "ram") or ((not isNes) and r.domain ~= "wram") then error("read " .. r.name .. ": domain " .. r.domain .. " does not belong to the " .. spec.console) end
      if r.addr < 0 or r.addr >= domainSize then error(string.format("read %s: address %X is outside %s", r.name, r.addr, domain)) end
    end
  end

  -- pad 1's buttons, as the core names them (joypad.get(1) gives them without the "P1 " prefix). The Game Boy cores' pad has no player
  -- prefix at all ("Up", "A"): joypad.get() lists those
  local have = {}
  if isGb then
    prefix = ""
    for k, _ in pairs(joypad.get() or {}) do have[tostring(k)] = true end
    if not have["Up"] then
      prefix = "P1 "
      have = {}
      for k, _ in pairs(joypad.get(1) or {}) do have["P1 " .. tostring(k)] = true end
    end
  else
    for k, _ in pairs(joypad.get(1) or {}) do have["P1 " .. tostring(k)] = true end
  end
  local consoleButtons = (isNes or isGb) and { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" }
                                or { "B", "Y", "Select", "Start", "Up", "Down", "Left", "Right", "A", "X", "L", "R" }
  for _, b in ipairs(consoleButtons) do
    local n = prefix .. b
    if not have[n] then
      local list = {}
      for k, _ in pairs(have) do list[#list + 1] = k end
      table.sort(list)
      error("the core's pad 1 has no button '" .. n .. "' (it has: " .. table.concat(list, ", ") .. ")")
    end
    padNames[#padNames + 1] = n
  end
  for _, s in ipairs(spec.inputs) do
    local held = {}
    for _, n in ipairs(padNames) do held[n] = false end
    for _, b in ipairs(s.names) do
      local c = canon[b:lower()]
      if not c or held[prefix .. c] == nil then error("input " .. s.frame .. ": '" .. b .. "' is not a " .. spec.console .. " button") end
      held[prefix .. c] = true
    end
    steps[#steps + 1] = { frame = s.frame, held = held }
  end
end

-- ------------------------------------------------------------------------------------------------ notes

local function writeNotes()
  out:write("note core: " .. tostring(coreName) .. " (EmuHawk's RecentCores), system " .. system .. ", board " .. tostring(safe(gameinfo.getboardtype))
            .. ", display type " .. tostring(safe(emu.getdisplaytype)) .. "\n")
  out:write("note rom: " .. tostring(safe(gameinfo.getromname)) .. ", sha1 " .. tostring(safe(gameinfo.getromhash)) .. ", database " .. tostring(safe(gameinfo.getstatus)) .. "\n")
  out:write("note memory: domain " .. domain .. " (" .. domainSize .. " bytes) with memory.read_u8; the core's domains: " .. table.concat(listOf(memory.getmemorydomainlist()), ", ") .. "\n")
  out:write("note pad: joypad.set on " .. table.concat(padNames, ", ") .. " (every button set before every frame)\n")
  out:write("note start: the script started at emu.framecount() " .. tostring(startedAt)
            .. (rebooted and "; client.reboot_core() restarted the core at frame 0" or " (EmuHawk started paused at power-on, then unpaused)") .. "\n")
  -- the power-on state of the read domain, before the first frame: a short signature of the RAM fill
  local first = {}
  for i = 0, 15 do first[#first + 1] = string.format("%02X", memory.read_u8(i, domain)) end
  local hash = safe(memory.hash_region, 0, domainSize, domain)
  out:write("note power-on " .. domain .. " (frame " .. emu.framecount() .. "): first bytes " .. table.concat(first, " ") .. ", sha256 " .. tostring(hash) .. "\n")
  out:write("note picture: client.screenshot of the core's frame buffer, " .. client.bufferwidth() .. "x" .. client.bufferheight() .. " at power-on\n")
  if isNes then
    local top, bottom = safe(nes.gettopscanline), safe(nes.getbottomscanline)
    out:write("note nes: scanlines " .. tostring(top) .. "-" .. tostring(bottom) .. " shown, clip left and right " .. tostring(safe(nes.getclipleftandright)) .. "\n")
  end
  out:write("note lua: " .. tostring(safe(client.get_lua_engine)) .. "\n")
end

-- ------------------------------------------------------------------------------------------------ run

local stepPos = 0
local held = nil
local frameMismatch = false
local sizes = {}

local lastPad = nil
local function beforeFrame()
  if spec.movie then return end   -- a movie run: EmuHawk's movie is the input (afterFrame notes what it pressed)
  while stepPos < #steps and steps[stepPos + 1].frame <= done do
    stepPos = stepPos + 1
    held = steps[stepPos].held
  end
  if held == nil then
    held = {}
    for _, n in ipairs(padNames) do held[n] = false end
  end
  joypad.set(held)
end

local sep = package.config:sub(1, 1)

local function moviePad()
  -- the buttons the movie pressed in the frame that just ran (movie.getinput, by BizHawk's names), joined with '+'
  local t = safe(movie.getinput, done)
  if type(t) ~= "table" then return "?" end
  local on = {}
  for _, n in ipairs(padNames) do if t[n] == true then on[#on + 1] = n:gsub("^P1 ", "") end end
  return #on > 0 and table.concat(on, "+") or "-"
end

local function afterFrame()
  if spec.movie then
    local p = moviePad()
    if p ~= lastPad then out:write("note movie-pad " .. done .. " " .. p .. "\n"); lastPad = p end
  end
  done = done + 1
  if not frameMismatch and emu.framecount() ~= done then
    frameMismatch = true
    out:write("note frames: after " .. done .. " frame advances emu.framecount() is " .. emu.framecount() .. "\n")
  end
  local line = { "t", tostring(done) }
  for _, r in ipairs(spec.reads) do line[#line + 1] = tostring(memory.read_u8(r.addr, r.bizDomain)) end
  out:write(table.concat(line, " ") .. "\n")
  if spec.shots[done] then
    local file = string.format("shot_%05d.png", done)
    client.screenshot(spec.out .. sep .. file)
    out:write("shot " .. done .. " " .. file .. "\n")
    local size = client.bufferwidth() .. "x" .. client.bufferheight()
    if not sizes[size] then sizes[size] = true; out:write("note picture: " .. size .. " from frame " .. done .. "\n") end
  end
  if done % 60 == 0 then out:flush() end
end

local ok, err = pcall(readSpec)
if ok then
  out = io.open(spec.out .. sep .. "result.txt", "wb")
  if not out then ok, err = false, "cannot write " .. spec.out .. sep .. "result.txt" end
end
if not ok then
  -- nowhere to report but the exit code (BrokenDev sees a run with no result: crashed)
  print("bdcompat: " .. oneLine(err))
  finish(3)
  return
end
out:write("# bdcompat-result 1\n")
out:flush()
ok, err = pcall(powerOn)
if ok then ok, err = pcall(setup) end
if not ok then
  out:write("emu BizHawk " .. tostring(safe(client.getversion)) .. " " .. tostring(safe(emu.getsystemid)) .. "\n")
  out:write("reads\n")
  crash(err)
  return
end
local names = {}
for i, r in ipairs(spec.reads) do names[i] = r.name end
out:write("emu BizHawk " .. client.getversion() .. " " .. tostring(coreName) .. " (" .. system .. ")\n")
out:write("reads " .. table.concat(names, " ") .. "\n")
if spec.movie then
  out:write("note movie: " .. tostring(spec.movie) .. ", loaded " .. tostring(safe(movie.isloaded)) .. ", mode " .. tostring(safe(movie.mode)) .. ", " .. tostring(safe(movie.length)) .. " frames\n")
  if not safe(movie.isloaded) then crash("EmuHawk did not load the movie " .. tostring(spec.movie)) return end
end
ok, err = pcall(writeNotes)
if not ok then out:write("note notes failed: " .. oneLine(err) .. "\n") end
out:flush()

local start = os.clock()
while done < spec.frames do
  ok, err = pcall(beforeFrame)
  if not ok then crash(err) return end
  emu.frameadvance()
  ok, err = pcall(afterFrame)
  if not ok then crash(err) return end
end
out:write(string.format("note run: %d frames, %.1f s of Lua CPU clock, lag frames %s\n", done, os.clock() - start, tostring(safe(emu.lagcount))))
out:write("note saveram: written by EmuHawk when it closes the game on exit (" .. tostring(spec.savedir) .. ")\n")
out:write("end done " .. done .. "\n")
finish(0)
