-- ============================================================================
--  Where does VRUN actually make sound?
-- ============================================================================
--  Before anything can be compared, the audio work needs a frame range in
--  which the ROM is genuinely driving the APU. A first probe at frame 250
--  found the only writes in $4000-$4017 were $4014 (OAM DMA) and $4016
--  (controller strobe) - i.e. not a single APU register write, so Mesen's
--  lazily-run APU sat frozen and every "phase" reading was meaningless. That
--  was a silent stretch of the game, not a harness fault.
--
--  This emits one line per frame: how many writes landed on the REAL APU
--  registers ($4000-$4013, $4015, $4017 - deliberately excluding $4014/$4016,
--  which are the DMA and controller ports sharing that address block), broken
--  out per channel group, plus app_state ($03AD: 0=Title 1=Game 2=Death
--  3=Shop 4=Ending 5=Options) so a run of silence can be attributed to the
--  screen the game is on.
--
--  ENV
--    VRUN_CENSUS_OUT     output path
--    VRUN_CENSUS_FRAMES  frames to run (default 1800)
--    VRUN_CENSUS_INPUT   BrokenNes --input syntax: "60:Start,66:,120:Right+A"
--    VRUN_CENSUS_RAMFILL fceux (default) | none
-- ============================================================================

local OUT = os.getenv("VRUN_CENSUS_OUT") or "apu_census.txt"
local FRAMES = tonumber(os.getenv("VRUN_CENSUS_FRAMES") or "") or 1800
local INPUT_SCRIPT = os.getenv("VRUN_CENSUS_INPUT") or ""
local RAMFILL = string.lower(os.getenv("VRUN_CENSUS_RAMFILL") or "fceux")

local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end
local function w(...) f:write(...) f:write("\n") end

------------------------------------------------------------------------------
-- Power-on RAM. MUST happen here, in the top-level chunk, not from a frame
-- callback: Mesen runs this chunk at cpu.cycleCount == 7, after the 6502 reset
-- sequence and before the ROM's first instruction. Applying it a frame later
-- stomps RAM the game has already initialised and wedges it (pc parks at
-- 0x180d) - a trap this project has already paid for once.
------------------------------------------------------------------------------
if RAMFILL == "fceux" then
	-- BrokenNes' Bus ctor writes FCEUX's repeating 8-byte pattern; Mesen's own
	-- RamPowerOnState has no equivalent, so it is written in explicitly.
	for a = 0, 2047 do
		emu.write(a, ((a % 8) < 4) and 0x00 or 0xFF, emu.memType.nesInternalRam)
	end
end

------------------------------------------------------------------------------
-- Input. Applied on inputPolled, NEVER on endFrame: by endFrame the pad is
-- already latched and emu.setInput is silently discarded, which makes a
-- "traced" run that actually pressed nothing.
------------------------------------------------------------------------------
local steps = {}
for chunk in string.gmatch(INPUT_SCRIPT, "[^,]+") do
	local fr, btns = string.match(chunk, "^%s*(%d+)%s*:%s*(.-)%s*$")
	if fr then
		local held = {}
		for b in string.gmatch(btns, "[^%+]+") do
			b = string.lower((string.gsub(b, "%s", "")))
			if b ~= "" then held[b] = true end
		end
		steps[#steps + 1] = { frame = tonumber(fr), held = held }
	end
end
table.sort(steps, function(a, b) return a.frame < b.frame end)

local frame = 0
local curHeld = {}
local nextStep = 1
local emu_setInput = emu.setInput

emu.addEventCallback(function()
	while nextStep <= #steps and steps[nextStep].frame <= frame do
		curHeld = steps[nextStep].held
		nextStep = nextStep + 1
	end
	emu_setInput(curHeld, 0)   -- (input, port): table FIRST for this build
end, emu.eventType.inputPolled)

------------------------------------------------------------------------------
-- The census itself.
------------------------------------------------------------------------------
local nPulse, nTri, nNoise, nDmc, nStatus, nFrameCtr = 0, 0, 0, 0, 0, 0

emu.addMemoryCallback(function(addr)
	if addr >= 0x4000 and addr <= 0x4007 then nPulse = nPulse + 1
	elseif addr >= 0x4008 and addr <= 0x400B then nTri = nTri + 1
	elseif addr >= 0x400C and addr <= 0x400F then nNoise = nNoise + 1
	elseif addr >= 0x4010 and addr <= 0x4013 then nDmc = nDmc + 1
	elseif addr == 0x4015 then nStatus = nStatus + 1
	elseif addr == 0x4017 then nFrameCtr = nFrameCtr + 1 end
end, emu.callbackType.write, 0x4000, 0x4017, emu.cpuType.nes)

w("# frame|appState|pulseWrites|triWrites|noiseWrites|dmcWrites|statusWrites|frameCtrWrites|total")

emu.addEventCallback(function()
	local total = nPulse + nTri + nNoise + nDmc + nStatus + nFrameCtr
	w(frame, "|", emu.read(0x03AD, emu.memType.nesMemory),
	  "|", nPulse, "|", nTri, "|", nNoise, "|", nDmc, "|", nStatus, "|", nFrameCtr, "|", total)
	nPulse, nTri, nNoise, nDmc, nStatus, nFrameCtr = 0, 0, 0, 0, 0, 0
	frame = frame + 1
	if frame >= FRAMES then f:close() emu.stop(0) end
end, emu.eventType.endFrame)
