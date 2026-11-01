-- ============================================================================
--  VRUN parity sweep - ROUTE DISCOVERY PROBE (Mesen side)
-- ============================================================================
--  NOT a tracer. This exists purely to answer "what does this input script
--  actually make the game DO", cheaply, so that the expensive paired
--  state_trace.lua / Workshop --trace runs can be pointed at input scripts that
--  reach real gameplay instead of at scripts that quietly stand still.
--
--  It logs the game's OWN state variables once per frame - app_state, room,
--  player x/y/hp, paused, save flags, score - straight out of work RAM. Those
--  addresses are the ones validated live against this exact build in
--  BrokenNes/UAT/vrun-explore.ps1: the published RAM map in docs/23-ram-map.md
--  was measured on a LATER build that inserted 2 bytes at $0384, so every
--  symbol after the insertion point sits 2 bytes LOWER in the Desktop ROM.
--  Do not "correct" these against the doc.
--
--  Deliberately a separate file from ppu2007_log.lua and state_trace.lua so
--  that two agents working this problem at once cannot clobber each other.
--
--  ENV
--    VRUN_PROBE_OUT     output path (default vrun_probe.txt, cwd-relative)
--    VRUN_PROBE_INPUT   input script, same contract as state_trace.lua
--                         "60:Start,66:,120:Right+A,180:"
--    VRUN_PROBE_FRAMES  frames to run (default 1200)
--    VRUN_PROBE_RAMFILL fceux (default) | zeros | ones - MUST match the tracer's
--                       fill or the route found here will not replay there.
--    VRUN_PROBE_EVERY   log every Nth frame (default 1). Transitions are ALWAYS
--                       logged regardless, so thinning never hides a room change.
--
--  EXIT  0 ok  2 bad env/input  5 output I/O failed
-- ============================================================================

local OUT_PATH = os.getenv("VRUN_PROBE_OUT") or "vrun_probe.txt"
local out = nil

local function die(code, msg)
	local target = out or io.open(OUT_PATH, "wb")
	if target then target:write("# FATAL(", tostring(code), "): ", msg, "\n"); target:close() end
	print("FATAL(" .. tostring(code) .. "): " .. msg)
	emu.stop(code)
	error(msg, 0)
end

local FRAMES  = tonumber(os.getenv("VRUN_PROBE_FRAMES") or "") or 1200
local EVERY   = tonumber(os.getenv("VRUN_PROBE_EVERY") or "") or 1
local SPEC    = os.getenv("VRUN_PROBE_INPUT") or ""
local RAMFILL = string.lower(os.getenv("VRUN_PROBE_RAMFILL") or "fceux")

------------------------------------------------------------------------------
-- Power-on RAM fill. Same reasoning (and same bytes) as state_trace.lua: this
-- ROM demonstrably READS uninitialised work RAM - a plain 400-frame boot logs
-- Mesen "Uninitialized memory read" for $05E0-$05FF and $07D5-$07E3 - so a
-- probe run under a different fill can take a different code path from the
-- traced run it is supposed to be scouting for.
------------------------------------------------------------------------------
do
	local pattern
	if RAMFILL == "fceux" then pattern = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }
	elseif RAMFILL == "zeros" then pattern = { 0 }
	elseif RAMFILL == "ones" then pattern = { 0xFF }
	elseif RAMFILL == "none" then pattern = nil
	else die(2, "VRUN_PROBE_RAMFILL must be fceux|zeros|ones|none, got '" .. RAMFILL .. "'") end
	if pattern then
		local n = #pattern
		for a = 0, 2047 do
			emu.write(a, pattern[(a % n) + 1], emu.memType.nesInternalRam)
		end
	end
end

------------------------------------------------------------------------------
-- Input script - byte-for-byte the same grammar the tracers use.
------------------------------------------------------------------------------
local BUTTONS = { "a", "b", "select", "start", "up", "down", "left", "right" }
local VALID = {}
for _, b in ipairs(BUTTONS) do VALID[b] = true end
local function newHeld()
	local t = {}
	for _, b in ipairs(BUTTONS) do t[b] = false end
	return t
end

