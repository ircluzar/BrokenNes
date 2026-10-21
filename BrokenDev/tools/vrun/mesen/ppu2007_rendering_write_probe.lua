-- ppu2007_rendering_write_probe.lua
--
-- WHY THIS EXISTS
-- ---------------
-- VRUN's screen-transition machine (step_transition() in src/game/main.fab) streams a blanked
-- nametable from the MAIN LOOP, not from the NMI, and relies on PPUMASK already being 0 by the
-- time it starts. On the organic route that always holds: goto_state() sets `transitioning` from
-- inside the main loop, the NMI at the end of THAT frame writes $2001 = 0, and the first
-- clear_row() only runs on the following frame. But a harness that POKES `transitioning` at a
-- frame boundary lands it mid-frame, so the very first clear_row() fires 32 $2007 writes while
-- rendering is still enabled - a case the ROM itself never produces, and one where BrokenNes'
-- PPU_FIX and Mesen visibly disagree.
--
-- This probe logs, from the Mesen side, every CPU write to $2007 inside a frame window together
-- with the PPU's own address register at the moment of the write, so the pair
--     "address at write time"  vs  "byte that ended up in CIRAM"
-- can be read off directly instead of inferred from a hash. It is a DIAGNOSTIC: it never
-- perturbs the run beyond reading emu.getState() (side-effect free) inside a write callback.
--
-- Deliberately separate from state_trace.lua rather than a flag on it: state_trace.lua is the
-- load-bearing cross-emulator tracer that a dozen parity claims rest on, and bolting a
-- per-write callback onto it would change its timing for every future run.
--
-- ENV
--   VRUN_TRACE_ROM     rom path (only used for the header line)
--   VRUN_TRACE_INPUT   "frame:Buttons,..." - same grammar as state_trace.lua
--   VRUN_TRACE_FRAMES  how many frames to run before stopping (default 700)
--   VRUN_TRACE_POKE    "frame:addr=value,..." - same grammar and, critically, the same INSTANT
--                      as state_trace.lua: immediately after the previous frame's boundary
--   VRUN_TRACE_OUT     output file (default ppu2007_writes.txt)
--   VRUN_WRITE_FROM /  VRUN_WRITE_TO   frame window to log (inclusive; default: everything)
--   VRUN_TRACE_RAMFILL fceux (default) | none - the FCEUX power-on work-RAM pattern, applied at
--                      SCRIPT LOAD, never from a frame callback (a frame late it stomps
--                      initialised RAM and wedges the game)
--
-- OUTPUT (one line per write, '#' lines are header):
--   frame|scanline|cycle|value|vramAddrBefore|maskBg|maskSpr
--
-- Run with:
--   Mesen.exe --testRunner <rom> ppu2007_rendering_write_probe.lua

local function die(code, msg)
	emu.log("FATAL: " .. msg)
	emu.stop(code)
end

local OUT_PATH   = os.getenv("VRUN_TRACE_OUT") or "ppu2007_writes.txt"
local ROM_PATH   = os.getenv("VRUN_TRACE_ROM") or "(unknown)"
local FRAMES     = tonumber(os.getenv("VRUN_TRACE_FRAMES") or "") or 700
local WRITE_FROM = tonumber(os.getenv("VRUN_WRITE_FROM") or "") or -1
local WRITE_TO   = tonumber(os.getenv("VRUN_WRITE_TO") or "") or 0x7fffffff
local RAMFILL    = string.lower(os.getenv("VRUN_TRACE_RAMFILL") or "fceux")

local out = io.open(OUT_PATH, "wb")
if not out then die(2, "cannot open output " .. OUT_PATH) end

-- ── power-on work RAM ─────────────────────────────────────────────────────────
-- Applied HERE, in the top-level chunk (cpu.cycleCount == 7, before the ROM's first
-- instruction), exactly as state_trace.lua does. Doing it from a frame callback instead is a
-- documented trap: one frame late it overwrites RAM the game has already initialised and parks
-- the PC at 0x180d.
if RAMFILL == "fceux" then
	for a = 0, 0x7FF do
		emu.write(a, (a & 4) ~= 0 and 0xFF or 0x00, emu.memType.nesInternalRam)
	end
end

-- ── input script ──────────────────────────────────────────────────────────────
-- Same grammar as state_trace.lua: "frame:Buttons+Buttons,frame:". A step's held set applies
-- from that frame INCLUSIVE until the next step. Applied on inputPolled, never on endFrame -
-- VRUN polls the pad in vblank, so driving input off a frame-END counter applies every press one
-- frame late and has twice masqueraded as an emulation divergence.
local BUTTONS = { a = "a", b = "b", select = "select", start = "start",
                  up = "up", down = "down", left = "left", right = "right" }
