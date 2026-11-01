-- ============================================================================
--  Mesen 2.1.1 Lua API surface dump
-- ============================================================================
--  WHY THIS EXISTS
--    The VRUN parity project has been carrying an unverified assumption -
--    "Mesen 2.1.1's headless Lua exposes no audio samples" - as the reason the
--    entire audio front has never been compared against the hardware baseline.
--    That claim was never actually tested; it was inferred. An assumption that
--    blocks a whole verification front is worth ten minutes of enumeration.
--
--    So: walk every table Mesen injects into the Lua sandbox and write the
--    complete key list to a file. If there is an audio hook - a sample getter,
--    an audioBufferReady-style event, a WAV recorder binding - it will be in
--    this dump. If it is not in this dump, it does not exist in this build,
--    and that is a fact rather than a guess.
--
--  ENV
--    VRUN_API_OUT   output path (default api_dump.txt in cwd)
--
--  Run headless:
--    Mesen.exe --testRunner <rom> api_dump.lua
--  This script stops the emulator immediately, so it never needs input or a
--  frame budget and cannot hang a CI run.
-- ============================================================================

local OUT = os.getenv("VRUN_API_OUT") or "api_dump.txt"
local f = io.open(OUT, "wb")
if not f then
	print("cannot open " .. OUT)
	emu.stop(5)
	return
end

local function w(...) f:write(...) f:write("\n") end

-- Deliberately bounded recursion: Mesen's injected tables are shallow (two
-- levels at most), but _G contains cycles via _G._G, so a depth cap plus a
-- seen-set is the difference between a dump and a hang.
local seen = {}

local function dumpTable(name, t, depth, maxDepth)
	if type(t) ~= "table" then
		w(string.rep("  ", depth) .. name .. " = " .. type(t))
		return
	end
	if seen[t] then
		w(string.rep("  ", depth) .. name .. " = <table already dumped>")
		return
	end
	seen[t] = true

	-- Sort so two runs of this script produce diffable output.
	local keys = {}
	for k, _ in pairs(t) do keys[#keys + 1] = tostring(k) end
	table.sort(keys)

	w(string.rep("  ", depth) .. name .. "  (" .. #keys .. " keys)")
	for _, k in ipairs(keys) do
		local v = t[k]
		local tv = type(v)
		local line = string.rep("  ", depth + 1) .. k .. " : " .. tv
		if tv == "number" or tv == "string" or tv == "boolean" then
			line = line .. " = " .. tostring(v)
		end
		w(line)
		if tv == "table" and depth + 1 < maxDepth then
			dumpTable(k, v, depth + 2, maxDepth)
		end
	end
end

w("# Mesen Lua API surface dump")
w("# _VERSION = " .. tostring(_VERSION))

-- The globals Mesen itself injects, plus the standard libraries whose presence
-- is gated on AllowIoOsAccess (recorded so a dump taken with the sandbox on
-- can never be mistaken for one taken with it off).
w("")
w("=== top-level globals ===")
do
	local keys = {}
	for k, _ in pairs(_G) do keys[#keys + 1] = tostring(k) end
	table.sort(keys)
	for _, k in ipairs(keys) do
		w("  " .. k .. " : " .. type(_G[k]))
	end
end

w("")
w("=== emu ===")
dumpTable("emu", emu, 0, 6)

-- Anything else Mesen might have injected under its own name. Enumerated
-- explicitly because a nil here is itself a finding worth recording.
for _, n in ipairs({ "audio", "sound", "apu", "recorder", "wav", "movie", "video", "debugger" }) do
	w("")
	w("=== global '" .. n .. "' ===")
	local g = _G[n]
	if g == nil then
		w("  (absent)")
	else
		dumpTable(n, g, 0, 4)
	end
end

f:close()
print("api dump written to " .. OUT)
emu.stop(0)
