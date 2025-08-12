namespace NesEmulator
{
public interface IBus
{
	byte Read(ushort address);
	void Write(ushort address, byte value);
}

public class Bus : IBus
{
		// Active CPU core (public handle). Internally we may host multiple concrete cores and switch.
		public ICPU cpu; // points to activeCpu
		private CPU_FMC cpuFmc; // first CPU core implementation
		private CPU_FIX cpuFix; // placeholder FIX CPU core (mirrors FMC for now)
		private ICPU activeCpu; // mirror pointer used for core bookkeeping
		public IPPU ppu; // points to activePpu
		private PPU_FMC ppuFmc; // first PPU core implementation
		private PPU_FIX ppuFix; // placeholder FIX PPU core (mirrors FMC for now)
		private IPPU activePpu;
		public APU_FIX apu; // modern core (renamed from APU)
		public APU_FMC apuJank; // legacy core (renamed from APUJANK)
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
		cpuFmc = new CPU_FMC(this);
		cpuFix = new CPU_FIX(this); // instanced but unused until selection added
		activeCpu = cpuFmc;
		cpu = activeCpu; // expose
		ppuFmc = new PPU_FMC(this); // instantiate FMC PPU core
		ppuFix = new PPU_FIX(this); // instantiate FIX placeholder
		activePpu = ppuFmc;
		ppu = activePpu;
		apu = new APU_FIX(this);
		apuJank = new APU_FMC(this);
		apuQN = new APU_QN(this);
		activeApu = apuJank; // default famiclone
		ram = new byte[2048];
	}

	// === CPU Core Hot-Swap Support (parallel to APU system) ===
	public enum CpuCore { FMC = 0, FIX = 1 /* future cores enumerate */ }

	public void SetCpuCore(CpuCore core)
	{
		// capture current state for possible transfer
		var prevState = activeCpu != null ? activeCpu.GetState() : new object();
		bool ignoreInvalid = activeCpu?.IgnoreInvalidOpcodes ?? false;
		ICPU newCpu = core switch {
			CpuCore.FMC => cpuFmc,
			CpuCore.FIX => cpuFix,
			_ => cpuFmc
		};
		if (!ReferenceEquals(newCpu, activeCpu))
		{
			try { newCpu.SetState(prevState); } catch { }
			// propagate current invalid opcode handling preference
			newCpu.IgnoreInvalidOpcodes = ignoreInvalid;
		}
		activeCpu = newCpu;
		cpu = activeCpu;
	}

	public CpuCore GetActiveCpuCore() => activeCpu == cpuFmc ? CpuCore.FMC : CpuCore.FIX;

	public enum ApuCore { Modern, Jank, QuickNes }

	// === PPU Core Hot-Swap Support ===
	public enum PpuCore { FMC = 0, FIX = 1 /* future cores */ }
	public void SetPpuCore(PpuCore core)
	{
		var prevState = activePpu != null ? activePpu.GetState() : new object();
		IPPU newPpu = core switch {
			PpuCore.FMC => ppuFmc,
			PpuCore.FIX => ppuFix,
			_ => ppuFmc
		};
		if (!ReferenceEquals(newPpu, activePpu))
		{
			try { newPpu.SetState(prevState); } catch { }
		}
		activePpu = newPpu;
		ppu = activePpu;
	}
	public PpuCore GetActivePpuCore() => activePpu == ppuFmc ? PpuCore.FMC : PpuCore.FIX;

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
		   apuJank = new APU_FMC(this);
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
