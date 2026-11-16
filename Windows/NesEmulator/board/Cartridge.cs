using System;
namespace NesEmulator
{
public class Cartridge
{
	public class UnsupportedMapperException : System.Exception {
		public int MapperId { get; }
		public string MapperName { get; }
		public UnsupportedMapperException(int id, string name)
			: base($"Unsupported mapper {id} ({name})") { MapperId=id; MapperName=name; }
	}
	public byte[] rom;
	public byte[] prgROM;
	public byte[] chrROM;
	public int prgBanks;
	public int chrBanks;
	public int mapperID;
	public bool mirrorHorizontal;
	public bool mirrorVertical;
	public Mirroring mirroringMode;
	public bool hasBattery;
	public bool hasTrainer;
	public byte[] prgRAM;
	public byte[] chrRAM;
	public IMapper mapper;

	private static string GetMapperName(int id) => id switch {
		0 => "NROM",
		1 => "MMC1",
		2 => "UNROM",
		3 => "CNROM",
		4 => "MMC3",
		33 => "Taito-TC0190 (Mapper33)",
		7 => "AxROM",
		9 => "MMC2",
			90 => "Mapper90",
			228 => "Action52 (Mapper228)",
			30 => "UNROM-512 (Mapper30)",
			11 => "Color Dreams (Mapper11)",
			41 => "Caltron 6-in-1 (Mapper41)",
			66 => "GxROM (Mapper66)",
			67 => "Sunsoft-3 (Mapper67)",
			68 => "Sunsoft-4 (Mapper68)",
			79 => "NINA-03/06 (Mapper79)",
			113 => "NINA/Sachen (Mapper113)",
			232 => "Camerica Quattro (Mapper232)",
			144 => "AGCI 50282 (Mapper144)",
		34 => "BNROM / NINA-001 (Mapper34)",
		64 => "Tengen RAMBO-1 (Mapper64)",
		69 => "Sunsoft FME-7 (Mapper69)",
		71 => "Camerica (Mapper71)",
		119 => "TQROM (Mapper119)",
		13 => "CPROM (Mapper13)",
		47 => "NES-QJ (Mapper47)",
		118 => "TxSROM (Mapper118)",
		_ => "Unknown"
	};

