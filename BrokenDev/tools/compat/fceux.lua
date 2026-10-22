-- BrokenDev compat adapter for FCEUX 2.7.0 (Windows, fceux64.exe; NES only). Run by lib\adapters\fceux.ps1:
--   fceux64.exe -lua fceux.lua <rom>        with BDC_SPEC = the spec file (BDC_EMU and BDC_NOTES optional, from the PowerShell side)
-- The spec and the result formats are described at the top of BrokenDev\lib\Compat.ps1.
--
-- What FCEUX 2.7.0 does, measured (see lib\adapters\fceux.ps1 for the experiments):
--   Frames: the script starts after the ROM is loaded and powered on, before any frame runs. The FIRST emu.frameadvance() returns without
--     emulating anything (framecount stays 0, CPU cycles stay 0); every later call runs exactly one frame (29780-29782 CPU cycles on NTSC).
--     So the loop counts emu.framecount() (frames completed since power-on), never its own calls. A frame whose framecount did not move
--     writes nothing; more than 5 in a row end the run as crashed.
--   Input: joypad.set(1, t) before emu.frameadvance() is latched by the frame that call runs (FCEUX keeps a Lua override for ONE input read),
--     so 'input f X' is set while emu.framecount() == f: X is held while frame f+1 runs, as the spec says. Keys (case-sensitive):
--     A B select start up down left right. Every button is set true or false every frame, so no keyboard input can leak in.
--   Memory: memory.readbyte(addr) on the CPU bus; $0000-$07FF is the 2 KB system RAM (no side effects).
--   Pictures: gui.gdscreenshot(true) returns FCEUX's back buffer, the frame that just completed (measured: the $2001 write that turns
--     rendering on shows in the picture taken right after that frame), 256x240, all 240 lines (FCEUX's window shows lines 8-231), in
--     FCEUX's current palette, without Lua drawings or on-screen messages. gui.savescreenshotas is NOT used: it saves the NEXT frame,
--     cropped to 256x224. Written as a 24-bit BMP by this script.
--   Sound: sound.get() reports the APU channels even with FCEUX's sound output off (600 frames identical to a run with sound on): a
--     channel counts as audible when FCEUX gives it a volume above 0 (squares and noise: the envelope volume / 15, 0 when FCEUX considers
--     the channel silent; triangle and DMC: 1 or 0). An approximation of "a sound was playing", not a measure of the mixed output.
--   Exit: emu.exit() asks FCEUX to close (DoFCEUExit at the next frame boundary): the game is closed (battery saves written) and FCEUX quits
--     with exit code 0.
--   Errors: emu.frameadvance() cannot be called inside pcall (Lua 5.1: "attempt to yield across metamethod/C-call boundary"), so the work
--     runs in a coroutine (see the end of the file) and any error still ends the result with 'end crashed <frame> lua: <message>'.

local specPath = os.getenv("BDC_SPEC")
local emuText = os.getenv("BDC_EMU") or "FCEUX 2.7.0 (fceux64.exe)"
local extraNotes = os.getenv("BDC_NOTES") or ""

local out = nil          -- the result file, once open
local outDir = nil       -- the spec's out folder, as soon as the spec names it
local done = 0           -- frames completed since power-on (emu.framecount())
local finished = false

local function oneLine(s) return (tostring(s):gsub("[\r\n]+", " ")) end

local function finish(state, text)
  if finished then return end
  finished = true
  local line = "end " .. state .. " " .. done .. (text and (" " .. oneLine(text)) or "") .. "\n"
  if not out and outDir then
    -- a spec error before the result was started: a result that says so (BrokenDev reads the reason from it)
    out = io.open(outDir .. "\\result.txt", "w")
    if out then out:write("# bdcompat-result 1\nemu " .. oneLine(emuText) .. "\nreads\n") end
  end
  if out then
    out:write(line)
    out:close()
    out = nil
  end
  if state ~= "done" and specPath then
    -- the reason also goes beside the spec, as the other adapters do (the only place left when the spec has no out folder)
    pcall(function() local f = io.open(specPath .. ".error.txt", "w"); f:write(oneLine(text or state) .. "\n"); f:close() end)
  end
end

local BUTTONS = { A = "A", B = "B", SELECT = "select", START = "start", UP = "up", DOWN = "down", LEFT = "left", RIGHT = "right" }
local ALL = { "A", "B", "select", "start", "up", "down", "left", "right" }

local function readSpec()
  if not specPath then error("BDC_SPEC is not set") end
  local f = io.open(specPath, "r")
  if not f then error("cannot open the spec " .. specPath) end
  local text = f:read("*a")
  f:close()
  local spec = { reads = {}, inputs = {}, shots = {}, frames = 0 }
  for line in text:gmatch("[^\r\n]+") do
    line = line:gsub("^%s+", ""):gsub("%s+$", "")
    if line ~= "" and line:sub(1, 1) ~= "#" then
      local key, rest = line:match("^(%S+)%s*(.*)$")
      if key == "console" then spec.console = rest
      elseif key == "rom" then spec.rom = rest
      elseif key == "out" then spec.out = rest; outDir = rest
      elseif key == "savedir" then spec.savedir = rest
      elseif key == "frames" then spec.frames = tonumber(rest)
      elseif key == "read" then
        local name, domain, addr = rest:match("^(%S+)%s+(%S+)%s+(%x+)$")
        if not name then error("bad read line '" .. line .. "'") end
        spec.reads[#spec.reads + 1] = { name = name, domain = domain, addr = tonumber(addr, 16) }
      elseif key == "input" then
        local fr, buttons = rest:match("^(%d+)%s+(%S+)$")
        if not fr then error("bad input line '" .. line .. "'") end
        local held = {}
        for _, k in ipairs(ALL) do held[k] = false end
        if buttons ~= "-" then
          for b in buttons:gmatch("[^+]+") do
            local k = BUTTONS[b:upper()]
            if not k then error("input: '" .. b .. "' is not a NES button") end
            held[k] = true
          end
        end
        spec.inputs[#spec.inputs + 1] = { frame = tonumber(fr), held = held }
      elseif key == "shot" then spec.shots[tonumber(rest)] = true
      else error("unknown spec directive '" .. key .. "'") end
    end
  end
  if spec.console ~= "nes" then error("FCEUX runs the NES only; the spec is for '" .. tostring(spec.console) .. "'") end
  if not spec.out or spec.out == "" then error("the spec has no out folder") end
  if not spec.frames or spec.frames <= 0 then error("the spec has no positive frames count") end
  for _, r in ipairs(spec.reads) do
    if r.domain ~= "ram" then error("read " .. r.name .. ": domain " .. r.domain .. " does not belong to the nes (ram only)") end
    if r.addr < 0 or r.addr > 0x7FF then error(string.format("read %s: address %X is outside the 2 KB RAM", r.name, r.addr)) end
  end
  table.sort(spec.inputs, function(a, b) return a.frame < b.frame end)
  return spec
end

-- the frame that just completed as a 24-bit BMP (bottom-up rows, BGR); gd string = 11-byte header, then A R G B per pixel, rows top-down
local function savePicture(path)
  local s = gui.gdscreenshot(true)
  local w = s:byte(3) * 256 + s:byte(4)
  local h = s:byte(5) * 256 + s:byte(6)
  if #s < 11 + w * h * 4 then error("gui.gdscreenshot returned " .. #s .. " bytes for " .. w .. "x" .. h) end
  local rowBytes = w * 3
  local pad = (4 - rowBytes % 4) % 4
  local size = 54 + (rowBytes + pad) * h
  local function le32(v) return string.char(v % 256, math.floor(v / 256) % 256, math.floor(v / 65536) % 256, math.floor(v / 16777216) % 256) end
  local function le16(v) return string.char(v % 256, math.floor(v / 256) % 256) end
  local parts = { "BM", le32(size), le32(0), le32(54), le32(40), le32(w), le32(h), le16(1), le16(24), le32(0), le32((rowBytes + pad) * h),
                  le32(2835), le32(2835), le32(0), le32(0) }
  local padStr = string.rep("\0", pad)
  local row = {}
  for y = h - 1, 0, -1 do
    local base = 11 + y * w * 4
    -- in chunks of 64 pixels: string.byte and string.char stay far below Lua's C stack limit
    local chunks = {}
    for x0 = 0, w - 1, 64 do
      local n = math.min(64, w - x0)
      local px = { s:byte(base + x0 * 4 + 1, base + (x0 + n) * 4) }
      for i = 0, n - 1 do
        row[i * 3 + 1] = px[i * 4 + 4]   -- B
        row[i * 3 + 2] = px[i * 4 + 3]   -- G
        row[i * 3 + 3] = px[i * 4 + 2]   -- R
      end
      chunks[#chunks + 1] = string.char(unpack(row, 1, n * 3))
    end
    parts[#parts + 1] = table.concat(chunks) .. padStr
  end
  local f = io.open(path, "wb")
  if not f then error("cannot write " .. path) end
  f:write(table.concat(parts))
  f:close()
  return w, h
end

-- sound: any channel audible now?
local function audibleNow()
  local ok, s = pcall(sound.get)
  if not ok or type(s) ~= "table" or type(s.rp2a03) ~= "table" then return nil end
  for _, ch in pairs(s.rp2a03) do
    if type(ch) == "table" and type(ch.volume) == "number" and ch.volume > 0 then return true end
  end
  return false
end

local function describeRam()
  -- the power-on fill as this run got it (the script runs before the first frame): FCEUX's RAMInitOption 0 is 00 00 00 00 FF FF FF FF
  local pattern = true
  local counts, distinct = {}, 0
  for a = 0, 0x7FF do
    local v = memory.readbyte(a)
    local want = (math.floor(a / 4) % 2 == 1) and 0xFF or 0x00
    if v ~= want then pattern = false end
    if not counts[v] then counts[v] = 0; distinct = distinct + 1 end
    counts[v] = counts[v] + 1
  end
  if pattern then return "00 00 00 00 FF FF FF FF repeated over all 2 KB (FCEUX RAMInitOption 0)" end
  local top = {}
  for v, c in pairs(counts) do top[#top + 1] = { v = v, c = c } end
  table.sort(top, function(a, b) return a.c > b.c end)
  local p = {}
  for i = 1, math.min(3, #top) do p[#p + 1] = string.format("%d x $%02X", top[i].c, top[i].v) end
  local first = {}
  for a = 0, 15 do first[#first + 1] = string.format("%02X", memory.readbyte(a)) end
  return distinct .. " distinct values (" .. table.concat(p, ", ") .. "); $0000-$000F: " .. table.concat(first, " ")
end

local function run()
  local spec = readSpec()
  out = io.open(spec.out .. "\\result.txt", "w")
  if not out then error("cannot write " .. spec.out .. "\\result.txt") end
  out:write("# bdcompat-result 1\n")
  out:write("emu " .. oneLine(emuText) .. "\n")
  local names = {}
  for i, r in ipairs(spec.reads) do names[i] = r.name end
  out:write("reads" .. (#names > 0 and (" " .. table.concat(names, " ")) or "") .. "\n")
  out:write("note memory: memory.readbyte on the CPU bus, $0000-$07FF = the 2 KB system RAM\n")
  out:write("note rom: " .. tostring(rom.getfilename()) .. " (md5 " .. tostring(rom.gethash("md5")) .. ")\n")
  out:write("note power-on RAM (read before the first frame): " .. describeRam() .. "\n")
  out:write("note pictures: gui.gdscreenshot(true) = the frame just completed, all 240 lines, FCEUX's palette, saved as 24-bit BMP\n")
  out:write("note input: joypad.set before each frame (pads 1 and 2, every button forced on or off)\n")
  out:write("note audio: sound.get() channel volumes, one line per 30 frames (FCEUX's sound output is off; the APU is emulated all the same)\n")
  for n in extraNotes:gmatch("[^|]+") do out:write("note " .. oneLine(n) .. "\n") end
  out:flush()

  emu.speedmode("nothrottle")   -- no throttle; measured: every frame is still rendered (pictures identical to a normal-speed run)

  local released = {}
  for _, k in ipairs(ALL) do released[k] = false end
  local held, pos = released, 0
  local audible = false
  local lastAudio = 0
  local cycles0 = debugger.getcyclescount()
  local stalls = 0
  while emu.framecount() < spec.frames do
    local before = emu.framecount()
    while pos < #spec.inputs and spec.inputs[pos + 1].frame <= before do
      pos = pos + 1
      held = spec.inputs[pos].held
    end
    joypad.set(1, held)
    joypad.set(2, released)
    coroutine.yield()            -- the main chunk runs emu.frameadvance() (see the end of the file)
    done = emu.framecount()
    if done == before then
      -- the first call of a session runs no frame (measured); more than a few in a row means FCEUX stopped emulating
      stalls = stalls + 1
      if stalls > 5 then error("emu.frameadvance() returned " .. stalls .. " times without running a frame (framecount " .. done .. ")") end
    elseif done ~= before + 1 then
      error("the frame counter jumped from " .. before .. " to " .. done)
    else
      stalls = 0
      local vals = {}
      for i, r in ipairs(spec.reads) do vals[i] = memory.readbyte(r.addr) end
      out:write("t " .. done .. (#vals > 0 and (" " .. table.concat(vals, " ")) or "") .. "\n")
      if audibleNow() then audible = true end
      if done % 30 == 0 then
        out:write("audio " .. done .. " " .. (audible and 1 or 0) .. "\n")
        audible = false
        lastAudio = done
      end
      if spec.shots[done] then
        local file = string.format("shot_%05d.bmp", done)
        savePicture(spec.out .. "\\" .. file)
        out:write("shot " .. done .. " " .. file .. "\n")
      end
      if done % 30 == 0 or spec.shots[done] then out:flush() end
    end
  end
  if done > lastAudio then out:write("audio " .. done .. " " .. (audible and 1 or 0) .. "\n") end
  local cyc = debugger.getcyclescount() - cycles0
  if done > 0 then
    local per = cyc / done
    local region = (per > 31000) and "PAL or Dendy" or "NTSC"
    out:write(string.format("note timing: %d CPU cycles in %d frames = %.1f per frame (%s)\n", cyc, done, per, region))
  end
end

-- emu.frameadvance() yields the script to FCEUX, and Lua 5.1 cannot yield across pcall ("attempt to yield across metamethod/C-call
-- boundary", measured). So run() is a coroutine that yields whenever it wants a frame, and this loop advances the frame: an error in
-- run() comes back from coroutine.resume as a message, and the result still gets its 'end crashed' line.
local co = coroutine.create(run)
while true do
  local ok, err = coroutine.resume(co)
  if not ok then finish("crashed", "lua: " .. tostring(err)); break end
  if coroutine.status(co) == "dead" then finish("done"); break end
  emu.frameadvance()
end
emu.exit()
