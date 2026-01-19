namespace NesEmulator
{
/// <summary>
/// PPU_CUBEX - Enhanced CUBE core with frame interpolation for smoother animations.
/// Implements automatic sprite/tile clustering, motion detection, and color morphing
/// to generate in-between frames using actual palette colors (not alpha blending).
/// </summary>
public class PPU_CUBEX : IPPU
{
	// Core metadata
	public string CoreName => "Cubex PPU";
	public string Description => "Enhanced CUBE core with frame smoothing. Detects sprite motion and applies color morphing between frames for smoother animations.";
	public int Performance => -8;
	public int Rating => 5;
	public string Category => "Experimental";
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

	// Lazy framebuffer allocation
	private byte[]? frameBuffer = null;
	// Palette resolved RGB cache
	private readonly byte[] paletteResolved = new byte[32*3];
	private bool paletteCacheBuilt = false;
	// Gradient cache
	private readonly byte[] gradientR = new byte[ScreenHeight];
	private readonly byte[] gradientG = new byte[ScreenHeight];
	private readonly byte[] gradientB = new byte[ScreenHeight];
	private int lastGradientBaseColor = -1;
	private bool gradientCacheValid = false;
	// Reusable arrays
	private readonly bool[] spritePixelDrawnReuse = new bool[ScreenWidth];
	// Shadow configuration
	private const int ShadowVerticalDistance = 1;
	private const int ShadowOffsetX = -1;
	private const int ShadowOffsetY = 1;
	private const float ShadowTransparency = 0.69f;
	private const float ShadowOpacity = 1f - ShadowTransparency;
	// Coverage histories
	private readonly byte[] spriteCoverageRows = new byte[ShadowVerticalDistance * ScreenWidth];
	private readonly byte[] bgCoverageRows = new byte[ShadowVerticalDistance * ScreenWidth];
	private int staticFrameCounter = 0;

	// ============================================================================
	// CUBEX FRAME SMOOTHING SYSTEM (Optimized)
	// ============================================================================
	
	// Previous frame data for comparison
	private byte[]? prevFrameBuffer = null;
	
	// Previous sprite positions for motion detection
	private readonly byte[] prevSpriteX = new byte[64];
	private readonly byte[] prevSpriteY = new byte[64];
	private readonly byte[] prevSpriteTile = new byte[64];
	
	// Motion vectors per sprite (simple dx, dy)
	private readonly sbyte[] spriteDeltaX = new sbyte[64];
	private readonly sbyte[] spriteDeltaY = new sbyte[64];
	private readonly bool[] spriteHasMotion = new bool[64];
	
	// Per-pixel tracking for motion trails
	private readonly byte[] pixelAge = new byte[ScreenWidth * ScreenHeight]; // How many frames since pixel changed
	private readonly byte[] prevPixelR = new byte[ScreenWidth * ScreenHeight];
	private readonly byte[] prevPixelG = new byte[ScreenWidth * ScreenHeight];
	private readonly byte[] prevPixelB = new byte[ScreenWidth * ScreenHeight];
	
	// Frame counter
	private int totalFramesProcessed = 0;
	
	// Morphing intensity (higher = more visible effect)
	private const int MorphFrames = 3; // Number of frames to morph over
	private const float MorphStrength = 0.6f; // How much of the morph to apply

	public PPU_CUBEX(Bus bus)
	{
		this.bus = bus;

		vram = new byte[2048];
		paletteRAM = new byte[32];
		oam = new byte[256];

		InitializeDefaultPalette();

		PPUADDR = 0x0000;
		PPUCTRL = 0x00;
		PPUSTATUS = 0x00;
		PPUMASK = 0x00;

		ppuDataBuffer = 0x00;

		scanlineCycle = 0;
		scanline = 0;
		
		RebuildResolvedPalette();
		paletteCacheBuilt = true;
		
		// Initialize smoothing system - no heavy setup needed
	}

	private void EnsureFrameBuffer()
	{
		if (frameBuffer == null || frameBuffer.Length != ScreenWidth * ScreenHeight * 4)
		{
			frameBuffer = new byte[ScreenWidth * ScreenHeight * 4];
		}
		if (prevFrameBuffer == null || prevFrameBuffer.Length != ScreenWidth * ScreenHeight * 4)
		{
			prevFrameBuffer = new byte[ScreenWidth * ScreenHeight * 4];
		}
	}

	public void Step(int elapsedCycles)
	{
		for (int c = 0; c < elapsedCycles; c++)
		{
			if (scanline == 0 && scanlineCycle == 0)
			{
				PPUSTATUS &= 0x3F;
				int ub = paletteRAM[0];
				if (!gradientCacheValid || ub != lastGradientBaseColor)
				{
					BuildGradientCache();
				}
				System.Array.Clear(spriteCoverageRows, 0, spriteCoverageRows.Length);
				System.Array.Clear(bgCoverageRows, 0, bgCoverageRows.Length);
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

			// MMC5 scanline counter tick
			if (scanline >= 0 && scanline < 240 && scanlineCycle == 3)
			{
				if (bus.cartridge.mapper is Mapper5 mmc5)
				{
					bool renderingEnabled = (PPUMASK & 0x18) != 0;
					mmc5.PpuScanlineHook(scanline, renderingEnabled);
					if (mmc5.IsIrqAsserted())
					{
						bus.cpu.RequestIRQ(true);
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
					
					// End of frame - apply smoothing
					OnFrameComplete();
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
	
	// ============================================================================
	// FRAME SMOOTHING CORE (Optimized for performance)
	// ============================================================================
	
	private void OnFrameComplete()
	{
		EnsureFrameBuffer();
		
		if (totalFramesProcessed > 0)
		{
			// Detect sprite motion
			DetectSpriteMotion();
			
			// Apply color morphing for changed pixels
			ApplyColorMorphing();
		}
		
		// Store current frame as previous
		Array.Copy(frameBuffer!, 0, prevFrameBuffer!, 0, frameBuffer!.Length);
		
		// Store current sprite positions
		for (int i = 0; i < 64; i++)
		{
			int offset = i * 4;
			prevSpriteX[i] = oam[offset + 3];
			prevSpriteY[i] = oam[offset];
			prevSpriteTile[i] = oam[offset + 1];
		}
		
		totalFramesProcessed++;
	}
	
	private void DetectSpriteMotion()
	{
		for (int i = 0; i < 64; i++)
		{
			int offset = i * 4;
			byte currX = oam[offset + 3];
			byte currY = oam[offset];
			byte currTile = oam[offset + 1];
			
			int dx = currX - prevSpriteX[i];
			int dy = currY - prevSpriteY[i];
			
			// Handle wrap-around
			if (dx > 128) dx -= 256;
			if (dx < -128) dx += 256;
			if (dy > 120) dy -= 240;
			if (dy < -120) dy += 240;
			
			// Clamp to sbyte range
			dx = Math.Clamp(dx, -127, 127);
			dy = Math.Clamp(dy, -127, 127);
			
			spriteDeltaX[i] = (sbyte)dx;
			spriteDeltaY[i] = (sbyte)dy;
			
			// Motion is valid if sprite moved but not too far
			spriteHasMotion[i] = (dx != 0 || dy != 0) && 
			                     Math.Abs(dx) <= 16 && Math.Abs(dy) <= 16 &&
			                     currY < 240 && currY > 0;
		}
	}
	
	private void ApplyColorMorphing()
	{
		// Simple per-pixel morphing: if a pixel changed color, find an intermediate
		// NES palette color to smooth the transition
		
		int pixelCount = ScreenWidth * ScreenHeight;
		
		for (int i = 0; i < pixelCount; i++)
		{
			int fbIdx = i * 4;
			
			byte currR = frameBuffer![fbIdx];
			byte currG = frameBuffer![fbIdx + 1];
			byte currB = frameBuffer![fbIdx + 2];
			
			byte prevR = prevPixelR[i];
			byte prevG = prevPixelG[i];
			byte prevB = prevPixelB[i];
			
			// Check if pixel color changed significantly
			int colorDiff = Math.Abs(currR - prevR) + Math.Abs(currG - prevG) + Math.Abs(currB - prevB);
			
			if (colorDiff > 40) // Significant change
			{
				// Check if this pixel is in a sprite's motion path
				bool inMotionPath = IsPixelInSpritePath(i % ScreenWidth, i / ScreenWidth);
				
				if (inMotionPath || pixelAge[i] < MorphFrames)
				{
					// Apply morphing - blend towards previous color using NES palette
					byte morphR, morphG, morphB;
					float t = inMotionPath ? MorphStrength : (MorphStrength * 0.5f);
					
					MorphToNearestPaletteColor(
						prevR, prevG, prevB,
						currR, currG, currB,
						t, out morphR, out morphG, out morphB);
					
					frameBuffer![fbIdx] = morphR;
					frameBuffer![fbIdx + 1] = morphG;
					frameBuffer![fbIdx + 2] = morphB;
				}
				
				// Reset age for changed pixels
				pixelAge[i] = 0;
			}
			else if (pixelAge[i] < 255)
			{
				pixelAge[i]++;
			}
			
			// Store for next frame
			prevPixelR[i] = currR;
			prevPixelG[i] = currG;
			prevPixelB[i] = currB;
		}
	}
	
	private bool IsPixelInSpritePath(int px, int py)
	{
		bool isSprite8x16 = (PPUCTRL & 0x20) != 0;
		int spriteHeight = isSprite8x16 ? 16 : 8;
		
		for (int i = 0; i < 64; i++)
		{
			if (!spriteHasMotion[i]) continue;
			
			int offset = i * 4;
			int spriteX = oam[offset + 3];
			int spriteY = oam[offset];
			
			// Check current sprite bounds
			if (px >= spriteX && px < spriteX + 8 &&
			    py >= spriteY && py < spriteY + spriteHeight)
			{
				return true;
			}
			
			// Check motion trail (where sprite was)
			int prevX = spriteX - spriteDeltaX[i];
			int prevY = spriteY - spriteDeltaY[i];
			
			if (px >= prevX && px < prevX + 8 &&
			    py >= prevY && py < prevY + spriteHeight)
			{
				return true;
			}
		}
		
		return false;
	}
	
	/// <summary>
	/// Find an intermediate NES palette color between two colors.
	/// t=0 means use 'from' color, t=1 means use 'to' color.
	/// </summary>
	private void MorphToNearestPaletteColor(
		byte fromR, byte fromG, byte fromB,
		byte toR, byte toG, byte toB,
		float t, out byte outR, out byte outG, out byte outB)
	{
		// Calculate target RGB (linear interpolation)
		int targetR = (int)(fromR + (toR - fromR) * (1f - t));
		int targetG = (int)(fromG + (toG - fromG) * (1f - t));
		int targetB = (int)(fromB + (toB - fromB) * (1f - t));
		
		// Find nearest NES palette color
		int bestIdx = 0;
		int bestDist = int.MaxValue;
		
		for (int i = 0; i < 64; i++)
		{
			int p = i * 3;
			int dr = targetR - PaletteBytes[p];
			int dg = targetG - PaletteBytes[p + 1];
			int db = targetB - PaletteBytes[p + 2];
			
			// Weighted distance (human eye more sensitive to green)
			int dist = dr * dr * 2 + dg * dg * 4 + db * db * 3;
			
			if (dist < bestDist)
			{
				bestDist = dist;
				bestIdx = i;
			}
		}
		
		int fp = bestIdx * 3;
		outR = PaletteBytes[fp];
		outG = PaletteBytes[fp + 1];
		outB = PaletteBytes[fp + 2];
	}

	// ============================================================================
	// RENDERING (inherited from CUBE with interpolation hooks)
	// ============================================================================

	bool[] bgMask = new bool[ScreenWidth];
	private void RenderScanline(int scanline)
	{
		EnsureFrameBuffer();

		if (bus?.cartridge == null)
		{
			FillFullGradientScanline(scanline);
			return;
		}

		bool bgEnabled = (PPUMASK & 0x08) != 0;
		bool sprEnabled = (PPUMASK & 0x10) != 0;

		Array.Clear(bgMask, 0, ScreenWidth);
		
		if (bgEnabled) RenderBackground(scanline, bgMask);
		if (sprEnabled) RenderSprites(scanline, bgMask);
	}

	private void FillFullGradientScanline(int y)
	{
		int baseIndex = y * ScreenWidth * 4;
		byte r = gradientR[y]; byte g = gradientG[y]; byte b = gradientB[y];
		for (int x = 0; x < ScreenWidth; x++)
		{
			int fi = baseIndex + (x << 2);
			frameBuffer![fi + 0] = r;
			frameBuffer![fi + 1] = g;
			frameBuffer![fi + 2] = b;
			frameBuffer![fi + 3] = 255;
		}
	}

	private void BuildGradientCache()
	{
		byte ubIdx = paletteRAM[0];
		lastGradientBaseColor = ubIdx;
		int pTop = (ubIdx & 0x3F) * 3;
		byte topR = PaletteBytes[pTop];
		byte topG = PaletteBytes[pTop+1];
		byte topB = PaletteBytes[pTop+2];
		double brightness = 0.299 * topR + 0.587 * topG + 0.114 * topB;
		byte botR, botG, botB;
		if (brightness >= 128)
		{
			botR = (byte)(topR + (int)((255 - topR) * 0.25));
			botG = (byte)(topG + (int)((255 - topG) * 0.25));
			botB = (byte)(topB + (int)((255 - topB) * 0.25));
		}
		else
		{
			botR = (byte)(topR * 0.5);
			botG = (byte)(topG * 0.5);
			botB = (byte)(topB * 0.5);
		}
		for (int y = 0; y < ScreenHeight; y++)
		{
			float tt = ScreenHeight <= 1 ? 0f : (float)y / (ScreenHeight - 1);
			gradientR[y] = (byte)(topR + (int)((botR - topR) * tt));
			gradientG[y] = (byte)(topG + (int)((botG - topG) * tt));
			gradientB[y] = (byte)(topB + (int)((botB - topB) * tt));
		}
		gradientCacheValid = true;
	}

	public byte[] GetFrameBuffer() { EnsureFrameBuffer(); return frameBuffer!; }

	public void ClearBuffers()
	{
		frameBuffer = null;
		prevFrameBuffer = null;
		paletteCacheBuilt = false;
		totalFramesProcessed = 0;
		
		System.Array.Clear(spriteCoverageRows, 0, spriteCoverageRows.Length);
		System.Array.Clear(bgCoverageRows, 0, bgCoverageRows.Length);
		System.Array.Clear(pixelAge, 0, pixelAge.Length);
		System.Array.Clear(prevPixelR, 0, prevPixelR.Length);
		System.Array.Clear(prevPixelG, 0, prevPixelG.Length);
		System.Array.Clear(prevPixelB, 0, prevPixelB.Length);
		System.Array.Clear(prevSpriteX, 0, prevSpriteX.Length);
		System.Array.Clear(prevSpriteY, 0, prevSpriteY.Length);
		System.Array.Clear(spriteHasMotion, 0, spriteHasMotion.Length);
	}

	public void GenerateStaticFrame()
	{
		EnsureFrameBuffer();
		uint frameSeed = (uint)staticFrameCounter * 0x9E3779B1u + 0xB5297A4Du;
		for (int y = 0; y < ScreenHeight; y++)
		{
			uint rowSeed = frameSeed ^ (uint)(y * 0x1F123BB5u);
			for (int x = 0; x < ScreenWidth; x++)
			{
				uint h0 = rowSeed ^ (uint)(x * 0xA24BAEDCu);
				h0 ^= h0 >> 15; h0 *= 0x2C1B3C6Du;
				h0 ^= h0 >> 12; h0 *= 0x297A2D39u;
				h0 ^= h0 >> 15;
				byte intensity = (byte)(h0 >> 24);
				byte baseGray = intensity;
				// Cyan/blue tint for CUBEX
				byte pr = (byte)(intensity / 4);
				byte pg = (byte)(40 + (intensity * 3) / 5);
				byte pb = (byte)(60 + (intensity * 4) / 5);
				byte r = (byte)((baseGray * 3 + pr) / 4);
				byte g = (byte)((baseGray * 3 + pg) / 4);
				byte b = (byte)((baseGray * 3 + pb) / 4);
				if ((h0 & 0x7FF) == 0) { r = g = b = 255; }
				int idx = (y * ScreenWidth + x) * 4;
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
		if (bus?.cartridge == null)
		{
			AddAnimatedTestElements();
		}
	}

	private void RenderBackground(int scanline, bool[] bgMask)
	{
		EnsureFrameBuffer();
		bool bgEnabledFlag = (PPUMASK & 0x08) != 0;
		if (!bgEnabledFlag)
		{
			FillFullGradientScanline(scanline);
			return;
		}

		byte gradR = gradientR[scanline];
		byte gradG = gradientG[scanline];
		byte gradB = gradientB[scanline];
		int gradBase = scanline * ScreenWidth * 4;
		for (int gx = 0; gx < ScreenWidth; gx++)
		{
			int gi = gradBase + (gx << 2);
			frameBuffer![gi + 0] = gradR;
			frameBuffer![gi + 1] = gradG;
			frameBuffer![gi + 2] = gradB;
			frameBuffer![gi + 3] = 255;
		}

		if (scanline >= ShadowVerticalDistance)
		{
			int srcRow = (scanline - ShadowVerticalDistance) % ShadowVerticalDistance;
			int srcBase = srcRow * ScreenWidth;
			int shadowBase = scanline * ScreenWidth * 4;
			for (int x = 0; x < ScreenWidth; x++)
			{
				if (bgCoverageRows[srcBase + x] == 0) continue;
				int sx = x + ShadowOffsetX;
				if ((uint)sx >= ScreenWidth) continue;
				int fi = shadowBase + (sx << 2);
				frameBuffer![fi + 0] = (byte)((frameBuffer![fi + 0] * 69) / 100);
				frameBuffer![fi + 1] = (byte)((frameBuffer![fi + 1] * 69) / 100);
				frameBuffer![fi + 2] = (byte)((frameBuffer![fi + 2] * 69) / 100);
			}
		}

		int coverageRowIndex = scanline % ShadowVerticalDistance;
		int bgRowBase = coverageRowIndex * ScreenWidth;
		for (int cx = 0; cx < ScreenWidth; cx++) bgCoverageRows[bgRowBase + cx] = 0;

		ushort renderV = v;

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
			int patternAddr = patternTable + (tileIndex * 16) + fineY;
			
			byte plane0 = Read((ushort)patternAddr);
			byte plane1 = Read((ushort)(patternAddr + 8));

			int attributeX = coarseX / 4;
			int attributeY = coarseY / 4;
			int attrAddr = baseNTAddr + 0x3C0 + attributeY * 8 + attributeX;
			byte attrByte = Read((ushort)attrAddr);

			int attrShift = ((coarseY % 4) / 2) * 4 + ((coarseX % 4) / 2) * 2;
			int paletteIndex = (attrByte >> attrShift) & 0x03;

			int scanlineBase = scanline * ScreenWidth * 4;

			if (!paletteCacheBuilt) { RebuildResolvedPalette(); paletteCacheBuilt = true; }
			for (int i = 0; i < 8; i++)
			{
				int pixel = tile * 8 + i - fineX;
				if ((uint)pixel >= ScreenWidth) continue;
				int bit = 1 << (7 - i);
				int colorIndex = ((plane0 & bit) != 0 ? 1 : 0) | (((plane1 & bit) != 0 ? 1 : 0) << 1);
				if (colorIndex == 0) continue;
				bgMask[pixel] = true;
				bgCoverageRows[bgRowBase + pixel] = 1;
				int palEntry = (1 + (paletteIndex << 2) + colorIndex - 1) & 0x1F;
				int pBase = palEntry * 3;
				int frameIndex = scanlineBase + (pixel << 2);
				frameBuffer![frameIndex + 0] = paletteResolved[pBase + 0];
				frameBuffer![frameIndex + 1] = paletteResolved[pBase + 1];
				frameBuffer![frameIndex + 2] = paletteResolved[pBase + 2];
				frameBuffer![frameIndex + 3] = 255;
			}

			IncrementX(ref renderV);
		}
	}

	private void RenderSprites(int scanline, bool[] bgMask)
	{
		EnsureFrameBuffer();
		bool showSprites = (PPUMASK & 0x10) != 0;
		if (!showSprites) return;

		if (scanline >= ShadowVerticalDistance)
		{
			int sourceRowIndex = (scanline - ShadowVerticalDistance) % ShadowVerticalDistance;
			int srcBase = sourceRowIndex * ScreenWidth;
			int shadowY = scanline;
			if (shadowY < ScreenHeight)
			{
				int shadowBase = shadowY * ScreenWidth * 4;
				for (int x = 0; x < ScreenWidth; x++)
				{
					if (spriteCoverageRows[srcBase + x] == 0) continue;
					int sx = x + ShadowOffsetX;
					if (sx < 0 || sx >= ScreenWidth) continue;
					int fi = shadowBase + sx * 4;
					frameBuffer![fi + 0] = (byte)((frameBuffer![fi + 0] * 69) / 100);
					frameBuffer![fi + 1] = (byte)((frameBuffer![fi + 1] * 69) / 100);
					frameBuffer![fi + 2] = (byte)((frameBuffer![fi + 2] * 69) / 100);
				}
			}
		}

		int coverageRowIndex = scanline % ShadowVerticalDistance;
		int spriteRowBase = coverageRowIndex * ScreenWidth;
		for (int cx = 0; cx < ScreenWidth; cx++) spriteCoverageRows[spriteRowBase + cx] = 0;

		bool isSprite8x16 = (PPUCTRL & 0x20) != 0;
		Array.Clear(spritePixelDrawnReuse, 0, spritePixelDrawnReuse.Length);

		if (!paletteCacheBuilt) { RebuildResolvedPalette(); paletteCacheBuilt = true; }
		for (int i = 0; i < 64; i++)
		{
			int offset = i * 4;
			byte spriteY = oam[offset];
			byte tileIndex = oam[offset + 1];
			byte attributes = oam[offset + 2];
			byte spriteX = oam[offset + 3];

			int paletteIndex = attributes & 0b11;
			bool flipX = (attributes & 0x40) != 0;
			bool flipY = (attributes & 0x80) != 0;
			bool priority = (attributes & 0x20) == 0;

			int tileHeight = isSprite8x16 ? 16 : 8;
			if (scanline < spriteY || scanline >= spriteY + tileHeight)
				continue;

			int subY = scanline - spriteY;
			if (flipY) subY = tileHeight - 1 - subY;

			int subTileIndex = isSprite8x16 ? (tileIndex & 0xFE) + (subY / 8) : tileIndex;
			int patternTable = isSprite8x16
				? ((tileIndex & 1) != 0 ? 0x1000 : 0x0000)
				: ((PPUCTRL & 0x08) != 0 ? 0x1000 : 0x0000);
			int baseAddr = patternTable + subTileIndex * 16;

			byte plane0 = Read((ushort)(baseAddr + (subY % 8)));
			byte plane1 = Read((ushort)(baseAddr + (subY % 8) + 8));

			for (int x = 0; x < 8; x++)
			{
				int bit = flipX ? x : 7 - x;
				int bit0 = (plane0 >> bit) & 1;
				int bit1 = (plane1 >> bit) & 1;
				int color = bit0 | (bit1 << 1);
				if (color == 0) continue;

				int px = spriteX + x;
				if (px < 0 || px >= ScreenWidth) continue;

				if (i == 0 && bgMask[px] && color != 0)
				{
					PPUSTATUS |= 0x40;
				}

				if (spritePixelDrawnReuse[px]) continue;

				bool shouldDraw = true;
				if (!priority && bgMask[px])
				{
					shouldDraw = false;
				}

				if (!shouldDraw) continue;

				int palBase = 0x11 + (paletteIndex << 2) + color - 1;
				palBase &= 0x1F;
				int pBase = palBase * 3;
				spriteCoverageRows[spriteRowBase + px] = 1;

				int frameIndex = (scanline * ScreenWidth + px) * 4;
				if (frameIndex + 3 < frameBuffer!.Length)
				{
					frameBuffer![frameIndex + 0] = paletteResolved[pBase + 0];
					frameBuffer![frameIndex + 1] = paletteResolved[pBase + 1];
					frameBuffer![frameIndex + 2] = paletteResolved[pBase + 2];
					frameBuffer![frameIndex + 3] = 255;
				}
				spritePixelDrawnReuse[px] = true;
			}
		}
	}

	private void AddAnimatedTestElements()
	{
		EnsureFrameBuffer();
		int frame = scanline + scanlineCycle / 100;
		
		for (int i = 0; i < 4; i++)
		{
			int spriteX = (32 + i * 64 + frame * (i + 1)) % (ScreenWidth - 16);
			int spriteY = 200 + (int)(Math.Sin(frame * 0.1 + i) * 20);
			
			DrawTestSprite(spriteX, spriteY, i);
		}
		
		int scanLineY = (frame * 2) % ScreenHeight;
		for (int x = 0; x < ScreenWidth; x++)
		{
			int index = (scanLineY * ScreenWidth + x) * 4;
			if (index + 3 < frameBuffer!.Length)
			{
				frameBuffer![index + 0] = 255;
				frameBuffer![index + 1] = 255;
				frameBuffer![index + 2] = 255;
			}
		}
	}

	private void DrawTestSprite(int x, int y, int spriteType)
	{
		EnsureFrameBuffer();
		int[] indices = {0x0F,0x16,0x2A,0x12};
		int idx = indices[spriteType % 4] & 0x3F;
		int p = idx * 3;
		(byte r, byte g, byte b) color = (PaletteBytes[p], PaletteBytes[p+1], PaletteBytes[p+2]);
		
		for (int dy = 0; dy < 8; dy++)
		{
			for (int dx = 0; dx < 8; dx++)
			{
				int px = x + dx;
				int py = y + dy;
				
				if (px >= 0 && px < ScreenWidth && py >= 0 && py < ScreenHeight)
				{
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

	// ============================================================================
	// PPU REGISTER ACCESS
	// ============================================================================

	public byte ReadPPURegister(ushort address)
	{
		byte result = 0x00;

		switch (address & 0x0007)
		{
			case 0x0002:
				result = PPUSTATUS;
				PPUSTATUS &= 0x3F;
				addrLatch = false;
				return result;
			case 0x0004:
				return oam[OAMADDR];
			case 0x0007:
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
			case 0x0000:
				PPUCTRL = value;
				t = (ushort)((t & 0xF3FF) | ((value & 0x03) << 10));
				break;
			case 0x0001:
				PPUMASK = value;
				break;
			case 0x0002:
				PPUSTATUS &= 0x7F;
				scrollLatch = false;
				break;
			case 0x0003:
				OAMADDR = value;
				break;
			case 0x0004:
				OAMDATA = value;
				oam[OAMADDR++] = OAMDATA;
				break;
			case 0x0005:
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
			case 0x0006:
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
			case 0x0007:
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
			UpdateResolvedPaletteEntry(mirrored);
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

	private void InitializeDefaultPalette()
	{
		paletteRAM[0x00] = 0x0F;
		paletteRAM[0x01] = 0x00;
		paletteRAM[0x02] = 0x10;
		paletteRAM[0x03] = 0x30;
		
		paletteRAM[0x04] = 0x0F;
		paletteRAM[0x05] = 0x06;
		paletteRAM[0x06] = 0x16;
		paletteRAM[0x07] = 0x26;
		
		paletteRAM[0x08] = 0x0F;
		paletteRAM[0x09] = 0x0A;
		paletteRAM[0x0A] = 0x1A;
		paletteRAM[0x0B] = 0x2A;
		
		paletteRAM[0x0C] = 0x0F;
		paletteRAM[0x0D] = 0x02;
		paletteRAM[0x0E] = 0x12;
		paletteRAM[0x0F] = 0x22;
		
		paletteRAM[0x10] = 0x0F;
		paletteRAM[0x11] = 0x14;
		paletteRAM[0x12] = 0x24;
		paletteRAM[0x13] = 0x34;
		
		paletteRAM[0x14] = 0x0F;
		paletteRAM[0x15] = 0x07;
		paletteRAM[0x16] = 0x17;
		paletteRAM[0x17] = 0x27;
		
		paletteRAM[0x18] = 0x0F;
		paletteRAM[0x19] = 0x13;
		paletteRAM[0x1A] = 0x23;
		paletteRAM[0x1B] = 0x33;
		
		paletteRAM[0x1C] = 0x0F;
		paletteRAM[0x1D] = 0x15;
		paletteRAM[0x1E] = 0x25;
		paletteRAM[0x1F] = 0x35;
	}

	private void UpdateResolvedPaletteEntry(int i)
	{
		int eff = (i >= 0x10 && (i & 0x03) == 0) ? i - 0x10 : i;
		int idx = paletteRAM[eff] & 0x3F;
		int p = idx * 3; int rBase = i * 3;
		paletteResolved[rBase] = PaletteBytes[p];
		paletteResolved[rBase+1] = PaletteBytes[p+1];
		paletteResolved[rBase+2] = PaletteBytes[p+2];
	}

	private void RebuildResolvedPalette()
	{
		for(int i=0;i<32;i++) UpdateResolvedPaletteEntry(i);
	}

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
		return new PpuSharedState { 
			vram=(byte[])vram.Clone(), 
			palette=(byte[])paletteRAM.Clone(), 
			oam=(byte[])oam.Clone(), 
			PPUCTRL=PPUCTRL,
			PPUMASK=PPUMASK,
			PPUSTATUS=PPUSTATUS,
			OAMADDR=OAMADDR,
			PPUSCROLLX=PPUSCROLLX,
			PPUSCROLLY=PPUSCROLLY,
			PPUDATA=PPUDATA,
			PPUADDR=PPUADDR,
			fineX=fineX,
			scrollLatch=scrollLatch,
			addrLatch=addrLatch,
			v=v,
			t=t,
			scanline=scanline,
			scanlineCycle=scanlineCycle, 
			ppuDataBuffer=ppuDataBuffer, 
			staticFrameCounter=staticFrameCounter 
		};
	}
	
	public void SetState(object state) {
		if (state is PpuSharedState s) {
			vram = (byte[])s.vram.Clone(); 
			paletteRAM=(byte[])s.palette.Clone(); 
			oam=(byte[])s.oam.Clone();
			if (s.frame != null && s.frame.Length == ScreenWidth * ScreenHeight * 4) { 
				EnsureFrameBuffer(); 
				frameBuffer=(byte[])s.frame.Clone(); 
			}
			PPUCTRL=s.PPUCTRL;
			PPUMASK=s.PPUMASK;
			PPUSTATUS=s.PPUSTATUS;
			OAMADDR=s.OAMADDR;
			PPUSCROLLX=s.PPUSCROLLX;
			PPUSCROLLY=s.PPUSCROLLY;
			PPUDATA=s.PPUDATA;
			PPUADDR=s.PPUADDR;
			fineX=s.fineX;
			scrollLatch=s.scrollLatch;
			addrLatch=s.addrLatch;
			v=s.v; 
			t=s.t; 
			scanline=s.scanline; 
			scanlineCycle=s.scanlineCycle; 
			ppuDataBuffer=s.ppuDataBuffer; 
			staticFrameCounter=s.staticFrameCounter;
			
			// Reset smoothing state on load
			totalFramesProcessed = 0;
			return; 
		}
		if (state is System.Text.Json.JsonElement je) {
			if (je.TryGetProperty("vram", out var pVram) && pVram.ValueKind==System.Text.Json.JsonValueKind.Array) { 
				int i=0; foreach(var el in pVram.EnumerateArray()){ if(i>=vram.Length) break; vram[i++]=(byte)el.GetInt32(); } 
			}
			if (je.TryGetProperty("palette", out var pPal) && pPal.ValueKind==System.Text.Json.JsonValueKind.Array) { 
				int i=0; foreach(var el in pPal.EnumerateArray()){ if(i>=paletteRAM.Length) break; paletteRAM[i++]=(byte)el.GetInt32(); } 
			}
			if (je.TryGetProperty("oam", out var pOam) && pOam.ValueKind==System.Text.Json.JsonValueKind.Array) { 
				int i=0; foreach(var el in pOam.EnumerateArray()){ if(i>=oam.Length) break; oam[i++]=(byte)el.GetInt32(); } 
			}
			if (je.TryGetProperty("frame", out var pFrame) && pFrame.ValueKind==System.Text.Json.JsonValueKind.Array) { 
				EnsureFrameBuffer(); 
				int i=0; foreach(var el in pFrame.EnumerateArray()){ if(i>=frameBuffer!.Length) break; frameBuffer![i++]=(byte)el.GetInt32(); } 
			}
			byte GetB(string name){return je.TryGetProperty(name,out var p)?(byte)p.GetInt32():(byte)0;} 
			ushort GetU16(string name){return je.TryGetProperty(name,out var p)?(ushort)p.GetInt32():(ushort)0;}
			PPUCTRL=GetB("PPUCTRL");
			PPUMASK=GetB("PPUMASK");
			PPUSTATUS=GetB("PPUSTATUS");
			OAMADDR=GetB("OAMADDR");
			PPUSCROLLX=GetB("PPUSCROLLX");
			PPUSCROLLY=GetB("PPUSCROLLY");
			PPUDATA=GetB("PPUDATA");
			PPUADDR=GetU16("PPUADDR");
			fineX=GetB("fineX");
			scrollLatch=je.TryGetProperty("scrollLatch", out var psl)&&psl.GetBoolean();
			addrLatch=je.TryGetProperty("addrLatch", out var pal)&&pal.GetBoolean();
			v=GetU16("v");
			t=GetU16("t");
			if(je.TryGetProperty("scanline",out var psl2)) scanline=psl2.GetInt32(); 
			if(je.TryGetProperty("scanlineCycle",out var psc)) scanlineCycle=psc.GetInt32(); 
			if(je.TryGetProperty("ppuDataBuffer", out var pdb)) ppuDataBuffer=(byte)pdb.GetInt32();
			
			// Reset smoothing state on load
			totalFramesProcessed = 0;
		}
	}
}
}
