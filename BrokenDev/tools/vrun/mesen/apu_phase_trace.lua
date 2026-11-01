-- ============================================================================
--  VRUN APU phase tracer - MESEN side
-- ============================================================================
--  WHY A SECOND APU TRACE, WHEN state_trace.lua ALREADY HAS 24 APU COLUMNS
--    Those 24 columns are the SETTLED state: periods, resolved volumes, length
--    counters, sweep bits, the DMC pointer, the frame-counter step. They agree
--    with BrokenNes already. What they deliberately leave out is every field
--    that carries the WAVEFORM's phase:
--
--      square1/2.dutyPos          which of the 8 duty steps is being output
--      triangle.sequencePosition  which of the 32 triangle steps
--      noise.shiftRegister        the 15-bit LFSR - i.e. WHICH noise
--      *.timer.timer              the live divider counters
--      *.timer.lastOutput         the channel's actual output sample
--      dmc.outputLevel/shiftRegister/bitsRemaining
--
--    They were excluded because Mesen runs NesApu lazily and emu.getState()
--    does not force a catch-up, so those fields lag the CPU by a variable
--    amount (measured elsewhere in this project: mean 1582 CPU cycles, max
--    2505 at a frame boundary). That lag makes them useless for proving
--    EQUALITY.
--
--    It does not make them useless for detecting GROSS DIVERGENCE, and that
--    distinction is the whole point of this file. A lag of at most 2505 cycles
--    bounds how far apart two correct emulators can look. A defect of the
--    class "this emulator stops clocking the noise LFSR while the channel is
--    muted" does not produce a bounded disagreement - it produces a permanent,
--    unbounded one, because the two LFSRs never resynchronise. Signal far
--    larger than the noise floor is still a measurement.
--
--    So: this trace records the phase fields, and the differ that consumes it
--    must classify per field as
--      AGREE            identical
--      LAG-CONSISTENT   differs, but by an amount the 2505-cycle lag explains
--      DIVERGENT        differs by more than any lag can explain
--    and only the third class is a finding. See apu-phase-diff.ps1.
--
--  RECORD FORMAT  UTF-8, LF. '#' lines are header. One record per frame, taken
--  at emu.eventType.endFrame, frame 0 upwards, columns pipe-separated:
--
--    frame|cpuCycle|apuPrevCycle|
--    p1duty|p1dutyPos|p1timer|p1period|p1out|
--    p2duty|p2dutyPos|p2timer|p2period|p2out|
--    tseq|ttimer|tperiod|tout|
--    nshift|ntimer|nperiod|nout|nmode|
--    dout|dshift|dbits|dsilence|dtimer|
--    fcstep|fcprev
--
--  ENV
--    VRUN_PHASE_OUT     output path
--    VRUN_PHASE_FRAMES  frames to run
--    VRUN_PHASE_INPUT   BrokenNes --input syntax
--    VRUN_PHASE_RAMFILL fceux (default) | none
-- ============================================================================

local OUT = os.getenv("VRUN_PHASE_OUT") or "apu_phase_mesen.txt"
local FRAMES = tonumber(os.getenv("VRUN_PHASE_FRAMES") or "") or 2400
local INPUT_SCRIPT = os.getenv("VRUN_PHASE_INPUT") or ""
local RAMFILL = string.lower(os.getenv("VRUN_PHASE_RAMFILL") or "fceux")

local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end
local function w(...) f:write(...) f:write("\n") end

-- Power-on RAM at SCRIPT LOAD (cpu.cycleCount == 7), never from a frame
-- callback - a frame late it stomps RAM the game has already initialised.
if RAMFILL == "fceux" then
	for a = 0, 2047 do
		emu.write(a, ((a % 8) < 4) and 0x00 or 0xFF, emu.memType.nesInternalRam)
	end
end

-- Input on inputPolled ONLY: by endFrame the pad is already latched and
-- emu.setInput is silently discarded.
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

