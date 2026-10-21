-- ============================================================================
--  VRUN deterministic replay tracer - MESEN side
-- ============================================================================
--  Emits one record per emulated frame so that the *same* trace, produced by a
--  second emulator from the same ROM and the same input script, can be diffed
--  line-by-line to find the exact frame at which the two implementations
--  diverge. The RGB framebuffer is deliberately NOT part of the record: NES
--  palettes are not standardised, so two correct emulators legitimately
--  produce different RGB.
--
--  What v2 adds is the rest of the picture, in emulator-INDEPENDENT form:
--  CIRAM, OAM, palette RAM as indices, the PPU registers and scroll latches,
--  CHR, and the rendered frame reduced back to NES palette INDICES (0-63 per
--  pixel). Without those, any defect that never reaches work RAM - i.e. the
--  entire visual half of "does it play the same" - is invisible to the diff.
--
--  RECORD FORMAT (shared contract - both tracers MUST emit exactly this)
--    UTF-8, LF line endings. Lines beginning with '#' are header/comment.
--    One record per frame, emitted at END of frame (after that frame's
--    rendering completes), starting at frame 0, incrementing by exactly 1:
--
--      v1 (what VRUN_TRACE_PPU=off still emits, byte for byte):
--        <frame>|<pc>|<a>|<x>|<y>|<sp>|<p>|<ramhash>
--
--      v2 (default) appends twelve PPU columns to exactly that prefix:
--        ...|<ntbhash>|<oamhash>|<palhash>|<ctrl>|<mask>|<stat>|<v>|<t>|<fx>|<w>|<chrhash>|<fbhash>
--
--      frame    decimal, 0-based
--      pc       4 lower-case hex digits, no 0x prefix
--      a x y sp p   2 lower-case hex digits each, no 0x prefix
--      ramhash  first 16 hex chars of the SHA-256 of the 2048 bytes of NES
--               work RAM ($0000-$07FF), hashed in address order
--      ntbhash  FNV-1a-64 over the 2048 bytes of CIRAM (nesNametableRam) in
--               address order - the raw 2KB the mapper mirrors, NOT the
--               $2000-$2FFF view
--      oamhash  FNV-1a-64 over the 256 bytes of primary OAM (nesSpriteRam)
--      palhash  FNV-1a-64 over the 32 palette bytes, canonicalised (below)
--      ctrl     PPUCTRL masked to 0xbf, mask PPUMASK, 2 hex each. Bit 6 of
--               $2000 is the EXT-bus direction bit; Mesen does not expose it,
--               no game sets it, and masking it on BOTH sides is honest where
--               inventing a 0 for it would not be.
--      stat     PPUSTATUS masked to 0xe0 - vblank/sprite0/overflow only. The
--               low five bits are PPU open bus, not state, and are not
--               comparable between emulators.
--      v t      current / temporary VRAM address, 4 hex, masked to 0x7fff
--      fx       fine X scroll, 1 hex digit
--      w        write toggle, 1 hex: bit0 = the $2005 latch, bit1 = the $2006
--               latch. Hardware shares ONE toggle, so Mesen (which models it
--               correctly) only ever emits 0 or 3. BrokenNes' PPU_FIX keeps two
--               independent latches, so it can emit 1 or 2 - split into two
--               bits precisely so the differ can name which one drifted.
--      chrhash  FNV-1a-64 over all of CHR RAM/ROM, or '-' when disabled
--      fbhash   FNV-1a-64 over 61440 bytes - the last completed frame as NES
--               palette indices, row major, one byte per pixel - or '-'
--
--      FNV-1a-64: h = 0xcbf29ce484222325; per byte h = (h ~ b) * 0x100000001b3
--      wrapping mod 2^64; printed as 16 lower-case hex. Chosen over SHA-256 for
--      the new columns because this side hashes them in pure Lua and CHR alone
--      is 32 KB per frame - a SHA-256 round costs two orders of magnitude more
--      than an xor and a multiply. Self-tested below against vectors computed
--      independently, exactly like the SHA-256 path.
--
--  PALETTE CANON (both tracers apply this identically before hashing/dumping)
--    each byte & 0x3f, then entries $10/$14/$18/$1c := $00/$04/$08/$0c. Those
--    four cells are write-mirrors of $3f00/$04/$08/$0c that no renderer reads.
--    Mesen keeps them in sync with their counterparts; BrokenNes' PPU_FIX
--    redirects the write and leaves the mirror at its power-on value. Unfolded,
--    palhash would differ on every frame of every trace with no picture behind
--    it - the exact class of false positive this tracer exists to kill.
--
--  FRAME AS PALETTE INDICES - READ THIS BEFORE TRUSTING fbhash
--    emu.getScreenBuffer() hands back 0x00RRGGBB *after* Mesen's palette has
--    been applied, so it cannot be compared with another emulator's RGB. It can
--    be INVERTED, though: Mesen renders NES pixels through a fixed 64-entry
--    table (Nes.UserPalette in settings.json, which this script reads), so
--    rgb -> index is a lookup. The inversion is ambiguous only where the table
--    itself is, and measured on both sides the collision classes are IDENTICAL:
--      {0d,0e,0f,1d,1e,1f,2e,2f,3e,3f} all black, {20,30} both white
--    so "lowest index wins" gives the same answer on both. Both headers print
--    their classes; if a palette ever breaks the property, that line says so
--    instead of the diff quietly lying. Every pixel is checked against the
--    table and any miss is counted and reported - which is also what catches a
--    frame drawn with the emphasis bits set, since emphasis moves the RGB off
--    the 64-entry table entirely.
--
--  INPUT SCRIPT (shared contract)
--    Comma-separated list of `frame:buttons` steps, e.g.
--      "60:Start,66:,120:Right+A,180:"
--    Buttons are A B Select Start Up Down Left Right joined by '+'
--    (case-insensitive here). An empty button list releases everything.
--    A step's held-button set applies to controller 1 from that frame
--    INCLUSIVE until the next step. Before the first step, nothing is held.
--
--  USAGE
--    VRUN_TRACE_OUT=C:/abs/path/trace.txt \
--    VRUN_TRACE_INPUT="60:Start,66:,120:Right+A,180:" \
--    VRUN_TRACE_FRAMES=1200 \
--    tools/mesen/Mesen.exe <rom.nes> \
--        --luaScriptsToLoad tools/state_trace.lua --testRunner
--
--  ENV
--    VRUN_TRACE_OUT      output path (default "state_trace.txt", cwd-relative)
--    VRUN_TRACE_INPUT    input script (default "" = never touch the pad)
--    VRUN_TRACE_FRAMES   number of records to emit (default 1200)
--    VRUN_TRACE_RAMFILL  power-on RAM fill: fceux (default) | zeros | ones | none
--                        See "POWER-ON RAM" below - this one matters.
--    VRUN_TRACE_SAMPLE   where in the frame the record is taken:
--                          frame-end (default) | mesen-endframe
--                        See "SAMPLE POINT" below - this one matters even more.
--    VRUN_TRACE_ROM      fallback ROM path for the sha256 header line, only
--                        needed if emu.getRomInfo().path is not openable.
--    VRUN_TRACE_PPU      on (default) | off. Off emits the v1 8-column record
--                        byte for byte, for diffing against an old trace.
--    VRUN_TRACE_PPU_CHR  on (default) | off - the chrhash column.
--    VRUN_TRACE_PPU_FB   on (default) | off - the fbhash column.
--    VRUN_TRACE_PRG      off (default) | on | full - the |prgbank|chrbank| columns
--                        and, with `full`, |prgwin|. These are the ONLY columns
--                        that can see a bank-selection divergence: chrhash covers
--                        all of CHR RAM and is identical whichever 8KB bank is
--                        mapped. Mirror with --prg-state on the BrokenNes side.
--                        See the PRG/bank block for the full argument.
--    VRUN_TRACE_POKE     "frame:addr=value,..." (frame decimal, addr/value HEX)
--                        forced writes to work RAM $000-$7ff, for reaching states
--                        no button sequence reaches. A poked run is PERTURBED and
--                        says so in its header; the cross-emulator comparison is
--                        only valid if --poke-at got the identical spec.
--
--  SEGMENTATION - for runs longer than ~8000 frames. Mesen's --testRunner has a
--  hard 100-SECOND wall clock (measured; see the SEGMENTATION block below), so a
--  long run has to be cut into pieces that each finish inside it. Normally driven
--  by tools/Run-MesenSegmented.ps1 rather than by hand.
--    VRUN_TRACE_FRAME_BASE     first frame number this segment emits (default 0)
--    VRUN_TRACE_SEG_IN         savestate to resume from (absent = cold power-on)
--    VRUN_TRACE_SEG_OUT        savestate to write for the next segment
--    VRUN_TRACE_SEG_OUT_FRAME  absolute frame to snapshot at (default: one past
--                        this segment's last record). Set it to the LAST record's
--                        frame to get the one-frame overlap the stitcher needs -
--                        Mesen's savestate does not carry the output screen
--                        buffer, so the first frame after a resume has a wrong
--                        fbhash and must be re-emitted and discarded.
--    VRUN_TRACE_APU      on (default) | off - the 24 APU columns (both pulses,
--                        triangle, noise, DMC, frame sequencer, synthesized
--                        $4015). Emitted in HARDWARE units so they diff
--                        digit-for-digit against `Workshop --trace`. See the
--                        "APU columns" block further down for what is
--                        deliberately absent and the measurement behind it.
--    VRUN_TRACE_AUDIO    on (default) | off - the 4 audio-fingerprint columns.
--                        ALWAYS emitted as '-' here: this build of Mesen has no
--                        way to hand audio samples to Lua and no command-line
--                        switch for its WAV recorder. They exist so both sides
--                        carry the same column count and the differ can report
--                        "not compared" rather than a column-count mismatch.
--    VRUN_TRACE_FB_AT    endframe (default) | record. WHERE the frame is
--                        grabbed. `endframe` takes it at PPU scanline 240 dot
--                        0, the instant the visible frame is finished and
--                        before anything of the next one can land in the
--                        buffer; the record emitted ~22 scanlines later carries
--                        it. That matches BrokenNes exactly: its PPU renders a
--                        whole scanline at the scanline's END, so at its
--                        sample point (scanline 0 dot 0) the framebuffer still
--                        holds the completed previous frame untouched.
--                        `record` grabs at the sample point instead. MEASURED, do
--                        not guess: over 900 frames of gameplay on game.nes,
--                        endframe matches BrokenNes on 899/900 frames at shift 0,
--                        while record matches on only 580/900 at shift 0 and
--                        847/899 at shift +1 - Mesen's screen buffer at the record
--                        point still holds the PREVIOUS frame. So `record` is kept
--                        only as the control that proves endframe is the aligned
--                        one; do not diff with it.
--    VRUN_TRACE_MESEN_SETTINGS  path to Mesen's settings.json, for the
--                        Nes.UserPalette table used to invert the framebuffer.
--                        Defaults to <mesen dir>/settings.json, derived from
--                        emu.getScriptDataFolder(). If it cannot be read, a
--                        built-in copy of Mesen 2.1.1's default palette is
--                        used and the header says so.
--
--  SAMPLE POINT - READ THIS BEFORE TRUSTING A DIFF
--    "End of frame" is ambiguous and the two emulators resolve it differently.
--    Measured on this exact Mesen build, emu.eventType.endFrame fires at PPU
--    scanline 240, cycle 0 - after the visible rendering but BEFORE vblank and
--    before the game's NMI handler runs. BrokenNes cannot sample there: its
--    NES.RunFrame() is a CPU-cycle budget (NtscAccurateFrameRate: 89342/89341
--    alternating PPU dots, NES.cs:766) that returns at the first instruction
--    boundary at or after the whole frame's worth of dots, i.e. at the start of
--    the next frame's scanline 0 - 22 scanlines (~2500 CPU cycles) LATER than
--    Mesen's endFrame, with the entire vblank and NMI handler in between.
--
--    Sampling at endFrame on this side and at RunFrame()'s return on the other
--    therefore compares two different instants of the same frame, and every
--    field diverges from frame 1 for a reason that is pure harness. Measured on
--    game.nes, 600 frames, no input: 1/600 RAM hashes agreed.
--
--    So the default here is `frame-end`, which reproduces BrokenNes' boundary
--    exactly: arm at startFrame (scanline -1, cycle 0) and take the record at
--    the first instruction boundary at or after PPU scanline 0, cycle 0. Same
--    rule, same instant. `mesen-endframe` restores the old scanline-240 point
--    and is kept only for inspecting Mesen against itself.
--
--  EXIT CODES
--    0 ok   2 bad env/input script   3 SHA-256 self-test failed
--    4 determinism precondition failed   5 output I/O failed
--
--  REQUIRES  Mesen's "Allow access to I/O and OS functions"
--    (tools/mesen/settings.json -> Debug.ScriptWindow.AllowIoOsAccess = true,
--    or Script window -> Settings -> Restrictions). Without it os.getenv and
--    io.open are absent and this script cannot run at all. Same gate the rest
--    of this directory's file-writing scripts need - see
--    docs/01-toolchain-setup.md section 3.2.
--
--  POWER-ON RAM - READ THIS BEFORE TRUSTING A DIFF
--    Mesen 2.1.1 offers exactly three power-on RAM fills (Nes.RamPowerOnState:
--    AllZeros / AllOnes / Random). BrokenNes does NOT use any of them: its
--    Bus ctor calls InitializeRamPowerOnPattern(), which writes FCEUX's
--    default repeating 8-byte pattern 00 00 00 00 FF FF FF FF
--    (Windows/NesEmulator/board/Bus.cs). A ROM that reads uninitialised RAM
--    would therefore take a different path on the two emulators from its very
--    first instruction, and every frame would look divergent for a reason that
--    has nothing to do with an emulation bug.
--
--    So: settings.json pins RamPowerOnState = AllZeros (which kills the
--    randomisation), this script VERIFIES at power-on that RAM really is all
--    zero, and then overwrites all 2048 bytes with the requested fill -
--    default `fceux`, i.e. bit-for-bit what BrokenNes starts with. The write
--    happens in this file's top-level chunk, which Mesen runs at
--    cpu.cycleCount == 7 - after the 6502 reset sequence (which only performs
--    three dummy stack READS and cannot have touched RAM) and BEFORE the
--    first instruction of the ROM executes. The fill actually in force is
--    recorded in the '#' header of every trace.
-- ============================================================================

local TRACER = "state_trace.lua v1.0"

------------------------------------------------------------------------------
-- Fatal-error plumbing. print()/emu.log() are invisible from outside a
-- headless --testRunner process, so a failure has to reach the caller through
-- the output file plus a distinct process exit code.
------------------------------------------------------------------------------
local OUT_PATH = os.getenv("VRUN_TRACE_OUT") or "state_trace.txt"
local out = nil

local function die(code, msg)
	local target = out
	if not target then target = io.open(OUT_PATH, "wb") end
	if target then
		target:write("# FATAL(", tostring(code), "): ", msg, "\n")
		target:close()
	end
	print("FATAL(" .. tostring(code) .. "): " .. msg)
	emu.stop(code)
	error(msg, 0)
end

------------------------------------------------------------------------------
-- SHA-256, pure Lua 5.4 (Mesen 2.1.1 embeds 5.4, so native 64-bit integers and
-- bitwise operators are available - no bit-library shims needed). Self-tested
-- against published vectors before a single trace line is written, because a
-- subtly wrong hash makes every frame look divergent and sends the whole
-- investigation down a rabbit hole.
------------------------------------------------------------------------------
local M32 = 0xFFFFFFFF

local K = {
	0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1,
	0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
	0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
	0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
	0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
	0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
	0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
	0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
	0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
	0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
	0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
}

local W = {}   -- reusable 64-word message schedule

-- Compress one 512-bit block. `h` is the 8-word state (mutated in place),
-- `m` is 16 big-endian 32-bit words, 1-indexed.
local function sha256_block(h, m)
	for i = 1, 16 do W[i] = m[i] end
	for i = 17, 64 do
		local v = W[i - 15]
		local s0 = (((v >> 7) | (v << 25)) ~ ((v >> 18) | (v << 14)) ~ (v >> 3)) & M32
		v = W[i - 2]
		local s1 = (((v >> 17) | (v << 15)) ~ ((v >> 19) | (v << 13)) ~ (v >> 10)) & M32
		W[i] = (W[i - 16] + s0 + W[i - 7] + s1) & M32
	end

	local a, b, c, d = h[1], h[2], h[3], h[4]
	local e, f, g, hh = h[5], h[6], h[7], h[8]

	for i = 1, 64 do
		local S1 = (((e >> 6) | (e << 26)) ~ ((e >> 11) | (e << 21)) ~ ((e >> 25) | (e << 7))) & M32
		local ch = (e & f) ~ ((~e) & g)
		local t1 = (hh + S1 + ch + K[i] + W[i]) & M32
		local S0 = (((a >> 2) | (a << 30)) ~ ((a >> 13) | (a << 19)) ~ ((a >> 22) | (a << 10))) & M32
		local maj = (a & b) ~ (a & c) ~ (b & c)
		local t2 = (S0 + maj) & M32
		hh = g; g = f; f = e; e = (d + t1) & M32
		d = c; c = b; b = a; a = (t1 + t2) & M32
	end

	h[1] = (h[1] + a) & M32; h[2] = (h[2] + b) & M32
	h[3] = (h[3] + c) & M32; h[4] = (h[4] + d) & M32
	h[5] = (h[5] + e) & M32; h[6] = (h[6] + f) & M32
	h[7] = (h[7] + g) & M32; h[8] = (h[8] + hh) & M32
end

local function sha256_init()
	return { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
	         0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 }
end

local BLOCK_FMT = ">" .. string.rep("I4", 16)

-- Generic SHA-256 over a Lua byte string. Used for the ROM hash, for the
-- self-test vectors, and as the independent cross-check of the fast RAM path.
local function sha256_hex(s)
	local h = sha256_init()
	local bits = #s * 8
	s = s .. "\128" .. string.rep("\0", (55 - #s) % 64) .. string.pack(">I8", bits)
	local m, pos, n = {}, 1, #s
	while pos <= n do
		local t = table.pack(string.unpack(BLOCK_FMT, s, pos))
		for j = 1, 16 do m[j] = t[j] end
		pos = t[17]
		sha256_block(h, m)
	end
	return string.format("%08x%08x%08x%08x%08x%08x%08x%08x",
		h[1], h[2], h[3], h[4], h[5], h[6], h[7], h[8])
end

------------------------------------------------------------------------------
-- SHA-256 self-test. Refuse to produce a trace at all if this fails.
------------------------------------------------------------------------------
do
	local vectors = {
		{ "", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" },
		{ "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" },
		{ "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
		  "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1" },
		-- the one that actually matters here: 2048 zero bytes, i.e. a
		-- zero-filled NES work RAM.
		{ string.rep("\0", 2048),
		  "e5a00aa9991ac8a5ee3109844d84a55583bd20572ad3ffcd42792f3c36b183ad" },
		-- and FCEUX's power-on pattern over the same 2048 bytes
		{ string.rep("\0\0\0\0\255\255\255\255", 256),
		  "9230156049936eb09bc3bfde6c692ad0e16f0dac1c0d16435ff2a21d33687a39" },
	}
	for i, v in ipairs(vectors) do
		local got = sha256_hex(v[1])
		if got ~= v[2] then
			die(3, string.format("SHA-256 self-test vector %d failed: got %s want %s", i, got, v[2]))
		end
	end
end

------------------------------------------------------------------------------
-- Fast path: hash the 2048 bytes of internal RAM straight out of the emulator.
-- emu.read32 returns a little-endian dword (verified: writing 11 22 33 44 to
-- $0000..$0003 reads back 0x44332211), and SHA-256 consumes big-endian words,
-- so one byte-swap per dword turns 512 reads into the 512 schedule words of
-- 32 blocks with no per-byte work at all. 2048 bytes is an exact multiple of
-- 64, so the padding is always one extra all-known block.
------------------------------------------------------------------------------
local MEM_RAM = emu.memType.nesInternalRam    -- 2048 bytes, direct-indexed 0..2047
local RAM_SIZE = 2048
local emu_read32 = emu.read32
local emu_read = emu.read

local PAD = {}
do
	PAD[1] = 0x80000000
	for j = 2, 14 do PAD[j] = 0 end
	PAD[15] = 0
	PAD[16] = RAM_SIZE * 8       -- 16384 bits, fits in the low length word
end

local blk = {}
local function ram_hash16()
	local h = sha256_init()
	local addr = 0
	for _ = 1, RAM_SIZE // 64 do
		for j = 1, 16 do
			local d = emu_read32(addr, MEM_RAM) & M32
			blk[j] = ((d & 0xff) << 24) | ((d & 0xff00) << 8) | ((d >> 8) & 0xff00) | ((d >> 24) & 0xff)
			addr = addr + 4
		end
		sha256_block(h, blk)
	end
	sha256_block(h, PAD)
	return string.format("%08x%08x", h[1], h[2])   -- first 16 hex chars
end

local function ram_bytes()
	local t = {}
	for a = 0, RAM_SIZE - 1 do t[a + 1] = string.char(emu_read(a, MEM_RAM)) end
	return table.concat(t)
end

------------------------------------------------------------------------------
-- FNV-1a, 64-bit - the hash for every v2 (PPU) column.
--
-- Not a replacement for the SHA-256 above: ramhash stays SHA-256 so a v1 trace
-- and a v2 trace remain comparable in that column. But the new columns hash
-- 35 KB per frame on top of it (CIRAM + OAM + palette + 32 KB of CHR) plus a
-- 61440-pixel frame, and a pure-Lua SHA-256 round is two orders of magnitude
-- more work than an xor and a 64-bit multiply. Lua 5.4's integers are 64-bit
-- and wrap on overflow, so this is bit-for-bit what the C# ulong side computes;
-- string.format("%016x") prints them unsigned, which the self-test below pins.
------------------------------------------------------------------------------
local FNV_OFFSET = 0xcbf29ce484222325   -- wraps to the right bit pattern; see Lua 5.4 manual 3.1
local FNV_PRIME  = 0x100000001b3

local function fnv_bytes(s)
	local h = FNV_OFFSET
	for i = 1, #s do h = (h ~ s:byte(i)) * FNV_PRIME end
	return h
end

local function fnv_hex(h) return string.format("%016x", h) end

do
	-- Vectors computed independently of this implementation. The last two are
	-- the ones that actually matter: a zero-filled 2 KB block (an untouched
	-- CIRAM) and FCEUX's power-on pattern. A silently wrong hash here would
	-- make every frame look divergent, exactly like a wrong SHA-256 would.
	local vectors = {
		{ "", "cbf29ce484222325" },
		{ "abc", "e71fa2190541574b" },
		{ string.rep("\0", 2048), "28c31cf8df2ec325" },
		{ string.rep("\0\0\0\0\255\255\255\255", 256), "175cb070add98725" },
	}
	for i, v in ipairs(vectors) do
		local got = fnv_hex(fnv_bytes(v[1]))
		if got ~= v[2] then
			die(3, string.format("FNV-1a-64 self-test vector %d failed: got %s want %s", i, got, v[2]))
		end
	end
	local t = {}
	for b = 0, 255 do t[#t + 1] = string.char(b) end
	local got = fnv_hex(fnv_bytes(table.concat(t)))
	if got ~= "4242dc5249c33625" then
		die(3, "FNV-1a-64 self-test vector 5 (bytes 00..ff) failed: got " .. got .. " want 4242dc5249c33625")
	end
end

------------------------------------------------------------------------------
-- PPU memory. Same read32 trick as the RAM hash: four bytes per native call
-- instead of one, which matters because CHR alone is 32 KB every frame.
-- read32 is little-endian (byte at `addr` is the low byte - verified in the
-- RAM path above), so the shifts below walk memory in address order.
------------------------------------------------------------------------------
local MEM_CIRAM = emu.memType.nesNametableRam     -- 2048 bytes of CIRAM, raw, pre-mirroring
local MEM_OAM   = emu.memType.nesSpriteRam        -- 256 bytes of primary OAM
local MEM_PAL   = emu.memType.nesPaletteRam       -- 32 bytes
local CIRAM_SIZE, OAM_SIZE, PAL_SIZE = 2048, 256, 32

local function fnv_mem(mt, size)
	local h = FNV_OFFSET
	local a = 0
	for _ = 1, size // 4 do
		local d = emu_read32(a, mt) & M32
		h = (h ~ (d & 0xff)) * FNV_PRIME
		h = (h ~ ((d >> 8) & 0xff)) * FNV_PRIME
		h = (h ~ ((d >> 16) & 0xff)) * FNV_PRIME
		h = (h ~ ((d >> 24) & 0xff)) * FNV_PRIME
		a = a + 4
	end
	for b = a, size - 1 do h = (h ~ emu_read(b, mt)) * FNV_PRIME end
	return h
end

local function mem_bytes(mt, size)
	local t = {}
	for a = 0, size - 1 do t[a + 1] = string.char(emu_read(a, mt)) end
	return table.concat(t)
end

-- As fnv_mem, but over an arbitrary [from, from+size) window rather than a whole
-- memory type. Needed for the CPU-visible PRG window, which lives at $8000 in the
-- CPU address space rather than at offset 0 of a dedicated buffer.
local function fnv_mem_range(mt, from, size)
	local h = FNV_OFFSET
	local a = from
	for _ = 1, size // 4 do
		local d = emu_read32(a, mt) & M32
		h = (h ~ (d & 0xff)) * FNV_PRIME
		h = (h ~ ((d >> 8) & 0xff)) * FNV_PRIME
		h = (h ~ ((d >> 16) & 0xff)) * FNV_PRIME
		h = (h ~ ((d >> 24) & 0xff)) * FNV_PRIME
		a = a + 4
	end
	for b = a, from + size - 1 do h = (h ~ emu_read(b, mt)) * FNV_PRIME end
	return h
end

local function mem_bytes_range(mt, from, size)
	local t = {}
	for a = 0, size - 1 do t[a + 1] = string.char(emu_read(from + a, mt)) end
	return table.concat(t)
end

-- PALETTE CANON - see the header block. $10/$14/$18/$1c are write-mirrors that
-- nothing renders from; Mesen keeps them in sync with $3f00/$04/$08/$0c while
-- BrokenNes leaves them at their power-on value, so folding them is the
-- difference between palhash meaning something and palhash differing forever.
local function palette_canon_bytes()
	local p = {}
	for a = 0, PAL_SIZE - 1 do p[a + 1] = emu_read(a, MEM_PAL) & 0x3F end
	p[0x11] = p[0x01]; p[0x15] = p[0x05]; p[0x19] = p[0x09]; p[0x1D] = p[0x0D]  -- 1-based for $10/$14/$18/$1c
	local t = {}
	for i = 1, PAL_SIZE do t[i] = string.char(p[i]) end
	return table.concat(t)
end

------------------------------------------------------------------------------
-- Environment / input script
------------------------------------------------------------------------------
local FRAMES = tonumber(os.getenv("VRUN_TRACE_FRAMES") or "") or 1200
if FRAMES ~= math.floor(FRAMES) or FRAMES < 1 then
	die(2, "VRUN_TRACE_FRAMES must be a positive integer, got " ..
		tostring(os.getenv("VRUN_TRACE_FRAMES")))
end
FRAMES = math.tointeger(FRAMES)

------------------------------------------------------------------------------
-- SEGMENTATION - how a run longer than Mesen's testRunner allows gets traced
------------------------------------------------------------------------------
-- MEASURED, and it is not what the old comment in this file guessed: Mesen
-- 2.1.1's --testRunner kills the process at 100 SECONDS of wall clock, always,
-- whatever the script is doing. Five independent runs, every one dead at 100s:
--   frame counter only, no getState, no callbacks   -> died at frame 35750
--   ditto, 20000 frames requested                   -> COMPLETED in 68s
--   getScreenBuffer + palette-index hash per frame  -> died at 19250, then 19000
--   the above + CIRAM/OAM/pal/CHR hashes            -> died at 15750, then 16000
--   this tracer, full columns                       -> died at ~8320
-- The death FRAME moves run to run and falls as per-frame work rises; the death
-- TIME does not move at all. It is a wall clock, not a frame cap and not the
-- whole-range exec memory callback the old note blamed (a callback-only control
-- completed 20000 frames clean). Debug.ScriptWindow.ScriptTimeout does not
-- change it: raised from 600 to 100000, the wall stayed at exactly 100s.
--
-- So the fix is not to make the script cheaper, it is to CUT THE RUN UP. A
-- segment loads the previous segment's savestate, emits its own slice of frames
-- with absolute frame numbers, and saves a savestate for the next one. Verified
-- byte-exact against an unsegmented control: 600+600 through a savestate and a
-- straight 1200 agree on cpu.cycleCount, pc, and the RAM / CIRAM / OAM / CHR
-- hashes, all four.
--
-- Mesen only permits createSavestate/loadSavestate inside an exec memory
-- callback for the main CPU ("This function must be called inside an exec memory
-- operation callback for the main CPU"), which the default frame-end sample path
-- already runs inside. Hence the SAMPLE guard below.
--
--   VRUN_TRACE_FRAME_BASE  first frame number this segment emits (default 0)
--   VRUN_TRACE_SEG_IN      savestate to resume from (absent = cold power-on)
--   VRUN_TRACE_SEG_OUT     savestate to write after the last frame of this segment
local FRAME_BASE = tonumber(os.getenv("VRUN_TRACE_FRAME_BASE") or "") or 0
if FRAME_BASE ~= math.floor(FRAME_BASE) or FRAME_BASE < 0 then
	die(2, "VRUN_TRACE_FRAME_BASE must be a non-negative integer, got " ..
		tostring(os.getenv("VRUN_TRACE_FRAME_BASE")))
end
FRAME_BASE = math.tointeger(FRAME_BASE)
local SEG_IN  = os.getenv("VRUN_TRACE_SEG_IN")
local SEG_OUT = os.getenv("VRUN_TRACE_SEG_OUT")
if SEG_IN  == "" then SEG_IN  = nil end
if SEG_OUT == "" then SEG_OUT = nil end

-- VRUN_TRACE_SEG_OUT_FRAME - absolute frame the outgoing savestate is taken at,
-- i.e. the frame the NEXT segment will resume on. Defaults to one past this
-- segment's last record, which butt-joins the segments with no overlap.
--
-- WHY YOU NORMALLY WANT IT ONE LOWER THAN THAT. Measured: a resumed segment
-- reproduces the unsegmented run exactly on every column - pc, registers,
-- ramhash, CIRAM, OAM, palette, all the PPU latches, chrhash, the bank columns
-- and all 24 APU columns - EXCEPT fbhash on the single first frame after the
-- load. Mesen's savestate does not carry the output screen buffer, so the first
-- getScreenBuffer() after a resume returns a frame the restored PPU did not
-- fully draw. One frame, one column, and it is a harness artifact rather than an
-- emulation difference - but it would still show up in a diff as a divergence.
-- So the driver overlaps segments by one frame: segment k snapshots at its LAST
-- record's frame, segment k+1 re-emits that frame as a throwaway warm-up, and
-- the stitcher keeps segment k's copy and drops segment k+1's. The stitched
-- trace is then byte-identical to an unsegmented one.
local SEG_OUT_FRAME = tonumber(os.getenv("VRUN_TRACE_SEG_OUT_FRAME") or "") or FRAME_BASE + FRAMES
SEG_OUT_FRAME = math.tointeger(SEG_OUT_FRAME) or (FRAME_BASE + FRAMES)
if SEG_OUT and (SEG_OUT_FRAME <= FRAME_BASE or SEG_OUT_FRAME > FRAME_BASE + FRAMES) then
	die(2, string.format(
		"VRUN_TRACE_SEG_OUT_FRAME must be in (%d, %d]; got %d",
		FRAME_BASE, FRAME_BASE + FRAMES, SEG_OUT_FRAME))
end
-- The last frame this segment emits, inclusive.
local FRAME_END = FRAME_BASE + FRAMES

local INPUT_SCRIPT = os.getenv("VRUN_TRACE_INPUT") or ""
local RAMFILL = string.lower(os.getenv("VRUN_TRACE_RAMFILL") or "fceux")
local SAMPLE = string.lower(os.getenv("VRUN_TRACE_SAMPLE") or "frame-end")
if SAMPLE ~= "frame-end" and SAMPLE ~= "prerender" and SAMPLE ~= "dot"
   and SAMPLE ~= "mesen-endframe" then
	die(2, "VRUN_TRACE_SAMPLE must be frame-end|prerender|dot|mesen-endframe, got '" .. SAMPLE .. "'")
end
-- Savestates are only legal inside an exec memory callback, and mesen-endframe is
-- the one sample mode that never enters one. Refuse rather than silently produce a
-- segment with no savestate, which would break the chain a dozen segments later.
if (SEG_IN or SEG_OUT) and SAMPLE == "mesen-endframe" then
	die(2, "VRUN_TRACE_SEG_IN/SEG_OUT need VRUN_TRACE_SAMPLE=frame-end|prerender|dot; " ..
		"mesen-endframe takes its record outside any exec callback, and Mesen only allows " ..
		"createSavestate/loadSavestate inside one.")
end

-- SAMPLE=dot: take the record at the first instruction boundary at or after
-- VRUN_TRACE_SAMPLE_DOT dots past emu.eventType.endFrame (scanline 240, cycle 0), optionally
-- sliding by VRUN_TRACE_SAMPLE_SLOPE dots per frame. Landmarks on that scale:
--   0     scanline 240 cycle 0  (Mesen's own endFrame)
--   7161  scanline -1  cycle 0  (start of pre-render == SAMPLE=prerender)
--   7502  scanline 0   cycle 0  (== SAMPLE=frame-end)
-- Two dials, because two different things move the correct sample point and they have to be
-- separated to be diagnosed:
--   * a CONSTANT offset is a power-on CPU/PPU alignment difference (Mesen even offers to
--     randomize that alignment) - boring, measure it once by sweeping SAMPLE_DOT;
--   * a per-frame SLOPE is a frame-length disagreement - the two emulators think an NTSC
--     frame is a different number of dots, which is an emulation defect and accumulates
--     without bound. Sweep SLOPE to measure it; a non-zero best-fit slope IS the finding.
local SAMPLE_DOT = tonumber(os.getenv("VRUN_TRACE_SAMPLE_DOT") or "") or 0
local SAMPLE_SLOPE = tonumber(os.getenv("VRUN_TRACE_SAMPLE_SLOPE") or "") or 0.0
if SAMPLE == "dot" then
	if SAMPLE_DOT ~= math.floor(SAMPLE_DOT) or SAMPLE_DOT < 0 or SAMPLE_DOT > 7502 then
		die(2, "VRUN_TRACE_SAMPLE_DOT must be an integer 0..7502 (scanline 240 cycle 0 through " ..
			"scanline 0 cycle 0), got " .. tostring(os.getenv("VRUN_TRACE_SAMPLE_DOT")))
	end
	SAMPLE_DOT = math.tointeger(SAMPLE_DOT)
elseif SAMPLE == "frame-end" then SAMPLE_DOT = 7502; SAMPLE_SLOPE = 0.0
else SAMPLE_DOT = 7161; SAMPLE_SLOPE = 0.0 end

------------------------------------------------------------------------------
-- v2 (PPU) toggles
------------------------------------------------------------------------------
local function envOnOff(name, default)
	local raw = os.getenv(name)
	if raw == nil or raw == "" then return default end
	raw = string.lower(raw)
	if raw == "on" or raw == "true" or raw == "1" or raw == "yes" then return true end
	if raw == "off" or raw == "false" or raw == "0" or raw == "no" then return false end
	die(2, name .. " must be on|off, got '" .. raw .. "'")
end

local PPU_ON  = envOnOff("VRUN_TRACE_PPU", true)
local PPU_CHR = envOnOff("VRUN_TRACE_PPU_CHR", true)
local PPU_FB  = envOnOff("VRUN_TRACE_PPU_FB", true)

-- APU / audio columns. Default on, exactly like the PPU ones, so a plain run of this
-- tracer is directly diffable against a plain run of `Workshop --trace`.
local APU_ON   = envOnOff("VRUN_TRACE_APU", true)
local AUDIO_ON = envOnOff("VRUN_TRACE_AUDIO", true)

local FB_AT = string.lower(os.getenv("VRUN_TRACE_FB_AT") or "endframe")
if FB_AT ~= "endframe" and FB_AT ~= "record" then
	die(2, "VRUN_TRACE_FB_AT must be endframe|record, got '" .. FB_AT .. "'")
end

-- CHR: this ROM is mapper 30 (UNROM-512), so it is CHR-RAM and the game bakes
-- its own tiles into it at runtime. That makes CHR a live, mutating input to
-- the picture rather than a constant, and worth a per-frame column.
local MEM_CHR, CHR_SIZE, CHR_DESC = nil, 0, "none"
if PPU_ON and PPU_CHR then
	local ramSize = emu.getMemorySize(emu.memType.nesChrRam) or 0
	local romSize = emu.getMemorySize(emu.memType.nesChrRom) or 0
	if ramSize > 0 then MEM_CHR, CHR_SIZE, CHR_DESC = emu.memType.nesChrRam, ramSize, "chr-ram"
	elseif romSize > 0 then MEM_CHR, CHR_SIZE, CHR_DESC = emu.memType.nesChrRom, romSize, "chr-rom" end
end

------------------------------------------------------------------------------
-- PRG / bank columns (VRUN_TRACE_PRG = off | on | full)
------------------------------------------------------------------------------
-- WHY THIS EXISTS. Until now the trace hashed CIRAM, OAM, palette, CHR and the
-- framebuffer, but nothing that names which BANK is selected. That is a real
-- blind spot on a mapper-30 cart, and worse than it sounds for CHR: chrhash
-- covers all 32 KB of CHR RAM, so it hashes the same value no matter which 8 KB
-- window the mapper currently points the PPU at. A pure bank-selection
-- divergence is therefore invisible to every column that existed before this
-- one, and would only ever be caught indirectly, if and when it happened to
-- change the picture.
--
-- Mesen exposes the mapper state directly in emu.getState() (verified on this
-- build against game.nes): "mapper.prgBank" is mapper 30's 16 KB PRG bank
-- register, and "mapper.chrMemoryOffset0" is the BYTE offset into CHR RAM that
-- PPU $0000 currently resolves to - i.e. the selected 8 KB CHR bank * 8192.
-- Both are emitted in the units the BrokenNes side can reproduce exactly:
-- prgbank as the raw bank index, chrbank as the byte offset.
--
--   on   : |prgbank|chrbank        - free, both come out of the getState() call
--                                    the record already makes.
--   full : |prgbank|chrbank|prgwin - adds an FNV-1a-64 over the 32 KB the CPU
--                                    actually sees at $8000-$FFFF. That is the
--                                    only column that can catch a mapper-30
--                                    FLASH write (this board self-programs its
--                                    own PRG), because a flashed byte changes
--                                    content without changing any bank number.
--                                    Costs ~32 KB of reads per frame.
--
-- prgwin reads through emu.memType.nesDebug, the debugger's view, NOT
-- nesMemory: the header's APU note records that a nesMemory read is the
-- SIDE-EFFECTING path (it is how a $4015 catch-up was forced), and a tracer must
-- never perturb the run it is measuring. Measured on this build over
-- $8000-$FFFF, nesDebug and nesMemory return identical bytes (0 differing of
-- 32768) and 32 KB of nesDebug reads move 0 of the 105 apu.* state keys.
local PRG_MODE = string.lower(os.getenv("VRUN_TRACE_PRG") or "off")
if PRG_MODE ~= "off" and PRG_MODE ~= "on" and PRG_MODE ~= "full" then
	die(2, "VRUN_TRACE_PRG must be off|on|full, got '" .. PRG_MODE .. "'")
end
local PRG_ON   = (PRG_MODE ~= "off")
local PRG_FULL = (PRG_MODE == "full")
local MEM_DBG  = emu.memType.nesDebug
local MEM_PRG  = emu.memType.nesPrgRom
local PRG_ROM_SIZE = emu.getMemorySize(MEM_PRG) or 0
local PRG_WIN_BASE, PRG_WIN_SIZE = 0x8000, 0x8000

-- Mesen's NES controller table uses exactly these eight lower-case field names
-- (verified live via emu.getInput(0)).
local BUTTONS = { "a", "b", "select", "start", "up", "down", "left", "right" }

local function newHeld()
	local t = {}
	for _, b in ipairs(BUTTONS) do t[b] = false end
	return t
end

local VALID = {}
for _, b in ipairs(BUTTONS) do VALID[b] = true end

-- Parse "60:Start,66:,120:Right+A,180:" into an ascending list of
-- { frame = n, held = {a=false,...} }. Later steps override earlier ones at
-- the same frame; steps may be listed out of order.
local function parseInputScript(spec)
	local steps = {}
	local order = 0
	for raw in string.gmatch(spec, "[^,]+") do
		local piece = raw:match("^%s*(.-)%s*$")
		if piece ~= "" then
			local fstr, bstr = piece:match("^([%w%-]+)%s*:%s*(.*)$")
			if not fstr then
				die(2, "malformed input-script step '" .. piece ..
					"' - expected <frame>:<buttons>, e.g. 120:Right+A")
			end
			local fnum = tonumber(fstr)
			if not fnum or fnum ~= math.floor(fnum) or fnum < 0 then
				die(2, "input-script step '" .. piece .. "' has a non-integer / negative frame")
			end
			local held = newHeld()
			for name in string.gmatch(bstr, "[^%+]+") do
				local btn = string.lower(name:match("^%s*(.-)%s*$"))
				if btn ~= "" then
					if not VALID[btn] then
						die(2, "input-script step '" .. piece .. "' names unknown button '" ..
							name .. "' - valid: A B Select Start Up Down Left Right")
					end
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
	-- collapse duplicate frames, last listed wins
	local collapsed = {}
	for _, s in ipairs(steps) do
		if #collapsed > 0 and collapsed[#collapsed].frame == s.frame then
			collapsed[#collapsed] = s
		else
			collapsed[#collapsed + 1] = s
		end
	end
	return collapsed
end

local STEPS = parseInputScript(INPUT_SCRIPT)

local function heldToString(h)
	local parts = {}
	for _, b in ipairs(BUTTONS) do if h[b] then parts[#parts + 1] = b end end
	if #parts == 0 then return "(none)" end
	return table.concat(parts, "+")
end

------------------------------------------------------------------------------
-- Determinism preconditions, checked LIVE against the running machine rather
-- than trusted from a config file.
------------------------------------------------------------------------------
local st0 = emu.getState()

if tostring(st0["region"]) ~= "Ntsc" then
	die(4, "region is '" .. tostring(st0["region"]) ..
		"', expected 'Ntsc' - pin Nes.Region=\"Ntsc\" in tools/mesen/settings.json " ..
		"(this ROM's NES 2.0 header declares timing byte 2 = multi-region, so 'Auto' is not enough)")
end

-- The top-level chunk runs before the ROM's first instruction, so whatever is
-- in RAM right now IS Mesen's power-on fill.
local powerOnHash = sha256_hex(ram_bytes())
local ZEROS_2K = "e5a00aa9991ac8a5ee3109844d84a55583bd20572ad3ffcd42792f3c36b183ad"
if powerOnHash ~= ZEROS_2K then
	die(4, "power-on RAM is not all-zero (sha256 " .. powerOnHash ..
		") - pin Nes.RamPowerOnState=\"AllZeros\" in tools/mesen/settings.json; " ..
		"a randomised fill makes every diff meaningless")
end

-- The top-level chunk must genuinely run before the ROM does, otherwise the
-- fill below would be stomping on live game state instead of setting power-on
-- conditions.
local bootCycle = st0["cpu.cycleCount"]
if type(bootCycle) ~= "number" or bootCycle > 16 then
	die(4, "script started at cpu.cycleCount=" .. tostring(bootCycle) ..
		"; expected <= 16 (immediately after the 7-cycle 6502 reset sequence). " ..
		"The power-on RAM fill would not be a power-on fill.")
end

-- Apply the requested power-on fill.
--
-- SEGMENTS SKIP IT. A resumed segment is not at power-on: the savestate about to be
-- loaded carries the game's live RAM, and laying the fill down here would stomp it.
-- (The write happens before the savestate load either way, so this is belt and
-- braces - but a fill that silently ran on a resume would be exactly the kind of
-- harness bug that costs a day, so it is refused explicitly rather than tolerated.)
local FILL_DESC
if SEG_IN then
	FILL_DESC = "none - SEGMENT RESUME (VRUN_TRACE_SEG_IN set); RAM comes from the savestate"
elseif RAMFILL == "fceux" then
	for a = 0, RAM_SIZE - 1 do
		emu.write(a, ((a & 4) ~= 0) and 0xFF or 0x00, MEM_RAM)
	end
	FILL_DESC = "fceux (repeating 00 00 00 00 FF FF FF FF) - matches BrokenNes Bus.InitializeRamPowerOnPattern()"
elseif RAMFILL == "zeros" then
	FILL_DESC = "zeros (Mesen Nes.RamPowerOnState=AllZeros, left untouched)"
elseif RAMFILL == "ones" then
	for a = 0, RAM_SIZE - 1 do emu.write(a, 0xFF, MEM_RAM) end
	FILL_DESC = "ones (all 0xFF)"
elseif RAMFILL == "none" then
	FILL_DESC = "none (whatever Mesen powered on with; sha256 " .. powerOnHash .. ")"
else
	die(2, "VRUN_TRACE_RAMFILL must be one of fceux|zeros|ones|none, got '" .. RAMFILL .. "'")
end

-- Poke schedule, parsed here (rather than beside applyPokes further down) purely so
-- the '#' header can report it - the header is written before that point in the file,
-- and a perturbed run whose trace does not SAY it was perturbed is a trap.
local POKES = {}      -- [frame] = { {addr, value}, ... }
local POKE_COUNT = 0
local POKE_SPEC = os.getenv("VRUN_TRACE_POKE") or ""
for tok in string.gmatch(POKE_SPEC, "[^,]+") do
	tok = tok:match("^%s*(.-)%s*$")
	if tok ~= "" then
		local fr, ad, va = tok:match("^(%d+):(%x+)=(%x+)$")
		if not fr then
			die(2, "VRUN_TRACE_POKE step must be 'frame:addr=value' with addr and value in hex, got '" .. tok .. "'")
		end
		fr, ad, va = math.tointeger(tonumber(fr)), tonumber(ad, 16), tonumber(va, 16)
		if ad > 0x7FF then
			die(2, string.format("VRUN_TRACE_POKE addr %x is outside work RAM $000-$7ff", ad))
		end
		if va > 0xFF then
			die(2, string.format("VRUN_TRACE_POKE value %x does not fit in a byte", va))
		end
		POKES[fr] = POKES[fr] or {}
		local L = POKES[fr]
		L[#L + 1] = { ad, va }
		POKE_COUNT = POKE_COUNT + 1
	end
end

------------------------------------------------------------------------------
-- Power-on PALETTE RAM - the exact analogue of the work-RAM fill above, and
-- just as load-bearing the moment the trace compares pixels.
--
-- Measured on game.nes at frame 10: CIRAM, OAM and CHR agreed with BrokenNes
-- byte for byte, while all 32 palette bytes and therefore all 61440 pixels
-- differed - purely because the game has not written its palette yet and the
-- two emulators power on with different contents. Mesen powers on with a fixed
-- non-zero pattern (09 01 00 01 00 02 02 0d ...); BrokenNes under --ppu FIX
-- carries the FMC core's cosmetic InitializeDefaultPalette() ramp across the
-- hot-swap. Real hardware is indeterminate, so neither is "right" and they
-- simply have to MATCH.
--
-- Default `zeros`, which is FCEUX's behaviour and what BrokenNes' --trace
-- applies with --power-on-palette zeros (also its default). `none` leaves
-- Mesen's own fill, for looking at what the untouched machine does.
------------------------------------------------------------------------------
local PALFILL = string.lower(os.getenv("VRUN_TRACE_PALFILL") or "zeros")
local PALFILL_DESC
-- Skipped on a segment resume for the same reason as the work-RAM fill: by then the
-- game has long since written its own palette, and a "power-on" fill would destroy it.
if SEG_IN then
	PALFILL_DESC = "none - SEGMENT RESUME (VRUN_TRACE_SEG_IN set); palette comes from the savestate"
elseif PALFILL == "zeros" then
	for a = 0, PAL_SIZE - 1 do emu.write(a, 0x00, MEM_PAL) end
	PALFILL_DESC = "zeros (OVERRIDE - all 32 bytes forced to 00; matches BrokenNes --power-on-palette zeros, and FCEUX)"
elseif PALFILL == "none" then
	PALFILL_DESC = "none (Mesen's own power-on palette RAM, left untouched)"
else
	die(2, "VRUN_TRACE_PALFILL must be zeros|none, got '" .. PALFILL .. "'")
end
local PALFILL_HASH = fnv_hex(fnv_bytes(palette_canon_bytes()))

-- Cross-check the fast read32-driven hash against the generic byte-string
-- implementation on the live, just-filled RAM. This catches an endianness or
-- padding mistake in the fast path that the static vectors above cannot.
local FILLED_HASH
do
	FILLED_HASH = sha256_hex(ram_bytes())
	local fast = ram_hash16()
	local slow = string.sub(FILLED_HASH, 1, 16)
	if fast ~= slow then
		die(3, "fast RAM hash " .. fast .. " != reference " .. slow .. " on live RAM")
	end
	-- Same cross-check for the v2 hash path: the read32-driven fnv_mem against
	-- the byte-string fnv_bytes, on live CIRAM. A wrong shift or a wrong
	-- endianness here would make ntbhash/chrhash differ on every single frame.
	local fastN = fnv_hex(fnv_mem(MEM_CIRAM, CIRAM_SIZE))
	local slowN = fnv_hex(fnv_bytes(mem_bytes(MEM_CIRAM, CIRAM_SIZE)))
	if fastN ~= slowN then
		die(3, "fast CIRAM fnv " .. fastN .. " != reference " .. slowN .. " on live CIRAM")
	end
end

------------------------------------------------------------------------------
-- ROM identity
------------------------------------------------------------------------------
local romInfo = emu.getRomInfo() or {}
local romPath = os.getenv("VRUN_TRACE_ROM") or romInfo.path or "(unknown)"
local romSha256, romSize = "(unavailable)", "(unavailable)"
do
	local rf = io.open(romPath, "rb")
	if rf then
		local data = rf:read("a")
		rf:close()
		if data then
			romSize = tostring(#data)
			romSha256 = sha256_hex(data)
		end
	end
end

------------------------------------------------------------------------------
-- Framebuffer -> NES palette indices
--
-- emu.getScreenBuffer() returns 0x00RRGGBB, i.e. AFTER Mesen's palette has been
-- applied, which is exactly the thing that cannot be compared across emulators.
-- Inverting it gives back the 0-63 index the PPU actually emitted, which can.
--
-- The table is read from Mesen's own settings.json (Nes.UserPalette) rather
-- than hardcoded, so a user who changes their palette gets a correct inversion
-- instead of a silently wrong one; the built-in copy of Mesen 2.1.1's default
-- is only a fallback for when that file cannot be read. Either way EVERY pixel
-- is checked against the table and misses are counted - which is also how a
-- frame drawn with the emphasis bits set is caught, since emphasis multiplies
-- the RGB and moves it off the 64-entry table entirely.
------------------------------------------------------------------------------
local FB_W, FB_H = 256, 240
local FB_PIXELS = FB_W * FB_H

-- Mesen 2.1.1's default NES palette, 0xRRGGBB, index 00..3f. Fallback only.
local MESEN_DEFAULT_PALETTE = {
	0x666666, 0x002A88, 0x1412A7, 0x3B00A4, 0x5C007E, 0x6E0040, 0x6C0600, 0x561D00,
	0x333500, 0x0B4800, 0x005200, 0x004F08, 0x00404D, 0x000000, 0x000000, 0x000000,
	0xADADAD, 0x155FD9, 0x4240FF, 0x7527FE, 0xA01ACC, 0xB71E7B, 0xB53120, 0x994E00,
	0x6B6D00, 0x388700, 0x0C9300, 0x008F32, 0x007C8D, 0x000000, 0x000000, 0x000000,
	0xFFFEFF, 0x64B0FF, 0x9290FF, 0xC676FF, 0xF36AFF, 0xFE6ECC, 0xFE8170, 0xEA9E22,
	0xBCBE00, 0x88D800, 0x5CE430, 0x45E082, 0x48CDDE, 0x4F4F4F, 0x000000, 0x000000,
	0xFFFEFF, 0xC0DFFF, 0xD3D2FF, 0xE8C8FF, 0xFBC2FF, 0xFEC4EA, 0xFECCC5, 0xF7D8A5,
	0xE4E594, 0xCFEF96, 0xBDF4AB, 0xB3F3CC, 0xB5EBF2, 0xB8B8B8, 0x000000, 0x000000,
}

-- <mesen dir>/settings.json, derived from ".../mesen/LuaScriptData/<script>".
local function mesenSettingsPath()
	local explicit = os.getenv("VRUN_TRACE_MESEN_SETTINGS")
	if explicit and explicit ~= "" then return explicit end
	local d = emu.getScriptDataFolder and emu.getScriptDataFolder() or nil
	if not d then return nil end
	local root = (d:gsub("[/\\]LuaScriptData[/\\][^/\\]*$", ""))
	return root .. "/settings.json"
end

local function loadUserPalette(path)
	if not path then return nil, "no settings.json path could be derived" end
	local fh = io.open(path, "rb")
	if not fh then return nil, "could not open " .. path end
	local s = fh:read("a"); fh:close()
	if not s then return nil, "could not read " .. path end
	local i = s:find('"UserPalette"', 1, true)
	if not i then return nil, "no UserPalette key in " .. path end
	local j = s:find("%[", i); if not j then return nil, "malformed UserPalette array" end
	local k = s:find("%]", j); if not k then return nil, "unterminated UserPalette array" end
	local t = {}
	for num in s:sub(j + 1, k - 1):gmatch("%-?%d+") do t[#t + 1] = tonumber(num) end
	-- 64 entries is the plain palette; 512 is the emphasis-expanded one, whose
	-- first 64 are the no-emphasis colours we invert against.
	if #t ~= 64 and #t ~= 512 then
		return nil, string.format("UserPalette has %d entries, expected 64 or 512", #t)
	end
	local out = {}
	for n = 1, 64 do out[n] = t[n] & 0xFFFFFF end   -- drop the alpha byte
	return out, path
end

local FB_ENABLED = false
local FB_PALETTE_SOURCE = "(disabled)"
local FB_COLLISIONS = "(n/a)"
local RGB_TO_INDEX = nil
local fbUnmapped = 0

if PPU_ON and PPU_FB then
	local pal, why = loadUserPalette(mesenSettingsPath())
	if pal then
		FB_PALETTE_SOURCE = "Nes.UserPalette from " .. why
	else
		pal = MESEN_DEFAULT_PALETTE
		FB_PALETTE_SOURCE = "built-in Mesen 2.1.1 default (" .. tostring(why) .. ")"
	end
	RGB_TO_INDEX = {}
	local classes, order = {}, {}
	for n = 1, 64 do
		local rgb = pal[n]
		if RGB_TO_INDEX[rgb] == nil then RGB_TO_INDEX[rgb] = n - 1 end   -- lowest index wins
		if not classes[rgb] then classes[rgb] = {}; order[#order + 1] = rgb end
		table.insert(classes[rgb], n - 1)
	end
	local parts = {}
	for _, rgb in ipairs(order) do
		local c = classes[rgb]
		if #c > 1 then
			local hexes = {}
			for _, ix in ipairs(c) do hexes[#hexes + 1] = string.format("%02x", ix) end
			parts[#parts + 1] = "{" .. table.concat(hexes, ",") .. "}->" .. string.format("%02x", c[1])
		end
	end
	FB_COLLISIONS = (#parts == 0) and "(none - table is injective)" or table.concat(parts, " ")
	FB_ENABLED = true
end

-- Reused across frames: one 61440-entry index table, and a byte-string only
-- built on the frames that actually get dumped.
local fbIdx = {}
local fbHash = "-"
local fbPendingDump = nil

local function captureFrame(wantBytes)
	if not FB_ENABLED then fbHash = "-"; fbPendingDump = nil; return end
	local buf = emu.getScreenBuffer()
	local map = RGB_TO_INDEX
	local h = FNV_OFFSET
	local unmapped = 0
	for i = 1, FB_PIXELS do
		local ix = map[buf[i]]
		if ix == nil then ix = 0xFF; unmapped = unmapped + 1 end
		fbIdx[i] = ix
		h = (h ~ ix) * FNV_PRIME
	end
	fbUnmapped = fbUnmapped + unmapped
	fbHash = fnv_hex(h)
	if wantBytes then
		local chunks = {}
		for i = 1, FB_PIXELS do chunks[i] = string.char(fbIdx[i]) end
		fbPendingDump = table.concat(chunks)
	else
		fbPendingDump = nil
	end
end

------------------------------------------------------------------------------
-- Output + header
------------------------------------------------------------------------------
-- "wb" is load-bearing on Windows: text mode would turn every "\n" into
-- "\r\n" and the trace would not be byte-comparable with the other tracer's.
out = io.open(OUT_PATH, "wb")
if not out then die(5, "cannot open output '" .. OUT_PATH .. "' for writing") end

local function H(fmt, ...)
	if select("#", ...) > 0 then
		out:write("# ", string.format(fmt, ...), "\n")
	else
		out:write("# ", fmt, "\n")
	end
end

H("tracer: %s (Mesen side)", TRACER)
H("mesen-trace: %s", PPU_ON and "v2" or "v1")
-- NOTE: there is deliberately no "format: ..." line here any more. The single
-- machine-readable column list is the composed "columns: ..." line further down,
-- which is assembled from the groups actually enabled (v1 + PPU + APU + audio).
-- A second spec line ahead of it was a trap: the differ takes the first one it
-- sees, so a hand-maintained "format:" listing only the v1+PPU columns silently
-- mis-parsed every record once the APU group was appended. Old traces that carry
-- only a "format:" line still parse - the differ accepts either key.
H("format-notes: lower-case hex, no 0x prefix; frame is 0-based decimal and increments by exactly 1;")
H("format-notes: pc is 4 hex digits, a/x/y/sp/p are 2 each; ramhash is the first 16 hex chars of the")
H("format-notes: SHA-256 of the 2048 bytes of NES work RAM $0000-$07FF hashed in address order.")
H("format-notes: the RGB framebuffer is deliberately not compared - NES palettes are not standardised.")
if PPU_ON then
H("format-notes: ntbhash|oamhash|palhash|chrhash|fbhash are FNV-1a-64 (h=cbf29ce484222325, per byte")
H("format-notes:   h=(h~b)*100000001b3 mod 2^64, 16 hex) over, in order: the 2048 bytes of CIRAM")
H("format-notes:   (emu.memType.nesNametableRam - the raw 2KB the mapper mirrors, NOT the $2000-$2fff")
H("format-notes:   view); the 256 bytes of primary OAM (nesSpriteRam); 32 canonicalised palette")
H("format-notes:   bytes; all %d bytes of %s; and 61440 palette-index pixels.", CHR_SIZE, CHR_DESC)
H("format-notes: palette canon = each byte & 0x3f, then $10/$14/$18/$1c := $00/$04/$08/$0c. Those")
H("format-notes:   four are write-mirrors nothing renders from; Mesen keeps them in sync with their")
H("format-notes:   $3f00 counterparts and BrokenNes does not, so unfolded they would differ forever.")
H("format-notes: ctrl = PPUCTRL & 0xbf. Mesen does not expose $2000 as a byte, so it is rebuilt from")
H("format-notes:   ppu.control.* plus the nametable-select bits, which live in t (bits 10-11) exactly")
H("format-notes:   as on hardware. Bit 6 is the EXT-bus direction bit: Mesen has no field for it, no")
H("format-notes:   game sets it, and masking it off BOTH sides is honest where inventing a 0 is not.")
H("format-notes: mask = PPUMASK, rebuilt from ppu.mask.* (bit0 grayscale, 1 bg-left, 2 spr-left,")
H("format-notes:   3 bg-enable, 4 spr-enable, 5/6/7 emphasis R/G/B).")
H("format-notes: stat = PPUSTATUS & 0xe0 - vblank/sprite0/overflow only. The low 5 bits are PPU open")
H("format-notes:   bus, not state, and are not comparable between emulators.")
H("format-notes: v = ppu.videoRamAddr, t = ppu.tmpVideoRamAddr (4 hex, & 0x7fff), fx = ppu.xScroll.")
H("format-notes: w = write toggle, 1 hex: bit0 the $2005 latch, bit1 the $2006 latch. Hardware shares")
H("format-notes:   ONE toggle and Mesen models it correctly, so this side only ever emits 0 or 3;")
H("format-notes:   BrokenNes' PPU_FIX keeps two independent latches and can emit 1 or 2. Two bits on")
H("format-notes:   purpose, so a differ can name which latch drifted instead of just 'w differs'.")
H("ppu-sample-point: CIRAM/OAM/palette/CHR and the PPU registers are read at the SAME instant as the")
H("ppu-sample-point:   CPU/RAM columns - the record point described above, no offset. CIRAM, OAM,")
H("ppu-sample-point:   palette and CHR are written during vblank and are stable by then, so they")
H("ppu-sample-point:   should match exactly. ctrl/mask/stat/v/t/fx/w are LIVE mid-frame registers and")
H("ppu-sample-point:   differ for the same boundary reason work RAM does: two emulators cannot be")
H("ppu-sample-point:   aligned below one instruction. Read those as diagnostics, not as a gate.")
H("fb-sample-point: %s", FB_AT == "endframe"
	and "endFrame (PPU scanline 240 dot 0) - the instant the visible frame is complete and before any of the next frame can land in the buffer. The record emitted ~22 scanlines later carries it. This matches BrokenNes, whose PPU renders each scanline at that scanline's END, so at its sample point (scanline 0 dot 0) its framebuffer still holds the completed previous frame untouched."
	or "the record point itself (scanline 0 dot 0). Only for proving that this and endframe agree; endframe is the safe default.")
H("fb-palette-inversion: %s", FB_ENABLED and "ON" or "OFF")
H("fb-palette-source: %s", FB_PALETTE_SOURCE)
H("fb-palette-collisions: %s", FB_COLLISIONS)
H("fb-palette-collisions:   (indices sharing an RGB, so indistinguishable after inversion; lowest")
H("fb-palette-collisions:   wins. This is only safe because the BrokenNes side's table collides")
H("fb-palette-collisions:   IDENTICALLY - compare this line against the other header's.)")
end
H("emulator: Mesen 2.1.1 (Windows x64, single-file), headless via --testRunner")
H("emulator-lua: %s", _VERSION)
H("rom-path: %s", romPath)
H("rom-name: %s", tostring(romInfo.name))
H("rom-size-bytes: %s", romSize)
H("rom-sha256: %s", romSha256)
H("rom-sha1-per-mesen: %s", string.lower(tostring(romInfo.fileSha1Hash)))
H("input-script: %s", INPUT_SCRIPT == "" and "(empty - controller 1 never touched)" or INPUT_SCRIPT)
H("input-steps: %d", #STEPS)
for _, s in ipairs(STEPS) do
	H("input-step: frame %d -> %s", s.frame, heldToString(s.held))
end
H("input-semantics: a step's held set is applied to controller 1 on every emu.eventType.inputPolled")
H("input-semantics: from that frame inclusive until the next step; before the first step nothing is held.")
H("frames: %d", FRAMES)
H("sample-point: %s", SAMPLE)
H("sample-dot-offset: %d + %g per frame   (dots past scanline 240 cycle 0; 7161 = pre-render", SAMPLE_DOT, SAMPLE_SLOPE)
H("sample-dot-offset: start, 7502 = scanline 0 cycle 0. Not meaningful for mesen-endframe.)")
if SAMPLE == "prerender" or SAMPLE == "dot" then
H("frame-boundary: one record per whole frame, taken at the first instruction boundary at or after")
H("frame-boundary: emu.eventType.startFrame (PPU scanline -1, cycle 0 - the start of the pre-render")
H("frame-boundary: line). That is ONE SCANLINE (341 dots, 113.67 CPU cycles) earlier than BrokenNes'")
H("frame-boundary: nominal boundary, and it is the point at which the two emulators' CPU state")
H("frame-boundary: actually coincides, because BrokenNes' PPU_FIX raises VBlank/NMI one full scanline")
H("frame-boundary: late (PPU_FIX.cs:165 sets PPUSTATUS bit 7 when scanlineCycle wraps at the END of")
H("frame-boundary: scanline 241, i.e. scanline 242 dot 0, instead of hardware's scanline 241 dot 1;")
H("frame-boundary: PPU_FIX.cs:122 likewise clears it at scanline 0 dot 0 instead of pre-render dot 1).")
H("frame-boundary: The game's whole frame of work is therefore phase-shifted 340 dots later against")
H("frame-boundary: the PPU. USE THIS MODE ONLY to look for divergence *beyond* that known offset;")
H("frame-boundary: use frame-end to see the offset itself.")
elseif SAMPLE == "frame-end" then
H("frame-boundary: one record per WHOLE frame, taken at the first instruction boundary at or after")
H("frame-boundary: PPU scanline 0, cycle 0 - i.e. after rendering AND vblank AND the pre-render line,")
H("frame-boundary: with the frame's NMI handler already run. Implemented by arming at")
H("frame-boundary: emu.eventType.startFrame (measured: scanline -1, cycle 0) and taking the record in")
H("frame-boundary: an exec memory callback over $0000-$FFFF as soon as ppu.scanline == 0.")
H("frame-boundary: THIS EXACTLY REPRODUCES BrokenNes' boundary: NES.RunFrame() budgets a whole frame")
H("frame-boundary: of PPU dots (89342/89341 alternating, NES.cs:766) and returns at the first")
H("frame-boundary: instruction boundary at or after that budget - same rule, same instant. Verified:")
H("frame-boundary: the first four sample PCs here are 842b/845c/8452/8486 against BrokenNes'")
H("frame-boundary: 842b/845c/8458/8486, whereas Mesen's own endFrame gives 8427/842a/c163/845a.")
H("frame-boundary: controller polls (scanline 241) fall INSIDE the frame they are numbered with,")
H("frame-boundary: matching BrokenNes' SetInputs(held_f) immediately before RunFrame() for frame f.")
else
H("frame-boundary: one record per emu.eventType.endFrame, which Mesen fires at NES PPU scanline 240,")
H("frame-boundary: cycle 0 - i.e. immediately after the frame's visible rendering completes and before")
H("frame-boundary: vblank/NMI. The first endFrame after power-on is frame 0 (verified: it fires at")
H("frame-boundary: cpu.cycleCount ~= 27279, well inside the first frame, with ppu.frameCount == 1).")
H("frame-boundary: *** NOT COMPARABLE WITH BrokenNes --trace *** - it samples ~2500 CPU cycles later,")
H("frame-boundary: at the start of the next scanline 0, with the whole vblank and NMI handler between")
H("frame-boundary: the two instants. Use VRUN_TRACE_SAMPLE=frame-end for a cross-emulator diff.")
end
H("p-register: bit 5 (the 6502's unused/always-set flag) is FORCED SET here. Mesen's emu.getState()")
H("p-register: 'cpu.ps' reports it clear (only 04/05/06/07/84 ever appear over a 600-frame run);")
H("p-register: BrokenNes' CPU_FIX keeps it set, as real hardware does on any PHP/interrupt push. Left")
H("p-register: alone, the p column would differ on 100%% of frames for a representation reason.")
H("ram-memtype: emu.memType.nesInternalRam (2048 bytes, direct-indexed 0..2047; byte-for-byte")
H("ram-memtype: identical to emu.memType.nesMemory $0000-$07FF - verified, 0 differing bytes)")
H("--- determinism pinned ---")
H("det: region = Ntsc            (settings.json Nes.Region; verified live: emu.getState().region == '%s')", tostring(st0["region"]))
H("det: console-type = %s     (settings.json Nes.ConsoleType)", tostring(st0["consoleType"]))
H("det: cpu clock rate = %s Hz  (live, NTSC)", tostring(st0["clockRate"]))
H("det: Nes.RamPowerOnState = AllZeros   (verified live: pre-boot RAM sha256 == 2048 zero bytes)")
H("det: power-on palette RAM fill applied by tracer = %s", PALFILL_DESC)
H("det:   Does not perturb execution: measured on game.nes over 900 frames of gameplay, 'zeros' vs")
H("det:   'none' changed pc and ramhash on 0/900 frames and CIRAM/OAM/CHR on 0/900. It moves only")
H("det:   palhash and fbhash, i.e. only the picture before the game's first palette write, so a v1")
H("det:   (CPU+RAM) trace is bit-identical either way.")
H("power-on-palhash: %s", PALFILL_HASH)
H("det:   fnv-1a-64 over the 32 canonicalised palette bytes the ROM's first instruction sees. Emit")
H("det:   the same line from the other tracer and compare it directly: if these differ, nothing")
H("det:   about the picture before the game's first palette write is meaningful. Palette RAM powers")
H("det:   on indeterminate on real hardware, so no fill is 'right' - but they must match.")
H("det: power-on RAM fill applied by tracer = %s", FILL_DESC)
-- A poked run is a PERTURBED run. Say so loudly and in full, so nobody reads this
-- trace as a recording of natural play - and so the other side's --poke-at can be
-- checked against it step for step.
if POKE_COUNT > 0 then
	H("PERTURBED: this run was POKED. %d forced work-RAM write(s). It is NOT natural play.", POKE_COUNT)
	H("PERTURBED:   spec = %s", POKE_SPEC)
	H("PERTURBED:   Each poke is applied immediately after the previous frame's record, i.e. at the")
	H("PERTURBED:   same instant the BrokenNes tracer's --poke-at writes before its RunFrame(). The")
	H("PERTURBED:   comparison between the two emulators stays valid ONLY if both were given the")
	H("PERTURBED:   same spec; the diff cannot detect a mismatch, so check this line against --poke-at.")
else
	H("det: poke schedule = none (unperturbed run)")
end
H("det:   applied at cpu.cycleCount = %s, i.e. after the 7-cycle 6502 reset sequence (three dummy", tostring(bootCycle))
H("det:   stack READS, no RAM writes) and before the ROM's first instruction.")
H("det:   post-fill pre-boot RAM sha256 = %s", FILLED_HASH)
H("det:   (full 64-hex digest of the 2048 bytes the ROM's first instruction sees. Emit the same line")
H("det:    from the other tracer and compare it directly - if these two differ, nothing downstream")
H("det:    of frame 0 is meaningful.)")
H("det:   *** MISMATCH WARNING *** Mesen 2.1.1 has no FCEUX-pattern power-on option of its own")
H("det:   (only AllZeros/AllOnes/Random). BrokenNes hardcodes FCEUX's 00x4/FFx4 pattern in")
H("det:   Windows/NesEmulator/board/Bus.cs InitializeRamPowerOnPattern(). The two sides only agree")
H("det:   because this tracer writes that pattern in explicitly. If VRUN_TRACE_RAMFILL is set to")
H("det:   anything but 'fceux', frame-0 RAM hashes WILL differ for a legitimate reason.")
H("det: Nes.RandomizeMapperPowerOnState = false")
H("det: Nes.RandomizeCpuPpuAlignment = false")
H("det: Nes.DisableGameDatabase = true    (no per-ROM database overrides)")
H("det: Nes.EnableOamDecay = false, Nes.EnablePpuOamRowCorruption = false")
H("det: Nes.PpuExtraScanlinesBeforeNmi = 0, Nes.PpuExtraScanlinesAfterNmi = 0")
H("det: Nes.DisableFlashSaves = true      (this ROM is mapper 30 with the battery bit set; without")
H("det:   this, Mesen persists flash/save RAM to tools/mesen/Saves and run 2 would start from run 1's")
H("det:   flash contents instead of the pristine ROM)")
H("det: Nes.OverwriteOriginalRom = false, Nes.EnableHdPacks = false")
H("det: Emulation.RunAheadFrames = 0, Emulation.EmulationSpeed = 100")
H("det: Preferences.EnableRewind = false")
H("det: Preferences.EnableAutoSaveState = false")
H("det: Preferences.AutoLoadPatches = false")
H("det: Cheats.DisableAllCheats = true")
H("det: Nes.BreakOnCrash = false, Debug.Debugger.BreakOnUninitRead = false (a break would stall the")
H("det:   headless run instead of tracing; this ROM *does* read uninitialised RAM, which is exactly")
H("det:   why the power-on fill above has to match)")
H("det: no frame skipping (Mesen 2 has none for the NES core; --testRunner runs unthrottled and")
H("det:   emulates every frame regardless of host speed)")
H("det: no savestate is loaded and none is created")
H("det: sha256 self-tested against 5 published/precomputed vectors before this header was written,")
H("det:   and the fast read32 RAM path was cross-checked against the byte-string implementation on")
H("det:   live RAM.")
------------------------------------------------------------------------------
-- APU columns (Mesen side)
------------------------------------------------------------------------------
-- Emitted in HARDWARE units so they compare digit-for-digit with the BrokenNes
-- tracer's --apu-state columns: an 11-bit period register, a length counter, the
-- volume actually in force, a DMC pointer. Not raw emulator internals, which use
-- different conventions on the two sides.
--
-- WHAT IS DELIBERATELY ABSENT, and this is the load-bearing part:
-- emu.getState() reports the APU from wherever NesApu::Run() last caught it up,
-- which at an arbitrary instruction boundary is BEHIND the CPU. Measured on this
-- exact build, 501 frame-boundary samples on game.nes: mean 1582 CPU cycles
-- behind, max 2505. Forcing a catch-up (a side-effecting $4015 read through
-- emu.memType.nesMemory) changes exactly 15 of the 105 apu.* keys and leaves the
-- other 90 untouched. The 15:
--     square1/square2.dutyPos, triangle.sequencePosition, noise.shiftRegister,
--     square1/square2/triangle/noise/dmc timer.timer, those timers' lastOutput,
--     dmc.bitsRemaining, frameCounter.previousCycle
-- i.e. precisely the sub-instruction phase of every channel. Every column below
-- comes from the other 90. There is NO side-effect-free way to force the catch-up
-- from Lua - the debug memType read does not do it, and the side-effecting one
-- clears the frame IRQ flag and so perturbs the run being traced - so those 15
-- are unreachable, not merely omitted.
local function apuColumns(s)
	local function I(k, mask)
		local v = s[k]
		if type(v) ~= "number" then v = 0 end
		v = math.tointeger(v) or math.floor(v)
		return v & mask
	end
	local function sweepBits(ch)
		local b = 0
		if s["apu." .. ch .. ".sweepEnabled"] then b = b | 0x80 end
		if s["apu." .. ch .. ".sweepNegate"] then b = b | 0x08 end
		-- The 3-bit sweep PERIOD field is left out on purpose: BrokenNes stores it
		-- already incremented by one and this build's convention could not be
		-- confirmed, so including it would manufacture a permanent off-by-one
		-- "divergence". Its only observable effect is on the period register, which
		-- IS compared every frame.
		return b | I("apu." .. ch .. ".sweepShift", 0x07)
	end
	-- Volume in force, not the raw register: the constant-volume parameter when the
	-- constant flag is set, otherwise the envelope decay level. (0 is truthy in Lua,
	-- so the and/or below is safe for a zero volume.)
	local p1vol = s["apu.square1.envelope.constantVolume"]
		and I("apu.square1.envelope.volume", 0x0F) or I("apu.square1.envelope.counter", 0x0F)
	local p2vol = s["apu.square2.envelope.constantVolume"]
		and I("apu.square2.envelope.volume", 0x0F) or I("apu.square2.envelope.counter", 0x0F)
	local nvol = s["apu.noise.envelope.constantVolume"]
		and I("apu.noise.envelope.volume", 0x0F) or I("apu.noise.envelope.counter", 0x0F)

	-- Synthesized $4015, never read. A real read clears the frame IRQ flag and would
	-- change the behaviour of the very run being traced. Bit 5 is open bus; bit 7 (DMC
	-- IRQ) is masked off on BOTH sides because this build exposes no DMC IRQ flag - a
	-- real hole in the comparison, stated rather than papered over.
	local st = 0
	if I("apu.square1.envelope.lengthCounter.counter", 0xFF) > 0 then st = st | 0x01 end
	if I("apu.square2.envelope.lengthCounter.counter", 0xFF) > 0 then st = st | 0x02 end
	if I("apu.triangle.lengthCounter.counter", 0xFF) > 0 then st = st | 0x04 end
	if I("apu.noise.envelope.lengthCounter.counter", 0xFF) > 0 then st = st | 0x08 end
	if I("apu.dmc.bytesRemaining", 0xFFFF) > 0 then st = st | 0x10 end
	if s["apu.frameCounter.irqFlag"] then st = st | 0x40 end

	local en = 0
	if s["apu.square1.envelope.lengthCounter.enabled"] then en = en | 0x01 end
	if s["apu.square2.envelope.lengthCounter.enabled"] then en = en | 0x02 end
	if s["apu.triangle.lengthCounter.enabled"] then en = en | 0x04 end
	if s["apu.noise.envelope.lengthCounter.enabled"] then en = en | 0x08 end
	-- bit 4 reserved 0: no DMC channel-enable is exposed, only bytesRemaining (in st).
	if I("apu.frameCounter.stepMode", 0xFF) ~= 0 then en = en | 0x20 end
	if s["apu.frameCounter.inhibitIRQ"] then en = en | 0x40 end
	if s["apu.dmc.irqEnabled"] then en = en | 0x80 end

	local flg = 0
	if s["apu.square1.envelope.lengthCounter.halt"] then flg = flg | 0x01 end
	if s["apu.square2.envelope.lengthCounter.halt"] then flg = flg | 0x02 end
	if s["apu.triangle.lengthCounter.halt"] then flg = flg | 0x04 end
	if s["apu.noise.envelope.lengthCounter.halt"] then flg = flg | 0x08 end
	if s["apu.square1.envelope.constantVolume"] then flg = flg | 0x10 end
	if s["apu.square2.envelope.constantVolume"] then flg = flg | 0x20 end
	if s["apu.noise.envelope.constantVolume"] then flg = flg | 0x40 end
	if s["apu.dmc.loopFlag"] then flg = flg | 0x80 end

	return string.format(
		"|%03x|%01x|%02x|%01x|%02x|%03x|%01x|%02x|%01x|%02x|%03x|%02x|%02x|%03x|%01x|%02x|%01x|%02x|%04x|%04x|%01x|%02x|%02x|%02x",
		I("apu.square1.realPeriod", 0x7FF), p1vol,
		I("apu.square1.envelope.lengthCounter.counter", 0xFF),
		I("apu.square1.duty", 0x03), sweepBits("square1"),
		I("apu.square2.realPeriod", 0x7FF), p2vol,
		I("apu.square2.envelope.lengthCounter.counter", 0xFF),
		I("apu.square2.duty", 0x03), sweepBits("square2"),
		-- triangle.timer.period IS the 11-bit register on this side (the triangle timer
		-- is clocked at CPU rate, so the register value is stored unscaled), unlike
		-- square, where timer.period is the doubled internal reload and realPeriod is
		-- the register.
		I("apu.triangle.timer.period", 0x7FF),
		I("apu.triangle.lengthCounter.counter", 0xFF),
		I("apu.triangle.linearCounter", 0x7F),
		-- Noise: Mesen stores the divider reload as (table entry - 1), so add it back to
		-- get the hardware CPU-cycle period (4,8,16,...,4068) both sides can agree on.
		(I("apu.noise.timer.period", 0xFFF) + 1) & 0xFFF,
		nvol, I("apu.noise.envelope.lengthCounter.counter", 0xFF),
		s["apu.noise.modeFlag"] and 1 or 0,
		I("apu.dmc.outputLevel", 0x7F),
		I("apu.dmc.currentAddr", 0xFFFF),
		I("apu.dmc.bytesRemaining", 0xFFFF),
		I("apu.frameCounter.currentStep", 0x07),
		st, en, flg)
end

if APU_ON then
H("apu-columns: |p1per|p1vol|p1len|p1dut|p1swp|p2per|p2vol|p2len|p2dut|p2swp|tper|tlen|tlin|nper|nvol|nlen|nmod|dout|dcur|drem|fcs|st|en|flg")
H("apu-notes: p1per|p2per|tper are the 11-bit period REGISTERS (apu.squareN.realPeriod and")
H("apu-notes:   apu.triangle.timer.period), not internal divider reloads.")
H("apu-notes: p1vol|p2vol|nvol are the volume actually in force - the constant-volume parameter")
H("apu-notes:   when the constant flag is set, otherwise the envelope decay level. Emitted resolved")
H("apu-notes:   because the two emulators keep those in different fields and their envelope DIVIDER")
H("apu-notes:   conventions differ by one.")
H("apu-notes: p1len|p2len|tlen|nlen are length counters; tlin is the triangle linear counter 0..7f.")
H("apu-notes: p1dut|p2dut are the duty SELECT 0..3. Duty POSITION is not emitted - see below.")
H("apu-notes: p1swp|p2swp = sweep packed as $4001 lays it out MINUS the 3-bit period field:")
H("apu-notes:   bit7 enable, bit3 negate, bits2-0 shift.")
H("apu-notes: nper = noise period in CPU CYCLES from the NTSC table (4,8,...,4068). Mesen stores")
H("apu-notes:   that minus one; this tracer adds it back. nmod = $400e bit 7 (short mode).")
H("apu-notes: dout = DMC output level 0..7f, dcur = running sample pointer, drem = bytes remaining.")
H("apu-notes: fcs = frame sequencer step. st = SYNTHESIZED $4015, bits 0-4 and 6 ONLY.")
H("apu-notes:   Synthesized rather than read: a real $4015 read clears the frame IRQ flag and would")
H("apu-notes:   change the behaviour of the run being traced. Bit 5 is open bus. Bit 7 (DMC IRQ) is")
H("apu-notes:   masked off on BOTH sides because emu.getState() exposes no DMC IRQ flag - that is a")
H("apu-notes:   genuine hole in the comparison, not something covered by another column.")
H("apu-notes: en = bits0-3 length-counter enables, bit5 5-step mode, bit6 frame IRQ inhibit, bit7")
H("apu-notes:   DMC IRQ enable. bit4 reserved 0 - no DMC channel-enable is exposed.")
H("apu-notes: flg = bits0-3 length halts, bits4-6 constant-volume flags, bit7 DMC loop.")
H("apu-sample-point: identical to the CPU/RAM one - the same record instant, no offset.")
H("apu-not-compared: the sub-instruction phase of every channel - duty position, triangle sequence")
H("apu-not-compared:   position, the noise LFSR, every timer divider counter, and the per-cycle")
H("apu-not-compared:   channel output level. MEASURED, not assumed: emu.getState() reports the APU")
H("apu-not-compared:   from wherever NesApu::Run() last caught it up. Over 501 frame-boundary samples")
H("apu-not-compared:   on game.nes it was a mean 1582 CPU cycles behind the CPU, max 2505. Forcing a")
H("apu-not-compared:   catch-up moves exactly 15 of the 105 apu.* keys - precisely those - and leaves")
H("apu-not-compared:   the other 90 untouched. Every column above comes from those 90. No")
H("apu-not-compared:   side-effect-free catch-up exists in this Lua API, so the 15 are unreachable.")
else
H("apu-columns: (none - VRUN_TRACE_APU=off). No wrong note, stuck channel or dead DMC can be seen")
H("apu-columns:   in this trace; do not read a clean diff as 'the audio matches'.")
end
if AUDIO_ON then
H("audio-trace: |asmp|arms|apk|azc emitted as '-' on this side, ALWAYS.")
H("audio-trace:   Mesen 2.1.1's headless Lua API exposes no audio samples - the emu table has")
H("audio-trace:   getScreenBuffer but no audio equivalent - and its WAV recorder is GUI-only with no")
H("audio-trace:   command-line switch. So the sample-level comparison is one-sided: BrokenNes fills")
H("audio-trace:   these columns and this side cannot. They are a same-emulator regression and")
H("audio-trace:   cross-APU-core oracle, not a cross-emulator one. The columns are still emitted so")
H("audio-trace:   both sides carry the same column count and the differ reports 'not compared'")
H("audio-trace:   rather than a column-count mismatch.")
end

if PRG_ON then
H("prg-columns: |prgbank|chrbank%s", PRG_FULL and "|prgwin" or "")
H("prg-columns: prgbank = emu.getState()['mapper.prgBank'], mapper 30's 16KB PRG bank register, 2 hex.")
H("prg-columns: chrbank = emu.getState()['mapper.chrMemoryOffset0'], the BYTE offset in CHR RAM that")
H("prg-columns:   PPU $0000 currently resolves to (selected 8KB CHR bank * 8192), 5 hex.")
if PRG_FULL then
H("prg-columns: prgwin  = FNV-1a-64 over the 32768 bytes the CPU sees at $8000-$ffff, read through")
H("prg-columns:   emu.memType.nesDebug (side-effect-free; nesMemory is the side-effecting path).")
end
H("prg-columns: WHY: chrhash covers all %d bytes of CHR, so it is IDENTICAL whichever 8KB bank is", CHR_SIZE)
H("prg-columns:   selected - a pure bank-selection divergence is invisible to it. Same for PRG, which")
H("prg-columns:   had no column at all before these. prgwin is additionally the only column that can")
H("prg-columns:   see a mapper-30 FLASH write, which changes PRG content without changing a bank number.")
H("prg-rom-size: %d bytes (%d x 16KB banks)", PRG_ROM_SIZE, PRG_ROM_SIZE // 0x4000)
else
H("prg-columns: (none - VRUN_TRACE_PRG=off). PRG banking, CHR BANK SELECTION and mapper-30 flash")
H("prg-columns:   writes are NOT checked by this trace. chrhash hashes all of CHR and cannot see")
H("prg-columns:   which bank is mapped; do not read a clean diff as 'the banking matches'.")
end

-- One authoritative, machine-readable column list for the differ.
--
-- The human-readable "format:" line above is written per-branch and does not know about
-- columns added after it, so this is built from the same flags that actually drive
-- record(). If a column set here ever falls out of step with what record() emits, the
-- differ fails LOUDLY on a field-count mismatch instead of silently mis-aligning two
-- traces - which is the whole reason this is emitted rather than inferred.
do
	local COLS = "frame|pc|a|x|y|sp|p|ramhash"
	if PPU_ON then
		COLS = COLS .. "|ntbhash|oamhash|palhash|ctrl|mask|stat|v|t|fx|w|chrhash|fbhash"
	end
	if PRG_ON then
		COLS = COLS .. "|prgbank|chrbank"
		if PRG_FULL then COLS = COLS .. "|prgwin" end
	end
	if APU_ON then
		COLS = COLS .. "|p1per|p1vol|p1len|p1dut|p1swp|p2per|p2vol|p2len|p2dut|p2swp"
		            .. "|tper|tlen|tlin|nper|nvol|nlen|nmod|dout|dcur|drem|fcs|st|en|flg"
	end
	if AUDIO_ON then COLS = COLS .. "|asmp|arms|apk|azc" end
	H("columns: %s", COLS)
end

H("--- records ---")

------------------------------------------------------------------------------
-- Run
------------------------------------------------------------------------------
-- Absolute frame number, so a segment's records carry the same indices they would
-- have had in one long unsegmented run and the pieces concatenate without renumbering.
local frame = FRAME_BASE   -- index of the frame currently being emulated
local stepIdx = 0          -- last step already applied
local held = newHeld()
local pending = {}         -- line buffer
local segWrote = nil       -- "# segment-out: ..." line, set once the savestate lands
local emu_getState = emu.getState
local emu_setInput = emu.setInput
local sfmt = string.format

local function onInputPolled()
	while stepIdx < #STEPS and STEPS[stepIdx + 1].frame <= frame do
		stepIdx = stepIdx + 1
		held = STEPS[stepIdx].held
	end
	emu_setInput(held, 0)   -- (input, port) - argument order verified for this
	                        -- exact Mesen 2.1.1 build; see docs/01-toolchain-setup.md
end

-- Optional raw work-RAM dumps at named frames, written at the same instant the ramhash
-- column is taken. A hash says THAT two frames differ; only the bytes say WHICH.
local DUMP_FRAMES = {}
do
	local spec = os.getenv("VRUN_TRACE_RAMDUMP") or ""
	for tok in string.gmatch(spec, "[^,]+") do
		local n = tonumber(tok)
		if not n or n ~= math.floor(n) or n < 0 then
			die(2, "VRUN_TRACE_RAMDUMP must be a comma-separated list of frame numbers, got '" .. spec .. "'")
		end
		DUMP_FRAMES[math.tointeger(n)] = true
	end
end
local DUMP_DIR = os.getenv("VRUN_TRACE_RAMDUMP_DIR") or "."

------------------------------------------------------------------------------
-- VRUN_TRACE_POKE - forced work-RAM writes, mirroring --poke-at on the other side
------------------------------------------------------------------------------
-- Syntax, identical to the BrokenNes tracer's --poke-at:
--     "frame:addr=value,frame:addr=value,..."   addr and value hex, frame decimal
-- e.g. "600:3ad=3,600:3d2=1"
--
-- WHY A POKE IS A LEGITIMATE PARITY TOOL. Whole regions of this game - the shop,
-- death, the ending, the options screen - have never been compared, because no
-- scripted button sequence found has reached them. A poke gets the two emulators
-- into that state directly. The run IS perturbed and the resulting trace is NOT a
-- recording of natural play; but if BOTH emulators are perturbed identically, at
-- the same instant, with the same bytes, then the COMPARISON between them stays
-- valid, which is the only thing the diff claims.
--
-- THE INSTANT MATTERS AND IT IS NOT OBVIOUS. The BrokenNes loop is
--     poke(f); SetInputs(); RunFrame();  -- record for f taken when RunFrame returns
-- so a poke for frame f lands at exactly the instant RunFrame(f-1) returned - which
-- is the same instant this script takes its record for frame f-1. So pokes for
-- frame f are applied here immediately AFTER the frame f-1 record is written (see
-- record()), and frame-0 pokes are applied in the top-level chunk, alongside the
-- power-on RAM fill. Applying them anywhere else - inputPolled, say - would put the
-- two emulators' writes on opposite sides of the game's own NMI handler and make a
-- perturbation difference look like an emulation difference.
local function applyPokes(f)
	local list = POKES[f]
	if not list then return end
	for _, pv in ipairs(list) do
		emu.write(pv[1], pv[2], emu.memType.nesInternalRam)
	end
end

-- Frame-0 pokes belong to the same top-level chunk that lays down the power-on
-- RAM fill: both run at cpu.cycleCount == 7, before the ROM's first instruction,
-- which is where the BrokenNes side's "poke, then RunFrame(0)" also puts them.
-- (The fill itself has already run by this point in the file, so a frame-0 poke
-- correctly lands on top of it rather than under it.)
--
-- Only the segment that actually STARTS at frame 0 does this. A resumed segment
-- would otherwise re-apply a frame-0 poke thousands of frames late, on top of live
-- state, which is nothing like what the other emulator did.
if FRAME_BASE == 0 and not SEG_IN and POKES[0] then applyPokes(0) end

local function dumpFile(name, data)
	local df = io.open(DUMP_DIR .. "/" .. name, "wb")
	if not df then die(5, "cannot write dump '" .. name .. "' into " .. DUMP_DIR) end
	df:write(data)
	df:close()
end

------------------------------------------------------------------------------
-- The twelve v2 columns, read at the same instant as the CPU/RAM ones.
--
-- Mesen exposes $2000 and $2001 as decoded fields rather than as the raw bytes
-- the CPU wrote, so they are rebuilt here. The nametable-select bits of $2000
-- are NOT a separate field anywhere because on real hardware they are not
-- stored separately: a $2000 write drops them straight into t bits 10-11, which
-- is where they are read back from.
------------------------------------------------------------------------------
local function ppuColumns(s)
	local t = s["ppu.tmpVideoRamAddr"] & 0xFFFF
	local ctrl = 0
	if s["ppu.control.nmiOnVerticalBlank"] then ctrl = ctrl | 0x80 end
	-- bit 6 (EXT bus direction) is not exposed by Mesen; masked off on both sides.
	if s["ppu.control.largeSprites"] then ctrl = ctrl | 0x20 end
	if (s["ppu.control.backgroundPatternAddr"] or 0) ~= 0 then ctrl = ctrl | 0x10 end
	if (s["ppu.control.spritePatternAddr"] or 0) ~= 0 then ctrl = ctrl | 0x08 end
	if s["ppu.control.verticalWrite"] then ctrl = ctrl | 0x04 end
	-- Bits 1-0 (nametable select) are deliberately NOT filled in. On hardware they are
	-- not stored in the $2000 latch at all - a $2000 write drops them into t bits 10-11
	-- and that is where the PPU reads them from, which is why Mesen has no field for
	-- them. Deriving them from t here and comparing against BrokenNes' raw $2000 byte
	-- made every $2006 write (which also moves t bits 10-11, without touching $2000)
	-- look like a PPUCTRL divergence: measured on game.nes, ctrl differed on frames 6
	-- and 10 for exactly that reason and for no other reason in 900 frames. Nothing is
	-- lost by dropping them - t is its own column and carries those two bits directly.

	local mask = 0
	if s["ppu.mask.grayscale"] then mask = mask | 0x01 end
	if s["ppu.mask.backgroundMask"] then mask = mask | 0x02 end
	if s["ppu.mask.spriteMask"] then mask = mask | 0x04 end
	if s["ppu.mask.backgroundEnabled"] then mask = mask | 0x08 end
	if s["ppu.mask.spritesEnabled"] then mask = mask | 0x10 end
	if s["ppu.mask.intensifyRed"] then mask = mask | 0x20 end
	if s["ppu.mask.intensifyGreen"] then mask = mask | 0x40 end
	if s["ppu.mask.intensifyBlue"] then mask = mask | 0x80 end

	local stat = 0
	if s["ppu.statusFlags.verticalBlank"] then stat = stat | 0x80 end
	if s["ppu.statusFlags.sprite0Hit"] then stat = stat | 0x40 end
	if s["ppu.statusFlags.spriteOverflow"] then stat = stat | 0x20 end

	-- Mesen models the single hardware write toggle, so it fills both bits; the
	-- BrokenNes side can emit 1 or 2 because PPU_FIX keeps two latches.
	local w = s["ppu.writeToggle"] and 3 or 0

	return sfmt("|%s|%s|%s|%02x|%02x|%02x|%04x|%04x|%01x|%01x|%s|%s",
		fnv_hex(fnv_mem(MEM_CIRAM, CIRAM_SIZE)),
		fnv_hex(fnv_mem(MEM_OAM, OAM_SIZE)),
		fnv_hex(fnv_bytes(palette_canon_bytes())),
		ctrl & 0xBC,   -- bit 6 EXT (not exposed) and bits 1-0 nametable (they live in t)
		mask,
		stat,
		s["ppu.videoRamAddr"] & 0x7FFF,
		t & 0x7FFF,
		s["ppu.xScroll"] & 0x07,
		w,
		(MEM_CHR and CHR_SIZE > 0) and fnv_hex(fnv_mem(MEM_CHR, CHR_SIZE)) or "-",
		fbHash)
end

-- See the PRG/bank block near the top for what these mean and why they exist.
-- Both numbers come straight out of the getState() table the record already has,
-- so `on` costs nothing; only `full`'s prgwin walks memory.
local function prgColumns(s)
	local bank = s["mapper.prgBank"]
	local chrb = s["mapper.chrMemoryOffset0"]
	-- A mapper that does not publish these keys must produce '-' rather than 0:
	-- 0 is a legal bank, and silently emitting it would make a missing value
	-- look like agreement on bank 0.
	local bankStr = (type(bank) == "number") and sfmt("%02x", math.tointeger(bank) & 0xFF) or "-"
	local chrStr  = (type(chrb) == "number") and sfmt("%05x", math.tointeger(chrb) & 0xFFFFF) or "-"
	if not PRG_FULL then return "|" .. bankStr .. "|" .. chrStr end
	return "|" .. bankStr .. "|" .. chrStr .. "|" ..
	       fnv_hex(fnv_mem_range(MEM_DBG, PRG_WIN_BASE, PRG_WIN_SIZE))
end

local function record(s)
	local line = sfmt("%d|%04x|%02x|%02x|%02x|%02x|%02x|%s",
		frame,
		s["cpu.pc"] & 0xFFFF,
		s["cpu.a"] & 0xFF,
		s["cpu.x"] & 0xFF,
		s["cpu.y"] & 0xFF,
		s["cpu.sp"] & 0xFF,
		-- bit 5 forced set - see the p-register note in the header.
		(s["cpu.ps"] | 0x20) & 0xFF,
		ram_hash16())

	if PPU_ON then
		-- FB_AT=record grabs here instead of at endFrame. Safe only if the
		-- emulator has not started overwriting the buffer with the new frame -
		-- which is exactly what the endframe default sidesteps.
		if FB_AT == "record" then captureFrame(DUMP_FRAMES[frame] == true) end
		line = line .. ppuColumns(s)
	end
	if PRG_ON then line = line .. prgColumns(s) end
	if APU_ON then line = line .. apuColumns(s) end
	-- Four placeholders, never values: this build of Mesen cannot produce audio samples
	-- headlessly at all. Emitted so both sides carry the same column count and the differ
	-- can say "not compared" instead of "column count mismatch". See the audio-trace block.
	if AUDIO_ON then line = line .. "|-|-|-|-" end
	pending[#pending + 1] = line

	if DUMP_FRAMES[frame] then
		dumpFile("ram_f" .. tostring(frame) .. ".bin", ram_bytes())
		if PPU_ON then
			dumpFile("ciram_f" .. tostring(frame) .. ".bin", mem_bytes(MEM_CIRAM, CIRAM_SIZE))
			dumpFile("oam_f" .. tostring(frame) .. ".bin", mem_bytes(MEM_OAM, OAM_SIZE))
			dumpFile("pal_f" .. tostring(frame) .. ".bin", palette_canon_bytes())
			if MEM_CHR and CHR_SIZE > 0 then
				dumpFile("chr_f" .. tostring(frame) .. ".bin", mem_bytes(MEM_CHR, CHR_SIZE))
			end
			if fbPendingDump then dumpFile("fb_f" .. tostring(frame) .. ".bin", fbPendingDump) end
		end
	end

	if #pending >= 128 then
		out:write(table.concat(pending, "\n"), "\n")
		pending = {}
	end

	frame = frame + 1
	-- Pokes for the frame we are about to emulate. This instant - just after the
	-- previous frame's record - is exactly where the BrokenNes loop pokes, i.e.
	-- immediately after its RunFrame() for frame-1 returned. See the POKES block.
	if POKE_COUNT > 0 then applyPokes(frame) end

	-- Hand the baton to the next segment. We are inside the exec callback that took
	-- this record - the only context Mesen permits createSavestate in - and at the
	-- frame boundary that follows it, so the next segment resumes exactly here.
	-- Normally SEG_OUT_FRAME is one below FRAME_END, giving the one-frame warm-up
	-- overlap the stitcher relies on; see the SEG_OUT_FRAME block.
	if SEG_OUT and frame == SEG_OUT_FRAME then
		local ok, blob = pcall(emu.createSavestate)
		if not ok or type(blob) ~= "string" then
			out:write("# FATAL(5): createSavestate failed: ", tostring(blob), "\n")
			out:close(); out = nil
			emu.stop(5)
			return
		end
		local sf = io.open(SEG_OUT, "wb")
		if not sf then
			out:write("# FATAL(5): cannot write savestate to ", SEG_OUT, "\n")
			out:close(); out = nil
			emu.stop(5)
			return
		end
		sf:write(blob); sf:close()
		segWrote = string.format("# segment-out: %s (%d bytes) resumes at frame %d\n",
			SEG_OUT, #blob, frame)
	end

	if frame >= FRAME_END then
		if #pending > 0 then out:write(table.concat(pending, "\n"), "\n") end
		-- A pixel whose RGB is absent from the palette table means the inversion
		-- behind fbhash did not hold, so say so in the trace rather than letting
		-- a reader assume the column meant what the header promised.
		if fbUnmapped > 0 then
			out:write("# WARNING: ", tostring(fbUnmapped), " framebuffer pixel(s) had an RGB value absent from the\n")
			out:write("#   palette table and were encoded as index ff. fbhash is NOT a reliable palette-index\n")
			out:write("#   comparison for this run - check the emphasis bits (mask & 0xe0) and fb-palette-source.\n")
		end
		-- A segment that was asked for a savestate and never wrote one would break the
		-- chain silently, several segments later and far from the cause. Fail here.
		if SEG_OUT and not segWrote then
			out:write("# FATAL(5): SEG_OUT requested but frame ", tostring(SEG_OUT_FRAME),
				" was never reached\n")
			out:close(); out = nil
			emu.stop(5)
			return
		end
		if segWrote then out:write(segWrote) end
		out:write("# end: ", tostring(FRAMES), " records, frames ",
			tostring(FRAME_BASE), "..", tostring(FRAME_END - 1), "\n")
		out:close()
		out = nil
		emu.stop(0)
	end
end

-- ---------------------------------------------------------------------------
-- Segment resume. Mesen refuses loadSavestate outside an exec memory callback
-- for the main CPU, so the restore rides a dedicated one-shot whole-range exec
-- callback that fires on the ROM's very first instruction and then removes
-- itself. Doing it here rather than piggy-backing on the sampling callback
-- matters: the sampling one is not armed until the first frame boundary, so the
-- segment would emulate a whole frame from power-on before being overwritten.
if SEG_IN then
	local loadRef
	local function doLoad()
		if loadRef then
			emu.removeMemoryCallback(loadRef, emu.callbackType.exec, 0x0000, 0xFFFF)
			loadRef = nil
		end
		local sf = io.open(SEG_IN, "rb")
		if not sf then die(2, "VRUN_TRACE_SEG_IN: cannot open savestate '" .. SEG_IN .. "'") end
		local blob = sf:read("a"); sf:close()
		local ok, err = pcall(emu.loadSavestate, blob)
		if not ok then
			die(4, "VRUN_TRACE_SEG_IN: loadSavestate failed on '" .. SEG_IN .. "': " .. tostring(err))
		end
	end
	loadRef = emu.addMemoryCallback(doLoad, emu.callbackType.exec, 0x0000, 0xFFFF)
end

emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)

-- Grab the completed frame at scanline 240 dot 0 (see the fb-sample-point
-- header note). Registered here, after DUMP_FRAMES exists, so the closure
-- captures the local rather than reaching for a global that is not there.
-- Deliberately NOT registered when FB_AT=record, so the buffer is walked once
-- per frame either way.
if PPU_ON and FB_ENABLED and FB_AT == "endframe" then
	emu.addEventCallback(function() captureFrame(DUMP_FRAMES[frame] == true) end, emu.eventType.endFrame)
end

if SAMPLE == "mesen-endframe" then
	emu.addEventCallback(function() record(emu_getState()) end, emu.eventType.endFrame)
else
	-- The exec callback has to cover $0000-$FFFF to see every instruction boundary, so it is
	-- REGISTERED per frame and REMOVED the instant the record is taken. That keeps the callback
	-- count at ~5 per frame instead of ~10000, which is worth doing on speed alone.
	--
	-- CORRECTION, measured. An earlier version of this comment claimed a permanently-registered
	-- whole-range exec callback "kills the process" at ~1300-1450 frames and that the per-frame
	-- registration avoided the abort. That diagnosis was wrong. A control that registers a
	-- whole-range exec callback and does nothing else completed 20000 frames clean, as did one
	-- that also called emu.getState() from inside it. What actually ends a long run is a 100
	-- SECOND wall clock in Mesen's --testRunner: five runs, five deaths, all at exactly 100s,
	-- at frame counts from 8320 to 35750 depending only on how much work the script did per
	-- frame. See the SEGMENTATION block near the top - that is what the segment support is for.
	-- The per-frame registration is still the right thing to do; it just buys throughput, and
	-- therefore frames-per-segment, rather than avoiding a crash.
	local floor = math.floor
	local cbRef = nil
	local onExec
	onExec = function()
		local s = emu_getState()
		local sl, cy = s["ppu.scanline"], s["ppu.cycle"]
		-- Dots since scanline 240 cycle 0. Monotonic across 240..260, -1, 0..239.
		local since
		if sl >= 240 then since = (sl - 240) * 341 + cy
		elseif sl < 0 then since = 7161 + cy
		else since = 7502 + sl * 341 + cy end
		if since < SAMPLE_DOT + floor(SAMPLE_SLOPE * frame + 0.5) then return end
		if cbRef then
			emu.removeMemoryCallback(cbRef, emu.callbackType.exec, 0x0000, 0xFFFF)
			cbRef = nil
		end
		record(s)
	end
	-- Arm as late as possible while still preceding the target: at startFrame (scanline -1) when
	-- the target is on the pre-render line or later, otherwise at endFrame (scanline 240).
	local minTarget = math.min(SAMPLE_DOT, SAMPLE_DOT + SAMPLE_SLOPE * (FRAMES - 1))
	local armEvent = (minTarget >= 7161) and emu.eventType.startFrame or emu.eventType.endFrame
	emu.addEventCallback(function()
		if not cbRef then
			cbRef = emu.addMemoryCallback(onExec, emu.callbackType.exec, 0x0000, 0xFFFF)
		end
	end, armEvent)
end
