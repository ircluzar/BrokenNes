using System;
using System.Runtime.InteropServices;
namespace NesEmulator
{
// Renamed original concrete PPU implementation to PPU_FMC. This file now hosts the FMC core logic.
public class PPU_SPD : IPPU
{
	// Core metadata (new IPPU contract)
	public string CoreName => "Speedhacks";
	public string Description => "Based on the Low Power (LOW) core, this variant adds speedhacks for faster emulation.";
	public int Performance => 25;
	public int Rating => 5;
	public string Category => "Enhanced";
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

	private byte fineX; //x
	private bool scrollLatch; //w
	private ushort v; //current VRAM address
	private ushort t; //temp VRAM address

	private int scanlineCycle;
	private int scanline;

	// Cached nametable mirroring map (0x1000 bytes covering $2000-$2FFF)
	private readonly ushort[] ntMirror = new ushort[0x1000];
	private Mirroring lastMirroringMode; // track last mode to rebuild map only when changed

	// Lazy framebuffer allocation to reduce startup memory; allocate on first use
	private byte[]? frameBuffer = null;
	// Reusable arrays to avoid per-scanline allocations
	private readonly bool[] spritePixelDrawnReuse = new bool[ScreenWidth];
	// Reusable list for sprite indices visible on a given scanline (sprite evaluation)
	private readonly int[] spriteLineList = new int[64];
	private int staticFrameCounter = 0;

	// Speedhack: pattern line expansion cache (tile row -> packed 2-bit color indices)
	// 512 tiles (256 per pattern table) * 8 rows each
	private readonly ulong[] patternRowCache = new ulong[512 * 8];
	private readonly bool[] patternRowValid = new bool[512 * 8];
	private uint lastChrSignature = 0; // dynamic CHR banking signature for cache invalidation

	// Speedhack: scanline tile batching buffers (33 tiles: 32 visible + 1 overflow)
	private readonly ulong[] batchRowBits = new ulong[33];
	private readonly byte[] batchPaletteIndex = new byte[33];
	private bool batchAllZero; // helper flag for blank scanline fast fill

	// Pre-split RGB palette components for faster inner-loop access
	private static readonly byte[] PaletteR;
	private static readonly byte[] PaletteG;
	private static readonly byte[] PaletteB;
	// Packed RGBA (little endian) 0xAABBGGRR for direct uint stores (alpha always 255)
	private static readonly uint[] PaletteRGBA; // length 64

	// Palette entry RGBA cache (32 palette RAM bytes -> packed RGBA) + dirty flags
	private readonly uint[] paletteEntryCache = new uint[32];
	private readonly bool[] paletteEntryDirty = new bool[32];

	// Sprite pattern row cache (optional) separate from BG to allow independent invalidation policy
	private readonly ulong[] spritePatternRowCache = new ulong[512 * 8];
	private readonly bool[] spritePatternRowValid = new bool[512 * 8];

	// Lookup table: expands a pattern plane byte into 16 bits (stored sparsely in a 64-bit)
	// Each pixel i (0..7) contributes its bit as the LSB of a 2-bit pair at (i*2).
	// To combine planes: rowBits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1);
	private static readonly ulong[] PlaneExpand = new ulong[256];

	static PPU_SPD()
	{
		PaletteR = new byte[64]; PaletteG = new byte[64]; PaletteB = new byte[64]; PaletteRGBA = new uint[64];
		for (int i = 0; i < 64; i++)
		{
			int p = i * 3;
			byte r = PaletteBytes[p]; byte g = PaletteBytes[p+1]; byte b = PaletteBytes[p+2];
			PaletteR[i] = r; PaletteG[i] = g; PaletteB[i] = b;
			// In memory frameBuffer uses little endian order R,G,B,A sequential bytes; pack for uint writes
			PaletteRGBA[i] = (uint)r | ((uint)g << 8) | ((uint)b << 16) | 0xFF000000u;
		}
		// Build plane expansion table (once)
		for (int v = 0; v < 256; v++)
		{
			ulong exp = 0UL;
			for (int k = 0; k < 8; k++)
			{
				int bitIndex = 7 - k; // NES bit order: leftmost pixel is bit 7
				if (((v >> bitIndex) & 1) != 0)
					exp |= 1UL << (k * 2); // set LSB of 2-bit color slot
			}
			PlaneExpand[v] = exp;
		}
	}

