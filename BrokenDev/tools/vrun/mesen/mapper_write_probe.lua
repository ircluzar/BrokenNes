-- ============================================================================
--  VRUN parity sweep - MAPPER REGISTER WRITE PROBE ($8000-$ffff, Mesen side)
-- ============================================================================
--  Emits one line per CPU write to cartridge register space, stamped with the
--  PPU scanline and dot it happened on AND with the bank state the write
--  produced, in the same field order as BrokenNes'
--  `Workshop --trace --mapper-write-log`:
--
--      frame|scanline|dot|addr|value|prgbank|chrbank|mir|flash
--
--  WHY THIS EXISTS
--  The state trace samples the mapper's CHR bank once per frame, at the frame
--  boundary. On a 60,000-frame VRUN run that sample reported bank 0 on 59,997
--  frames and bank 1 on 3 - not because the game barely banks CHR, but because
--  it banks MID-FRAME and restores bank 0 before the frame ends. A frame-
--  boundary sample is nearly blind to it, so "0 divergences" in that column is
--  not evidence that mapper-30 CHR banking is right. Recording the WRITE - the
--  event - instead of the state left behind afterwards is.
--
--  THE ORDERING PROBLEM, AND HOW THIS SOLVES IT
--  Mesen fires a write memory callback BEFORE the write is applied, so
--  emu.getState() inside the callback reports the bank the write is about to
--  REPLACE. BrokenNes' observer fires after. Rather than reimplement mapper
--  30's decode here in Lua - which would make this script agree with BrokenNes
--  by construction and measure nothing - each write is held back one step and
--  emitted when the NEXT bank state is read. That is sound because the ONLY
--  thing that can move a mapper-30 bank is a write to $8000-$ffff, and this
--  callback sees every one of them: the pre-state of write N+1 IS the
--  post-state of write N. The last write of a frame is completed from the
--  endFrame sample for the same reason.
--
--  SCANLINE CONVENTION. Mesen numbers the pre-render line -1; BrokenNes numbers
--  it 261. Emitted unchanged; the comparison script maps.
--
--  FRAME NUMBERING - THE TRAP THAT MADE THIS SCRIPT WRONG ON ITS FIRST RUN.
--  emu.eventType.endFrame fires at PPU scanline 240 cycle 0, which is NOT a
--  frame boundary in BrokenNes' sense: NES.RunFrame() returns at the first
--  instruction boundary at or after scanline 0 cycle 0, so BrokenNes counts a
--  frame's own VBLANK (scanlines 240-260) and PRE-RENDER line as part of that
--  frame. Stamping writes with a raw endFrame counter therefore numbers every
--  vblank write one too high, and - far worse - hands the controller poll (which
--  VRUN does in vblank) the NEXT frame's input, one frame EARLY. Measured on a
--  400-frame run: Mesen reacted to Start at frame 120 where BrokenNes reacted at
--  frame 121, and 9,300 writes downstream "diverged" for that reason alone.
--
--  The fix costs nothing. With k = number of endFrame events so far, a write on
--  scanline s belongs to BrokenNes frame  k when 0 <= s <= 239, and k-1 when
--  s >= 240 or s == -1 (vblank and pre-render, which endFrame has already
--  counted past). Verified line-for-line against BrokenNes on the writes either
--  side of the boundary, including a pre-render write logged as Mesen -1 / k=120
--  against BrokenNes 261 / frame 119. This is the same boundary state_trace.lua
--  reaches with its VRUN_TRACE_SAMPLE=frame-end exec callback, but derived from
--  the scanline the write already carries instead of polling ~113 emu.getState()
--  calls per frame to find it.
--
--  DOT CONVENTION. Mesen's ppu.cycle here is cycle-exact. BrokenNes' dot comes
--  from IPpuProbe and lags by up to one CPU instruction, because that core
--  catches the PPU up between instructions rather than between cycles. A small
--  constant-ish dot offset is therefore expected and is a harness artefact; a
--  DRIFTING one is not, and is what this column is here to expose.
--
--  MIRRORING. Mesen exposes no mirroring enum, so it is read out of the
--  nametable page offsets it does expose: chrMemoryOffset32/36/40/44 are the
--  256-byte pages PPU $2000/$2400/$2800/$2c00 resolve to, and offset >= 1024
--  means CIRAM page B. h = A,A,B,B (CIRAM A10 = PPU A11, iNES flags6 bit0
--  clear); v = A,B,A,B; a/b = all four on one page.
--
--  FLASH. mapper.flash.cycle and mapper.flash.softwareId, packed as
--  (softwareId<<8)|cycle. Reported for interest only: two emulators can model
--  the same SST39SF0x0 with different internal encodings and still behave
--  identically, so this column must never be a divergence criterion.
--
--  ENV
--    VRUN_MW_OUT      output path (default vrun_mapperwrite.txt)
--    VRUN_MW_INPUT    input script, same grammar as state_trace.lua
--    VRUN_MW_FRAMES   frames to run (default 300)
--    VRUN_MW_FROM / VRUN_MW_TO   frame window to log (default all)
--    VRUN_MW_RAMFILL  fceux (default) | zeros | ones
--    VRUN_MW_POKE     "frame:addr=value,..." (frame decimal, addr/value HEX)
--                     forced writes to work RAM $000-$7ff, mirroring BrokenNes'
--                     --poke-at. THE INSTANT MATTERS: BrokenNes pokes just
--                     before RunFrame() for that frame, i.e. at its frame
--                     boundary (PPU scanline 0 dot 0). Poking from endFrame
--                     instead would land at scanline 240 - a whole vblank and
--                     NMI earlier - and a poked frame counter would then be one
--                     frame out of step with the BrokenNes side, which for a
--                     per-frame timer like VRUN's bank_flip_timer moves the
--                     whole effect by a frame and reads as a huge divergence.
--
--  NO SAVESTATE SEGMENTATION. Mesen's --testRunner has a hard 100-SECOND wall
--  clock, which is why state_trace.lua needs Run-MesenSegmented.ps1. This probe
--  does about one emu.getState() per mapper write plus one per frame - roughly a
--  thirtieth of that script's per-frame work - and measured 10,000 frames of
--  game.nes in 89s (93s with pokes), so it fits without segmentation. Past that
--  it does not, and rather than ship an untested resume path this script simply
--  has none: add one modelled on state_trace.lua's SEG_IN/SEG_OUT (which is
--  verified byte-identical against an unsegmented run) if a longer run is needed.
--
--  EXIT  0 ok  2 bad env/input  5 output I/O failed
-- ============================================================================

