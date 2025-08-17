namespace NesEmulator
{
// Renamed original concrete PPU implementation to PPU_FMC. This file now hosts the FMC core logic.
public class PPU_SPD : IPPU
{
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

	// Lazy framebuffer allocation to reduce startup memory; allocate on first use
	private byte[]? frameBuffer = null;
	// Reusable arrays to avoid per-scanline allocations
	private readonly bool[] spritePixelDrawnReuse = new bool[ScreenWidth];
	// Removed unused staticLfsr field (was reserved for future static effect)
	private int staticFrameCounter = 0;

	// Speedhack: pattern line expansion cache (tile row -> packed 2-bit color indices)
	// 512 tiles (256 per pattern table) * 8 rows each
	private readonly ulong[] patternRowCache = new ulong[512 * 8];
	private readonly bool[] patternRowValid = new bool[512 * 8];

	public PPU_SPD(Bus bus)
	{
		this.bus = bus;

		vram = new byte[2048];
		paletteRAM = new byte[32];
		oam = new byte[256];

		// Initialize palette RAM with some default values
		InitializeDefaultPalette();

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
		for (int c = 0; c < elapsedCycles; c++)
		{
			if (scanline == 0 && scanlineCycle == 0)
			{
				PPUSTATUS &= 0x3F;
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

				if (scanline == 241)
				{
					PPUSTATUS |= 0x80;
					if ((PPUCTRL & 0x80) != 0)
					{
						bus.cpu.RequestNMI();
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
		int ubp = (ubIdx & 0x3F) * 3;
		byte ubR = PaletteBytes[ubp];
		byte ubG = PaletteBytes[ubp+1];
		byte ubB = PaletteBytes[ubp+2];
		int scanlineBaseAll = scanline * ScreenWidth * 4;

		ushort renderV = v;
		bool usePatternCache = bus?.SpeedConfig?.PpuPatternCache == true; // toggle
		for (int tile = 0; tile < 33; tile++)
		{
			int coarseX = renderV & 0x001F;
			int coarseY = (renderV >> 5) & 0x001F;
			int nameTable = (renderV >> 10) & 0x0003;
			int baseNTAddr = 0x2000 + (nameTable * 0x400);
			int tileAddr = baseNTAddr + (coarseY * 32) + coarseX;
			byte tileIndex = Read((ushort)tileAddr);
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
					byte plane0 = Read((ushort)patternAddr);
					byte plane1 = Read((ushort)(patternAddr + 8));
					ulong bits = 0UL;
					for (int k = 0; k < 8; k++)
					{
						int bitIndex = 7 - k;
						int bit0 = (plane0 >> bitIndex) & 1;
						int bit1 = (plane1 >> bitIndex) & 1;
						int cidx = bit0 | (bit1 << 1);
						bits |= (ulong)cidx << (k * 2);
					}
					patternRowCache[rowIndex] = bits;
					patternRowValid[rowIndex] = true;
					rowBits = bits;
				}
				else
				{
					rowBits = patternRowCache[rowIndex];
				}
			}
			else
			{
				int patternAddr = patternTable + (tileIndex * 16) + fineY;
				byte plane0 = Read((ushort)patternAddr);
				byte plane1 = Read((ushort)(patternAddr + 8));
				ulong bits = 0UL;
				for (int k = 0; k < 8; k++)
				{
					int bitIndex = 7 - k;
					int bit0 = (plane0 >> bitIndex) & 1;
					int bit1 = (plane1 >> bitIndex) & 1;
					int cidx = bit0 | (bit1 << 1);
					bits |= (ulong)cidx << (k * 2);
				}
				rowBits = bits;
			}

			int attributeX = coarseX / 4;
			int attributeY = coarseY / 4;
			int attrAddr = baseNTAddr + 0x3C0 + attributeY * 8 + attributeX;
			byte attrByte = Read((ushort)attrAddr);
			int attrShift = ((coarseY % 4) / 2) * 4 + ((coarseX % 4) / 2) * 2;
			int paletteIndex = (attrByte >> attrShift) & 0x03;
			int scanlineBase = scanlineBaseAll;

			for (int i = 0; i < 8; i++)
			{
				int pixel = tile * 8 + i - fineX;
				if ((uint)pixel >= ScreenWidth) continue;
				int colorIndex = (int)((rowBits >> (i * 2)) & 0x3);
				int frameIndex = scanlineBase + pixel * 4;
				if (colorIndex == 0)
				{
					frameBuffer![frameIndex + 0] = ubR;
					frameBuffer![frameIndex + 1] = ubG;
					frameBuffer![frameIndex + 2] = ubB;
					frameBuffer![frameIndex + 3] = 255;
				}
				else
				{
					bgMask[pixel] = true;
					int paletteBase = 1 + (paletteIndex << 2);
					byte idx = paletteRAM[(paletteBase + colorIndex - 1) & 0x1F];
					int p = (idx & 0x3F) * 3;
					frameBuffer![frameIndex + 0] = PaletteBytes[p];
					frameBuffer![frameIndex + 1] = PaletteBytes[p+1];
					frameBuffer![frameIndex + 2] = PaletteBytes[p+2];
					frameBuffer![frameIndex + 3] = 255;
				}
			}

			IncrementX(ref renderV);
		}
	}

	private void RenderSprites(int scanline, bool[] bgMask)
	{
		// Check if sprite rendering is enabled
		bool showSprites = (PPUMASK & 0x10) != 0;
		if (!showSprites) return;

		EnsureFrameBuffer();

		bool isSprite8x16 = (PPUCTRL & 0x20) != 0;
		Array.Clear(spritePixelDrawnReuse, 0, spritePixelDrawnReuse.Length);

		// Process all 64 sprites in OAM
		for (int i = 0; i < 64; i++)
		{
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

			// Check if sprite is on this scanline
			int tileHeight = isSprite8x16 ? 16 : 8;
			if (scanline < spriteY || scanline >= spriteY + tileHeight)
				continue;

			// Calculate which row of the sprite we're rendering
			int subY = scanline - spriteY;
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

				// Sprite 0 hit detection
				if (i == 0 && bgMask[px] && color != 0)
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
					if (frameIndex + 3 < frameBuffer!.Length)
					{
						frameBuffer![frameIndex + 0] = spriteColor.r;
						frameBuffer![frameIndex + 1] = spriteColor.g;
						frameBuffer![frameIndex + 2] = spriteColor.b;
						frameBuffer![frameIndex + 3] = 255;
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
		{
			return bus.cartridge.PPURead(address);
		}
		else if (address >= 0x2000 && address <= 0x3EFF)
		{
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
			// Invalidate pattern cache for modified tile (CHR RAM only; harmless for CHR ROM)
			InvalidatePatternAddress(address);
		}
		else if (address >= 0x2000 && address <= 0x3EFF)
		{
			ushort mirrored = MirrorVRAMAddress(address);
			vram[mirrored] = value;
		}
		else if (address >= 0x3F00 && address <= 0x3FFF)
		{
			ushort mirrored = (ushort)(address & 0x1F);
			if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10;
			paletteRAM[mirrored] = value;
		}
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
			if (je.TryGetProperty("vram", out var pVram) && pVram.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pVram.EnumerateArray()){ if(i>=vram.Length) break; vram[i++]=(byte)el.GetInt32(); } }
			if (je.TryGetProperty("palette", out var pPal) && pPal.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pPal.EnumerateArray()){ if(i>=paletteRAM.Length) break; paletteRAM[i++]=(byte)el.GetInt32(); } }
			if (je.TryGetProperty("oam", out var pOam) && pOam.ValueKind==System.Text.Json.JsonValueKind.Array) { int i=0; foreach(var el in pOam.EnumerateArray()){ if(i>=oam.Length) break; oam[i++]=(byte)el.GetInt32(); } }
			if (je.TryGetProperty("frame", out var pFrame) && pFrame.ValueKind==System.Text.Json.JsonValueKind.Array) { EnsureFrameBuffer(); int i=0; foreach(var el in pFrame.EnumerateArray()){ if(i>=frameBuffer!.Length) break; frameBuffer![i++]=(byte)el.GetInt32(); } }
			byte GetB(string name){return je.TryGetProperty(name,out var p)?(byte)p.GetInt32():(byte)0;} ushort GetU16(string name){return je.TryGetProperty(name,out var p)?(ushort)p.GetInt32():(ushort)0;}
			PPUCTRL=GetB("PPUCTRL");PPUMASK=GetB("PPUMASK");PPUSTATUS=GetB("PPUSTATUS");OAMADDR=GetB("OAMADDR");PPUSCROLLX=GetB("PPUSCROLLX");PPUSCROLLY=GetB("PPUSCROLLY");PPUDATA=GetB("PPUDATA");PPUADDR=GetU16("PPUADDR");fineX=GetB("fineX");scrollLatch=je.TryGetProperty("scrollLatch", out var psl)&&psl.GetBoolean();addrLatch=je.TryGetProperty("addrLatch", out var pal)&&pal.GetBoolean();v=GetU16("v");t=GetU16("t");if(je.TryGetProperty("scanline",out var psl2)) scanline=psl2.GetInt32(); if(je.TryGetProperty("scanlineCycle",out var psc)) scanlineCycle=psc.GetInt32(); if(je.TryGetProperty("ppuDataBuffer", out var pdb)) ppuDataBuffer=(byte)pdb.GetInt32();
		}
	}
}
}
