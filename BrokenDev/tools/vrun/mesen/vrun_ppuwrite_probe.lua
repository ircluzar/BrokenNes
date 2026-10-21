-- ============================================================================
--  VRUN parity sweep - $2000-$2007 WRITE-PHASE PROBE (Mesen side)
-- ============================================================================
--  Emits one line per CPU write to a PPU register, stamped with the PPU
--  scanline and dot it happened on, in the SAME field order as BrokenNes'
--  `Workshop --trace --ppu-write-log`:
--
--      frame|scanline|dot|reg|value|maskAfter
--
--  WHY THIS EXISTS
--  A per-frame state trace samples each emulator once per frame, and the two
--  harnesses cannot sample at the same instruction. That makes every CPU
--  register, work RAM, the PPU t/w latches and the APU register columns
--  disagree on a few percent of frames for a reason that is pure harness - and
--  it makes those columns useless for answering the question that actually
--  matters, which is whether BrokenNes' CPU and PPU are in the SAME PHASE
--  RELATIONSHIP as Mesen's.
--
--  A PPU register write answers that directly: it is a CPU event stamped with a
--  PPU clock. There are thousands per frame. Line up the two logs by sequence
--  index and the dot difference is the phase error, measured rather than
--  argued: all zero = identical phase; a constant non-zero = the (harmless)
--  power-on CPU/PPU alignment Mesen itself offers to randomise; a growing one =
--  a frame-length disagreement, which is a real defect.
--
--  SCANLINE CONVENTION. Mesen numbers the pre-render line -1; BrokenNes numbers
--  it 261. This script emits Mesen's number unchanged and the comparison script
--  does the mapping, so nothing here has to guess what the other side does.
--
--  Deliberately a distinct filename from ppu2007_log.lua / ppu2007_pair.lua /
--  ppu2007_probe.lua, which another agent is actively editing.
--
--  ENV
--    VRUN_PW_OUT     output path (default vrun_ppuwrite.txt)
--    VRUN_PW_INPUT   input script, same grammar as state_trace.lua
--    VRUN_PW_FRAMES  frames to run (default 300)
--    VRUN_PW_FROM / VRUN_PW_TO   frame window to log (default all)
--    VRUN_PW_RAMFILL fceux (default) | zeros | ones
--
--  EXIT  0 ok  2 bad env/input  5 output I/O failed
-- ============================================================================

local OUT_PATH = os.getenv("VRUN_PW_OUT") or "vrun_ppuwrite.txt"
local out = nil
local function die(code, msg)
	local t = out or io.open(OUT_PATH, "wb")
	if t then t:write("# FATAL(", tostring(code), "): ", msg, "\n"); t:close() end
	print("FATAL(" .. tostring(code) .. "): " .. msg)
	emu.stop(code)
	error(msg, 0)
end

local FRAMES  = tonumber(os.getenv("VRUN_PW_FRAMES") or "") or 300
local FROM    = tonumber(os.getenv("VRUN_PW_FROM") or "") or 0
local TO      = tonumber(os.getenv("VRUN_PW_TO") or "") or 999999
local SPEC    = os.getenv("VRUN_PW_INPUT") or ""
local RAMFILL = string.lower(os.getenv("VRUN_PW_RAMFILL") or "fceux")

-- Power-on RAM fill, applied in the top-level chunk (Mesen runs this at cpu.cycleCount == 7,
-- after the reset sequence and before the ROM's first instruction). Applying it any later
-- overwrites RAM the game has already initialised - the exact defect that made the deprecated
-- ppu2007_log.lua record zero $2007 writes.
do
	local pat
	if RAMFILL == "fceux" then pat = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }
	elseif RAMFILL == "zeros" then pat = { 0 }
	elseif RAMFILL == "ones" then pat = { 0xFF }
	else die(2, "VRUN_PW_RAMFILL must be fceux|zeros|ones") end
	local n = #pat
	for a = 0, 2047 do emu.write(a, pat[(a % n) + 1], emu.memType.nesInternalRam) end
end

-- Palette RAM powers on indeterminate on hardware, so neither emulator's fill is "right" - but
-- they must MATCH, exactly as state_trace.lua and `--power-on-palette zeros` already agree to.
-- Leaving Mesen's own fill in place here while the BrokenNes side zeroes made the two runs
-- diverge in what the game's vblank flush queue did, which reads as an emulation defect and is
-- not one.
for a = 0, 31 do emu.write(a, 0x00, emu.memType.nesPaletteRam) end

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
			if not fnum or fnum ~= math.floor(fnum) or fnum < 0 then die(2, "bad frame in '" .. piece .. "'") end
			local held = newHeld()
			for name in string.gmatch(bstr, "[^%+]+") do
				local btn = string.lower(name:match("^%s*(.-)%s*$"))
				if btn ~= "" then
					if not VALID[btn] then die(2, "unknown button '" .. name .. "'") end
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
	local c = {}
	for _, s in ipairs(steps) do
		if #c > 0 and c[#c].frame == s.frame then c[#c] = s else c[#c + 1] = s end
	end
	return c
end
local STEPS = parseInputScript(SPEC)

out = io.open(OUT_PATH, "wb")
if not out then die(5, "cannot open " .. OUT_PATH) end
out:write("# vrun_ppuwrite_probe (Mesen). scanline -1 = pre-render (BrokenNes calls it 261).\n")
out:write("# frame|scanline|dot|reg|value|maskAfter\n")

local frame, stepIdx = 0, 0
local held = newHeld()
local lines = {}
local sfmt = string.format
local getState = emu.getState

-- Mesen fires a write callback BEFORE the write is applied, so PPUMASK read back here is the
-- OLD value. For a $2001 write the byte being written IS the new mask; for anything else the
-- current mask is already the value that will be in force. Rebuilt from the decoded flags for
-- the same reason state_trace.lua rebuilds it - Mesen stores no raw $2001 byte.
local function maskNow(s)
	local m = 0
	if s["ppu.mask.grayscale"] then m = m | 0x01 end
	if s["ppu.mask.backgroundMask"] then m = m | 0x02 end
	if s["ppu.mask.spriteMask"] then m = m | 0x04 end
	if s["ppu.mask.backgroundEnabled"] then m = m | 0x08 end
	if s["ppu.mask.spritesEnabled"] then m = m | 0x10 end
	if s["ppu.mask.intensifyRed"] then m = m | 0x20 end
	if s["ppu.mask.intensifyGreen"] then m = m | 0x40 end
	if s["ppu.mask.intensifyBlue"] then m = m | 0x80 end
	return m
end

emu.addMemoryCallback(function(addr, value)
	if frame < FROM or frame > TO then return end
	local s = getState()
	local reg = 0x2000 + (addr & 7)          -- $2000-$3FFF mirrors every 8 bytes
	local mask = (reg == 0x2001) and (value & 0xFF) or maskNow(s)
	lines[#lines + 1] = sfmt("%d|%d|%d|%04x|%02x|%02x",
		frame, s["ppu.scanline"], s["ppu.cycle"], reg, value & 0xFF, mask)
end, emu.callbackType.write, 0x2000, 0x3FFF)

emu.addEventCallback(function()
	while stepIdx < #STEPS and STEPS[stepIdx + 1].frame <= frame do
		stepIdx = stepIdx + 1
		held = STEPS[stepIdx].held
	end
	emu.setInput(held, 0)
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
	frame = frame + 1
	if frame >= FRAMES then
		out:write(table.concat(lines, "\n"), "\n# done\n")
		out:close()
		emu.stop(0)
	end
end, emu.eventType.endFrame)
