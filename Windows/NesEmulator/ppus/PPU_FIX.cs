namespace NesEmulator
{
// FIX family: the exclusive target for ongoing hardware-accuracy work (see
// project_fix_core_family memory). Started as an exact duplicate of PPU_FMC, the "standard
// control component" baseline - every other named core is either a speed/perf tradeoff or an
// intentional gimmick/personality core (see project_ppu_core_personalities memory) and is
// frozen going forward. Accuracy fixes land here, not on PPU_FMC or any other named core.
public class PPU_FIX : IPPU, IPpuProbe
{
	// Core metadata (new IPPU contract)
	public string CoreName => "Fix";
	public string Description => "Hardware-accuracy target core, forked from the Famiclone (FMC) baseline. All ongoing PPU accuracy work lands here; the other named cores are frozen.";
	public int Performance => 0;
	public int Rating => 3;
	public string Category => "Accuracy";
	private Bus bus;

	private byte[] vram; //2KB VRAM
	private byte[] paletteRAM; //32 bytes Palette RAM
	private byte[] oam; //256 bytes OAM

	private const int ScreenWidth = 256;
	private const int ScreenHeight = 240;
	private const int CyclesPerScanlines = 341;
	private const int TotalScanlines = 262;

	private byte PPUCTRL; //$2000
	private byte PPUMASK; //$2001
	private byte PPUSTATUS; //$2002
	private byte OAMADDR; //$2003
	private byte OAMDATA; //$2004
	private byte PPUSCROLLX, PPUSCROLLY; //$2005
	private ushort PPUADDR; //$2006
	private byte PPUDATA; //$2007

	// Hardware has exactly ONE write toggle ("w") shared by $2005 and $2006 - the second write to
	// EITHER register is signalled by the same flip-flop, and a $2002 READ clears it. PPU_FIX used
	// to keep two independent latches (addrLatch for $2006, scrollLatch for $2005) with only the
	// $2006 one cleared by a $2002 read, which lets the pair reach states hardware cannot be in
	// (one half-set while the other is clear) - measured against Mesen as w=1 vs w=0 on 25 frames
	// of a 10879-frame replay. The two fields survive only in PpuSharedState (shared with the
	// frozen cores); GetState writes this single bit into both of them.
	private bool w = false;
	private byte ppuDataBuffer;

	// The PPU has its own 8-bit dynamic latch on the CPU<->PPU data pins ("PPU I/O bus", commonly
	// called "PPU open bus") - completely separate from the CPU's own open bus (Bus.lastBusValue).
	// It is refreshed by every CPU access to $2000-$2007, including writes to the read-only $2002,
	// and is what a read of a write-only register (or the unused low bits of $2002, or the
	// undriven high bits of a palette read) returns. Unlike the CPU bus it is NOT touched by
	// instruction opcode/operand fetches - only by these register accesses - and each bit decays
	// back to 0 after roughly 600ms if nothing refreshes it.
	private byte ppuOpenBus;
	private readonly long[] ppuOpenBusDecayAt = new long[8]; // absolute dot count at which each bit decays to 0
	private long ppuDotCounter;
	private const long PpuOpenBusDecayDots = 3_200_000; // ~600ms of PPU dots (real hardware decay)

	private void RefreshPpuOpenBus(byte value, byte mask = 0xFF)
	{
		ppuOpenBus = (byte)((ppuOpenBus & (~mask & 0xFF)) | (value & mask));
		long deadline = ppuDotCounter + PpuOpenBusDecayDots;
		for (int b = 0; b < 8; b++) if ((mask & (1 << b)) != 0) ppuOpenBusDecayAt[b] = deadline;
	}

	private byte ReadPpuOpenBus()
	{
		for (int b = 0; b < 8; b++) if (ppuDotCounter >= ppuOpenBusDecayAt[b]) ppuOpenBus &= (byte)~(1 << b);
		return ppuOpenBus;
	}

	private byte fineX; //x
	private ushort v; //current VRAM address
	private ushort t; //temp VRAM address

	// The address this core's scanline-batch background renderer walks from, latched out of the
	// live v at dot 256 - the last dot of the visible fetch window, i.e. before the dot-256 vertical
	// increment moves v on to the next line. It exists because v is now the REAL loopy-v: by the
	// time the batch renderer runs at the end of the scanline, v has already been vertically
	// incremented (dot 256), had its horizontal bits reloaded from t (dot 257) and been advanced by
	// the two prefetch tile fetches (dots 328/336). The latch is exactly the value the old code
	// produced by doing CopyXFromTToV() immediately before rendering, so what gets drawn is
	// unchanged - see the pipeline block in Step().
	private ushort renderAddr;

	private int scanlineCycle;
	private int scanline;

	// KNOWN GAP, deliberately not implemented here - see the note above the scanline-wrap test in
	// Step(). Hardware drops the last dot of the pre-render scanline every other frame (89342 dots
	// then 89341), and this core does not.

	// === The loopy-v render pipeline's per-dot schedule, precomputed ===
	//
	// vPipelineOn answers "is this scanline one where the PPU drives v, and is rendering on"; it is
	// recomputed only when the scanline advances or PPUMASK changes. DotAction then answers "does
	// this dot do anything" in one indexed load. Together they keep the 89342-iteration Step loop
	// paying a bool test and an array read for the ~93% of dots that do nothing.
	private const byte ActNone = 0, ActIncX = 1, ActEndOfLine = 2, ActCopyHori = 3, ActCopyVert = 4;

	// 512 entries, not 341, and indexed with `& 511`: that lets the JIT drop the bounds check on
	// what is otherwise the single hottest array read in the emulator. Entries 341-511 are never
	// reached (scanlineCycle is 0-340) and stay ActNone.
	private const int DotActionMask = 511;

	private static readonly byte[] DotAction = BuildDotAction();

	private static byte[] BuildDotAction()
	{
		var a = new byte[DotActionMask + 1];
		// One coarse-X increment per background tile fetched: dots 8, 16, ... 256, then the two
		// tiles prefetched for the next scanline at 328 and 336.
		for (int d = 8; d <= 256; d += 8) a[d] = ActIncX;
		a[328] = ActIncX;
		a[336] = ActIncX;
		a[256] = ActEndOfLine;                       // that increment PLUS the vertical one
		a[257] = ActCopyHori;                        // hori(v) := hori(t)
		for (int d = 280; d <= 304; d++) a[d] = ActCopyVert;  // vert(v) := vert(t), pre-render only
		return a;
	}

	private bool vPipelineOn;

	// The horizontal half of v (coarse X + nametable-X, bits 0-4 and 10) as hardware last loaded it
	// at a dot-257 hori(v) := hori(t) copy - i.e. the scroll the NEXT scanline actually starts from.
	//
	// This exists because renderAddr took that half straight out of the LIVE t, at dot 256 of the
	// scanline being drawn. For a game that only touches $2005/$2006 in vblank the two are the same
	// value and nothing changes. For one whose vblank flush overruns onto the pre-render line they
	// are not: hardware freezes scanline 0's horizontal scroll at pre-render dot 257 and a $2005
	// arriving at dot 262 misses that frame entirely (Mesen draws the resulting one-frame tear on
	// VRUN's pause screen), while reading t at scanline 0 dot 256 picks the write up 340 dots later
	// and quietly draws the settled row instead. Gated by SpeedConfig.PpuScanlineHoriFromLatch
	// because it changes rendering for every ROM, not only for the late-write case.
	private ushort horiLatch;
	private bool horiFromLatch; // cached SpeedConfig.PpuScanlineHoriFromLatch, refreshed per scanline

	// Cached SpeedConfig.CpuCyclePrecisePpu, refreshed alongside horiFromLatch. Gates the $2007
	// rendering-collision model in WritePPURegister/ReadPPURegister - see CollideDataAccessWithRender.
	private bool dataPortCollision;

	private void UpdateVPipelineFlag()
	{
		// Visible scanlines and the pre-render line are the ones that fetch; with rendering off the
		// PPU stops driving v entirely and it stays wherever the CPU left it.
		bool wasOn = vPipelineOn;
		vPipelineOn = (scanline < 240 || scanline == 261) && (PPUMASK & 0x18) != 0;
		// Read once per scanline (and per $2001 write) rather than per dot; bus can be null during
		// construction and core hot-swap, which is why this is not done in the constructor.
		horiFromLatch = bus?.SpeedConfig?.PpuScanlineHoriFromLatch ?? false;
		dataPortCollision = bus?.SpeedConfig?.CpuCyclePrecisePpu ?? false;
		// Turning rendering ON mid-frame: no dot-257 copy has run since, so the line that follows
		// starts from wherever the CPU left v, not from t. Seeding the latch from v here is the
		// same statement hardware makes by simply not having reloaded anything.
		if (vPipelineOn && !wasOn) horiLatch = (ushort)(v & 0x041F);
	}

	// PPUMASK-derived colour-output state, refreshed whenever $2001 changes and at the top of every
	// scanline. Both are pure output-stage effects - they never touch what is stored in palette RAM.
	//   greyMask  - PPUMASK bit 0 (greyscale) ANDs the palette INDEX with $30 on its way out of
	//               palette RAM, collapsing all four colour columns onto the grey one.
	//   emphBase  - byte offset into EmphasisPaletteBytes selected by PPUMASK bits 5/6/7.
	private int emphBase;
	private int greyMask = 0x3F;

	private void RefreshColorMask()
	{
		emphBase = ((PPUMASK >> 5) & 0x07) * 192;
		greyMask = (PPUMASK & 0x01) != 0 ? 0x30 : 0x3F;
		UpdateVPipelineFlag(); // PPUMASK bits 3/4 also gate the loopy-v pipeline
	}

	// Lazy framebuffer allocation to reduce startup memory; allocate on first use
	private byte[]? frameBuffer = null;
	// Reusable arrays to avoid per-scanline allocations
	private readonly bool[] spritePixelDrawnReuse = new bool[ScreenWidth];
	private int staticFrameCounter = 0;

	public PPU_FIX(Bus bus)
	{
		this.bus = bus;

		vram = new byte[2048];
		paletteRAM = new byte[32];
		oam = new byte[256];

		// Palette RAM powers on zero-filled, matching FCEUX (its PALRAM is zeroed at power-on).
		// Real hardware powers up with indeterminate palette contents, so there is no single
		// "correct" fill - but matching FCEUX is what lets a .fm2 recorded there replay
		// identically here, which is the point of the FIX cores. The old cosmetic default ramp
		// (InitializeDefaultPalette, kept below but no longer called) made every game's first
		// frames differ from FCEUX before the game wrote its own palette.
		// paletteRAM is already zero-filled by `new byte[32]`.

		PPUADDR = 0x0000;
		PPUCTRL = 0x00;
		PPUSTATUS = 0x00;
		PPUMASK = 0x00;

		ppuDataBuffer = 0x00;

		scanlineCycle = 0;
		scanline = 0;
		
		// Defer framebuffer allocation and any test pattern generation until first use
	}

	private void EnsureFrameBuffer()
	{
		if (frameBuffer == null || frameBuffer.Length != ScreenWidth * ScreenHeight * 4)
		{
			frameBuffer = new byte[ScreenWidth * ScreenHeight * 4];
		}
	}


	// New batched step to reduce managed/WASM call overhead; processes 'elapsedCycles' PPU cycles
	public void Step(int elapsedCycles)
	{
		ppuDotCounter += elapsedCycles;
		for (int c = 0; c < elapsedCycles; c++)
		{
			// Hardware clears VBlank (bit 7), sprite-0 hit (bit 6) and sprite overflow (bit 5) at
			// PRE-RENDER scanline 261, dot 1 - not at scanline 0 dot 0, and not only bits 7/6.
			// Clearing a full scanline late shifted the entire vblank window against rendering:
			// measured against Mesen as a constant ~326-dot phase offset, stable across 3000 frames
			// (see project_vrun_accuracy memory). Latent for a game that only waits on NMI, but it
			// is the classic cause of a corrupt top scanline for one that busy-waits on $2002 and
			// then races VRAM writes against the end of vblank, and it mis-times any raster effect
			// that counts cycles from the NMI.
			if (scanline == 261 && scanlineCycle == 1)
			{
				PPUSTATUS &= 0x1F;
			}

			// Hardware raises VBlank at scanline 241 dot 1, asserting /NMI there when enabled. This
			// used to live in the end-of-scanline block below, which fired it at the END of 241 -
			// i.e. scanline 242 dot 0, 340 dots late.
			if (scanline == 241 && scanlineCycle == 1)
			{
				PPUSTATUS |= 0x80;
				if ((PPUCTRL & 0x80) != 0)
				{
					bus.cpu.RequestNMI();
				}
			}

			// MMC5 IRQ tick at early cycle 3 when rendering enabled
			if (scanline >= 0 && scanline < 240 && scanlineCycle == 3)
			{
				bool renderingOn = (PPUMASK & 0x18) != 0;
				if (bus.cartridge.mapper is Mapper5 mmc5)
				{
					mmc5.PpuScanlineHook(scanline, renderingOn);
					if (mmc5.IsIrqAsserted()) bus.cpu.RequestIRQ(true);
				}
			}

			if (scanline >= 0 && scanline < 240 && scanlineCycle == 260)
			{
				if ((PPUMASK & 0x18) != 0 && bus.cartridge.mapper is Mapper4)
				{
					Mapper4 mmc3 = (Mapper4)bus.cartridge.mapper;
					mmc3.RunScanlineIRQ();
					if (mmc3.IRQPending())
					{
						bus.cpu.RequestIRQ(true);
						mmc3.ClearIRQ();
					}
				}
			}

			// === The rendering pipeline's own updates to v (the "loopy-v") ===
			//
			// v is the PPU's live VRAM address register, not a shadow of the last CPU write. On a
			// scanline where rendering is enabled the PPU drives it itself, and the CPU only sees
			// that value through $2007 or by a debugger looking at it. This block is that driver,
			// at the dots hardware uses:
			//   dots 8,16..256  coarse X += 1 (bit 10, the nametable-X select, flips when it
			//                   wraps past 31) - one increment per background tile fetched
			//   dot 256         fine Y += 1, carrying into coarse Y, which wraps at 29 with a
			//                   nametable-Y flip (and at 31 without one, for out-of-range scrolls)
			//   dot 257         hori(v) := hori(t) - reloads coarse X + nametable X for the line
			//   dots 280-304    (pre-render only) vert(v) := vert(t), repeated every dot so a CPU
			//                   write landing inside the window is overwritten, as on hardware
			//   dots 328,336    coarse X += 1 twice more - the two tiles prefetched for the NEXT
			//                   scanline, which is why v reads t+2 coarse X at the start of a line
			//
			// It runs ONLY while rendering is enabled, which is what makes $2007 usable: with
			// rendering off (forced blank or vblank) hardware leaves v exactly where the CPU put
			// it. The old code ran CopyXFromTToV()/IncrementY() unconditionally on every visible
			// scanline and assigned v = t at the end of pre-render, so v was clobbered even during
			// forced blank - which is why $2007 needed a private PPUADDR shadow to work at all.
			//
			// Cost matters here: this is inside a loop that runs 89342 times per frame, so the
			// question "does this dot do anything" is answered by one cached bool (vPipelineOn,
			// recomputed only when the scanline advances or $2001 is written) plus one lookup in a
			// 341-entry table, instead of the five or six comparisons the dot numbers would need.
			// Measured on page1_binty: spelling the tests out cost 13% of frame time, this costs 3%.
			byte dotAct;
			if (vPipelineOn && (dotAct = DotAction[scanlineCycle & DotActionMask]) != ActNone)
			{
				switch (dotAct)
				{
					case ActIncX:
						IncrementX(ref v);
						break;
					case ActEndOfLine:
						IncrementX(ref v);
						// Latch before the vertical increment: this is the address the
						// scanline-batch renderer needs (see renderAddr's declaration). The
						// horizontal half comes from the dot-257 latch when that is enabled, which
						// is where hardware froze it - see horiLatch.
						renderAddr = (ushort)((v & 0xFBE0) | (horiFromLatch ? horiLatch : (ushort)(t & 0x041F)));
						IncrementY();
						break;
					case ActCopyHori:
						CopyXFromTToV();
						// What hardware just loaded into v's horizontal half IS the next scanline's
						// starting scroll, frozen at this dot. Taken from v rather than t so a
						// later CPU write to t cannot retroactively change it.
						horiLatch = (ushort)(v & 0x041F);
						break;
					case ActCopyVert:
						if (scanline == 261) CopyYFromTToV();
						break;
				}
			}

			scanlineCycle++;

			// 341 dots on every scanline, including the pre-render one.
			//
			// Hardware does NOT do that: every other frame it drops the last dot of the pre-render
			// scanline, making the frame 89341 dots instead of 89342 (the half-dot average is where
			// NTSC's 60.0988fps comes from). NES.RunFrame's NtscAccurateFrameRate budget already
			// alternates 89342/89341 (NES.cs ntscFrameParityToggle), so a PPU that always runs 89342
			// slides one dot backwards through its own frame every other frame.
			//
			// That was implemented here and then removed, on measurement. Against Mesen over 900
			// frames of game.nes it made every column WORSE, not better: v 835 -> 888 divergent, pc
			// 841 -> 891, ramhash 620 -> 769. The reason is a second, larger error it can only add
			// to: NES.RunFrame trues the PPU up with `Step(ntscDotBudget - globalCpuCycle*3)`
			// without folding those dots back into globalCpuCycle, so the next frame's target
			// re-issues them and the PPU is over-stepped. Measured drift of the frame boundary
			// through the PPU frame: about -0.145 dots/frame with no dot skip, about +0.29 with it,
			// i.e. the missing skip's -0.5 is currently the thing holding the over-step in check.
			// Adding the skip is only correct once the frame driver stops over-stepping, and that
			// is a change to NES.cs - shared by four projects, and it would move the goldens of
			// every core, not just this one.
			if (scanlineCycle >= 341)
			{
				scanlineCycle = 0;

				// Still rendered a whole scanline at a time, at the scanline's end, from the
				// address latched at dot 256 - byte for byte what the old CopyXFromTToV() + render
				// pair produced. Only the bookkeeping around it moved into the dot-accurate block.
				if (scanline >= 0 && scanline < 240)
				{
					RenderScanline(scanline);
				}

				// (VBlank set / NMI assert moved to scanline 241 dot 1 in the per-dot section above -
				// firing it here meant the end of scanline 241, i.e. 340 dots late.)
				// (v = t at the end of pre-render replaced by the dot 280-304 vert(v) := vert(t)
				// copy above, which is both the right dots and the right BITS: hardware reloads only
				// fine Y, coarse Y and nametable Y there, never the horizontal half.)

				scanline++;
				if (scanline == TotalScanlines)
				{
					scanline = 0;
				}
				UpdateVPipelineFlag();
			}
		}
	}

	private readonly bool[] bgMask = new bool[ScreenWidth];
	private void RenderScanline(int scanline)
	{
		// Ensure a framebuffer exists before writing pixels
		EnsureFrameBuffer();
		// If no ROM is loaded, keep the test pattern
		if (bus?.cartridge == null)
		{
			return;
		}

		// Greyscale + colour emphasis are output-stage effects applied to EVERY pixel this
		// scanline produces, backdrop included, so resolve them once here rather than per pixel.
		RefreshColorMask();

		// If both background & sprites are disabled this scanline, proactively clear it
		// so the power-on test pattern from initialization doesn't visually linger and
		// confuse debugging (otherwise the old pixels remain untouched).
		bool bgEnabled = (PPUMASK & 0x08) != 0; // bit 3
		bool sprEnabled = (PPUMASK & 0x10) != 0; // bit 4
		if (!bgEnabled && !sprEnabled)
		{
			EnsureFrameBuffer();
			// Universal background color. Emphasis and greyscale still apply with rendering off -
			// they are video-output stage effects, downstream of everything the renderer does.
			byte ubIdx = paletteRAM[0];
			int p = emphBase + (ubIdx & greyMask) * 3;
			byte r = EmphasisPaletteBytes[p]; byte g = EmphasisPaletteBytes[p+1]; byte b = EmphasisPaletteBytes[p+2];
			int baseIndex = scanline * ScreenWidth * 4;
			for (int x = 0; x < ScreenWidth; x++)
			{
				int fi = baseIndex + x * 4;
				frameBuffer![fi+0] = r;
				frameBuffer![fi+1] = g;
				frameBuffer![fi+2] = b;
				frameBuffer![fi+3] = 255;
			}
			return; // nothing else to draw
		}

		// Ensure framebuffer exists before rendering (needed after ClearBuffers during hotswap)
		EnsureFrameBuffer();
		
		// Clear scanline buffers
		Array.Clear(bgMask, 0, ScreenWidth);
		
		// Render background first (if enabled)
		if (bgEnabled) RenderBackground(scanline, bgMask);
		// Sprite evaluation (and possible overflow-flag set) runs whenever EITHER
		// background or sprite rendering is on, independent of whether sprite pixels
		// actually get drawn - RenderSprites internally gates the pixel-drawing loop
		// on sprEnabled while always running evaluation.
		if (bgEnabled || sprEnabled) RenderSprites(scanline, bgMask);
	}

	public byte[] GetFrameBuffer() { EnsureFrameBuffer(); return frameBuffer!; }

	public void ClearBuffers()
	{
		// Release framebuffer so it will be recreated lazily on demand.
		frameBuffer = null;
	}

	public void GenerateStaticFrame()
	{
		EnsureFrameBuffer();
		// Old TV style static: fully decorrelated spatial noise each frame (no directional drift).
		// We derive a pseudo-random value from (x,y,frame) using a cheap integer hash.
		int w = ScreenWidth; int h = ScreenHeight;
		uint frameSeed = (uint)staticFrameCounter * 0x9E3779B1u + 0xB5297A4Du; // mix frame into seed
		for (int y = 0; y < h; y++)
		{
			uint rowSeed = frameSeed ^ (uint)(y * 0x1F123BB5u);
			for (int x = 0; x < w; x++)
			{
				uint h0 = rowSeed ^ (uint)(x * 0xA24BAEDCu);
				// Mix (Wang / xorshift-ish)
				h0 ^= h0 >> 15; h0 *= 0x2C1B3C6Du;
				h0 ^= h0 >> 12; h0 *= 0x297A2D39u;
				h0 ^= h0 >> 15;
				// Intensity 0..255 from high bits
				byte intensity = (byte)(h0 >> 24);
				// Optional subtle purple tint: mix grayscale with a lavender bias
				// Weight grayscale 75%, purple bias 25%.
				byte baseGray = intensity;
				// Purple bias curve (lavender ramp)
				byte pr = (byte)(40 + (intensity * 3) / 5);   // tends toward higher red
				byte pg = (byte)(intensity / 4);              // subdued green
				byte pb = (byte)(60 + (intensity * 4) / 5);   // stronger blue for violet
				byte r = (byte)((baseGray * 3 + pr) / 4);
				byte g = (byte)((baseGray * 3 + pg) / 4);
				byte b = (byte)((baseGray * 3 + pb) / 4);
				// Rare bright spark
				if ((h0 & 0x7FF) == 0) { r = g = b = 255; }
				int idx = (y * w + x) * 4;
				frameBuffer![idx + 0] = r;
				frameBuffer![idx + 1] = g;
				frameBuffer![idx + 2] = b;
				frameBuffer![idx + 3] = 255;
			}
		}
		staticFrameCounter++;
	}

	public void UpdateFrameBuffer()
	{
		// This method is called after rendering a frame
		// The frame buffer is already updated in RenderScanline
		// Add some animated elements for testing
		EnsureFrameBuffer();
		if (bus?.cartridge == null)
		{
			AddAnimatedTestElements();
		}
	}

	private void RenderBackground(int scanline, bool[] bgMask)
	{
		// Check if background rendering is enabled
		if ((PPUMASK & 0x08) == 0) return;
		// PPUMASK bit 1: show background in the leftmost 8 screen columns (left-edge clipping).
		bool showBgLeft = (PPUMASK & 0x02) != 0;
		// Guard against null during hot-swap
		if (bgMask == null || frameBuffer == null || paletteRAM == null || vram == null) return;

		EnsureFrameBuffer();
		// Cache frameBuffer reference locally to prevent race condition if ClearBuffers is called during render
		var fb = frameBuffer;

		// Inform mapper we're about to do background pattern fetches (MMC5 A/B CHR banking)
		if (bus?.cartridge?.mapper is IMapper mBg)
			mBg.PpuPhaseHint(false, (PPUCTRL & 0x20) != 0, (PPUMASK & 0x18) != 0);

	// Cache universal background color once per scanline
	byte ubIdx = paletteRAM[0];
	// Hoisted out of the per-pixel loop below: these are fixed for the whole scanline.
	int emph = emphBase, grey = greyMask;
	var pal = EmphasisPaletteBytes;
	int ubp = emph + (ubIdx & grey) * 3;
	byte ubR = pal[ubp];
	byte ubG = pal[ubp+1];
	byte ubB = pal[ubp+2];

		// Latched at dot 256 out of the live loopy-v; see renderAddr and the pipeline block in
		// Step(). Equal to what the old `CopyXFromTToV(); RenderScanline();` pair walked from.
		ushort renderV = renderAddr;

		// Render 33 tiles (32 visible + 1 for scrolling)
		for (int tile = 0; tile < 33; tile++)
		{
			// Extract nametable coordinates from current VRAM address
			int coarseX = renderV & 0x001F;
			int coarseY = (renderV >> 5) & 0x001F;
			int nameTable = (renderV >> 10) & 0x0003;

			// Calculate nametable address
			int baseNTAddr = 0x2000 + (nameTable * 0x400);
			int tileAddr = baseNTAddr + (coarseY * 32) + coarseX;
			// Notify mapper of NT tile fetch (for MMC5 Mode 1 tracking)
			if (bus?.cartridge?.mapper is IMapper mapperNt) mapperNt.PpuNtFetch((ushort)tileAddr);
			byte tileIndex = Read((ushort)tileAddr);

			// Get fine Y scroll (which row within the 8x8 tile)
			int fineY = (renderV >> 12) & 0x7;
			
			// Determine pattern table (background uses PPUCTRL bit 4)
			int patternTable = (PPUCTRL & 0x10) != 0 ? 0x1000 : 0x0000;
			int patternAddr = patternTable + (tileIndex * 16) + fineY;
			
			// Read the two bit planes for this row of the tile
			byte plane0 = Read((ushort)patternAddr);
			byte plane1 = Read((ushort)(patternAddr + 8));

			// Determine palette index: try MMC5 Mode 1 override first, else use attribute table
			int paletteIndex;
			int mmc5Pal = (bus?.cartridge?.mapper is IMapper mapperPal) ? mapperPal.GetMmc5Mode1BgPaletteIndex() : -1;
			if (mmc5Pal >= 0) paletteIndex = mmc5Pal;
			else {
				int attributeX = coarseX / 4;
				int attributeY = coarseY / 4;
				int attrAddr = baseNTAddr + 0x3C0 + attributeY * 8 + attributeX;
				byte attrByte = Read((ushort)attrAddr);
				int attrShift = ((coarseY % 4) / 2) * 4 + ((coarseX % 4) / 2) * 2;
				paletteIndex = (attrByte >> attrShift) & 0x03;
			}

			// Pre-calculate frame buffer base for this scanline
			int scanlineBase = scanline * ScreenWidth * 4;

			// Render the 8 pixels of this tile
			for (int i = 0; i < 8; i++)
			{
				int pixel = tile * 8 + i - fineX;
				if (pixel < 0 || pixel >= ScreenWidth) continue;

				// Left-edge clipping: force the universal backdrop color (and leave
				// bgMask unset, i.e. transparent) in the leftmost 8 columns when disabled.
				if (pixel < 8 && !showBgLeft)
				{
					int clipFrameIndex = scanlineBase + pixel * 4;
					fb![clipFrameIndex + 0] = ubR;
					fb![clipFrameIndex + 1] = ubG;
					fb![clipFrameIndex + 2] = ubB;
					fb![clipFrameIndex + 3] = 255;
					continue;
				}

				int bitIndex = 7 - i;
				int bit0 = (plane0 >> bitIndex) & 1;
				int bit1 = (plane1 >> bitIndex) & 1;
				int colorIndex = bit0 | (bit1 << 1);

				int frameIndex = scanlineBase + pixel * 4;
				if (colorIndex == 0)
				{
					// Universal background color
				fb![frameIndex + 0] = ubR;
				fb![frameIndex + 1] = ubG;
				fb![frameIndex + 2] = ubB;
				fb![frameIndex + 3] = 255;
				}
				else
				{
					bgMask[pixel] = true;
					int paletteBase = 1 + (paletteIndex << 2);
					byte idx = paletteRAM[(paletteBase + colorIndex - 1) & 0x1F];
					int p = emph + (idx & grey) * 3;
				fb![frameIndex + 0] = pal[p];
				fb![frameIndex + 1] = pal[p+1];
				fb![frameIndex + 2] = pal[p+2];
				fb![frameIndex + 3] = 255;
				}
			}

			// Increment to next tile
			IncrementX(ref renderV);
		}
	}

	private void RenderSprites(int scanline, bool[] bgMask)
	{
		// Check if sprite rendering is enabled. This flag only gates the per-sprite
		// pixel-drawing/hit-test loop further down - evaluation below (which can set the
		// overflow flag) always runs, since real hardware evaluates sprites whenever either
		// background or sprite rendering is on (see RenderScanline call site).
		bool showSprites = (PPUMASK & 0x10) != 0;
		// PPUMASK bit 2: show sprites in the leftmost 8 screen columns (left-edge clipping).
		bool showSprLeft = (PPUMASK & 0x04) != 0;
		// Guard against null during hot-swap
		if (bgMask == null || frameBuffer == null || paletteRAM == null || oam == null || vram == null) return;

		EnsureFrameBuffer();
		// Cache frameBuffer reference locally to prevent race condition if ClearBuffers is called during render
		var fb = frameBuffer;

		bool isSprite8x16 = (PPUCTRL & 0x20) != 0;
		// Inform mapper we're about to do sprite pattern fetches (MMC5 A/B CHR banking) -
		// only relevant when sprites are actually drawn below.
		if (showSprites && bus?.cartridge?.mapper is IMapper mSpr)
			mSpr.PpuPhaseHint(true, isSprite8x16, (PPUMASK & 0x18) != 0);
		Array.Clear(spritePixelDrawnReuse, 0, spritePixelDrawnReuse.Length);

		// Real hardware evaluates at most 8 sprites per scanline and sets the overflow flag
		// (PPUSTATUS bit 5) when a 9th in-range sprite exists - this also gates sprite-0-hit,
		// since sprite 0 can only be hit on a line if it falls within the first 8 evaluated.
		// This loop runs unconditionally (see showSprites comment above).
		int spriteEvalCount = 0;
		Span<int> spriteLineIdx = stackalloc int[8];
		for (int si = 0; si < 64; si++)
		{
			byte sy = oam[si * 4];
			int sh = isSprite8x16 ? 16 : 8;
			// OAM byte 0 is the sprite's Y coordinate minus 1: sprite data is delayed by
			// one scanline on real hardware, so the first displayed row is sy + 1.
			int spriteTop = sy + 1;
			if (scanline < spriteTop || scanline >= spriteTop + sh) continue;
			if (spriteEvalCount < 8) spriteLineIdx[spriteEvalCount++] = si;
			else { PPUSTATUS |= 0x20; break; }
		}

		// Sprite pixel drawing (and sprite-0-hit testing) only happens when sprites are
		// actually enabled for display on screen - matches hardware, where the hit flag
		// cannot be set unless both background and sprite rendering are on.
		if (!showSprites) return;

		// Process only the (up to 8) sprites selected above
		for (int li = 0; li < spriteEvalCount; li++)
		{
			int i = spriteLineIdx[li];
			int offset = i * 4;
			byte spriteY = oam[offset];
			byte tileIndex = oam[offset + 1];
			byte attributes = oam[offset + 2];
			byte spriteX = oam[offset + 3];

			// Extract sprite attributes
			int paletteIndex = attributes & 0b11;
			bool flipX = (attributes & 0x40) != 0;
			bool flipY = (attributes & 0x80) != 0;
			bool priority = (attributes & 0x20) == 0; // 0 = in front of background

			int tileHeight = isSprite8x16 ? 16 : 8;

			// Calculate which row of the sprite we're rendering (Y delayed by one scanline,
			// same as the evaluation loop above)
			int subY = scanline - (spriteY + 1);
			if (flipY) subY = tileHeight - 1 - subY;

			// For 8x16 sprites, determine which tile and pattern table
			int subTileIndex = isSprite8x16 ? (tileIndex & 0xFE) + (subY / 8) : tileIndex;
			int patternTable = isSprite8x16
				? ((tileIndex & 1) != 0 ? 0x1000 : 0x0000)
				: ((PPUCTRL & 0x08) != 0 ? 0x1000 : 0x0000);
			int baseAddr = patternTable + subTileIndex * 16;

			// Read pattern data for this row
			byte plane0 = Read((ushort)(baseAddr + (subY % 8)));
			byte plane1 = Read((ushort)(baseAddr + (subY % 8) + 8));

			// Render 8 pixels of the sprite
			for (int x = 0; x < 8; x++)
			{
				int bit = flipX ? x : 7 - x;
				int bit0 = (plane0 >> bit) & 1;
				int bit1 = (plane1 >> bit) & 1;
				int color = bit0 | (bit1 << 1);
				if (color == 0) continue; // Transparent pixel

				int px = spriteX + x;
				if (px < 0 || px >= ScreenWidth) continue;
				// Left-edge clipping: PPUMASK bit 2 (0x04) disables sprites in the leftmost
				// 8 screen columns. When clipped, the pixel is treated as fully transparent
				// for both drawing and sprite-0-hit purposes.
				if (px < 8 && !showSprLeft) continue;

				// Sprite 0 hit detection. Real hardware never sets the hit flag when the
				// colliding pixel is at x=255 (documented PPU quirk).
				if (i == 0 && px != 255 && bgMask[px] && color != 0)
				{
					PPUSTATUS |= 0x40;
				}

				// Skip if another sprite already drew here
				if (spritePixelDrawnReuse[px]) continue;

				// Check sprite priority
				bool shouldDraw = true;
				if (!priority && bgMask[px])
				{
					shouldDraw = false;
				}

				if (shouldDraw)
				{
					var spriteColor = GetSpriteColor(color, paletteIndex);
					int frameIndex = (scanline * ScreenWidth + px) * 4;
					if (frameIndex + 3 < fb!.Length)
					{
						fb![frameIndex + 0] = spriteColor.r;
						fb![frameIndex + 1] = spriteColor.g;
						fb![frameIndex + 2] = spriteColor.b;
						fb![frameIndex + 3] = 255;
					}
						spritePixelDrawnReuse[px] = true;
				}
			}
		}
	}

	private (byte r, byte g, byte b) GetSpriteColor(int colorIndex, int paletteIndex)
	{
		int paletteBase = 0x11 + (paletteIndex << 2);
		byte idx = paletteRAM[paletteBase + (colorIndex - 1)];
		int p = emphBase + (idx & greyMask) * 3;
		return (EmphasisPaletteBytes[p], EmphasisPaletteBytes[p+1], EmphasisPaletteBytes[p+2]);
	}

	private (byte r, byte g, byte b) GetColorFromPalette(int colorIndex, int paletteIndex)
	{
		byte idx;
		if (colorIndex == 0)
		{
			idx = paletteRAM[0];
		}
		else
		{
			int paletteBase = 1 + (paletteIndex << 2);
			idx = paletteRAM[(paletteBase + colorIndex - 1) & 0x1F];
		}
		int p = emphBase + (idx & greyMask) * 3;
		return (EmphasisPaletteBytes[p], EmphasisPaletteBytes[p+1], EmphasisPaletteBytes[p+2]);
	}

	// Add some animated elements to make the test pattern more interesting
	private void AddAnimatedTestElements()
	{
		EnsureFrameBuffer();
		int frame = scanline + scanlineCycle / 100;
		
		// Add moving "sprites" for testing
		for (int i = 0; i < 4; i++)
		{
			int spriteX = (32 + i * 64 + frame * (i + 1)) % (ScreenWidth - 16);
			int spriteY = 200 + (int)(Math.Sin(frame * 0.1 + i) * 20);
			
			DrawTestSprite(spriteX, spriteY, i);
		}
		
		// Add a moving scan line effect
		int scanLineY = (frame * 2) % ScreenHeight;
		for (int x = 0; x < ScreenWidth; x++)
		{
			int index = (scanLineY * ScreenWidth + x) * 4;
			if (index + 3 < frameBuffer!.Length)
			{
				frameBuffer![index + 0] = 255; // Bright white scan line
				frameBuffer![index + 1] = 255;
				frameBuffer![index + 2] = 255;
			}
		}
	}

	// Draw a simple test sprite
	private void DrawTestSprite(int x, int y, int spriteType)
	{
		EnsureFrameBuffer();
		int[] indices = {0x0F,0x16,0x2A,0x12};
		int idx = indices[spriteType % 4] & 0x3F;
		int p = idx * 3;
		(byte r, byte g, byte b) color = (PaletteBytes[p], PaletteBytes[p+1], PaletteBytes[p+2]);
		
		// Draw an 8x8 sprite with a simple pattern
		for (int dy = 0; dy < 8; dy++)
		{
			for (int dx = 0; dx < 8; dx++)
			{
				int px = x + dx;
				int py = y + dy;
				
				if (px >= 0 && px < ScreenWidth && py >= 0 && py < ScreenHeight)
				{
					// Simple cross pattern
					bool shouldDraw = (dx == 4) || (dy == 4) || 
					                 (dx == dy) || (dx == 7 - dy);
					
					if (shouldDraw)
					{
						int index = (py * ScreenWidth + px) * 4;
						if (index + 3 < frameBuffer!.Length)
						{
							frameBuffer![index + 0] = color.r;
							frameBuffer![index + 1] = color.g;
							frameBuffer![index + 2] = color.b;
							frameBuffer![index + 3] = 255;
						}
					}
				}
			}
		}
	}

	public byte ReadPPURegister(ushort address)
	{
		byte result = 0x00;

		switch (address & 0x0007)
		{
			case 0x0002: // PPU Status
				{
					// Only bits 7-5 (VBlank/Sprite0Hit/Overflow) are actually driven by the
					// status register; bits 4-0 are whatever the PPU's own I/O bus (distinct
					// from the CPU's open bus) is still holding from the last $2000-$2007 access.
					byte status = PPUSTATUS;
					result = (byte)((status & 0xE0) | (ReadPpuOpenBus() & 0x1F));
					RefreshPpuOpenBus(status, 0xE0); // the read itself redrives bits 7-5 onto the latch
					PPUSTATUS &= 0x3F; // Clear VBlank flag on read
					w = false; // the ONE shared $2005/$2006 write toggle - both halves, not just $2006
					return result;
				}
			case 0x0004: // OAM Data
				result = oam[OAMADDR];
				RefreshPpuOpenBus(result);
				return result;
			case 0x0007: // PPU Data
				// Addressed by the live v, not by a private shadow: hardware has one address
				// register and $2007 uses it. (Which is also why a $2007 access while rendering is
				// on lands wherever the fetch pipeline has driven v to - see Step().)
				if ((v & 0x3FFF) >= 0x3F00)
				{
					// Palette reads are immediate, but the internal read buffer still gets
					// refilled with the underlying nametable byte "under" the palette mirror -
					// a real hardware quirk (a bare Read(v) here would refill the buffer
					// with the palette byte itself, corrupting the next non-palette $2007 read).
					byte pal = Read(v);
					// Greyscale (PPUMASK bit 0) masks the value on the way OUT of palette RAM -
					// it never affects what gets written. Palette RAM itself only drives 6 bits;
					// the top 2 come from the PPU I/O bus, same as $2002's low bits above.
					if ((PPUMASK & 0x01) != 0) pal &= 0x30;
					result = (byte)((ReadPpuOpenBus() & 0xC0) | (pal & 0x3F));
					ppuDataBuffer = Read((ushort)(v - 0x1000));
				}
				else
				{
					result = ppuDataBuffer;
					ppuDataBuffer = Read(v);
				}
				RefreshPpuOpenBus(result);
				// v is 15 bits on hardware; the increment wraps within it. While the PPU is fetching, a read
				// collides with the render pipeline exactly as a write does (the data is not corrupted,
				// only the address bookkeeping) - see CollideDataAccessWithRender.
				if (dataPortCollision && vPipelineOn) CollideDataAccessWithRender();
				else v = (ushort)((v + ((PPUCTRL & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
				PPUADDR = v;
				return result;
			default:
				// Write-only registers ($2000/$2001/$2003/$2005/$2006): the PPU drives nothing
				// here - this returns whatever the PPU's own I/O bus last latched.
				return ReadPpuOpenBus();
		}
	}

	public void WritePPURegister(ushort address, byte value)
	{
		// Any CPU write to $2000-$2007 drives the byte onto the PPU's own I/O bus, including a
		// write to the read-only $2002 (the write has no effect on PPUSTATUS itself, but it
		// still drives the latch - see RefreshPpuOpenBus).
		RefreshPpuOpenBus(value);
		switch (address & 0x0007)
		{
			case 0x0000: // PPU Control
				{
					bool oldNmiEnable = (PPUCTRL & 0x80) != 0;
					PPUCTRL = value;
					t = (ushort)((t & 0xF3FF) | ((value & 0x03) << 10));
					bool newNmiEnable = (PPUCTRL & 0x80) != 0;
					// Hardware: the /NMI line is the combinational AND of PPUSTATUS.VBlank and
					// PPUCTRL.NMI-enable. Enabling NMI while the VBlank flag is already latched
					// (from a previous, unread VBlank) fires an NMI right here, not just at the
					// next scanline-241 boundary. This is the blargg nmi_control quirk (AccuracyCoin
					// "NMI Control" tests 3/5/6/7).
					if (!oldNmiEnable && newNmiEnable && (PPUSTATUS & 0x80) != 0)
					{
						bus.cpu.RequestNMI();
					}
				}
				break;
			case 0x0001: // PPU Mask
				PPUMASK = value;
				// Greyscale / emphasis take effect from the very next pixel drawn, so resolve the
				// output-stage lookup here as well as per scanline - a mid-frame $2001 write is
				// exactly how the whole-screen flash effects this implements are done.
				RefreshColorMask();
				break;
			case 0x0002: // PPU Status
				// $2002 is read-only. A write drives the PPU I/O bus (above) and does nothing else:
				// it must NOT clear VBlank, and it must NOT touch the write toggle either - only a
				// READ of $2002 clears w (see the corresponding case in ReadPPURegister). This used
				// to clear the $2005 half of the old split latch, which no hardware behaviour
				// justifies and which would now clear the $2006 half with it.
				break;
			case 0x0003: // OAM Address
				OAMADDR = value;
				break;
			case 0x0004: // OAM Data
				OAMDATA = value;
				oam[OAMADDR++] = OAMDATA;
				break;
			case 0x0005: // PPU Scroll
				if (!w)
				{
					PPUSCROLLX = value;
					fineX = (byte)(value & 0x07);
					t = (ushort)((t & 0xFFE0) | (value >> 3));
				}
				else
				{
					PPUSCROLLY = value;
					t = (ushort)((t & 0x8FFF) | ((value & 0x07) << 12));
					t = (ushort)((t & 0xFC1F) | ((value & 0xF8) << 2));
				}
				w = !w; // the shared toggle, not a $2005-private one
				break;
			case 0x0006: // PPU Address
				if (!w)
				{
					// Hardware latches only the low SIX bits of the high byte into t and clears
					// bit 14: t is 15 bits wide and $2006 cannot set the top one.
					t = (ushort)((t & 0x00FF) | ((value & 0x3F) << 8));
				}
				else
				{
					t = (ushort)((t & 0xFF00) | value);
					v = t; // the second write copies t into the live address register
					PPUADDR = v;
				}
				w = !w; // same shared toggle as $2005
				break;
			case 0x0007: // PPU Data
				PPUDATA = value;
				if (dataPortCollision && vPipelineOn)
				{
					// Rendering owns the VRAM address bus. The byte that lands is the address's own
					// low byte, not the CPU's data - see CollideDataAccessWithRender for the model.
					Write(v, (byte)(v & 0xFF));
					CollideDataAccessWithRender();
				}
				else
				{
					Write(v, PPUDATA);
					v = (ushort)((v + ((PPUCTRL & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
				}
				PPUADDR = v;
				break;
		}
	}

	public byte Read(ushort address)
	{
		address = (ushort)(address & 0x3FFF);

		if (address < 0x2000)
		{
			return bus.cartridge.PPURead(address);
		}
		else if (address >= 0x2000 && address <= 0x3EFF)
		{
			// Allow mapper to override nametable reads (MMC5 $5105 ExRAM/Fill)
			if (bus?.cartridge?.mapper is IMapper mNt && mNt.TryPpuNametableRead(address, out byte v)) return v;
			// If mapper provides per-quadrant NT mode 0/1, resolve CIRAM A/B directly
			if (bus?.cartridge?.mapper is IMapper mMode)
			{
				int mode = mMode.GetMmc5NtModeForAddress(address);
				if (mode == 0 || mode == 1)
				{
					int inner = address & 0x03FF;
					ushort ciramBase = (ushort)(mode == 0 ? 0x0000 : 0x0400);
					return vram[ciramBase + inner];
				}
			}
			ushort mirrored = MirrorVRAMAddress(address);
			return vram[mirrored];
		}
		else if (address >= 0x3F00 && address <= 0x3FFF)
		{
			ushort mirrored = (ushort)(address & 0x1F);
			if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10;
			return paletteRAM[mirrored];
		}

		return 0;
	}

	public void Write(ushort address, byte value)
	{
		address = (ushort)(address & 0x3FFF);

		if (address < 0x2000)
		{
			bus.cartridge.PPUWrite(address, value);
		}
		else if (address >= 0x2000 && address <= 0x3EFF)
		{
			// Allow mapper to override nametable writes (ExRAM/Fill)
			if (bus?.cartridge?.mapper is IMapper mNt && mNt.TryPpuNametableWrite(address, value)) return;
			// If mapper provides per-quadrant NT mode 0/1, resolve CIRAM A/B directly
			if (bus?.cartridge?.mapper is IMapper mMode)
			{
				int mode = mMode.GetMmc5NtModeForAddress(address);
				if (mode == 0 || mode == 1)
				{
					int inner = address & 0x03FF;
					ushort ciramBase = (ushort)(mode == 0 ? 0x0000 : 0x0400);
					vram[ciramBase + inner] = value; return;
				}
			}
			ushort mirrored = MirrorVRAMAddress(address);
			vram[mirrored] = value;
		}
		else if (address >= 0x3F00 && address <= 0x3FFF)
		{
			ushort mirrored = (ushort)(address & 0x1F);
			if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10;
			// Palette RAM cells are physically only 6 bits wide; the top 2 bits never get
			// written and read back as whatever the PPU I/O bus holds (see ReadPPURegister's
			// $2007 case). All render-time lookups already mask with & 0x3F, so this doesn't
			// change what's drawn - it only changes what a $2007 read reports.
			paletteRAM[mirrored] = (byte)(value & 0x3F);
		}
	}

	private ushort MirrorVRAMAddress(ushort address)
	{
		ushort offset = (ushort)(address & 0x0FFF);

		int ntIndex = offset / 0x400;
		int innerOffset = offset % 0x400;

		switch (bus.cartridge.mirroringMode)
		{
			case Mirroring.Vertical:
				return (ushort)((ntIndex % 2) * 0x400 + innerOffset);
			case Mirroring.Horizontal:
				return (ushort)(((ntIndex / 2) * 0x400) + innerOffset);
			case Mirroring.SingleScreenA:
				return (ushort)(innerOffset);
			case Mirroring.SingleScreenB:
				return (ushort)(0x400 + innerOffset);
			default:
				return offset;
		}
	}

	public void WriteOAMDMA(byte page)
	{
		bus.FastOamDma(page, oam, ref OAMADDR);
	}

	// A $2007 access while the PPU is fetching (rendering on, on a visible or pre-render line).
	//
	// The CPU and the fetch pipeline are then both driving the VRAM address bus. Hardware resolves
	// it by bumping v with a coarse-X increment AND a Y increment together, instead of PPUCTRL
	// bit 2's +1/+32 - and on a write, what reaches memory is the address's low byte rather than the
	// CPU's data. Measured against Mesen 2.1.1 on VRUN, whose screen-clear lands 32 writes on
	// scanlines 3-4 when the ending is entered from live gameplay: Mesen's v walks
	// 3000,4001,5002,6003,7004,0025,1027,... (fine Y and coarse X both advancing per write, the fine-Y
	// carry rolling into coarse Y), and the attribute bytes at 23C0-23C8 end up C0..C8, their own
	// address low bytes. Modelling it as the rendering-off path wrote the data byte into 20
	// consecutive nametable cells instead.
	//
	// Gated on SpeedConfig.CpuCyclePrecisePpu, and deliberately so. Whether an access collides
	// depends on the exact dot it lands on, and on the batched path the PPU is only caught up
	// after each instruction returns - up to 21 dots off. An earlier unconditional version of this
	// changed SMB3's golden frame on exactly that basis, so the model only runs where the PPU's
	// position at the access is trustworthy.
	private void CollideDataAccessWithRender()
	{
		IncrementX(ref v);
		IncrementY();
	}

	private void IncrementY()
	{
		if ((v & 0x7000) != 0x7000)
		{
			v += 0x1000;
		}
		else
		{
			v &= 0x8FFF;
			int y = (v & 0x03E0) >> 5;
			if (y == 29)
			{
				y = 0;
				v ^= 0x0800;
			}
			else if (y == 31)
			{
				y = 0;
			}
			else
			{
				y += 1;
			}
			v = (ushort)((v & 0xFC1F) | (y << 5));
		}
	}

	private void IncrementX(ref ushort addr)
	{
		if ((addr & 0x001F) == 31)
		{
			addr &= 0xFFE0;
			addr ^= 0x0400;
		}
		else
		{
			addr++;
		}
	}

	// hori(v) := hori(t) - coarse X (bits 0-4) and the nametable-X select (bit 10).
	private void CopyXFromTToV()
	{
		v = (ushort)((v & 0xFBE0) | (t & 0x041F));
	}

	// vert(v) := vert(t) - fine Y (bits 12-14), coarse Y (bits 5-9) and the nametable-Y select
	// (bit 11). The complement of CopyXFromTToV; hardware performs it on every dot of 280-304 of
	// the pre-render scanline. The old code approximated the whole thing with `v = t`, which also
	// overwrote the horizontal half a scanline's worth of prefetching had already advanced.
	private void CopyYFromTToV()
	{
		v = (ushort)((v & 0x041F) | (t & 0x7BE0));
	}

	// Removed eager test pattern generation; rendering occurs only when needed

	// Initialize palette RAM with reasonable defaults
	private void InitializeDefaultPalette()
	{
		// Background palette 0 (typically used for most graphics)
		paletteRAM[0x00] = 0x0F; // Universal background color (black)
		paletteRAM[0x01] = 0x00; // Dark color
		paletteRAM[0x02] = 0x10; // Medium color
		paletteRAM[0x03] = 0x30; // Light color
		
		// Background palette 1
		paletteRAM[0x04] = 0x0F;
		paletteRAM[0x05] = 0x06; // Brown
		paletteRAM[0x06] = 0x16; // Red
		paletteRAM[0x07] = 0x26; // Pink
		
		// Background palette 2
		paletteRAM[0x08] = 0x0F;
		paletteRAM[0x09] = 0x0A; // Green
		paletteRAM[0x0A] = 0x1A; // Light green
		paletteRAM[0x0B] = 0x2A; // Lighter green
		
		// Background palette 3
		paletteRAM[0x0C] = 0x0F;
		paletteRAM[0x0D] = 0x02; // Blue
		paletteRAM[0x0E] = 0x12; // Light blue
		paletteRAM[0x0F] = 0x22; // Lighter blue
		
		// Sprite palette 0
		paletteRAM[0x10] = 0x0F; // Transparent (not used)
		paletteRAM[0x11] = 0x14; // Purple
		paletteRAM[0x12] = 0x24; // Light purple
		paletteRAM[0x13] = 0x34; // Very light purple
		
		// Sprite palette 1
		paletteRAM[0x14] = 0x0F;
		paletteRAM[0x15] = 0x07; // Orange
		paletteRAM[0x16] = 0x17; // Light orange
		paletteRAM[0x17] = 0x27; // Yellow
		
		// Sprite palette 2
		paletteRAM[0x18] = 0x0F;
		paletteRAM[0x19] = 0x13; // Purple
		paletteRAM[0x1A] = 0x23; // Light purple
		paletteRAM[0x1B] = 0x33; // Very light purple
		
		// Sprite palette 3
		paletteRAM[0x1C] = 0x0F;
		paletteRAM[0x1D] = 0x15; // Magenta
		paletteRAM[0x1E] = 0x25; // Light magenta
		paletteRAM[0x1F] = 0x35; // Very light magenta
	}

	//NES 64 Color Palette
	static readonly byte[] PaletteBytes = new byte[] {
		84,84,84, 0,30,116, 8,16,144, 48,0,136,
		68,0,100, 92,0,48, 84,4,0, 60,24,0,
		32,42,0, 8,58,0, 0,64,0, 0,60,0,
		0,50,60, 0,0,0, 0,0,0, 0,0,0,
		152,150,152, 8,76,196, 48,50,236, 92,30,228,
		136,20,176, 160,20,100, 152,34,32, 120,60,0,
		84,90,0, 40,114,0, 8,124,0, 0,118,40,
		0,102,120, 0,0,0, 0,0,0, 0,0,0,
		236,238,236, 76,154,236, 120,124,236, 176,98,236,
		228,84,236, 236,88,180, 236,106,100, 212,136,32,
		160,170,0, 116,196,0, 76,208,32, 56,204,108,
		56,180,204, 60,60,60, 0,0,0, 0,0,0,
		236,238,236, 168,204,236, 188,188,236, 212,178,236,
		236,174,236, 236,174,212, 236,180,176, 228,196,144,
		204,210,120, 180,222,120, 168,226,144, 152,226,180,
		160,214,228, 160,162,160, 0,0,0, 0,0,0
	};

	// === PPUMASK colour emphasis (bits 5/6/7) ===
	//
	// The 64-colour table above, expanded to the 8 emphasis combinations the PPU's video output can
	// be in. Layout: 8 blocks of 64 rgb triples, block = ((PPUMASK >> 5) & 7), so the byte offset of
	// a colour is block*192 + index*3 - which is exactly the emphBase/greyMask pair the renderers
	// carry. Built once at type-init, so the hot loops pay one add over the old flat lookup and no
	// branch at all; the alternative, testing three mask bits and multiplying per pixel, would put
	// float work in the innermost loop of every scanline for an effect that is off almost always.
	//
	// The rule: a SET emphasis bit attenuates the OTHER two channels (bit 5 "red" darkens green and
	// blue, bit 6 "green" darkens red and blue, bit 7 "blue" darkens red and green), each affected
	// channel once, to ~74.6% - the NTSC figure from the nesdev wiki. Setting all three therefore
	// attenuates every channel and simply darkens the whole picture, which is what the game under
	// test relies on: it writes PPUMASK=$ff for its damage flash, i.e. greyscale AND all three
	// emphasis bits, and expects a desaturated, dimmed screen.
	//
	// Note this deliberately leaves PaletteBytes itself untouched and 192 bytes long: the trace
	// tooling reflects on it to invert rendered RGB back to NES palette indices.
	private static readonly byte[] EmphasisPaletteBytes = BuildEmphasisPalette();

	private static byte[] BuildEmphasisPalette()
	{
		var table = new byte[8 * 64 * 3];
		for (int e = 0; e < 8; e++)
		{
			bool emphR = (e & 1) != 0, emphG = (e & 2) != 0, emphB = (e & 4) != 0;
			bool attenR = emphG || emphB, attenG = emphR || emphB, attenB = emphR || emphG;
			for (int i = 0; i < 64; i++)
			{
				int s = i * 3, d = e * 192 + i * 3;
				table[d + 0] = Attenuate(PaletteBytes[s + 0], attenR);
				table[d + 1] = Attenuate(PaletteBytes[s + 1], attenG);
				table[d + 2] = Attenuate(PaletteBytes[s + 2], attenB);
			}
		}
		return table;
	}

	// x0.746, rounded to nearest, in integer arithmetic so the table is bit-reproducible.
	private static byte Attenuate(byte c, bool on) => on ? (byte)((c * 746 + 500) / 1000) : c;

	public object GetState() {
		// Do NOT serialize the large framebuffer; it can be regenerated. This keeps saves small and fast.
		return new PpuSharedState {
			vram=(byte[])vram.Clone(),
			palette=(byte[])paletteRAM.Clone(),
			oam=(byte[])oam.Clone(),
			// frame omitted intentionally
			PPUCTRL=PPUCTRL,PPUMASK=PPUMASK,PPUSTATUS=PPUSTATUS,OAMADDR=OAMADDR,
			PPUSCROLLX=PPUSCROLLX,PPUSCROLLY=PPUSCROLLY,PPUDATA=PPUDATA,PPUADDR=PPUADDR,
			// PpuSharedState is shared with the frozen cores and still carries two latch fields.
			// This core has one; write it into both so a reader sees a state hardware can be in
			// (the trace tooling reports them as bit0/bit1 of a single 'w' column and expects 0 or 3).
			fineX=fineX,scrollLatch=w,addrLatch=w,v=v,t=t,
			scanline=scanline,scanlineCycle=scanlineCycle, ppuDataBuffer=ppuDataBuffer,
			staticFrameCounter=staticFrameCounter
		};
	}
	public void SetState(object state) {
		if (state is PpuSharedState s) {
			vram = (byte[])s.vram.Clone(); paletteRAM=(byte[])s.palette.Clone(); oam=(byte[])s.oam.Clone();
			// Legacy compatibility: if a frame is present and matches expected length, copy it; otherwise leave empty
			if (s.frame != null && s.frame.Length == ScreenWidth * ScreenHeight * 4) { EnsureFrameBuffer(); frameBuffer = (byte[])s.frame.Clone(); }
			// Either stored latch being set means the shared toggle was set - covers a state saved
			// by this core (which writes both) and one carried across from a core that keeps them apart.
			PPUCTRL=s.PPUCTRL;PPUMASK=s.PPUMASK;PPUSTATUS=s.PPUSTATUS;OAMADDR=s.OAMADDR;PPUSCROLLX=s.PPUSCROLLX;PPUSCROLLY=s.PPUSCROLLY;PPUDATA=s.PPUDATA;PPUADDR=s.PPUADDR;fineX=s.fineX;w=s.scrollLatch||s.addrLatch;v=s.v; t=s.t; scanline=s.scanline; scanlineCycle=s.scanlineCycle; ppuDataBuffer=s.ppuDataBuffer; staticFrameCounter=s.staticFrameCounter; RefreshColorMask(); renderAddr=(ushort)((v & 0xFBE0) | (t & 0x041F)); horiLatch=(ushort)(t & 0x041F); return; }
		if (state is System.Text.Json.JsonElement je) {
			if (je.TryGetProperty("vram", out var pVram)) { if (pVram.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pVram.EnumerateArray()){ if(i>=vram.Length) break; vram[i++]=(byte)el.GetInt32(); } } else if (pVram.ValueKind==System.Text.Json.JsonValueKind.String) { try { var b=pVram.GetBytesFromBase64(); Array.Copy(b,vram,Math.Min(b.Length, vram.Length)); } catch {} } }
			if (je.TryGetProperty("palette", out var pPal)) { if (pPal.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pPal.EnumerateArray()){ if(i>=paletteRAM.Length) break; paletteRAM[i++]=(byte)el.GetInt32(); } } else if (pPal.ValueKind==System.Text.Json.JsonValueKind.String) { try { var b=pPal.GetBytesFromBase64(); Array.Copy(b,paletteRAM,Math.Min(b.Length, paletteRAM.Length)); } catch {} } }
			if (je.TryGetProperty("oam", out var pOam)) { if (pOam.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pOam.EnumerateArray()){ if(i>=oam.Length) break; oam[i++]=(byte)el.GetInt32(); } } else if (pOam.ValueKind==System.Text.Json.JsonValueKind.String) { try { var b=pOam.GetBytesFromBase64(); Array.Copy(b,oam,Math.Min(b.Length, oam.Length)); } catch {} } }
			if (je.TryGetProperty("frame", out var pFrame) && pFrame.ValueKind==System.Text.Json.JsonValueKind.Array) { EnsureFrameBuffer(); int i=0; foreach(var el in pFrame.EnumerateArray()){ if(i>=frameBuffer!.Length) break; frameBuffer![i++]=(byte)el.GetInt32(); } }
			byte GetB(string name){return je.TryGetProperty(name,out var p)?(byte)p.GetInt32():(byte)0;} ushort GetU16(string name){return je.TryGetProperty(name,out var p)?(ushort)p.GetInt32():(ushort)0;}
			PPUCTRL=GetB("PPUCTRL");PPUMASK=GetB("PPUMASK");PPUSTATUS=GetB("PPUSTATUS");OAMADDR=GetB("OAMADDR");PPUSCROLLX=GetB("PPUSCROLLX");PPUSCROLLY=GetB("PPUSCROLLY");PPUDATA=GetB("PPUDATA");PPUADDR=GetU16("PPUADDR");fineX=GetB("fineX");w=(je.TryGetProperty("scrollLatch", out var psl)&&psl.GetBoolean())||(je.TryGetProperty("addrLatch", out var pal)&&pal.GetBoolean());v=GetU16("v");t=GetU16("t");if(je.TryGetProperty("scanline",out var psl2)) scanline=psl2.GetInt32(); if(je.TryGetProperty("scanlineCycle",out var psc)) scanlineCycle=psc.GetInt32(); if(je.TryGetProperty("ppuDataBuffer", out var pdb)) ppuDataBuffer=(byte)pdb.GetInt32();
			RefreshColorMask(); renderAddr=(ushort)((v & 0xFBE0) | (t & 0x041F)); horiLatch=(ushort)(t & 0x041F); // reconstruct the dot-257 latch from t, matching what renderAddr is rebuilt from
		}
	}

	// === IPpuProbe: side-effect-free observation of this core's own counters and address space ===
	//
	// These exist so external tooling (the VRUN corruption detector's late-PPU-write check, the
	// Nametable memory domain, a hex editor) can see WHERE IN THE FRAME something happened and WHAT
	// the nametables hold, without going through ReadPPURegister - which would clear $2002's vblank
	// flag, reset the address latch and shift the $2007 read buffer, i.e. change the very run being
	// measured. Nothing below mutates a single field of this core except ProbePpuBusWrite, which is
	// an explicit tooling write.
	//
	// TIMING CAVEAT, stated here rather than left for a caller to discover: NES.RunFrame executes
	// CPU instructions and only then catches the PPU up (FlushBatch), so at the instant of a CPU
	// write these counters are BEHIND the true dot by however many CPU cycles have accumulated
	// since the last flush - at most the batch threshold plus one instruction's cycles, times 3
	// dots per CPU cycle. With SpeedConfig.NtscAccurateFrameRate on (the --trace and --corrupt
	// default) that threshold is 1, so the lag is bounded by a single instruction, ~18 dots - about
	// 5% of one 341-dot scanline. Small against the 6820-dot vblank window a scanline check cares
	// about, but not zero: a write landing within ~18 dots of a scanline boundary can be attributed
	// to the previous line.
	public int ProbeScanline => scanline;
	public int ProbeDot => scanlineCycle;
	public byte ProbeMask => PPUMASK;
	public byte ProbePpuBusRead(ushort address) => Read(address);
	public void ProbePpuBusWrite(ushort address, byte value) => Write(address, value);
}
}