local OUT_PATH = os.getenv("VRUN_MW_OUT") or "vrun_mapperwrite.txt"
local out = nil
local function die(code, msg)
	local t = out or io.open(OUT_PATH, "wb")
	if t then t:write("# FATAL(", tostring(code), "): ", msg, "\n"); t:close() end
	print("FATAL(" .. tostring(code) .. "): " .. msg)
	emu.stop(code)
	error(msg, 0)
end

local FRAMES    = tonumber(os.getenv("VRUN_MW_FRAMES") or "") or 300
local FROM      = tonumber(os.getenv("VRUN_MW_FROM") or "") or 0
local TO        = tonumber(os.getenv("VRUN_MW_TO") or "") or 999999999
local SPEC      = os.getenv("VRUN_MW_INPUT") or ""
local RAMFILL   = string.lower(os.getenv("VRUN_MW_RAMFILL") or "fceux")

-- The run deliberately overshoots by one endFrame so the last requested frame's vblank is seen
-- (see the endFrame handler); clamping the window here is what stops the overshoot leaking into
-- the output as a partial extra frame that BrokenNes never emitted.
if TO > FRAMES - 1 then TO = FRAMES - 1 end

-- Power-on RAM fill, applied in the TOP-LEVEL CHUNK. Mesen runs this at cpu.cycleCount == 7,
-- after the reset sequence and before the ROM's first instruction. Applying it from a frame
-- callback instead is one frame too late: it stomps RAM the game has already initialised and
-- wedges VRUN with pc parked at 0x180d. This has cost two separate investigations; leave it here.
do
	local pat
	if RAMFILL == "fceux" then pat = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }
	elseif RAMFILL == "zeros" then pat = { 0 }
	elseif RAMFILL == "ones" then pat = { 0xFF }
	else die(2, "VRUN_MW_RAMFILL must be fceux|zeros|ones") end
	local n = #pat
	for a = 0, 2047 do emu.write(a, pat[(a % n) + 1], emu.memType.nesInternalRam) end
	-- Palette RAM powers on indeterminate on hardware, so neither emulator's fill is "right" -
	-- but they must MATCH, exactly as state_trace.lua and `--power-on-palette zeros` agree to.
	for a = 0, 31 do emu.write(a, 0x00, emu.memType.nesPaletteRam) end
