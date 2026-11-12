-- Mesen 2.1.1 side of the Nested certification lockstep (Workshop --nested-cert): the same buttons per controller POLL, the NES work RAM per poll.
-- Run:  Mesen.exe <rom> --luaScriptsToLoad mesen-poll-lockstep.lua --testRunner [--timeout=N]      (a private Mesen copy; never the user's install)
--
--   MESEN_OUT     output directory: <tag>.ram (2048 bytes per poll), <tag>.polls.txt ("poll frame" per poll), <tag>.frames.txt (polls per frame)
--   MESEN_TAG     file name prefix
--   MESEN_SYSTEM  nes | snes. NES: a poll = a CPU write to $4016 with bit 0 clear. SNES (a Project Nested ROM): a poll = a write to $4016 with bit 0 set
--                 (Nested turns each NES $4016=0 write into a SNES 1/0 strobe); the NES RAM image is SNES work RAM $0000-$07FF.
--   MESEN_POLLS   the CSV Workshop --nested-cert writes (<stem>.nested-cert.polls.csv): column 4 = the NES buttons of each poll (hex; A B Select Start
--                 Up Down Left Right = bits 0..7). SNES: NES A = SNES B, NES B = SNES Y (Nested's default, non-custom input).
--   MESEN_MAXPOLLS stop after this many polls (default: the CSV's count).
-- Measures only.

local outDir = os.getenv("MESEN_OUT") or "."
local tag = os.getenv("MESEN_TAG") or "mesen"
local snes = (os.getenv("MESEN_SYSTEM") or "nes"):lower() == "snes"
local buttons = {}
for line in io.lines(os.getenv("MESEN_POLLS")) do
  local p, nf, sf, b = line:match("^(%d+),(%d+),(%d+),(%x+)")
  if p then buttons[#buttons + 1] = tonumber(b, 16) end
end
local maxPolls = tonumber(os.getenv("MESEN_MAXPOLLS") or "") or #buttons

local nesNames = { "a", "b", "select", "start", "up", "down", "left", "right" }
local snesNames = { "b", "y", "select", "start", "up", "down", "left", "right" }
local function inputFor(bits)
  local t = {}
  local names = snes and snesNames or nesNames
  for i = 0, 7 do if (bits >> i) & 1 == 1 then t[names[i + 1]] = true end end
  return t
end

local ramFile = assert(io.open(outDir .. "/" .. tag .. ".ram", "wb"))
local pollFile = assert(io.open(outDir .. "/" .. tag .. ".polls.txt", "w"))
local frameFile = assert(io.open(outDir .. "/" .. tag .. ".frames.txt", "w"))
local polls, frame, inFrame = 0, 0, 0
local memType = snes and emu.memType.snesWorkRam or emu.memType.nesInternalRam

local function sample()
  local t = {}
  for a = 0, 2047 do t[a + 1] = string.char(emu.read(a, memType)) end
  ramFile:write(table.concat(t))
  pollFile:write(polls .. " " .. (frame + 1) .. "\n")
end

local function onWrite(address, value)
  local strobe = snes and (value & 1) == 1 or (not snes and (value & 1) == 0)
  if not strobe then return end
  if polls < maxPolls then sample() end
  polls = polls + 1
  inFrame = inFrame + 1
end

if snes then
  -- $4016 answers in every bank $00-$3F and $80-$BF; Nested writes it with whatever data bank its IO code runs under.
  for bank = 0, 0xBF do
    if bank < 0x40 or bank >= 0x80 then
      local a = bank * 0x10000 + 0x4016
      emu.addMemoryCallback(onWrite, emu.callbackType.write, a, a, emu.cpuType.snes, emu.memType.snesMemory)
    end
  end
else
  emu.addMemoryCallback(onWrite, emu.callbackType.write, 0x4016, 0x4016, emu.cpuType.nes, emu.memType.nesMemory)
end

-- The buttons of the poll being latched: Mesen raises inputPolled on the strobe-HIGH write (NES: before the $4016=0 write counted as that
-- poll, so the poll being latched is `polls`. SNES: Nested latches on the strobe-high write that onWrite has just counted: polls-1.
-- MESEN_POLL_OFFSET overrides (measured: with the wrong one, the first press lands one poll late and the RAM streams split there).
local offset = tonumber(os.getenv("MESEN_POLL_OFFSET") or "") or 0
emu.addEventCallback(function()
  local k = polls + offset
  if k < 0 then k = 0 end
  if k >= #buttons then k = #buttons - 1 end
  emu.setInput(inputFor(buttons[k + 1] or 0), 0)
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
  frame = frame + 1
  frameFile:write(inFrame .. "\n")
  inFrame = 0
  if polls >= maxPolls or (polls == 0 and frame >= 3000) then
    ramFile:close(); pollFile:close(); frameFile:close()
    emu.stop(0)
  end
end, emu.eventType.endFrame)