	public Cartridge(byte[] romData)
	{
		rom = romData;
		#if DIAG_LOG
		// Debug: Log ROM data information (stripped in Release for perf / noise reduction)
		Console.WriteLine($"ROM Data Length: {rom.Length}");
		if (rom.Length >= 16)
		{
			Console.WriteLine($"Header bytes: {rom[0]:X2} {rom[1]:X2} {rom[2]:X2} {rom[3]:X2}");
			Console.WriteLine($"Expected: 4E 45 53 1A (NES\\x1A)");
		}
		#endif
		
		if (rom.Length < 16)
		{
			throw new Exception($"ROM too small: {rom.Length} bytes. Need at least 16 bytes for iNES header.");
		}
		
		if (rom[0] != 0x4E || rom[1] != 0x45 || rom[2] != 0x53 || rom[3] != 0x1A)
		{
			throw new Exception($"Invalid iNES Header! Got: {rom[0]:X2} {rom[1]:X2} {rom[2]:X2} {rom[3]:X2}, Expected: 4E 45 53 1A");
		}
		prgBanks = rom[4];
		chrBanks = rom[5];
		byte flag6 = rom[6];
		byte flag7 = rom[7];
		mirrorVertical = (flag6 & 0x01) != 0;
		mirrorHorizontal = !mirrorVertical;
		hasBattery = (flag6 & 0x02) != 0;
		hasTrainer = (flag6 & 0x04) != 0;
		if ((flag6 & 0x08) != 0)
		{
			// Four-screen mirroring not implemented
		}
		else if ((flag6 & 0x01) != 0)
		{
			mirroringMode = Mirroring.Vertical;
		}
		else
		{
			mirroringMode = Mirroring.Horizontal;
		}
		mapperID = flag6 >> 4 | ((flag7 >> 4) << 4);
		// "Old iNES" (DiskDude-era) headers: byte 7 bits 2-3 = 01 or 11 is neither iNES (00) nor NES 2.0 (10), so its upper
		// nibble is junk, not the mapper's high bits - Mesen's and FCEUX's rule. Transformers (byte 7 = $44) otherwise reads
		// as mapper 67 instead of its real CNROM.
		if ((flag7 & 0x0C) == 0x04 || (flag7 & 0x0C) == 0x0C) mapperID = flag6 >> 4;
		int prgSize = prgBanks * 16 * 1024;
		int chrSize = chrBanks * 8 * 1024;
		int offset = 16; // iNES header size
		if (hasTrainer)
		{
			// Skip 512-byte trainer block (will map to $7000-$71FF if needed)
			if (rom.Length < offset + 512)
				throw new Exception("ROM truncated: trainer flag set but no 512-byte trainer present");
			offset += 512; // advance past trainer
		}
		{
			// Known dumps whose header names the wrong board (Mesen's game database agrees): CRC32 of PRG+CHR -> mapper.
			int bodyLen = Math.Min(prgSize + chrSize, rom.Length - offset);
			uint crc = Crc32(rom, offset, bodyLen);
			if (HeaderFixups.TryGetValue(crc, out int fixedMapper)) mapperID = fixedMapper;
		}
		prgROM = new byte[prgSize];
		Array.Copy(rom, offset, prgROM, 0, prgSize);
		offset += prgSize;
		if (chrBanks > 0)
		{
			chrROM = new byte[chrSize];
			Array.Copy(rom, offset, chrROM, 0, chrSize);
		}
		else
		{
			chrROM = new byte[0]; // No CHR-ROM
		}
		// MMC5 boards carry up to 64 KiB of work RAM (two 32 KiB chips, $5113 bits 0-2); an iNES 1.0 header cannot say
		// how much, so give the largest, as Mesen and FCEUX do. With 8 KiB every bank aliased onto one: Koei's EWROM/ETROM
		// games (Nobunaga's Ambition 2, Uncharted Waters, Genghis Khan...) keep their game state across several banks.
		prgRAM = new byte[mapperID == 5 ? 64 * 1024 : 8 * 1024];
		// Mapper 30 (UNROM-512) always uses CHR-RAM banked in 8KB windows, up to 32KB (4 banks) -
		// give it the extra room so the generic CHR-RAM path (save states, the RTC "CHR" corruption
		// domain, PeekChr/PokeChr) sees the same memory the mapper actually renders from, instead of
		// only ever touching a phantom, unbanked first 8KB.
		chrRAM = new byte[mapperID == 30 ? 32 * 1024 : mapperID == 13 ? 16 * 1024 : 8 * 1024];   // CPROM: 16KB

		// If trainer present, load it into PRG RAM at $7000-$71FF per iNES spec (offset 0x1000 into 8KB $6000-$7FFF range)
		if (hasTrainer)
		{
			int trainerSource = 16; // immediately after header
			for (int i = 0; i < 512; i++)
			{
				int dest = 0x1000 + i; // $7000 base relative to $6000
				if (dest < prgRAM.Length)
					prgRAM[dest] = rom[trainerSource + i];
			}
		}
		
		#if DIAG_LOG
		Console.WriteLine($"Cartridge loaded: PRG={prgBanks}x16KB, CHR={chrBanks}x8KB, Mapper={mapperID}, Trainer={(hasTrainer?"Yes":"No")}, Battery={(hasBattery?"Yes":"No")}");
		#endif
		
		string mapperName = GetMapperName(mapperID);
		switch (mapperID) {
			case 0:
				mapper = new Mapper0(this);
				break;
			case 1:
				mapper = new Mapper1(this);
				break;
			case 2:
				mapper = new Mapper2(this);
				break;
			case 3:
				mapper = new Mapper3(this); // CNROM
				break;
			case 4:
				mapper = new Mapper4(this);
				break;
			case 5:
				mapper = new Mapper5(this); // MMC5 (partial)
				break;
			case 33:
				mapper = new Mapper33(this); // Taito TC0190/TC0350 (no IRQ)
				break;
				case 7:
					mapper = new Mapper7(this);
					break;
			case 9:
				mapper = new Mapper9(this); // MMC2 (Punch-Out!!)
				break;
			case 90:
				mapper = new Mapper90(this);
				break;
			case 228:
				mapper = new Mapper228(this);
				break;
			case 30:
				mapper = new Mapper30(this); // UNROM-512
				break;
			case 11: mapper = new Mapper11(this); break;   // Color Dreams
			case 41: mapper = new Mapper41(this); break;   // Caltron 6-in-1
			case 66: mapper = new Mapper66(this); break;   // GxROM
			case 67: mapper = new Mapper67(this); break;   // Sunsoft-3
			case 68: mapper = new Mapper68(this); break;   // Sunsoft-4
			case 79: mapper = new Mapper79(this); break;   // NINA-03/06
			case 113: mapper = new Mapper113(this); break; // NINA/Sachen
			case 232: mapper = new Mapper232(this); break; // Camerica BF9096
			case 144: mapper = new Mapper144(this); break; // AGCI 50282 (Death Race)
			case 34: mapper = new Mapper34(this); break;
			case 64: mapper = new Mapper64(this); break;
			case 69: mapper = new Mapper69(this); break;
			case 71: mapper = new Mapper71(this); break;
			case 119: mapper = new Mapper119(this); break;
			case 13: mapper = new Mapper13(this); break;    // CPROM (16KB CHR-RAM)
			case 47: mapper = new Mapper47(this); break;    // NES-QJ
			case 118: mapper = new Mapper118(this); break;  // TxSROM
			default:
				#if DIAG_LOG
				Console.WriteLine($"Mapper {mapperID} ({mapperName}) is not supported");
				#endif
				throw new UnsupportedMapperException(mapperID, mapperName);
		}
		mapper.Reset();
	}

