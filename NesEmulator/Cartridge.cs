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
		7 => "AxROM",
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
		prgRAM = new byte[8 * 1024];
		chrRAM = new byte[8 * 1024];

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
				case 7:
					mapper = new Mapper7(this);
					break;
			default:
				#if DIAG_LOG
				Console.WriteLine($"Mapper {mapperID} ({mapperName}) is not supported");
				#endif
				throw new UnsupportedMapperException(mapperID, mapperName);
		}
		mapper.Reset();
	}

	public byte CPURead(ushort address)
	{
		return mapper.CPURead(address);
	}

	public void CPUWrite(ushort address, byte value)
	{
		mapper.CPUWrite(address, value);
	}

	public byte PPURead(ushort address)
	{
		return mapper.PPURead(address);
	} 
	
	public void PPUWrite(ushort address, byte value)
	{
		mapper.PPUWrite(address, value);
	}

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