end

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

-- Pokes, keyed by BrokenNes frame number. Same grammar and same hex/decimal split as --poke-at.
local POKES, POKE_COUNT = {}, 0
for raw in string.gmatch(os.getenv("VRUN_MW_POKE") or "", "[^,]+") do
	local piece = raw:match("^%s*(.-)%s*$")
	if piece ~= "" then
		local fstr, astr, vstr = piece:match("^(%d+)%s*:%s*(%x+)%s*=%s*(%x+)$")
		if not fstr then die(2, "malformed poke '" .. piece .. "' (want frame:addr=value, addr/value hex)") end
		local fr, ad, va = math.tointeger(tonumber(fstr)), tonumber(astr, 16), tonumber(vstr, 16)
		if ad < 0 or ad > 0x7FF then die(2, "poke address out of work RAM: " .. astr) end
		if va < 0 or va > 0xFF then die(2, "poke value out of range: " .. vstr) end
		POKES[fr] = POKES[fr] or {}
		table.insert(POKES[fr], { ad, va })
		POKE_COUNT = POKE_COUNT + 1
	end
end

out = io.open(OUT_PATH, "wb")
if not out then die(5, "cannot open " .. OUT_PATH) end
out:write("# mapper_write_probe (Mesen). scanline -1 = pre-render (BrokenNes calls it 261).\n")
out:write("# frame|scanline|dot|addr|value|prgbank|chrbank|mir|flash\n")

-- k = endFrame events so far. NEVER used as a frame number directly; see FRAME NUMBERING above.
local k = 0
local stepIdx = 0
local held = newHeld()
local lines = {}
local sfmt = string.format
local getState = emu.getState

-- BrokenNes' frame index for something happening on scanline `sl`.
local function bnFrame(sl)
	if sl >= 0 and sl <= 239 then return k end
	return k - 1
end

-- The write held back one step; see "THE ORDERING PROBLEM" above.
local pend = nil

-- CHR bank as an 8KB bank INDEX, to match BrokenNes' chrbank column (which is the mapper's own
-- bank number, not a byte offset). chrMemoryOffset0 is the byte offset PPU $0000 resolves to.
local function chrBankOf(s)
	local off = s["mapper.chrMemoryOffset0"]
	if off == nil then return -1 end
	return off // 8192
end

local function mirrorOf(s)
	local nt = {}
	for i, idx in ipairs({ 32, 36, 40, 44 }) do
		local v = s["mapper.chrMemoryOffset" .. idx]
		if v == nil then return "?" end
		nt[i] = (v >= 1024) and 1 or 0
	end
	local k = nt[1] .. nt[2] .. nt[3] .. nt[4]
	if k == "0011" then return "h" end     -- CIRAM A10 = PPU A11
	if k == "0101" then return "v" end     -- CIRAM A10 = PPU A10
	if k == "0000" then return "a" end
	if k == "1111" then return "b" end
	return "?"
end

local function flashOf(s)
	local cyc = s["mapper.flash.cycle"] or 0
	local sid = s["mapper.flash.softwareId"] or 0
	if type(sid) == "boolean" then sid = sid and 1 or 0 end
	return ((sid & 0xFF) << 8) | (cyc & 0xFF)
