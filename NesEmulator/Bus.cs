namespace NesEmulator
{
public interface IBus
{
	byte Read(ushort address);
	void Write(ushort address, byte value);
}

public class Bus : IBus
{
		public ICPU cpu;
		public IPPU ppu;
		public APU_FIX apu; // modern core (renamed from APU)
		public APU_FMB apuJank; // legacy core (renamed from APUJANK)
		public APU_QN apuQN; // QuickNes core
		private IAPU activeApu; // current active
		private bool famicloneMode = true; // legacy flag: true when last user toggle selected famiclone; derived from activeApu for reporting
		private readonly byte[] apuRegLatch = new byte[0x18]; // $4000-$4017 last written values
	public Cartridge cartridge;
	public byte[] ram; //2KB RAM
	public Input input = new Input();

	public Bus(Cartridge cartridge)
	{
		this.cartridge = cartridge;
		cpu = new CPU(this);
		ppu = new PPU(this);
		apu = new APU_FIX(this);
		apuJank = new APU_FMB(this);
		apuQN = new APU_QN(this);
		activeApu = apuJank; // default famiclone
		ram = new byte[2048];
	}

	public enum ApuCore { Modern, Jank, QuickNes }

	public void SetApuCore(ApuCore core)
	{
		switch(core)
		{
			case ApuCore.Modern: activeApu = apu; break;
			case ApuCore.Jank: activeApu = apuJank; break;
			case ApuCore.QuickNes: activeApu = apuQN; break;
		}
		// sync legacy flag for callers that still query famiclone boolean
		famicloneMode = core == ApuCore.Jank;
		// Reapply latched register values so the new core picks up current state
		for (int i=0;i<apuRegLatch.Length;i++)
		{
			ushort addr = (ushort)(0x4000 + i);
			if (addr == 0x4014) continue; // skip OAMDMA
			activeApu.WriteAPURegister(addr, apuRegLatch[i]);
		}
	}

	public ApuCore GetActiveApuCore()
	{
		if (activeApu == apuQN) return ApuCore.QuickNes;
		if (activeApu == apuJank) return ApuCore.Jank;
		return ApuCore.Modern;
	}

	public void SetFamicloneMode(bool on)
	{
		// Route to specific cores under the hood
		SetApuCore(on ? ApuCore.Jank : ApuCore.Modern);
	}
	public bool GetFamicloneMode() => activeApu == apuJank;
	public IAPU ActiveAPU => activeApu;

		// --- APU Hard Reset Support ---
		// When switching games rapidly, leftover ring buffer audio or latched register values
		// could audibly "bleed" into the next title or cause famiclone/native mode confusion.
		// Recreate both cores and clear latches so the new cartridge starts from a pristine state.
		public void HardResetAPUs()
		{
		   var prev = GetActiveApuCore();
		   apu = new APU_FIX(this);
		   apuJank = new APU_FMB(this);
		   apuQN = new APU_QN(this);
		   // Preserve the previously selected core
		   SetApuCore(prev);
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

	// --- QuickNes helpers ---
	public void UseQuickNesAPU() => SetApuCore(ApuCore.QuickNes);
	public void SetApuRegion(bool pal)
	{
		if (activeApu is APU_QN qn) qn.SetRegion(pal);
	}
	public void SetApuNonlinearMixing(bool enabled)
	{
		if (activeApu is APU_QN qn) qn.SetNonlinearMixing(enabled);
	}
}
}
