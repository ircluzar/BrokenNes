namespace NesEmulator
{
public interface IBus
{
	byte Read(ushort address);
	void Write(ushort address, byte value);
}

public class Bus : IBus
{
		// Core dictionaries
		private readonly System.Collections.Generic.Dictionary<string, ICPU> _cpuCores; // eager for now
		// Lazy APU & PPU: cache types and create instances on demand to avoid early large buffer allocations
		private readonly System.Collections.Generic.Dictionary<string, System.Type> _apuTypes;
		private readonly System.Collections.Generic.Dictionary<string, IAPU> _apuInstances = new(System.StringComparer.OrdinalIgnoreCase);
		// Lazy PPU: cache types and create instances on demand to avoid early large buffer allocations
		private readonly System.Collections.Generic.Dictionary<string, System.Type> _ppuTypes;
		private readonly System.Collections.Generic.Dictionary<string, IPPU> _ppuInstances = new(System.StringComparer.OrdinalIgnoreCase);
		// Active instances & legacy public handles (kept for minimal external changes)
		private ICPU activeCpu;
		private IPPU activePpu;
		private IAPU activeApu;
		public ICPU cpu; // points to activeCpu
		public IPPU ppu; // points to activePpu
		public IAPU apu; // default modern
		public IAPU apuJank; // legacy famiclone
		public IAPU apuQN; // QuickNes
		private bool famicloneMode = true; // legacy flag: true when last user toggle selected famiclone; derived from activeApu for reporting
		private readonly byte[] apuRegLatch = new byte[0x18]; // $4000-$4017 last written values
	public Cartridge cartridge;
	public byte[] ram; //2KB RAM
	public Input input = new Input();

	public Bus(Cartridge cartridge)
	{
		this.cartridge = cartridge;
		_cpuCores = CoreRegistry.CreateInstances<ICPU>(this, "CPU_");
		_apuTypes = new System.Collections.Generic.Dictionary<string, System.Type>(CoreRegistry.ApuTypes, System.StringComparer.OrdinalIgnoreCase);
		_ppuTypes = new System.Collections.Generic.Dictionary<string, System.Type>(CoreRegistry.PpuTypes, System.StringComparer.OrdinalIgnoreCase);
		// Defaults
		activeCpu = _cpuCores.TryGetValue("FMC", out var cpuFmc) ? cpuFmc : (_cpuCores.Count>0 ? System.Linq.Enumerable.First(_cpuCores.Values) : throw new System.Exception("No CPU cores found"));
		cpu = activeCpu;
		// Prefer FMC PPU by default; fall back to any available. Create lazily.
		activePpu = GetOrCreatePpu("FMC") ?? GetOrCreatePpu("CUBE") ?? (CreateFirstAvailablePpu() ?? throw new System.Exception("No PPU cores found"));
		ppu = activePpu;
		// APU defaults (lazy)
		apu = GetOrCreateApu("FIX") ?? CreateFirstAvailableApu() ?? throw new System.Exception("No APU cores found");
		apuJank = GetOrCreateApu("FMC") ?? apu;
		apuQN = GetOrCreateApu("QN") ?? apu;
		activeApu = apuJank; // default famiclone selection
		ram = new byte[2048];
		// Optional: allow cores to run deferred initialization that requires a constructed Bus
		TryInitializeCores();
	}

	// Optional extension point: cores may implement this to receive a post-ctor Bus reference
	public interface IBusAware { void Initialize(Bus bus); }

	private void TryInitializeCores()
	{
		try { foreach (var c in _cpuCores.Values) if (c is IBusAware ia) ia.Initialize(this); } catch {}
		try { foreach (var kv in _ppuInstances) if (kv.Value is IBusAware ia) ia.Initialize(this); } catch {}
		try { foreach (var kv in _apuInstances) if (kv.Value is IBusAware ia) ia.Initialize(this); } catch {}
	}

	// === CPU Core Hot-Swap Support (parallel to APU system) ===
	public enum CpuCore { FMC = 0, FIX = 1, LOW = 2 }

	public void SetCpuCore(CpuCore core)
	{
		// capture current state for possible transfer
		var prevState = activeCpu != null ? activeCpu.GetState() : new object();
		bool ignoreInvalid = activeCpu?.IgnoreInvalidOpcodes ?? false;
		ICPU? newCpu = core switch {
			CpuCore.FMC => GetCpu("FMC") ?? activeCpu,
			CpuCore.FIX => GetCpu("FIX") ?? activeCpu,
			CpuCore.LOW => GetCpu("LOW") ?? activeCpu,
			_ => GetCpu("FMC") ?? activeCpu
		};
		if (newCpu != null && !ReferenceEquals(newCpu, activeCpu))
		{
			try { newCpu.SetState(prevState); } catch { }
			// propagate current invalid opcode handling preference
			newCpu.IgnoreInvalidOpcodes = ignoreInvalid;
		}
		if (newCpu != null) { activeCpu = newCpu; cpu = activeCpu; }
	}

	public CpuCore GetActiveCpuCore() {
		var id = System.Linq.Enumerable.FirstOrDefault(_cpuCores, kv => object.ReferenceEquals(kv.Value, activeCpu)).Key;
		return id switch { "FMC" => CpuCore.FMC, "FIX" => CpuCore.FIX, "LOW" => CpuCore.LOW, _ => CpuCore.FMC };
	}
	private ICPU? GetCpu(string id) => _cpuCores.TryGetValue(id, out var c)?c:null;

	public enum ApuCore { Modern, Jank, QuickNes }

	// === Generic reflection-driven core discovery helpers ===
	public System.Collections.Generic.IReadOnlyList<string> GetCpuCoreIds() => _cpuCores.Keys.OrderBy(k=>k, System.StringComparer.OrdinalIgnoreCase).ToList();
	public System.Collections.Generic.IReadOnlyList<string> GetPpuCoreIds() => _ppuTypes.Keys.OrderBy(k=>k, System.StringComparer.OrdinalIgnoreCase).ToList();
	public System.Collections.Generic.IReadOnlyList<string> GetApuCoreIds() => _apuTypes.Keys.OrderBy(k=>k, System.StringComparer.OrdinalIgnoreCase).ToList();

	// Generic setters by suffix id (e.g. "FMC", "FIX", "NGTV") allowing new cores without editing enums
	public bool SetCpuCoreById(string id)
	{
		if (string.IsNullOrWhiteSpace(id)) return false;
		if (!_cpuCores.TryGetValue(id, out var newCpu)) return false;
		if (ReferenceEquals(newCpu, activeCpu)) { cpu = activeCpu; return true; }
		var prevState = activeCpu.GetState();
		bool ignoreInvalid = activeCpu.IgnoreInvalidOpcodes;
		try { newCpu.SetState(prevState); } catch { }
		newCpu.IgnoreInvalidOpcodes = ignoreInvalid;
		activeCpu = newCpu; cpu = activeCpu; return true;
	}
	public bool SetPpuCoreById(string id)
	{
		if (string.IsNullOrWhiteSpace(id)) return false;
		var newPpu = GetOrCreatePpu(id);
		if (newPpu == null) return false;
		if (ReferenceEquals(newPpu, activePpu)) { ppu = activePpu; return true; }
		var prevState = activePpu.GetState();
		// Drop large transient buffers on the old PPU before switching to reduce memory
		try { if (activePpu != null) activePpu.ClearBuffers(); } catch { }
		try { newPpu.SetState(prevState); } catch { }
		// Ensure the new PPU starts with clean buffers for a fresh redraw
		try { newPpu.ClearBuffers(); } catch { }
		activePpu = newPpu; ppu = activePpu; return true;
	}
	public bool SetApuCoreById(string id)
	{
		if (string.IsNullOrWhiteSpace(id)) return false;
		var newApu = GetOrCreateApu(id);
		if (newApu == null) return false;
		if (ReferenceEquals(newApu, activeApu))
		{
			// Even if unchanged, update legacy flag deterministically
			famicloneMode = id.Equals("FMC", System.StringComparison.OrdinalIgnoreCase);
			return true;
		}
		activeApu = newApu; // reapply latched registers so new core inherits state
		for (int i=0;i<apuRegLatch.Length;i++)
		{
			ushort addr = (ushort)(0x4000 + i);
			if (addr == 0x4014) continue;
			try { activeApu.WriteAPURegister(addr, apuRegLatch[i]); } catch { }
		}
		famicloneMode = id.Equals("FMC", System.StringComparison.OrdinalIgnoreCase); // legacy semantic
		return true;
	}

	// === PPU Core Hot-Swap Support ===
		public enum PpuCore { FMC = 0, FIX = 1, LQ = 2, CUBE = 3, LOW = 4, BFR = 5 }
		public void SetPpuCore(PpuCore core)
	{
		var prevState = activePpu != null ? activePpu.GetState() : new object();
			IPPU? newPpu = core switch {
				PpuCore.FMC => GetPpu("FMC") ?? activePpu,
				PpuCore.FIX => GetPpu("FIX") ?? activePpu,
				PpuCore.LQ => GetPpu("LQ") ?? activePpu,
				PpuCore.CUBE => GetPpu("CUBE") ?? activePpu,
				PpuCore.LOW => GetPpu("LOW") ?? activePpu,
				PpuCore.BFR => GetPpu("BFR") ?? activePpu,
				_ => GetPpu("FMC") ?? activePpu
			};
		if (newPpu != null && !ReferenceEquals(newPpu, activePpu))
		{
			// Drop buffers on old core to reduce memory pressure during swaps
			try { if (activePpu != null) activePpu.ClearBuffers(); } catch { }
			try { newPpu.SetState(prevState); } catch { }
			// Ensure clean start on the new core too
			try { newPpu.ClearBuffers(); } catch { }
		}
	if (newPpu != null) { activePpu = newPpu; ppu = activePpu; }
	}
		public PpuCore GetActivePpuCore() {
			// Find by instance dictionary
			foreach (var kv in _ppuInstances)
				if (object.ReferenceEquals(kv.Value, activePpu))
					return kv.Key switch { "FMC" => PpuCore.FMC, "FIX" => PpuCore.FIX, "LQ" => PpuCore.LQ, "CUBE" => PpuCore.CUBE, "LOW" => PpuCore.LOW, "BFR" => PpuCore.BFR, _ => PpuCore.FMC };
			return PpuCore.FMC;
		}

		private IPPU? GetPpu(string id) => GetOrCreatePpu(id);
		private IPPU? GetOrCreatePpu(string id)
		{
			if (_ppuInstances.TryGetValue(id, out var existing)) return existing;
			if (!_ppuTypes.TryGetValue(id, out var type)) return null;
			var created = CoreRegistry.CreateInstance<IPPU>(type, this);
			if (created != null) _ppuInstances[id] = created;
			return created;
		}

		private IPPU? CreateFirstAvailablePpu()
		{
			foreach (var id in _ppuTypes.Keys)
			{
				var p = GetOrCreatePpu(id);
				if (p != null) return p;
			}
			return null;
		}

		private IAPU? GetOrCreateApu(string id)
		{
			if (_apuInstances.TryGetValue(id, out var existing)) return existing;
			if (!_apuTypes.TryGetValue(id, out var type)) return null;
			var created = CoreRegistry.CreateInstance<IAPU>(type, this);
			if (created != null) _apuInstances[id] = created;
			return created;
		}

		private IAPU? CreateFirstAvailableApu()
		{
			foreach (var id in _apuTypes.Keys)
			{
				var a = GetOrCreateApu(id);
				if (a != null) return a;
			}
			return null;
		}

	public void SetApuCore(ApuCore core)
	{
		switch(core)
		{
			case ApuCore.Modern: activeApu = apu ?? GetOrCreateApu("FIX") ?? activeApu; break;
			case ApuCore.Jank: activeApu = apuJank ?? GetOrCreateApu("FMC") ?? activeApu; break;
			case ApuCore.QuickNes: activeApu = apuQN ?? GetOrCreateApu("QN") ?? activeApu; break;
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
		// Recreate cores and clear latches so the new cartridge starts from a pristine state.
		public void HardResetAPUs()
		{
		   var prev = GetActiveApuCore();
		   // Drop queued audio on the currently active core to avoid bleed into next game
		   try { activeApu?.ClearAudioBuffers(); } catch {}
		   // Drop and recreate known instances
		   void Recreate(string key, ref IAPU field)
		   {
		       _apuInstances.Remove(key);
		       var inst = GetOrCreateApu(key);
		       if (inst != null) field = inst;
		   }
		   Recreate("FIX", ref apu);
		   Recreate("FMC", ref apuJank);
		   Recreate("QN", ref apuQN);
		   // Restore previously selected active core (will reapply register latches next)
		   SetApuCore(prev);
		   // Clear latches to avoid carrying old writes between games
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
