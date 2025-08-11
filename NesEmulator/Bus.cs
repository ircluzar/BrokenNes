namespace NesEmulator
{
public interface IBus
{
	byte Read(ushort address);
	void Write(ushort address, byte value);
}

public class Bus : IBus
{
	public CPU cpu;
	public PPU ppu;
	public APU apu; // modern core
	public APUJANK apuJank; // legacy core
	private IAPU activeApu; // current active
	private bool famicloneMode = true; // default ON -> APUJANK
	private readonly byte[] apuRegLatch = new byte[0x18]; // $4000-$4017 last written values
	public Cartridge cartridge;
	public byte[] ram; //2KB RAM
	public Input input = new Input();

	public Bus(Cartridge cartridge)
	{
		this.cartridge = cartridge;
		cpu = new CPU(this);
		ppu = new PPU(this);
		apu = new APU(this);
		apuJank = new APUJANK(this);
		activeApu = apuJank; // default famiclone
		ram = new byte[2048];
	}

	public void SetFamicloneMode(bool on)
	{
		if (famicloneMode == on) return;
		famicloneMode = on;
		activeApu = famicloneMode ? (IAPU)apuJank : apu;
		// Reapply latched register values to newly active core so it picks up current configuration
		for (int i=0;i<apuRegLatch.Length;i++)
		{
			ushort addr = (ushort)(0x4000 + i);
			// Skip write-only DMA 0x4014 (i==0x14) since handled by CPU normally; but keep other registers
			if (addr == 0x4014) continue;
			activeApu.WriteAPURegister(addr, apuRegLatch[i]);
		}
	}
	public bool GetFamicloneMode() => famicloneMode;
	public IAPU ActiveAPU => activeApu;

		// --- APU Hard Reset Support ---
		// When switching games rapidly, leftover ring buffer audio or latched register values
		// could audibly "bleed" into the next title or cause famiclone/native mode confusion.
		// Recreate both cores and clear latches so the new cartridge starts from a pristine state.
		public void HardResetAPUs()
		{
			apu = new APU(this);
			apuJank = new APUJANK(this);
			// Preserve current famiclone mode selection but point to fresh instance
			activeApu = famicloneMode ? (IAPU)apuJank : apu;
			System.Array.Clear(apuRegLatch, 0, apuRegLatch.Length);
		}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	public byte Read(ushort address)
	{
		// Fast-path internal RAM (most frequent)
		if (address < 0x2000)
			return ram[address & 0x07FF];

		if (address < 0x4000)
		{
			ushort reg = (ushort)(0x2000 + (address & 0x0007));
			return ppu.ReadPPURegister(reg);
		}

		if (address == 0x4016)
			return input.Read4016();

		if (address <= 0x4017 && address >= 0x4000)
			return activeApu.ReadAPURegister(address);

		if (address >= 0x6000)
			return cartridge.CPURead(address);

		return 0; // open bus behavior simplified
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	public void Write(ushort address, byte value)
	{
		if (address < 0x2000)
		{
			ram[address & 0x07FF] = value;
			return;
		}
		if (address < 0x4000)
		{
			ushort reg = (ushort)(0x2000 + (address & 0x0007));
			ppu.WritePPURegister(reg, value);
			return;
		}
		if (address == 0x4016)
		{
			input.Write4016(value); return;
		}
		if (address == 0x4014)
		{
			ppu.WriteOAMDMA(value); return;
		}
		if (address <= 0x4017 && address >= 0x4000)
		{
			int idx = address - 0x4000;
			if (idx >=0 && idx < apuRegLatch.Length) apuRegLatch[idx] = value;
			activeApu.WriteAPURegister(address, value); return;
		}
		if (address >= 0x6000)
		{
			cartridge.CPUWrite(address, value); return;
		}
	}

	// === Debug Peek/Poke (raw CPU address space) ===
	public byte PeekByte(ushort address) => Read(address);
	public void PokeByte(ushort address, byte value) => Write(address, value);
	public byte PeekRam(int index) => (index >=0 && index < ram.Length) ? ram[index] : (byte)0;
	public void PokeRam(int index, byte value) { if (index>=0 && index < ram.Length) ram[index]=value; }

	public void StepAPU(int cpuCycles) => activeApu.Step(cpuCycles);
	public float[] GetAudioSamples(int max=0) => activeApu.GetAudioSamples(max);
	public int GetQueuedSamples() => activeApu.GetQueuedSampleCount();
	public int GetAudioSampleRate() => activeApu.GetSampleRate();
}
}