local STEPS = {}
for tok in string.gmatch(os.getenv("VRUN_TRACE_INPUT") or "", "[^,]+") do
	local fr, btns = tok:match("^%s*(%d+):(.*)$")
	if not fr then die(2, "bad input step '" .. tok .. "'") end
	local held = {}
	for b in string.gmatch(btns, "[^%+]+") do
		local key = BUTTONS[string.lower((b:gsub("%s", "")))]
		if not key then die(2, "unknown button '" .. b .. "'") end
		held[key] = true
	end
	STEPS[#STEPS + 1] = { frame = math.tointeger(tonumber(fr)), held = held }
end
table.sort(STEPS, function(x, y) return x.frame < y.frame end)

-- ── pokes ─────────────────────────────────────────────────────────────────────
local POKES, POKE_COUNT = {}, 0
for tok in string.gmatch(os.getenv("VRUN_TRACE_POKE") or "", "[^,]+") do
	local fr, ad, va = tok:match("^%s*(%d+):(%x+)=(%x+)%s*$")
	if not fr then die(2, "bad poke step '" .. tok .. "'") end
	fr, ad, va = math.tointeger(tonumber(fr)), tonumber(ad, 16), tonumber(va, 16)
	if ad > 0x7FF then die(2, "poke addr outside work RAM") end
	POKES[fr] = POKES[fr] or {}
	local L = POKES[fr]; L[#L + 1] = { ad, va }
	POKE_COUNT = POKE_COUNT + 1
end
-- The INSTANT a poke lands is load-bearing here, not incidental. VRUN's transition machine
-- blanks the screen from the NMI (scanline 241) and only then streams VRAM from the main loop,
-- so a poke that sets `transitioning` BEFORE that NMI produces a clean, blanked clear, and one
-- that lands AFTER it produces 32 $2007 writes into a rendering PPU. Those are different
-- experiments, and ~100 CPU cycles apart. So the probe records where it poked.
local pokeLog = {}
local function applyPokes(f)
	local list = POKES[f]
	if not list then return end
	local s = emu.getState()
	pokeLog[#pokeLog + 1] = string.format("# poke frame %d applied at scanline %d cycle %d",
		f, s["ppu.scanline"], s["ppu.cycle"])
	for _, pv in ipairs(list) do emu.write(pv[1], pv[2], emu.memType.nesInternalRam) end
end
if POKES[0] then applyPokes(0) end

out:write("# ppu2007-rendering-write-probe\n")
out:write("# rom: ", ROM_PATH, "\n")
out:write("# input: ", os.getenv("VRUN_TRACE_INPUT") or "(none)", "\n")
out:write("# poke: ", os.getenv("VRUN_TRACE_POKE") or "(none)", "\n")
out:write("# window: frames ", tostring(WRITE_FROM), "..", tostring(WRITE_TO), "\n")
out:write("# columns: frame|scanline|cycle|value|vramAddrBefore|bgOn|sprOn\n")

local frame = 0
local held = {}

-- emu.getState() returns a FLAT table with DOTTED keys. Nested access (s.ppu.scanline) yields
-- nil and takes the whole script down with exit 255, so every read below is s["ppu.x"].
local function onWrite2007(addr, value)
	if frame < WRITE_FROM or frame > WRITE_TO then return end
	local s = emu.getState()
	out:write(string.format("%d|%d|%d|%02x|%04x|%d|%d\n",
		frame, s["ppu.scanline"], s["ppu.cycle"], value & 0xFF,
		s["ppu.videoRamAddr"] & 0x7FFF,
		s["ppu.mask.backgroundEnabled"] and 1 or 0,
		s["ppu.mask.spritesEnabled"] and 1 or 0))
end

local function onInputPolled()
	for _, st in ipairs(STEPS) do
		if st.frame <= frame then held = st.held else break end
	end
	emu.setInput(held, 0)   -- (input, port) - argument order matters and is easy to get backwards
end

local function onFrameBoundary()
	if frame >= FRAMES then
		for _, l in ipairs(pokeLog) do out:write(l, "\n") end
		out:close(); out = nil
		emu.stop(0)
		return
	end
	frame = frame + 1
	if POKE_COUNT > 0 then applyPokes(frame) end
end

emu.addMemoryCallback(onWrite2007, emu.callbackType.write, 0x2007, 0x2007)
emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)

-- VRUN_POKE_AT selects WHERE in the frame the poke lands, because for THIS experiment that is
-- the whole experiment rather than a detail. VRUN's transition machine blanks the screen from
-- the NMI (scanline 241) and only then streams VRAM from the main loop, so a poke landing
-- before that NMI gives a clean blanked clear and one landing after it gives 32 $2007 writes
-- into a live, rendering PPU. Those are ~21 scanlines and one NMI apart.
--
--   framestart (default) - scanline 0 cycle 0, i.e. 7502 dots past Mesen's endFrame event.
--                          This is what state_trace.lua's default VRUN_TRACE_SAMPLE=frame-end
--                          resolves to (SAMPLE_DOT = 7502), and it is where `Workshop --trace`
--                          pokes too. Use it to reproduce what the real tracer pair does.
--   endframe             - scanline 240 cycle 0, Mesen's own endFrame event. BEFORE the NMI,
--                          so the game blanks first. Kept because the contrast between the two
--                          is the measurement that names the mechanism.
local POKE_AT = string.lower(os.getenv("VRUN_POKE_AT") or "framestart")
if POKE_AT == "framestart" then
	-- Same shape as state_trace.lua's sampler: arm a whole-range exec callback at startFrame
	-- (scanline -1) and fire on the first instruction boundary at or after scanline 0 cycle 0,
	-- then remove it again so the callback count stays at ~1 per frame instead of ~10000.
	local cbRef
	local function onExec()
		local s = emu.getState()
		local sl, cy = s["ppu.scanline"], s["ppu.cycle"]
		local since
		if sl >= 240 then since = (sl - 240) * 341 + cy
		elseif sl < 0 then since = 7161 + cy
		else since = 7502 + sl * 341 + cy end
		if since < 7502 then return end
		if cbRef then
			emu.removeMemoryCallback(cbRef, emu.callbackType.exec, 0x0000, 0xFFFF)
			cbRef = nil
		end
		onFrameBoundary()
	end
	emu.addEventCallback(function()
		if not cbRef then cbRef = emu.addMemoryCallback(onExec, emu.callbackType.exec, 0x0000, 0xFFFF) end
	end, emu.eventType.startFrame)
else
	emu.addEventCallback(onFrameBoundary, emu.eventType.endFrame)
end
