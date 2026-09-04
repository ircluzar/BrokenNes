namespace NesEmulator
{
// FIX family: the exclusive target for ongoing hardware-accuracy work (see
// project_fix_core_family memory). Started as an exact duplicate of PPU_FMC, the "standard
// control component" baseline - every other named core is either a speed/perf tradeoff or an
// intentional gimmick/personality core (see project_ppu_core_personalities memory) and is
// frozen going forward. Accuracy fixes land here, not on PPU_FMC or any other named core.
public class PPU_FIX : IPPU
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

	private bool addrLatch = false;
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
	private bool scrollLatch; //w
	private ushort v; //current VRAM address
	private ushort t; //temp VRAM address

	private int scanlineCycle;
	private int scanline;

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

			scanlineCycle++;

			if (scanlineCycle >= 341)
			{
				scanlineCycle = 0;

				if (scanline >= 0 && scanline < 240)
				{
					CopyXFromTToV();
					RenderScanline(scanline);
					IncrementY();
				}

				// (VBlank set / NMI assert moved to scanline 241 dot 1 in the per-dot section above -
				// firing it here meant the end of scanline 241, i.e. 340 dots late.)

				if (scanline == 261)
				{
					v = t;
				}

				scanline++;
				if (scanline == TotalScanlines)
				{
					scanline = 0;
				}
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

		// If both background & sprites are disabled this scanline, proactively clear it
		// so the power-on test pattern from initialization doesn't visually linger and
		// confuse debugging (otherwise the old pixels remain untouched).
		bool bgEnabled = (PPUMASK & 0x08) != 0; // bit 3
		bool sprEnabled = (PPUMASK & 0x10) != 0; // bit 4
		if (!bgEnabled && !sprEnabled)
		{
			EnsureFrameBuffer();
			// Universal background color
			byte ubIdx = paletteRAM[0];
			int p = (ubIdx & 0x3F) * 3;
			byte r = PaletteBytes[p]; byte g = PaletteBytes[p+1]; byte b = PaletteBytes[p+2];
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
	int ubp = (ubIdx & 0x3F) * 3;
	byte ubR = PaletteBytes[ubp];
	byte ubG = PaletteBytes[ubp+1];
	byte ubB = PaletteBytes[ubp+2];

		ushort renderV = v;

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
					int p = (idx & 0x3F) * 3;
				fb![frameIndex + 0] = PaletteBytes[p];
				fb![frameIndex + 1] = PaletteBytes[p+1];
				fb![frameIndex + 2] = PaletteBytes[p+2];
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
		int p = (idx & 0x3F) * 3;
		return (PaletteBytes[p], PaletteBytes[p+1], PaletteBytes[p+2]);
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
		int p = (idx & 0x3F) * 3;
		return (PaletteBytes[p], PaletteBytes[p+1], PaletteBytes[p+2]);
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
					addrLatch = false; // Reset address latch
					return result;
				}
			case 0x0004: // OAM Data
				result = oam[OAMADDR];
				RefreshPpuOpenBus(result);
				return result;
			case 0x0007: // PPU Data
				if (PPUADDR >= 0x3F00)
				{
					// Palette reads are immediate, but the internal read buffer still gets
					// refilled with the underlying nametable byte "under" the palette mirror -
					// a real hardware quirk (a bare Read(PPUADDR) here would refill the buffer
					// with the palette byte itself, corrupting the next non-palette $2007 read).
					byte pal = Read(PPUADDR);
					// Greyscale (PPUMASK bit 0) masks the value on the way OUT of palette RAM -
					// it never affects what gets written. Palette RAM itself only drives 6 bits;
					// the top 2 come from the PPU I/O bus, same as $2002's low bits above.
					if ((PPUMASK & 0x01) != 0) pal &= 0x30;
					result = (byte)((ReadPpuOpenBus() & 0xC0) | (pal & 0x3F));
					ppuDataBuffer = Read((ushort)(PPUADDR - 0x1000));
				}
				else
				{
					result = ppuDataBuffer;
					ppuDataBuffer = Read(PPUADDR);
				}
				RefreshPpuOpenBus(result);
				PPUADDR += (ushort)((PPUCTRL & 0x04) != 0 ? 32 : 1);
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
				break;
			case 0x0002: // PPU Status
				// $2002 is read-only: a write only resets the address/scroll latch (below) and
				// drives open bus - it must NOT clear VBlank. Only a READ clears bit 7 (see the
				// corresponding case in ReadPPURegister).
				scrollLatch = false;
				break;
			case 0x0003: // OAM Address
				OAMADDR = value;
				break;
			case 0x0004: // OAM Data
				OAMDATA = value;
				oam[OAMADDR++] = OAMDATA;
				break;
			case 0x0005: // PPU Scroll
				if (!scrollLatch)
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
				scrollLatch = !scrollLatch;
				break;
			case 0x0006: // PPU Address
				if (!addrLatch)
				{
					t = (ushort)((value << 8) | (t & 0x00FF));
					PPUADDR = t;
				}
				else
				{
					t = (ushort)((t & 0xFF00) | value);
					PPUADDR = t;
					v = t;
				}
				addrLatch = !addrLatch;
				break;
			case 0x0007: // PPU Data
				PPUDATA = value;
				Write(PPUADDR, PPUDATA);
				PPUADDR += (ushort)((PPUCTRL & 0x04) != 0 ? 32 : 1);
				v = PPUADDR;
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

	private void CopyXFromTToV()
	{
		v = (ushort)((v & 0xFBE0) | (t & 0x041F));
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

	public object GetState() {
		// Do NOT serialize the large framebuffer; it can be regenerated. This keeps saves small and fast.
		return new PpuSharedState {
			vram=(byte[])vram.Clone(),
			palette=(byte[])paletteRAM.Clone(),
			oam=(byte[])oam.Clone(),
			// frame omitted intentionally
			PPUCTRL=PPUCTRL,PPUMASK=PPUMASK,PPUSTATUS=PPUSTATUS,OAMADDR=OAMADDR,
			PPUSCROLLX=PPUSCROLLX,PPUSCROLLY=PPUSCROLLY,PPUDATA=PPUDATA,PPUADDR=PPUADDR,
			fineX=fineX,scrollLatch=scrollLatch,addrLatch=addrLatch,v=v,t=t,
			scanline=scanline,scanlineCycle=scanlineCycle, ppuDataBuffer=ppuDataBuffer,
			staticFrameCounter=staticFrameCounter
		};
	}
	public void SetState(object state) {
		if (state is PpuSharedState s) {
			vram = (byte[])s.vram.Clone(); paletteRAM=(byte[])s.palette.Clone(); oam=(byte[])s.oam.Clone();
			// Legacy compatibility: if a frame is present and matches expected length, copy it; otherwise leave empty
			if (s.frame != null && s.frame.Length == ScreenWidth * ScreenHeight * 4) { EnsureFrameBuffer(); frameBuffer = (byte[])s.frame.Clone(); }
			PPUCTRL=s.PPUCTRL;PPUMASK=s.PPUMASK;PPUSTATUS=s.PPUSTATUS;OAMADDR=s.OAMADDR;PPUSCROLLX=s.PPUSCROLLX;PPUSCROLLY=s.PPUSCROLLY;PPUDATA=s.PPUDATA;PPUADDR=s.PPUADDR;fineX=s.fineX;scrollLatch=s.scrollLatch;addrLatch=s.addrLatch;v=s.v; t=s.t; scanline=s.scanline; scanlineCycle=s.scanlineCycle; ppuDataBuffer=s.ppuDataBuffer; staticFrameCounter=s.staticFrameCounter; return; }
		if (state is System.Text.Json.JsonElement je) {
			if (je.TryGetProperty("vram", out var pVram)) { if (pVram.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pVram.EnumerateArray()){ if(i>=vram.Length) break; vram[i++]=(byte)el.GetInt32(); } } else if (pVram.ValueKind==System.Text.Json.JsonValueKind.String) { try { var b=pVram.GetBytesFromBase64(); Array.Copy(b,vram,Math.Min(b.Length, vram.Length)); } catch {} } }
			if (je.TryGetProperty("palette", out var pPal)) { if (pPal.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pPal.EnumerateArray()){ if(i>=paletteRAM.Length) break; paletteRAM[i++]=(byte)el.GetInt32(); } } else if (pPal.ValueKind==System.Text.Json.JsonValueKind.String) { try { var b=pPal.GetBytesFromBase64(); Array.Copy(b,paletteRAM,Math.Min(b.Length, paletteRAM.Length)); } catch {} } }
			if (je.TryGetProperty("oam", out var pOam)) { if (pOam.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pOam.EnumerateArray()){ if(i>=oam.Length) break; oam[i++]=(byte)el.GetInt32(); } } else if (pOam.ValueKind==System.Text.Json.JsonValueKind.String) { try { var b=pOam.GetBytesFromBase64(); Array.Copy(b,oam,Math.Min(b.Length, oam.Length)); } catch {} } }
			if (je.TryGetProperty("frame", out var pFrame) && pFrame.ValueKind==System.Text.Json.JsonValueKind.Array) { EnsureFrameBuffer(); int i=0; foreach(var el in pFrame.EnumerateArray()){ if(i>=frameBuffer!.Length) break; frameBuffer![i++]=(byte)el.GetInt32(); } }
			byte GetB(string name){return je.TryGetProperty(name,out var p)?(byte)p.GetInt32():(byte)0;} ushort GetU16(string name){return je.TryGetProperty(name,out var p)?(ushort)p.GetInt32():(ushort)0;}
			PPUCTRL=GetB("PPUCTRL");PPUMASK=GetB("PPUMASK");PPUSTATUS=GetB("PPUSTATUS");OAMADDR=GetB("OAMADDR");PPUSCROLLX=GetB("PPUSCROLLX");PPUSCROLLY=GetB("PPUSCROLLY");PPUDATA=GetB("PPUDATA");PPUADDR=GetU16("PPUADDR");fineX=GetB("fineX");scrollLatch=je.TryGetProperty("scrollLatch", out var psl)&&psl.GetBoolean();addrLatch=je.TryGetProperty("addrLatch", out var pal)&&pal.GetBoolean();v=GetU16("v");t=GetU16("t");if(je.TryGetProperty("scanline",out var psl2)) scanline=psl2.GetInt32(); if(je.TryGetProperty("scanlineCycle",out var psc)) scanlineCycle=psc.GetInt32(); if(je.TryGetProperty("ppuDataBuffer", out var pdb)) ppuDataBuffer=(byte)pdb.GetInt32();
		}
	}
}
}