	// CRC32 (PRG+CHR, no header) -> the board it really is. Only dumps verified against Mesen 2's NesDB.
	private static readonly System.Collections.Generic.Dictionary<uint, int> HeaderFixups = new()
	{
		{ 0x8EAB381Cu, 79 },   // Deathbots (U): headered as 113, really AVE NINA-06 (79); as 113 its writes flip the mirroring
		{ 0x5CAA3E61u, 144 },  // Death Race (U): headered as 11, really AGCI 50282 (144); as 11 it crashes on frame 14
		{ 0xBD29178Au, 113 },  // Dudes With Attitude (U): headered as 79 with vertical mirroring; it is horizontal (113 bit 7 clear): as 79 the island map it builds in $2400 never shows on $2000 (compat catalog pilot 2026-11-15)
		{ 0xC47EFC0Eu, 113 },  // Trolls on Treasure Island (U): same AVE board and the same header error as Dudes With Attitude; as 79 the world map is built in the hidden nametable (lockstep vs Mesen: CIRAM halves swapped)
	};
	private static uint Crc32(byte[] data, int start, int length)
	{
		uint crc = 0xFFFFFFFFu;
		for (int i = start; i < start + length; i++)
		{
			crc ^= data[i];
			for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
		}
		return ~crc;
	}

	public byte CPURead(ushort address)
	{
		return mapper.CPURead(address);
	}

	// Whether the mapper leaves the CPU data bus undriven for this address (see IMapper.IsCpuReadOpenBus).
	public bool IsCpuReadOpenBus(ushort address)
	{
		return mapper.IsCpuReadOpenBus(address);
	}

	public void CPUWrite(ushort address, byte value)
	{
		mapper.CPUWrite(address, value);
	}

	/// <summary>Butchering mode (corruption/Butcher.cs). Null unless its cartridge pins are faulted: one null test per CHR access.</summary>
	public Butcher? Butcher;

	public byte PPURead(ushort address)
	{
		byte v = mapper.PPURead(address);   // the real fetch always happens: its latches and IRQ edges are the game's
		if (Butcher == null) return v;
		return ButcherRead(address, v);
	}