end

-- Complete the held-back write using `s`, a state read strictly after that write was applied and
-- before any other $8000-$ffff write could have moved a bank.
local function flush(s)
	if pend == nil then return end
	if pend.frame >= FROM and pend.frame <= TO then
		lines[#lines + 1] = sfmt("%d|%d|%d|%04x|%02x|%02x|%x|%s|%04x",
			pend.frame, pend.sl, pend.dot, pend.addr, pend.value,
			s["mapper.prgBank"] or 0, chrBankOf(s), mirrorOf(s), flashOf(s))
	end
	pend = nil
end

emu.addMemoryCallback(function(addr, value)
	local s = getState()
	flush(s)                                  -- s is the PREVIOUS write's post-state
	local sl = s["ppu.scanline"]
	pend = { frame = bnFrame(sl), sl = sl, dot = s["ppu.cycle"],
	         addr = addr & 0xFFFF, value = value & 0xFF }
end, emu.callbackType.write, 0x8000, 0xFFFF)

-- Input MUST be driven from inputPolled, not endFrame. VRUN polls the pad in vblank and endFrame
-- fires at scanline 240, so an endFrame-driven press lands one frame late and masquerades as an
-- emulation divergence. This has fooled the parity work twice.
--
-- The frame number the poll is answering for comes from bnFrame(), not from k: the poll happens in
-- vblank, which endFrame has already counted past. Read from the live scanline rather than assumed
-- to be 241, so a game that polls anywhere else still gets the frame it is actually in.
emu.addEventCallback(function()
	local f = bnFrame(getState()["ppu.scanline"])
	while stepIdx < #STEPS and STEPS[stepIdx + 1].frame <= f do
		stepIdx = stepIdx + 1
		held = STEPS[stepIdx].held
	end
	emu.setInput(held, 0)
end, emu.eventType.inputPolled)

-- Pokes fire at BrokenNes' frame boundary, not Mesen's. Mesen offers no scanline-0 event, so this
-- arms a whole-range exec callback at startFrame (scanline -1, cycle 0) and takes the first
-- instruction boundary at or after scanline 0 - the same rule NES.RunFrame() returns on, and the
-- same trick state_trace.lua's VRUN_TRACE_SAMPLE=frame-end uses. Armed ONLY on frames that
-- actually carry a poke, so an unpoked run pays nothing for this.
if POKE_COUNT > 0 then
	local cbRef = nil
	local onExec = function()
		if getState()["ppu.scanline"] < 0 then return end   -- still on the pre-render line
		if cbRef then
			emu.removeMemoryCallback(cbRef, emu.callbackType.exec, 0x0000, 0xFFFF)
			cbRef = nil
		end
		-- k is the frame whose scanline 0 we have just reached; see bnFrame().
		local list = POKES[k]
		if list then
			for _, p in ipairs(list) do emu.write(p[1], p[2], emu.memType.nesInternalRam) end
		end
	end
	emu.addEventCallback(function()
		-- At startFrame we are on scanline -1, which bnFrame() attributes to frame k-1; the frame
		-- that begins at the upcoming scanline 0 is k.
		if POKES[k] and not cbRef then
			cbRef = emu.addMemoryCallback(onExec, emu.callbackType.exec, 0x0000, 0xFFFF)
		end
	end, emu.eventType.startFrame)
end

emu.addEventCallback(function()
	flush(getState())                         -- pending write gets its post-state here
	k = k + 1
	-- BrokenNes frame f is only COMPLETE once its vblank and pre-render have run, which is one
	-- endFrame after k first reached f+1. So covering frames 0..FRAMES-1 needs k = FRAMES+1; the
	-- visible-scanline writes of frame FRAMES that this also sweeps up are dropped by the
	-- frame-window filter in flush().
	if k > FRAMES then
		out:write(table.concat(lines, "\n"), "\n# done\n")
		out:close()
		emu.stop(0)
	end
end, emu.eventType.endFrame)
