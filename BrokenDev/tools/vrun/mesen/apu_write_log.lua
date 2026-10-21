-- ============================================================================
--  APU register-write stream - MESEN side
-- ============================================================================
--  The write stream is the ONE part of the audio path that is exactly
--  comparable between two emulators. It is what the game asked the sound
--  hardware to do, in hardware units, at instruction boundaries: no lazy-update
--  lag, no mixer, no resampler. So it cleanly splits any audio difference into
--  "the two machines asked for different music" (a CPU/timing divergence that
--  has nothing to do with the APU) and "they asked for the same music and it
--  came out different" (an APU defect).
--
--  Deliberately excludes $4014 (OAM DMA) and $4016 (controller strobe): they
--  share the $40xx block but are not APU registers, and counting them makes a
--  silent game look busy - a trap this investigation already hit once.
--
--  Output columns match ApuAudioCli --write-log exactly:  frame|addr|value
--  with addr as 4 lower-case hex digits and value as 2, one line per write.
--
--  ENV VRUN_WLOG_OUT, VRUN_WLOG_FRAMES, VRUN_WLOG_INPUT, VRUN_WLOG_RAMFILL
-- ============================================================================

local OUT = os.getenv("VRUN_WLOG_OUT") or "apu_write_log.txt"
local FRAMES = tonumber(os.getenv("VRUN_WLOG_FRAMES") or "") or 2400
local INPUT_SCRIPT = os.getenv("VRUN_WLOG_INPUT") or ""
local RAMFILL = string.lower(os.getenv("VRUN_WLOG_RAMFILL") or "fceux")

local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end
local function w(...) f:write(...) f:write("\n") end

-- Power-on RAM at script load (cpu.cycleCount == 7), never from a callback.
if RAMFILL == "fceux" then
	for a = 0, 2047 do
		emu.write(a, ((a % 8) < 4) and 0x00 or 0xFF, emu.memType.nesInternalRam)
	end
end

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

-- TWO frame counters, and the difference between them is load-bearing.
--
--   inputFrame  advanced on startFrame (PPU scanline -1, cycle 0 - the true start of a
--               frame). This is the one the INPUT SCHEDULE uses.
--   frame       advanced on endFrame, which Mesen fires at scanline 240. This is the one
--               records are LABELLED with, matching the rest of this directory.
--
-- Using `frame` for input as well looks natural and is wrong. VRUN polls the controller
-- during vblank - i.e. AFTER scanline 240 - so a poll belonging to emulated frame N
-- happens when the endFrame-driven counter has already ticked to N+1, and every button
-- press lands one frame later than the same script produces in BrokenNes, whose loop sets
-- the pad before RunFrame(). Measured on game.nes with "60:Start,66:": the APU register
-- write streams differed by exactly three writes, and Mesen with the press moved to frame
-- 61 reproduced BrokenNes' stream byte for byte - i.e. the whole "divergence" was this
-- offset. Driving input from startFrame removes it at the source.
local frame, inputFrame, curHeld, nextStep = 0, 0, {}, 1
local emu_setInput = emu.setInput
emu.addEventCallback(function() inputFrame = inputFrame + 1 end, emu.eventType.startFrame)
emu.addEventCallback(function()
	while nextStep <= #steps and steps[nextStep].frame <= inputFrame do
		curHeld = steps[nextStep].held; nextStep = nextStep + 1
	end
	emu_setInput(curHeld, 0)
end, emu.eventType.inputPolled)

w("# apu_write_log.lua v1.0 - MESEN side")
w("# frames: ", tostring(FRAMES), "  ramfill: ", RAMFILL)
w("# input: ", INPUT_SCRIPT)
w("# columns: frame|addr|value   (hex addr, hex value, one line per APU register write)")

emu.addMemoryCallback(function(addr, value)
	-- $4014 is OAM DMA and $4016 the controller port; neither reaches the APU.
	if addr == 0x4014 or addr == 0x4016 then return end
	w(frame, "|", string.format("%04x", addr), "|", string.format("%02x", value))
end, emu.callbackType.write, 0x4000, 0x4017, emu.cpuType.nes)

emu.addEventCallback(function()
	frame = frame + 1
	if frame >= FRAMES then f:close() emu.stop(0) end
end, emu.eventType.endFrame)