-- TWO frame counters. `inputFrame` (startFrame, PPU scanline -1 cycle 0 - the true start
-- of a frame) drives the INPUT SCHEDULE; `frame` (endFrame, scanline 240) labels records.
-- Using the endFrame counter for input as well is a silent one-frame error: VRUN polls the
-- controller during vblank, AFTER scanline 240, so the poll belonging to emulated frame N
-- happens once the endFrame counter has already ticked to N+1 - every press lands a frame
-- later than the same script produces in BrokenNes. Measured on game.nes: with the naive
-- counter the two APU register-write streams differed by three writes and Mesen with the
-- press moved to frame 61 reproduced BrokenNes' stream exactly; with this counter the
-- streams are byte-identical over 3142 writes.
local frame = 0
local inputFrame = 0
local curHeld = {}
local nextStep = 1
local emu_setInput = emu.setInput
emu.addEventCallback(function() inputFrame = inputFrame + 1 end, emu.eventType.startFrame)
emu.addEventCallback(function()
	while nextStep <= #steps and steps[nextStep].frame <= inputFrame do
		curHeld = steps[nextStep].held
		nextStep = nextStep + 1
	end
	emu_setInput(curHeld, 0)
end, emu.eventType.inputPolled)

w("# apu_phase_trace.lua v1.0 - MESEN side")
w("# rom: ", tostring(os.getenv("VRUN_PHASE_ROM") or "(see --testRunner arg)"))
w("# frames: ", tostring(FRAMES), "  ramfill: ", RAMFILL)
w("# input: ", INPUT_SCRIPT)
w("# NOTE Mesen runs NesApu lazily; these fields lag the CPU by a bounded but")
w("# variable amount (mean ~1582, max ~2505 CPU cycles at a frame boundary).")
w("# Treat a bounded disagreement as lag, an unbounded one as a defect.")
w("# columns: frame|cpuCycle|apuPrevCycle|p1duty|p1dutyPos|p1timer|p1period|p1out|p2duty|p2dutyPos|p2timer|p2period|p2out|tseq|ttimer|tperiod|tout|nshift|ntimer|nperiod|nout|nmode|dout|dshift|dbits|dsilence|dtimer|fcstep|fcprev")

local function b2i(v) return v and 1 or 0 end

-- UNITS. The period columns must be in the SAME units as BrokenNes' or the diff
-- reports unit conversions as emulation defects. Measured on this build:
--   square:   apu.squareN.realPeriod IS the 11-bit register. apu.squareN.timer.period
--             is the internal divider (realPeriod*2+1) - and it stays 0 until the
--             ROM's first period write, so deriving the register from it reports a
--             fake divergence on every frame before that write. Use realPeriod.
--   triangle: apu.triangle.timer.period already IS the 11-bit register.
--   noise:    apu.noise.timer.period is the NTSC table value MINUS ONE, so add it
--             back to get the hardware-defined cycle count BrokenNes reports.
local function noisePeriod(s) return s["apu.noise.timer.period"] + 1 end

emu.addEventCallback(function()
	local s = emu.getState()
	w(frame,
	  "|", s["cpu.cycleCount"],
	  "|", s["apu.frameCounter.previousCycle"],
	  "|", s["apu.square1.duty"],
	  "|", s["apu.square1.dutyPos"],
	  "|", s["apu.square1.timer.timer"],
	  "|", s["apu.square1.realPeriod"],
	  "|", s["apu.square1.timer.lastOutput"],
	  "|", s["apu.square2.duty"],
	  "|", s["apu.square2.dutyPos"],
	  "|", s["apu.square2.timer.timer"],
	  "|", s["apu.square2.realPeriod"],
	  "|", s["apu.square2.timer.lastOutput"],
	  "|", s["apu.triangle.sequencePosition"],
	  "|", s["apu.triangle.timer.timer"],
	  "|", s["apu.triangle.timer.period"],
	  "|", s["apu.triangle.timer.lastOutput"],
	  "|", s["apu.noise.shiftRegister"],
	  "|", s["apu.noise.timer.timer"],
	  "|", noisePeriod(s),
	  "|", s["apu.noise.timer.lastOutput"],
	  "|", b2i(s["apu.noise.modeFlag"]),
	  "|", s["apu.dmc.outputLevel"],
	  "|", s["apu.dmc.shiftRegister"],
	  "|", s["apu.dmc.bitsRemaining"],
	  "|", b2i(s["apu.dmc.silenceFlag"]),
	  "|", s["apu.dmc.timer.timer"],
	  "|", s["apu.frameCounter.currentStep"],
	  "|", s["apu.frameCounter.previousCycle"])
	frame = frame + 1
	if frame >= FRAMES then f:close() emu.stop(0) end
end, emu.eventType.endFrame)
