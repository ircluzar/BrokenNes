-- Census of every $4001/$4005 (pulse sweep) write VRUN makes.
--
-- WHY: APU_FIX's Sweep() takes negate, shift and period but NOT the sweep
-- ENABLE bit ($4001/$4005 bit 7) - it adjusts the channel's period whenever the
-- shift count is non-zero. Hardware only adjusts the period when the sweep is
-- ENABLED and the shift count is non-zero (the separate "muting" rule, which
-- does ignore the enable bit, is implemented correctly elsewhere in that file).
-- So a write with bit7 = 0 and a non-zero shift is the exact input that would
-- make BrokenNes sweep a note that hardware holds steady.
--
-- This counts how often the ROM actually produces that input, so the defect can
-- be reported as live or as theoretical rather than guessed at either way.
--
-- ENV VRUN_SWEEP_OUT, VRUN_SWEEP_FRAMES, VRUN_SWEEP_INPUT
local OUT = os.getenv("VRUN_SWEEP_OUT") or "sweep_writes.txt"
local FRAMES = tonumber(os.getenv("VRUN_SWEEP_FRAMES") or "") or 2400
local INPUT_SCRIPT = os.getenv("VRUN_SWEEP_INPUT") or ""

local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end
local function w(...) f:write(...) f:write("\n") end

for a = 0, 2047 do
	emu.write(a, ((a % 8) < 4) and 0x00 or 0xFF, emu.memType.nesInternalRam)
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

local frame, curHeld, nextStep = 0, {}, 1
emu.addEventCallback(function()
	while nextStep <= #steps and steps[nextStep].frame <= frame do
		curHeld = steps[nextStep].held; nextStep = nextStep + 1
	end
	emu.setInput(curHeld, 0)
end, emu.eventType.inputPolled)

local total, enabledShift, disabledShift, shiftZero = 0, 0, 0, 0
local seen = {}

emu.addMemoryCallback(function(addr, value)
	if addr ~= 0x4001 and addr ~= 0x4005 then return end
	total = total + 1
	local en = (value & 0x80) ~= 0
	local shift = value & 0x07
	if shift == 0 then shiftZero = shiftZero + 1
	elseif en then enabledShift = enabledShift + 1
	else disabledShift = disabledShift + 1 end
	local key = string.format("%04X=%02X en=%s neg=%s shift=%d period=%d",
		addr, value, tostring(en), tostring((value & 0x08) ~= 0), shift, (value >> 4) & 7)
	seen[key] = (seen[key] or 0) + 1
	w(frame, "|", string.format("%04X", addr), "|", string.format("%02X", value),
	  "|en=", tostring(en), "|shift=", shift)
end, emu.callbackType.write, 0x4000, 0x4017, emu.cpuType.nes)

emu.addEventCallback(function()
	frame = frame + 1
	if frame >= FRAMES then
		w("# ---- summary ----")
		w("# total sweep writes: ", total)
		w("# shift==0 (sweep does nothing either way): ", shiftZero)
		w("# enabled + shift!=0 (both emulators sweep): ", enabledShift)
		w("# DISABLED + shift!=0 (hardware holds, APU_FIX sweeps): ", disabledShift)
		w("# distinct values written:")
		for k, v in pairs(seen) do w("#   ", k, "  x", v) end
		f:close()
		emu.stop(0)
	end
end, emu.eventType.endFrame)
