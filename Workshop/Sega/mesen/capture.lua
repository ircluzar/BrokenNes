-- Mesen 2.1.1 capture script for the Master System / Game Gear oracle comparison (black box: run Mesen, take its screenshots and state at fixed frame counts).
-- Run by mesen-capture.ps1:  Mesen.exe --testRunner <rom> capture.lua   (environment variables below)
--
--   MESEN_OUT     directory the files go to (must exist)
--   MESEN_TAG     file name prefix
--   MESEN_FRAMES  comma list of frame counts (ranges "a-b" allowed): the picture of frame N is written to <tag>_f<N>.png. Mesen's screenshot call returns the frame before the one just completed
--                 (one frame of lag, measured by comparing it with this emulator's frames), so the script takes it after frame N+1 and names it N. MESEN_LAG overrides the lag (default 1).
--   MESEN_INPUT   "frame:Btn+Btn,frame:"  held buttons per frame (frames counted from 0, an entry holds until the next; applied when the game polls, by the number of completed frames).
--                 Names are the Workshop's PadButtons names: Up Down Left Right A B Start Select  (see keyOf below)
--   MESEN_GG      "1" when the ROM is a Game Gear game (Start is the pause button there)
--   MESEN_DUMP    "1" also writes <tag>_f<N>.state.txt (cpu.* and vdp.* keys) and <tag>_f<N>.vram.bin / .cram.bin / .ram.bin (16 KB VRAM, 64 B colour RAM, 8 KB work RAM)
--   MESEN_STATE   "1" also writes <tag>_f<N>.state.txt (the whole emu.getState table, sorted)
--   MESEN_PORTS   "1" logs every Z80 port write as "frame port value" to <tag>.ports.txt (read callbacks are not available)
--   MESEN_EXEC    "a-b" logs the address of every executed instruction while frames a..b run, to <tag>.exec.txt (compare with the Workshop's --trace-pc);
--                 with MESEN_EXECREGS=1 each line also carries AF BC DE HL SP as they are before the instruction (slow: reads the whole state per instruction)
--
-- The screenshot is whatever Mesen shows (256 x 192 for the Master System, 160 x 144 for the Game Gear with the settings mesen-capture.ps1 writes).

local LAG = tonumber(os.getenv("MESEN_LAG") or "1")
local outDir = os.getenv("MESEN_OUT") or "."
local tag = os.getenv("MESEN_TAG") or "shot"
local wanted, last = {}, 0
for token in (os.getenv("MESEN_FRAMES") or ""):gmatch("[^,]+") do
  local a, b = token:match("^(%d+)%-(%d+)$")      -- "300-400" is every frame from 300 to 400
  if not a then a = token:match("^(%d+)$"); b = a end
  a, b = tonumber(a), tonumber(b)
  for n = a, b do wanted[n] = true end
  if b > last then last = b end
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
local done = 0      -- frames completed

-- (the script is sorted and `index` only grows: a position walks along it, instead of the whole script being scanned at every poll, which made a 20,000-entry play of 60,000 frames crawl)
local scriptPos = 0
local function applyScript(index)
  while scriptPos < #script and script[scriptPos + 1].frame <= index do
    scriptPos = scriptPos + 1
    held = script[scriptPos].held
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
  -- the cartridge RAM (a battery game's save): up to 32 KB, as far as the oracle's cartridge has any
  local sram = {}
  for i = 0, 0x7FFF do
    local ok, v = pcall(emu.read, i, emu.memType.smsCartRam)
    if not ok then break end
    sram[#sram + 1] = string.char(v & 0xFF)
  end
  writeFile(outDir .. "/" .. tag .. "_f" .. n .. ".sram.bin", table.concat(sram))
end

-- Mesen starts the VDP at a random scanline (the Z80 and the VDP are not aligned at power-on, and a game that depends on it shows different frames run to run). The line is written to
-- <tag>.phase.txt as "scanline=<n> vdpcycle=<dots> r=<R>" at the first instruction, so the Workshop can start this emulator's VDP at the same line (--start-line / --mesen).
do
  local done = os.getenv("MESEN_STATE_IN") ~= nil and os.getenv("MESEN_STATE_IN") ~= ""   -- a run that continues from a state keeps the first run's phase file
  emu.addMemoryCallback(function(address, value)
    if done then return end
    done = true
    local st = emu.getState()
    local f = assert(io.open(outDir .. "/" .. tag .. ".phase.txt", "wb"))
    f:write(string.format("scanline=%d vdpcycle=%d r=%d vcounter=%d counter=%s latch=%s linepend=%s vintpend=%s\n", st["vdp.scanline"], st["vdp.cycle"], st["cpu.r"], st["vdp.vcounter"],
      tostring(st["vdp.scanlineCounter"]), tostring(st["vdp.scanlineCounterLatch"]), tostring(st["vdp.scanlineIrqPending"]), tostring(st["vdp.verticalBlankIrqPending"])))
    f:close()
  end, emu.callbackType.exec, 0x0000, 0x0000, emu.cpuType.sms, emu.memType.smsMemory)
end

-- MESEN_PORTS=1: every Z80 port write as "frame port value" (frame = the 1-based number of the frame running), to compare with the Workshop's --trace-io
local portLog = nil
if os.getenv("MESEN_PORTS") == "1" then
  portLog = assert(io.open(outDir .. "/" .. tag .. ".ports.txt", "wb"))
  emu.addMemoryCallback(function(address, value)
    portLog:write(string.format("%d %02X %02X\n", done + 1, address & 0xFF, value & 0xFF))
  end, emu.callbackType.write, 0x00, 0xFF, emu.cpuType.sms, emu.memType.smsPort)
end

-- MESEN_EXEC="a-b": every executed instruction's address (hex, one per line) while frames a..b run (frame = 1-based number of the frame running), to <tag>.exec.txt
local execLog, execFrom, execTo = nil, 0, 0
do
  local a, b = (os.getenv("MESEN_EXEC") or ""):match("^(%d+)%-(%d+)$")
  if a then
    execFrom, execTo = tonumber(a), tonumber(b)
    execLog = assert(io.open(outDir .. "/" .. tag .. ".exec.txt", "wb"))
    local withRegs = os.getenv("MESEN_EXECREGS") == "1"
    local withCycles = os.getenv("MESEN_EXECCYC") == "1"   -- "pc cycleCount": the Z80's own cycle count before the instruction (compare with --trace-pc-cyc)
    emu.addMemoryCallback(function(address, value)
      if done + 1 >= execFrom and done + 1 <= execTo then
        if withCycles then
          execLog:write(string.format("%04X %d\n", address, emu.getState()["cpu.cycleCount"]))
        elseif withRegs then
          local st = emu.getState()
          execLog:write(string.format("%04X %02X%02X %02X%02X %02X%02X %02X%02X %04X\n", address, st["cpu.a"], st["cpu.flags"], st["cpu.b"], st["cpu.c"], st["cpu.d"], st["cpu.e"], st["cpu.h"], st["cpu.l"], st["cpu.sp"]))
        else
          execLog:write(string.format("%04X\n", address))
        end
      end
    end, emu.callbackType.exec, 0x0000, 0xFFFF, emu.cpuType.sms, emu.memType.smsMemory)
  end
end

-- MESEN_WATCH="0191,0200": each execution of those instruction addresses is logged as "frame pc cycleCount scanline vdpcycle" to <tag>.watch.txt (frame = 1-based number of the frame running);
-- the cycle count is the Z80's own count since power-on, so it compares with the Workshop's TotalCycles at the same instruction (--watch-pc).
local watchLog = nil
do
  local list = os.getenv("MESEN_WATCH") or ""
  if list ~= "" then
    watchLog = assert(io.open(outDir .. "/" .. tag .. ".watch.txt", "wb"))
    for hex in list:gmatch("[^,]+") do
      local pc = tonumber(hex, 16)
      emu.addMemoryCallback(function(address, value)
        local st = emu.getState()
        -- the last columns: the frame interrupt flag and its enable, the line interrupt flag and its enable, the CPU's interrupt inputs (cpu.activeIrqs) and its IFF1
        watchLog:write(string.format("%d %04X %d %d %d vint=%s vie=%s line=%s lie=%s irqs=%s iff1=%s lc=%s latch=%s\n", done + 1, address, st["cpu.cycleCount"], st["vdp.scanline"], st["vdp.cycle"],
          tostring(st["vdp.verticalBlankIrqPending"]), tostring(st["vdp.enableVerticalBlankIrq"]), tostring(st["vdp.scanlineIrqPending"]), tostring(st["vdp.enableScanlineIrq"]),
          tostring(st["cpu.activeIrqs"]), tostring(st["cpu.iff1"]), tostring(st["vdp.scanlineCounter"]), tostring(st["vdp.scanlineCounterLatch"])))
      end, emu.callbackType.exec, pc, pc, emu.cpuType.sms, emu.memType.smsMemory)
    end
  end
end

-- MESEN_VWATCH="3F00-3FFF[,c]" with MESEN_VFRAMES="a-b": every write to VRAM addresses (or colour RAM with ",c") in those frames as "frame scanline dot addr value cpucycle" to <tag>.vwatch.txt
-- (the scanline and dot are the VDP's own at the moment the write happens, which is when the chip commits it: compare with the Workshop's --watch-vram)
local vwatchLog = nil
do
  local spec = os.getenv("MESEN_VWATCH") or ""
  local lo, hi, cram = spec:match("^(%x+)%-(%x+)(,?c?)$")
  if lo then
    local fa, fb = (os.getenv("MESEN_VFRAMES") or "0-999999"):match("^(%d+)%-(%d+)$")
    fa, fb = tonumber(fa), tonumber(fb)
    lo, hi = tonumber(lo, 16), tonumber(hi, 16)
    vwatchLog = assert(io.open(outDir .. "/" .. tag .. ".vwatch.txt", "wb"))
    emu.addMemoryCallback(function(address, value)
      if done + 1 < fa or done + 1 > fb then return end
      local st = emu.getState()
      vwatchLog:write(string.format("%d %d %d %04X %02X %d\n", done + 1, st["vdp.scanline"], st["vdp.cycle"], address, value & 0xFF, st["cpu.cycleCount"]))
    end, emu.callbackType.write, lo, hi, emu.cpuType.sms, cram == ",c" and emu.memType.smsPaletteRam or emu.memType.smsVideoRam)
  end
end

-- MESEN_MWATCH="C012[-C020]" with MESEN_VFRAMES="a-b": every Z80 memory write to those addresses as "frame cycleCount value pc" to <tag>.mwatch.txt (compare with the Workshop's --watch-write)
local mwatchLog = nil
do
  local lo, hi = (os.getenv("MESEN_MWATCH") or ""):match("^(%x+)%-?(%x*)$")
  if lo then
    local fa, fb = (os.getenv("MESEN_VFRAMES") or "0-999999"):match("^(%d+)%-(%d+)$")
    fa, fb = tonumber(fa), tonumber(fb)
    lo = tonumber(lo, 16); hi = hi ~= "" and tonumber(hi, 16) or lo
    mwatchLog = assert(io.open(outDir .. "/" .. tag .. ".mwatch.txt", "wb"))
    emu.addMemoryCallback(function(address, value)
      if done + 1 < fa or done + 1 > fb then return end
      local st = emu.getState()
      mwatchLog:write(string.format("%d %d %04X %02X %04X\n", done + 1, st["cpu.cycleCount"], address, value & 0xFF, st["cpu.pc"] or 0))
    end, emu.callbackType.write, lo, hi, emu.cpuType.sms, emu.memType.smsMemory)
  end
end

-- MESEN_PSG="1": the sound chips' registers at the end of every frame, one line per frame to <tag>.psg.txt as "frame tone0 tone1 tone2 vol0 vol1 vol2 volNoise noiseControl panning" (decimal: the three tone periods, the four
-- attenuations, the noise register, the Game Gear stereo mask), and with MESEN_FM="1" <tag>.fm.txt as "frame audioControl reg0 ... reg63" (the YM2413's register file). The oracle's port-write callbacks do not fire (the SMS port space is not
-- reachable by them), so the chips' state at the frame's end is what an event-level comparison with this emulator's chips can use (Workshop --sms-run --sound-regs / --fm-regs-file).
local psgLog, fmLog = nil, nil
if os.getenv("MESEN_PSG") == "1" then psgLog = assert(io.open(outDir .. "/" .. tag .. ".psg.txt", "wb")) end
if os.getenv("MESEN_FM") == "1" then fmLog = assert(io.open(outDir .. "/" .. tag .. ".fm.txt", "wb")) end
local function logSound(n)
  local st = emu.getState()
  if psgLog then
    psgLog:write(string.format("%d %d %d %d %d %d %d %d %d %d\n", n, st["psg.tone[0].reloadValue"], st["psg.tone[1].reloadValue"], st["psg.tone[2].reloadValue"],
      st["psg.tone[0].volume"], st["psg.tone[1].volume"], st["psg.tone[2].volume"], st["psg.noise.volume"], st["psg.noise.control"], st["psg.gameGearPanningReg"]))
  end
  if fmLog then
    local t = { tostring(n), tostring(st["fmAudio.audioControl"]) }
    for i = 0, 63 do t[#t + 1] = tostring(st["fmAudio.opll->reg" .. i]) end
    fmLog:write(table.concat(t, " ") .. "\n")
  end
end

-- MESEN_STATE_OUT="file:N" writes the oracle's own save state after frame N to that file and stops; MESEN_STATE_IN="file:N" continues from such a state as if frame N had just completed.
-- Mesen's test runner ends every run after exactly 100 seconds (measured: about 38,000 frames of a bare script, fewer with dumps), so a longer play is made in segments (mesen-capture.ps1 -Segment).
-- The oracle only lets a script create or load a state inside an exec callback of the main CPU: a callback that removes itself after the first instruction is registered for the moment.
local stateOutPath, stateOutFrame = (os.getenv("MESEN_STATE_OUT") or ""):match("^(.+):(%d+)$")
local stateInPath, stateInFrame = (os.getenv("MESEN_STATE_IN") or ""):match("^(.+):(%d+)$")
stateOutFrame = tonumber(stateOutFrame); stateInFrame = tonumber(stateInFrame)
local function onceAtNextInstruction(fn)
  local id
  id = emu.addMemoryCallback(function(address, value)
    emu.removeMemoryCallback(id, emu.callbackType.exec, 0x0000, 0xFFFF, emu.cpuType.sms, emu.memType.smsMemory)
    fn()
  end, emu.callbackType.exec, 0x0000, 0xFFFF, emu.cpuType.sms, emu.memType.smsMemory)
end
local stateLoaded = false

-- the input of frame index f (0-based; the frame that starts at the end of the picture of frame f-1) is what the game reads when it polls while `done` frames have completed
emu.addEventCallback(function()
  applyScript(done)
  emu.setInput(held, 0)
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
  done = done + 1
  if execLog and done + 1 >= execFrom and done <= execTo then execLog:write("F" .. (done + 1) .. "\n") end   -- marks where frame done+1 starts
  -- the screenshot Mesen takes in this callback is the picture of the frame BEFORE the one just completed (measured: one frame of lag), so frame N's picture is taken at completed frame N+1
  if wanted[done - LAG] and done - LAG >= 1 then
    writeFile(outDir .. "/" .. tag .. "_f" .. (done - LAG) .. ".png", emu.takeScreenshot())
  end
  if (psgLog or fmLog) and done <= last then logSound(done) end
  -- state and memory are not delayed: they are the state at the end of frame N
  if wanted[done] then
    if os.getenv("MESEN_DUMP") == "1" or os.getenv("MESEN_STATE") == "1" then dumpState(done) end
    if os.getenv("MESEN_DUMP") == "1" then dumpVdp(done) end
  end
  if stateInPath and not stateLoaded then
    stateLoaded = true
    onceAtNextInstruction(function()
      local g = assert(io.open(stateInPath, "rb")); local s = g:read("*a"); g:close()
      emu.loadSavestate(s)
      done = stateInFrame
    end)
  end
  if stateOutPath and done == stateOutFrame then
    onceAtNextInstruction(function()
      local s = emu.createSavestate()
      local g = assert(io.open(stateOutPath, "wb")); g:write(s); g:close()
      if psgLog then psgLog:close() end
      if fmLog then fmLog:close() end
      emu.stop(0)
    end)
  end
  if done >= last + LAG and not stateOutPath then
    if portLog then portLog:close() end
    if execLog then execLog:close() end
    if watchLog then watchLog:close() end
    if vwatchLog then vwatchLog:close() end
    if mwatchLog then mwatchLog:close() end
    if psgLog then psgLog:close() end
    if fmLog then fmLog:close() end
    emu.stop(0)
  end
end, emu.eventType.endFrame)