local function parseInputScript(spec)
	local steps, order = {}, 0
	for raw in string.gmatch(spec, "[^,]+") do
		local piece = raw:match("^%s*(.-)%s*$")
		if piece ~= "" then
			local fstr, bstr = piece:match("^([%w%-]+)%s*:%s*(.*)$")
			if not fstr then die(2, "malformed step '" .. piece .. "'") end
			local fnum = tonumber(fstr)
			if not fnum or fnum ~= math.floor(fnum) or fnum < 0 then
				die(2, "step '" .. piece .. "' has a non-integer / negative frame")
			end
			local held = newHeld()
			for name in string.gmatch(bstr, "[^%+]+") do
				local btn = string.lower(name:match("^%s*(.-)%s*$"))
				if btn ~= "" then
					if not VALID[btn] then die(2, "step '" .. piece .. "' names unknown button '" .. name .. "'") end
					held[btn] = true
				end
			end
			order = order + 1
			steps[#steps + 1] = { frame = math.tointeger(fnum), held = held, order = order }
		end
	end
	table.sort(steps, function(x, y)
		if x.frame ~= y.frame then return x.frame < y.frame end
		return x.order < y.order
	end)
	local collapsed = {}
	for _, s in ipairs(steps) do
		if #collapsed > 0 and collapsed[#collapsed].frame == s.frame then collapsed[#collapsed] = s
		else collapsed[#collapsed + 1] = s end
	end
	return collapsed
end
local STEPS = parseInputScript(SPEC)

------------------------------------------------------------------------------
-- Validated addresses for the Desktop build (see header).
------------------------------------------------------------------------------
local A = {
	app_state    = 0x03AD,
	paused       = 0x036C,
	current_room = 0x0358,
	player_x     = 0x0327,
	player_y     = 0x0328,
	player_hp    = 0x032B,
	player_hp_max= 0x032A,
	score_lo     = 0x0393,
	score_hi     = 0x0394,
	save_exists  = 0x03D1,
	save_req     = 0x03D2,
	nbr0         = 0x0323,   -- left/right/up/down room id, 255 = none
	shop_cursor  = 0x0397,
}

out = io.open(OUT_PATH, "wb")
if not out then die(5, "cannot open " .. OUT_PATH) end
out:write("# vrun_parity_probe frames=", FRAMES, " every=", EVERY,
          " ramfill=", RAMFILL, "\n")
out:write("# input: ", SPEC, "\n")
out:write("# frame|app|room|x|y|hp|hpmax|paused|saveex|savereq|score|nL|nR|nU|nD|cur\n")

local frame, stepIdx = 0, 0
local held = newHeld()
local prevKey = nil
local buf = {}

local function onInputPolled()
	while stepIdx < #STEPS and STEPS[stepIdx + 1].frame <= frame do
		stepIdx = stepIdx + 1
		held = STEPS[stepIdx].held
	end
	emu.setInput(held, 0)
end

-- nesInternalRam is direct-indexed 0..2047 and is byte-for-byte the $0000-$07FF window of
-- nesMemory (state_trace.lua verified that: 0 differing bytes) - but reading it does not go
-- through the CPU bus, so a probe read can never perturb the run it is measuring. Every
-- address in table A is below $0800, so the direct index is the address itself.
local MEM_RAM = emu.memType.nesInternalRam
local function rd(a) return emu.read(a, MEM_RAM) end

local function onEndFrame()
	local app  = rd(A.app_state)
	local room = rd(A.current_room)
	local x    = rd(A.player_x)
	local y    = rd(A.player_y)
	local hp   = rd(A.player_hp)
	local hpm  = rd(A.player_hp_max)
	local pau  = rd(A.paused)
	local sx   = rd(A.save_exists)
	local sr   = rd(A.save_req)
	local sc   = rd(A.score_hi) * 256 + rd(A.score_lo)
	local nL, nR, nU, nD = rd(A.nbr0), rd(A.nbr0 + 1), rd(A.nbr0 + 2), rd(A.nbr0 + 3)
	local cur  = rd(A.shop_cursor)

	-- A transition in any of the "situation" fields is always worth a line even
	-- when thinning, because those are exactly the frames a route is aimed at.
	local key = string.format("%d/%d/%d/%d/%d/%d", app, room, hp, pau, sx, sr)
	if frame % EVERY == 0 or key ~= prevKey then
		buf[#buf + 1] = string.format("%d|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d",
			frame, app, room, x, y, hp, hpm, pau, sx, sr, sc, nL, nR, nU, nD, cur)
	end
	prevKey = key

	frame = frame + 1
	if frame >= FRAMES then
		out:write(table.concat(buf, "\n"), "\n# done\n")
		out:close()
		emu.stop(0)
	end
end

emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
emu.addEventCallback(onEndFrame, emu.eventType.endFrame)
