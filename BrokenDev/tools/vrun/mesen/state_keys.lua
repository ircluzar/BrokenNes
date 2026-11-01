-- Dump every key emu.getState() returns for the NES, with its runtime type and
-- a sample value. state_trace.lua documents that the table is FLAT with dotted
-- keys ("ppu.scanline"), so this is a one-level walk on purpose.
--
-- WHY: the audio-parity work needs to know exactly which APU internals Mesen
-- will hand over. Envelope decay level, sweep divider, length counter, timer
-- counter, sequence position, noise LFSR and the DMC output level are what
-- actually determine the waveform between register writes; the register values
-- alone (already proven equal) do not.
local OUT = os.getenv("VRUN_KEYS_OUT") or "state_keys.txt"
local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end

-- Take the sample a few frames in, not at power-on, so counters hold non-zero
-- values and an "always 0" field cannot be mistaken for a live one.
local n = 0
emu.addEventCallback(function()
	n = n + 1
	if n < 120 then return end
	local s = emu.getState()
	local keys = {}
	for k, _ in pairs(s) do keys[#keys + 1] = tostring(k) end
	table.sort(keys)
	f:write("# emu.getState() keys at frame ", n, "  (", #keys, " total)\n")
	for _, k in ipairs(keys) do
		local v = s[k]
		f:write(k, " : ", type(v), " = ", tostring(v), "\n")
	end
	f:close()
	emu.stop(0)
end, emu.eventType.endFrame)