	public PPU_SPD(Bus bus)
	{
		this.bus = bus;

		vram = new byte[2048];
		paletteRAM = new byte[32];
		oam = new byte[256];

		// Initialize palette RAM with some default values
		InitializeDefaultPalette();
		// Ensure palette cache starts valid (all dirty so first fetch builds entries)
		for (int i = 0; i < 32; i++) paletteEntryDirty[i] = true;

		PPUADDR = 0x0000;
		PPUCTRL = 0x00;
		PPUSTATUS = 0x00;
		PPUMASK = 0x00;

		ppuDataBuffer = 0x00;

		scanlineCycle = 0;
		scanline = 0;
		
		// Defer framebuffer allocation and any test pattern generation until first use
		lastMirroringMode = bus!.cartridge!.mirroringMode;
		RebuildNtMirror(lastMirroringMode);
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
		for (int c = 0; c < elapsedCycles; c++)
		{
			if (scanline == 0 && scanlineCycle == 0)
			{
				PPUSTATUS &= 0x3F;
			}

			// MMC5 (Mapper5) IRQ counter tick at early cycle 3 of visible scanlines when rendering is on
			if (scanline >= 0 && scanline < 240 && scanlineCycle == 3)
			{
				bool renderingOn = (PPUMASK & 0x18) != 0;
				if (bus!.cartridge!.mapper is Mapper5 mmc5)
				{
					mmc5.PpuScanlineHook(scanline, renderingOn);
					if (mmc5.IsIrqAsserted()) bus!.cpu!.RequestIRQ(true);
				}
			}

			if (scanline >= 0 && scanline < 240 && scanlineCycle == 260)
			{
				if ((PPUMASK & 0x18) != 0 && bus!.cartridge!.mapper is Mapper4)
				{
					Mapper4 mmc3 = (Mapper4)bus!.cartridge!.mapper;
					mmc3.RunScanlineIRQ();
					if (mmc3.IRQPending())
					{
						bus!.cpu!.RequestIRQ(true);
						mmc3.ClearIRQ();
					}
				}
			}

			scanlineCycle++;

			if (scanlineCycle >= 341)
			{
				scanlineCycle = 0;

				// Visible scanlines: perform operations in closer-to-hardware order:
				// 1. Render scanline using current v (fine X already latched)
				// 2. Increment Y (equivalent to cycle 256)
				// 3. Copy horizontal bits from t to v (equivalent to cycle 257)
				// NOTE: We keep a scanline-level granularity, but ordering matters for
				// games relying on split scrolling / MMC1 title effects (e.g., Zelda II).
				if (scanline >= 0 && scanline < 240)
				{
					RenderScanline(scanline);
					IncrementY();
					CopyXFromTToV();
				}

				if (scanline == 241)
				{
					PPUSTATUS |= 0x80;
					if ((PPUCTRL & 0x80) != 0)
					{
						bus!.cpu!.RequestNMI();
					}
				}

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

	bool[] bgMask = new bool[ScreenWidth];
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
			// Universal background color fast fill
			uint ub = PaletteRGBA[paletteRAM[0] & 0x3F];
			int baseIndex = scanline * ScreenWidth * 4;
			Span<byte> line = frameBuffer!.AsSpan(baseIndex, ScreenWidth * 4);
			var lineU32 = MemoryMarshal.Cast<byte,uint>(line);
			for (int i = 0; i < lineU32.Length; i++) lineU32[i] = ub;
			return; // nothing else to draw
		}

		// Clear scanline buffers
		Array.Clear(bgMask, 0, ScreenWidth);
		
		// Render background first (if enabled)
		if (bgEnabled) RenderBackground(scanline, bgMask);
		// Then render sprites on top (if enabled)
		if (sprEnabled) RenderSprites(scanline, bgMask);
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

		EnsureFrameBuffer();

		// Cache universal background color once per scanline.
		byte ubIdx = paletteRAM[0];
		int ubPal = ubIdx & 0x3F;
		byte ubR = PaletteR[ubPal];
		byte ubG = PaletteG[ubPal];
		byte ubB = PaletteB[ubPal];
		uint ubPacked = PaletteRGBA[ubPal];
		int scanlineBaseAll = scanline * ScreenWidth * 4;

		ushort renderV = v;
		var cfg = bus!.SpeedConfig; // snapshot
		bool usePatternCache = cfg?.PpuPatternCache == true;
		bool useBatch = cfg?.PpuTileBatching == true;
		bool skipBlank = cfg?.PpuSkipBlankScanlines == true;
		if (usePatternCache && bus!.cartridge!.mapper != null)
		{
			uint sig = bus!.cartridge!.mapper.GetChrBankSignature();
			if (sig != lastChrSignature)
			{
				System.Array.Clear(patternRowValid, 0, patternRowValid.Length);
				if (bus!.SpeedConfig?.PpuSpritePatternCache == true)
					System.Array.Clear(spritePatternRowValid, 0, spritePatternRowValid.Length);
				lastChrSignature = sig;
			}
		}

		// Rebuild nametable mirroring map if mapper changed mirroring mid-frame
		var curMode = bus!.cartridge!.mirroringMode;
		if (curMode != lastMirroringMode) { RebuildNtMirror(curMode); lastMirroringMode = curMode; }

		bool twoPass = useBatch && skipBlank; // only do expensive two-pass when blank skipping active
		// Tell mapper that background fetches are about to occur (MMC5 A/B CHR banking)
		if (bus!.cartridge!.mapper is IMapper mBg)
			mBg.PpuPhaseHint(false, (PPUCTRL & 0x20) != 0, (PPUMASK & 0x18) != 0);
		bool unsafeScan = cfg?.PpuUnsafeScanline == true;
		bool deferAttr = cfg?.PpuDeferAttributeFetch != false; // default on

		// Always begin by clearing the scanline to universal background color to avoid stale pixels
		{
			Span<uint> clearLine = MemoryMarshal.Cast<byte,uint>(frameBuffer!.AsSpan(scanlineBaseAll, ScreenWidth * 4));
			clearLine.Fill(ubPacked); // vectorized fill
		}
		if (twoPass)
		{
			batchAllZero = true;
			// First pass: decode metadata for 33 tiles
			for (int tile = 0; tile < 33; tile++)
			{
				int coarseX = renderV & 0x001F;
				int coarseY = (renderV >> 5) & 0x001F;
				int nameTable = (renderV >> 10) & 0x0003;
				int baseNTAddr = 0x2000 + (nameTable * 0x400);
				int tileAddr = baseNTAddr + (coarseY * 32) + coarseX;
				// Fast path: pattern index fetch (nametable region) - avoid full Read overhead
				byte tileIndex = vram[ntMirror[tileAddr & 0x0FFF]];
				int fineY = (renderV >> 12) & 0x7;
				int patternTable = (PPUCTRL & 0x10) != 0 ? 0x1000 : 0x0000;
				ulong rowBits;
				if (usePatternCache)
				{
					int globalTile = ((patternTable >> 12) & 1) * 256 + tileIndex; // 0..511
					int rowIndex = globalTile * 8 + fineY;
					if (!patternRowValid[rowIndex])
					{
						int patternAddr = patternTable + (tileIndex * 16) + fineY;
						byte plane0 = bus!.cartridge!.PPURead((ushort)patternAddr);
						byte plane1 = bus!.cartridge!.PPURead((ushort)(patternAddr + 8));
						ulong bits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1);
						patternRowCache[rowIndex] = bits; patternRowValid[rowIndex] = true; rowBits = bits;
					}
					else rowBits = patternRowCache[rowIndex];
				}
				else
				{
					int patternAddr = patternTable + (tileIndex * 16) + fineY;
					byte plane0 = bus!.cartridge!.PPURead((ushort)patternAddr);
					byte plane1 = bus!.cartridge!.PPURead((ushort)(patternAddr + 8));
					rowBits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1);
				}
				int attributeX = coarseX / 4;
				int attributeY = coarseY / 4;
				int attrAddr = baseNTAddr + 0x3C0 + attributeY * 8 + attributeX;
				byte attrByte = vram[ntMirror[attrAddr & 0x0FFF]]; // fast nametable attribute read
				int attrShift = ((coarseY % 4) / 2) * 4 + ((coarseX % 4) / 2) * 2;
				int paletteIndex = (attrByte >> attrShift) & 0x03;
				batchRowBits[tile] = rowBits;
				batchPaletteIndex[tile] = (byte)paletteIndex;
				if (rowBits != 0UL) batchAllZero = false;
				IncrementX(ref renderV);
			}
			// Fast fill if entire scanline row bits are zero (all colorIndex 0)
			if (skipBlank && batchAllZero)
			{
				int baseIndex = scanline * ScreenWidth * 4;
				Span<byte> line = frameBuffer!.AsSpan(baseIndex, ScreenWidth * 4);
				var lineU32 = MemoryMarshal.Cast<byte,uint>(line);
				for (int i = 0; i < lineU32.Length; i++) lineU32[i] = ubPacked;
				return; // nothing else to draw
			}
			// Second pass: render using packed 32-bit writes
			bool usePalCache = bus!.SpeedConfig?.PpuPaletteCache == true;
			Span<uint> lineU32Pass = MemoryMarshal.Cast<byte, uint>(frameBuffer!.AsSpan(scanlineBaseAll, ScreenWidth * 4));
			for (int tile = 0; tile < 33; tile++)
			{
				ulong rowBits = batchRowBits[tile];
				if (rowBits == 0UL) continue; // already cleared to ubPacked
				int paletteIndex = batchPaletteIndex[tile];
				uint p1=0,p2=0,p3=0; // color 0 uses ubPacked
				int paletteBase = 1 + (paletteIndex << 2);
				byte e1 = (byte)(paletteBase & 0x1F);
				byte e2 = (byte)((paletteBase + 1) & 0x1F);
				byte e3 = (byte)((paletteBase + 2) & 0x1F);
				p1 = usePalCache ? FetchPaletteEntryPacked(e1) : PaletteRGBA[paletteRAM[e1] & 0x3F];
				p2 = usePalCache ? FetchPaletteEntryPacked(e2) : PaletteRGBA[paletteRAM[e2] & 0x3F];
				p3 = usePalCache ? FetchPaletteEntryPacked(e3) : PaletteRGBA[paletteRAM[e3] & 0x3F];
				for (int i = 0; i < 8; i++)
				{
					int pixel = tile * 8 + i - fineX; if ((uint)pixel >= ScreenWidth) continue;
					int ci = (int)((rowBits >> (i * 2)) & 0x3);
					if (ci == 0) continue; // leave ubPacked
					bgMask[pixel] = true;
					uint packed = ci switch {1=>p1,2=>p2,3=>p3,_=>ubPacked};
					lineU32Pass[pixel] = packed;
				}
			}
		}
		else
		{
				// Single-pass path (with optional pattern cache)
				ushort rv2 = v;
				bool usePalCache2 = bus!.SpeedConfig?.PpuPaletteCache == true;
				Span<uint> lineU32 = MemoryMarshal.Cast<byte, uint>(frameBuffer!.AsSpan(scanlineBaseAll, ScreenWidth * 4));
				for (int tile = 0; tile < 33; tile++)
				{
					int coarseX = rv2 & 0x001F;
					int coarseY = (rv2 >> 5) & 0x001F;
					int nameTable = (rv2 >> 10) & 0x0003;
					int baseNTAddr = 0x2000 + (nameTable * 0x400);
					int tileAddr = baseNTAddr + (coarseY * 32) + coarseX;
					byte tileIndex = vram[ntMirror[tileAddr & 0x0FFF]]; // fast nametable fetch
					int fineY = (rv2 >> 12) & 0x7;
					int patternTable = (PPUCTRL & 0x10) != 0 ? 0x1000 : 0x0000;
					int patternAddr = patternTable + (tileIndex * 16) + fineY;
					ulong rowBits;
					if (usePatternCache)
					{
						int globalTile = ((patternTable >> 12) & 1) * 256 + tileIndex;
						int rowIndex = globalTile * 8 + fineY;
						if (!patternRowValid[rowIndex])
						{
							byte plane0 = bus!.cartridge!.PPURead((ushort)patternAddr);
							byte plane1 = bus!.cartridge!.PPURead((ushort)(patternAddr + 8));
							ulong bits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1);
							patternRowCache[rowIndex] = bits; patternRowValid[rowIndex] = true; rowBits = bits;
						}
						else rowBits = patternRowCache[rowIndex];
					}
					else
					{
								byte plane0 = bus!.cartridge!.PPURead((ushort)patternAddr);
								byte plane1 = bus!.cartridge!.PPURead((ushort)(patternAddr + 8));
							rowBits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1);
					}
					int paletteIndex = 0;
					if (!deferAttr || rowBits != 0UL)
					{
						int attributeX = coarseX / 4;
						int attributeY = coarseY / 4;
						int attrAddr = baseNTAddr + 0x3C0 + attributeY * 8 + attributeX;
						byte attrByte = vram[ntMirror[attrAddr & 0x0FFF]];
						int attrShift = ((coarseY % 4) / 2) * 4 + ((coarseX % 4) / 2) * 2;
						paletteIndex = (attrByte >> attrShift) & 0x03;
					}
					if (rowBits == 0UL)
					{
						IncrementX(ref rv2);
						continue; // all background color
					}
					uint p1=0,p2=0,p3=0; // lazy init
					for (int i = 0; i < 8; i++)
					{
						int pixel = tile * 8 + i - fineX; if ((uint)pixel >= ScreenWidth) continue;
						int colorIndex = (int)((rowBits >> (i * 2)) & 0x3);
						if (colorIndex == 0) { lineU32[pixel] = ubPacked; continue; }
						bgMask[pixel] = true;
						if (p1==0 && p2==0 && p3==0)
						{
							int basePal = 1 + (paletteIndex << 2);
							byte e1=(byte)(basePal & 0x1F); byte e2=(byte)((basePal+1)&0x1F); byte e3=(byte)((basePal+2)&0x1F);
							p1 = usePalCache2 ? FetchPaletteEntryPacked(e1) : PaletteRGBA[paletteRAM[e1] & 0x3F];
							p2 = usePalCache2 ? FetchPaletteEntryPacked(e2) : PaletteRGBA[paletteRAM[e2] & 0x3F];
							p3 = usePalCache2 ? FetchPaletteEntryPacked(e3) : PaletteRGBA[paletteRAM[e3] & 0x3F];
						}
						lineU32[pixel] = colorIndex switch {1=>p1,2=>p2,3=>p3,_=>ubPacked};
					}
					IncrementX(ref rv2);
				}
			}
		// End RenderBackground
	}

	private void RenderSprites(int scanline, bool[] bgMask)
	{
		bool showSprites = (PPUMASK & 0x10) != 0; if (!showSprites) return;
		EnsureFrameBuffer();
		bool isSprite8x16 = (PPUCTRL & 0x20) != 0;
		// Tell mapper that sprite fetches are about to occur (MMC5 A/B CHR banking)
		if (bus!.cartridge!.mapper is IMapper mSpr)
			mSpr.PpuPhaseHint(true, isSprite8x16, (PPUMASK & 0x18) != 0);
		Array.Clear(spritePixelDrawnReuse, 0, spritePixelDrawnReuse.Length);
		var cfg = bus!.SpeedConfig;
		bool eval = cfg?.PpuSpriteLineEvaluation != false;
		bool usePatternCache = cfg?.PpuSpritePatternCache == true && bus!.cartridge!.mapper != null;
		bool fastSprite = cfg?.PpuSpriteFastPath == true;
		bool palCache = cfg?.PpuPaletteCache == true;
		int spritesToDraw = 64;
		if (eval)
		{
			int count = 0; bool overflow = false;
			for (int i = 0; i < 64; i++)
			{
				int off = i * 4; byte sY = oam[off]; int tileH = isSprite8x16 ? 16 : 8;
				if (scanline < sY || scanline >= sY + tileH) continue;
				if (count < 8) spriteLineList[count++] = i; else { overflow = true; break; }
			}
			if (overflow) PPUSTATUS |= 0x20; spritesToDraw = count;
			Span<uint> spriteLineU32 = MemoryMarshal.Cast<byte,uint>(frameBuffer!.AsSpan(scanline * ScreenWidth * 4, ScreenWidth * 4));
			for (int si = 0; si < spritesToDraw; si++)
			{
				int i = spriteLineList[si]; int off = i * 4;
				byte spriteY = oam[off]; byte tileIndex = oam[off + 1]; byte attributes = oam[off + 2]; byte spriteX = oam[off + 3];
				int paletteIndex = attributes & 0x03; bool flipX = (attributes & 0x40) != 0; bool flipY = (attributes & 0x80) != 0; bool priority = (attributes & 0x20) == 0;
				int tileH = isSprite8x16 ? 16 : 8; int subY = scanline - spriteY; if (flipY) subY = tileH - 1 - subY;
				int subTileIndex = isSprite8x16 ? (tileIndex & 0xFE) + (subY / 8) : tileIndex;
				int patternTable = isSprite8x16 ? ((tileIndex & 1) != 0 ? 0x1000 : 0x0000) : ((PPUCTRL & 0x08) != 0 ? 0x1000 : 0x0000);
				int baseAddr = patternTable + subTileIndex * 16; ushort rowAddr = (ushort)(baseAddr + (subY % 8));
				byte plane0, plane1; ulong rowBits = 0UL; int fineY = subY % 8;
		    if (usePatternCache)
				{
					int globalTile = ((patternTable >> 12) & 1) * 256 + subTileIndex; int rowIndex = globalTile * 8 + fineY;
					if (!spritePatternRowValid[rowIndex])
					{
			    plane0 = bus!.cartridge!.PPURead(rowAddr); plane1 = bus!.cartridge!.PPURead((ushort)(rowAddr + 8));
						ulong bits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1); spritePatternRowCache[rowIndex]=bits; spritePatternRowValid[rowIndex]=true; rowBits = bits;
					}
					else rowBits = spritePatternRowCache[rowIndex];
				}
				else
				{
			    plane0 = bus!.cartridge!.PPURead(rowAddr); plane1 = bus!.cartridge!.PPURead((ushort)(rowAddr + 8));
						rowBits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1);
				}
				uint pal1=0, pal2=0, pal3=0; if (fastSprite){int basePal = 0x11 + (paletteIndex << 2); pal1 = FetchPaletteEntryPacked((byte)basePal); pal2 = FetchPaletteEntryPacked((byte)(basePal+1)); pal3 = FetchPaletteEntryPacked((byte)(basePal+2)); }
				for (int x = 0; x < 8; x++)
				{
					int srcPixel = flipX ? (7 - x) : x; int color = (int)((rowBits >> (srcPixel * 2)) & 0x3); if (color==0) continue; int px = spriteX + x; if ((uint)px >= ScreenWidth) continue;
					if (i==0 && bgMask[px]) PPUSTATUS |= 0x40; if (spritePixelDrawnReuse[px]) continue; if (!priority && bgMask[px]) continue;
					uint packed;
					if (fastSprite) packed = color switch {1=>pal1,2=>pal2,3=>pal3,_=>pal1};
					else { int palBase = 0x11 + (paletteIndex << 2); byte idx = paletteRAM[palBase + (color -1)]; packed = PaletteRGBA[idx & 0x3F]; }
					spriteLineU32[px] = packed;
					spritePixelDrawnReuse[px]=true;
				}
			}
			return;
		}
		// Legacy full sprite pass (still used when evaluation disabled)
		for (int i = 0; i < 64; i++)
		{
			int off = i*4; byte spriteY = oam[off]; byte tileIndex = oam[off+1]; byte attributes = oam[off+2]; byte spriteX = oam[off+3];
			int paletteIndex = attributes & 0x03; bool flipX = (attributes & 0x40)!=0; bool flipY=(attributes & 0x80)!=0; bool priority=(attributes & 0x20)==0;
			int tileH=isSprite8x16?16:8; if (scanline < spriteY || scanline >= spriteY+tileH) continue;
			int subY = scanline - spriteY; if (flipY) subY = tileH -1 - subY; int subTileIndex = isSprite8x16 ? (tileIndex & 0xFE)+(subY/8) : tileIndex;
			int patternTable = isSprite8x16 ? ((tileIndex & 1)!=0?0x1000:0x0000) : ((PPUCTRL & 0x08)!=0?0x1000:0x0000);
			int baseAddr = patternTable + subTileIndex*16; ushort rowAddr = (ushort)(baseAddr + (subY % 8)); byte plane0, plane1; ulong rowBits; int fineY = subY % 8;
			if (usePatternCache)
			{
				int globalTile=((patternTable>>12)&1)*256+subTileIndex; int rowIndex=globalTile*8+fineY;
				if(!spritePatternRowValid[rowIndex]){plane0=bus!.cartridge!.PPURead(rowAddr); plane1=bus!.cartridge!.PPURead((ushort)(rowAddr+8)); ulong bits=PlaneExpand[plane0] | (PlaneExpand[plane1] << 1); spritePatternRowCache[rowIndex]=bits; spritePatternRowValid[rowIndex]=true; rowBits=bits;} else rowBits = spritePatternRowCache[rowIndex];
			}
			else { plane0=bus!.cartridge!.PPURead(rowAddr); plane1=bus!.cartridge!.PPURead((ushort)(rowAddr+8)); rowBits = PlaneExpand[plane0] | (PlaneExpand[plane1] << 1); }
			uint pal1=0,pal2=0,pal3=0; if(fastSprite){int basePal=0x11 + (paletteIndex<<2); pal1=FetchPaletteEntryPacked((byte)basePal); pal2=FetchPaletteEntryPacked((byte)(basePal+1)); pal3=FetchPaletteEntryPacked((byte)(basePal+2)); }
			Span<uint> legacyLineU32 = MemoryMarshal.Cast<byte,uint>(frameBuffer!.AsSpan(scanline * ScreenWidth * 4, ScreenWidth * 4));
			for(int x=0;x<8;x++){int srcPixel = flipX ? (7 - x) : x; int color=(int)((rowBits>>(srcPixel*2)) & 0x3); if(color==0) continue; int px=spriteX+x; if((uint)px>=ScreenWidth) continue; if(i==0 && bgMask[px]) PPUSTATUS |= 0x40; if(spritePixelDrawnReuse[px]) continue; if(!priority && bgMask[px]) continue; uint packed; if(fastSprite){packed = color switch {1=>pal1,2=>pal2,3=>pal3,_=>pal1};} else {int palBase=0x11+(paletteIndex<<2); byte idx=paletteRAM[palBase + (color-1)]; packed=PaletteRGBA[idx & 0x3F];} legacyLineU32[px]=packed; spritePixelDrawnReuse[px]=true; }
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
				result = PPUSTATUS;
				PPUSTATUS &= 0x3F; // Clear VBlank flag on read
				addrLatch = false; // Reset address latch
				return result;
			case 0x0004: // OAM Data
				return oam[OAMADDR];
			case 0x0007: // PPU Data
				result = ppuDataBuffer;
				ppuDataBuffer = Read(PPUADDR);
				
				if (PPUADDR >= 0x3F00)
				{
					result = ppuDataBuffer;
				}
				
				PPUADDR += (ushort)((PPUCTRL & 0x04) != 0 ? 32 : 1);
				return result;
			default:
				return 0;
		}
	}

	public void WritePPURegister(ushort address, byte value)
	{
		switch (address & 0x0007)
		{
			case 0x0000: // PPU Control
				PPUCTRL = value;
				t = (ushort)((t & 0xF3FF) | ((value & 0x03) << 10));
				break;
			case 0x0001: // PPU Mask
				PPUMASK = value;
				break;
			case 0x0002: // PPU Status
				PPUSTATUS &= 0x7F;
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
			return bus!.cartridge!.PPURead(address);
		if (address < 0x3F00)
			return vram[ntMirror[address & 0x0FFF]]; // nametable & mirrors
		ushort mirrored = (ushort)(address & 0x1F);
		if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10;
		return paletteRAM[mirrored];
	}

	public void Write(ushort address, byte value)
	{
		address = (ushort)(address & 0x3FFF);
		if (address < 0x2000)
		{
			bus!.cartridge!.PPUWrite(address, value);
			InvalidatePatternAddress(address); // CHR RAM invalidation
			return;
		}
		if (address < 0x3F00)
		{
			vram[ntMirror[address & 0x0FFF]] = value; return;
		}
		ushort mirrored = (ushort)(address & 0x1F);
		if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10;
		paletteRAM[mirrored] = value;
		if (bus?.SpeedConfig?.PpuPaletteCache == true) paletteEntryDirty[mirrored] = true;
	}

	private void InvalidatePatternAddress(ushort address)
	{
		if (bus?.SpeedConfig?.PpuPatternCache != true) return;
		// Pattern tables at $0000-$1FFF (two 4KB tables). Each tile = 16 bytes.
		int tileIndex = address / 16; // 0..511
		if ((uint)tileIndex >= 512) return;
		// If writing only one byte of a tile row, we conservatively invalidate all 8 rows of the tile.
		int baseRow = tileIndex * 8;
		for (int r = 0; r < 8; r++) patternRowValid[baseRow + r] = false;
		if (bus?.SpeedConfig?.PpuSpritePatternCache == true)
			for (int r = 0; r < 8; r++) spritePatternRowValid[baseRow + r] = false;
	}

	private void RebuildNtMirror(Mirroring mode)
	{
		for (int offset = 0; offset < 0x1000; offset++)
		{
			int ntIndex = offset / 0x400;
			int innerOffset = offset & 0x3FF;
			ushort mapped = mode switch
			{
				Mirroring.Vertical => (ushort)(((ntIndex & 1) * 0x400) + innerOffset),
				Mirroring.Horizontal => (ushort)(((ntIndex >> 1) * 0x400) + innerOffset),
				Mirroring.SingleScreenA => (ushort)innerOffset,
				Mirroring.SingleScreenB => (ushort)(0x400 + innerOffset),
				_ => (ushort)offset
			};
			ntMirror[offset] = mapped;
		}
	}

	public void WriteOAMDMA(byte page)
	{
		bus!.FastOamDma(page, oam, ref OAMADDR);
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

	private void RefreshAllPaletteCache()
	{
		if (bus?.SpeedConfig?.PpuPaletteCache == true)
		{
			for (int i = 0; i < 32; i++) paletteEntryDirty[i] = true; // lazy fill on demand
		}
	}

	// Obtain packed RGBA from paletteRAM index with caching if enabled
	private uint FetchPaletteEntryPacked(byte paletteIndex)
	{
		if (bus?.SpeedConfig?.PpuPaletteCache == true)
		{
			if (paletteEntryDirty[paletteIndex])
			{
				byte idx = paletteRAM[paletteIndex]; int ci = idx & 0x3F; paletteEntryCache[paletteIndex] = PaletteRGBA[ci]; paletteEntryDirty[paletteIndex] = false;
			}
			return paletteEntryCache[paletteIndex];
		}
		return PaletteRGBA[paletteRAM[paletteIndex] & 0x3F];
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
			PPUCTRL=s.PPUCTRL;PPUMASK=s.PPUMASK;PPUSTATUS=s.PPUSTATUS;OAMADDR=s.OAMADDR;PPUSCROLLX=s.PPUSCROLLX;PPUSCROLLY=s.PPUSCROLLY;PPUDATA=s.PPUDATA;PPUADDR=s.PPUADDR;fineX=s.fineX;scrollLatch=s.scrollLatch;addrLatch=s.addrLatch;v=s.v; t=s.t; scanline=s.scanline; scanlineCycle=s.scanlineCycle; ppuDataBuffer=s.ppuDataBuffer; staticFrameCounter=s.staticFrameCounter; RefreshAllPaletteCache(); return; }
		if (state is System.Text.Json.JsonElement je) {
			if (je.TryGetProperty("vram", out var pVram) && pVram.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pVram.EnumerateArray()){ if(i>=vram.Length) break; vram[i++]=(byte)el.GetInt32(); } }
			if (je.TryGetProperty("palette", out var pPal) && pPal.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pPal.EnumerateArray()){ if(i>=paletteRAM.Length) break; paletteRAM[i++]=(byte)el.GetInt32(); } }
			if (je.TryGetProperty("oam", out var pOam) && pOam.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pOam.EnumerateArray()){ if(i>=oam.Length) break; oam[i++]=(byte)el.GetInt32(); } }
			if (je.TryGetProperty("frame", out var pFrame) && pFrame.ValueKind==System.Text.Json.JsonValueKind.Array) { EnsureFrameBuffer(); int i=0; foreach(var el in pFrame.EnumerateArray()){ if(i>=frameBuffer!.Length) break; frameBuffer![i++]=(byte)el.GetInt32(); } }
			byte GetB(string name){return je.TryGetProperty(name,out var p)?(byte)p.GetInt32():(byte)0;} ushort GetU16(string name){return je.TryGetProperty(name,out var p)?(ushort)p.GetInt32():(ushort)0;}
			PPUCTRL=GetB("PPUCTRL");PPUMASK=GetB("PPUMASK");PPUSTATUS=GetB("PPUSTATUS");OAMADDR=GetB("OAMADDR");PPUSCROLLX=GetB("PPUSCROLLX");PPUSCROLLY=GetB("PPUSCROLLY");PPUDATA=GetB("PPUDATA");PPUADDR=GetU16("PPUADDR");fineX=GetB("fineX");scrollLatch=je.TryGetProperty("scrollLatch", out var psl)&&psl.GetBoolean();addrLatch=je.TryGetProperty("addrLatch", out var pal)&&pal.GetBoolean();v=GetU16("v");t=GetU16("t");if(je.TryGetProperty("scanline",out var psl2)) scanline=psl2.GetInt32(); if(je.TryGetProperty("scanlineCycle",out var psc)) scanlineCycle=psc.GetInt32(); if(je.TryGetProperty("ppuDataBuffer", out var pdb)) ppuDataBuffer=(byte)pdb.GetInt32(); RefreshAllPaletteCache();
		}
	}
}
}