	public void PPUWrite(ushort address, byte value)
	{
		if (Butcher != null && ButcherWrite(address, value)) return;
		mapper.PPUWrite(address, value);
	}

	// The CHR chip's own pins, after the mapper's banking: the index the mapper picked is bent, and the chip answers for the bent index.
	private byte ButcherRead(ushort address, byte v)
	{
		var f = Butcher?.Cart;
		if (f == null || address >= 0x2000 || f.Misses() || !mapper.TryPpuToChrIndex(address, out int idx)) return v;
		var chip = chrBanks > 0 ? chrROM : chrRAM;
		if (chip.Length == 0) return v;
		if (f.AddrActive) { int b = f.Addr(idx); if (b != idx) v = chip[(uint)b % (uint)chip.Length]; }
		return f.DataActive ? f.Data(v) : v;
	}

	/// <summary>True when the write was taken (bent into CHR-RAM).</summary>
	private bool ButcherWrite(ushort address, byte value)
	{
		var f = Butcher?.Cart;
		if (f == null || !f.BendWrites || chrBanks > 0 || address >= 0x2000 || f.Misses() || !mapper.TryPpuToChrIndex(address, out int idx)) return false;
		if (chrRAM.Length == 0) return false;
		int b = f.AddrActive ? f.Addr(idx) : idx;
		chrRAM[(uint)b % (uint)chrRAM.Length] = f.DataActive ? f.Data(value) : value;
		return true;
	}

	/// <summary>Size of the CHR chip the cartridge pins belong to (CHR-ROM, else CHR-RAM).</summary>
	public int ChrChipSize => chrBanks > 0 ? chrROM.Length : chrRAM.Length;

	public void SetMirroring(Mirroring mode)
	{
		mirroringMode = mode;
		mirrorVertical = mode == Mirroring.Vertical;
		mirrorHorizontal = mode == Mirroring.Horizontal;
	}

		// === Direct memory access helpers (debug/edit) ===
		public int PrgRomSize => prgROM.Length;
		public int ChrRomSize => chrROM.Length;
		public int PrgRamSize => prgRAM.Length;
		public int ChrRamSize => chrRAM.Length;

		public void RefreshRomDomainsFromRom()
		{
			if (rom == null || rom.Length < 16) return;
			int offset = 16;
			if (hasTrainer) offset += 512;
			int prgSize = prgBanks * 16 * 1024;
			if (prgROM != null && prgROM.Length == prgSize && rom.Length >= offset + prgSize)
			{
				Buffer.BlockCopy(rom, offset, prgROM, 0, prgSize);
			}
			offset += prgSize;
			if (chrBanks > 0)
			{
				int chrSize = chrBanks * 8 * 1024;
				if (chrROM != null && chrROM.Length == chrSize && rom.Length >= offset + chrSize)
				{
					Buffer.BlockCopy(rom, offset, chrROM, 0, chrSize);
				}
			}
		}

		public byte PeekPrg(int index) => (index >= 0 && index < prgROM.Length) ? prgROM[index] : (byte)0;
		public void PokePrg(int index, byte value) { if (index >= 0 && index < prgROM.Length) prgROM[index] = value; }

		public byte PeekPrgRam(int index) => (index >= 0 && index < prgRAM.Length) ? prgRAM[index] : (byte)0;
		public void PokePrgRam(int index, byte value) { if (index >= 0 && index < prgRAM.Length) prgRAM[index] = value; }

		public byte PeekChr(int index)
		{
			if (index < 0) return 0;
			if (chrBanks > 0)
				return index < chrROM.Length ? chrROM[index] : (byte)0;
			return index < chrRAM.Length ? chrRAM[index] : (byte)0;
		}
		public void PokeChr(int index, byte value)
		{
			if (index < 0) return;
			if (chrBanks > 0)
			{
				if (index < chrROM.Length) chrROM[index] = value; // allow patching ROM for debugging
			}
			else if (index < chrRAM.Length) chrRAM[index] = value;
		}
}
public enum Mirroring
{
	Horizontal,
	Vertical,
	SingleScreenA,
	SingleScreenB
}
}
