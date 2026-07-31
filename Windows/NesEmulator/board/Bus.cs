namespace NesEmulator
{
public interface IBus
{
	byte Read(ushort address);
	void Write(ushort address, byte value);
}

public class Bus : IBus
{
		// === Page Table ===
		// 256 pages of 256 bytes each cover full 64KB CPU address space.
		// Pages pointing to internal RAM or other linear data regions allow direct index without mirror masking.
		// Callback pages fall back to branch logic for PPU/APU/cartridge access.
		private struct Page { public byte[]? data; public int offset; public bool writable; }
		private Page[] pages = new Page[256];
		private bool pageTableInitialized;
		private void BuildPageTable()
		{
			// Internal 2KB RAM mirrored every 0x0800 up to 0x1FFF
			for (int p = 0x00; p <= 0x1F; p++)
			{
				pages[p].data = ram; // single backing array
				pages[p].offset = (p % 0x08) * 0x100; // mirror by 2KB (8 pages)
				pages[p].writable = true;
			}
			// PPU registers 0x2000-0x3FFF mirrored every 8 bytes: leave as callback (data=null)
			for (int p = 0x20; p <= 0x3F; p++) pages[p] = default;
			// APU + IO + Expansion 0x4000-0x5FFF remain callback (future fine pages possible)
			for (int p = 0x40; p <= 0x5F; p++) pages[p] = default;
			// Cartridge space 0x6000-0xFFFF: callback (mappers may later patch with direct data spans for common banks)
			for (int p = 0x60; p <= 0xFF; p++) pages[p] = default;
			pageTableInitialized = true;
		}

		// --- Lightweight instrumentation ---
		public struct Instrumentation
		{
			public long Reads; public long Writes; public long ApuSteps; public long OamDmaWrites; public long BatchFlushes;
			public void Reset(){ Reads=Writes=ApuSteps=OamDmaWrites=BatchFlushes=0; }
			public Instrumentation Snapshot() => this; // value copy
		}
		private Instrumentation instr;
		public Instrumentation GetInstrumentation() => instr.Snapshot();
		public void ResetInstrumentation() => instr.Reset();
		// Open bus: real hardware has no "return 0" for unmapped reads - the data bus simply
		// holds whatever byte was last driven onto it (decaying after ~600ms with no activity,
		// which isn't modeled here; this is the same no-decay approximation most emulators use).
		// Updated on every successful read/write so an unmapped-address read can fall back to it.
		private byte lastBusValue = 0;
		// Exposed so PPU cores can return real open-bus behavior for write-only/unimplemented
		// PPU registers ($2000/$2001/$2003/$2005/$2006) instead of a hardcoded 0.
		public byte GetOpenBus() => lastBusValue;
		// Restores the open-bus value from a savestate - SaveState()/LoadState() previously dropped
		// this silently (it lives on Bus, not inside any CPU/PPU/APU core's own GetState()), so a
		// reload always reset it to 0 regardless of what the live bus was last driven with.
		public void SetOpenBus(byte value) => lastBusValue = value;
		// Accumulated CPU stall cycles injected by hardware operations (e.g., OAM DMA) for fast-path approximations.
		internal int PendingCpuStallCycles = 0;
		public int ConsumePendingCpuStallCycles(){ int c = PendingCpuStallCycles; PendingCpuStallCycles = 0; return c; }
		// A DMC sample fetch halts the CPU while the APU takes the bus. Like OAM DMA's stall this is
		// real hardware behavior, not a speed/accuracy trade-off, so it is unconditional. The exact
		// cost is 3-4 cycles depending on where the request lands relative to the CPU's read/write
		// cycle (and as little as 2 when it collides with an OAM DMA); a flat 4 is the standard
		// approximation and is what the batched CPU->APU stepping here can actually express - the
		// APU only runs after the CPU has already executed its cycles, so the stall is accounted at
		// the next FlushBatch rather than injected mid-instruction. That makes the *aggregate* cycle
		// budget correct (the CPU no longer gets free work during every DMA) without being able to
		// place the stolen cycle exactly; tests that check which specific cycle a DMA steals still
		// need genuine mid-instruction interleaving.
		public void AddDmcDmaStallCycles()
		{
			// Inside a precise window the DMC's stall is applied on the spot by PreciseTick, so it
			// goes to its own counter. Critically this keeps it separate from OAM DMA's 513-cycle
			// stall, which shares PendingCpuStallCycles but must keep landing exactly where it
			// always has (see CpuFastOamDmaStall) - letting PreciseTick swallow that one instead
			// measurably breaks NMI timing.
			if (preciseWindow) PendingDmcStallCycles += 4; else PendingCpuStallCycles += 4;
		}
		private int PendingDmcStallCycles;

		// === Precise-DMA window ===
		// The batched CPU->PPU/APU model above runs a whole instruction (in fact up to ~24 cycles
		// of them) before the APU gets to see those cycles, so a DMC sample fetch always lands
		// late: its bus read updates the open bus *after* the CPU reads that should have observed
		// it, and its stall cycles apply at the next batch boundary instead of the exact cycle.
		// When NES.RunFrame predicts a fetch is imminent (see IDmcDmaSchedulable) it opens this
		// window for one instruction, and every CPU bus access then advances PPU/APU by exactly
		// one CPU cycle first - the 6502 performs one bus access per cycle, so access count is
		// cycle position. That places the fetch on its real cycle without per-cycle interleaving
		// anywhere else: the window is armed for well under 1% of instructions, and when it's
		// closed the only cost on the hot path is the predictable `preciseWindow` branch below.
		private bool preciseWindow;
		private bool insidePreciseTick; // re-entrancy guard: the DMA's OWN bus read must not re-tick
		// Kept separate because they mean different things to the caller: AccessCycles are cycles
		// the CPU itself spent (so they offset against the instruction's own cycle count), while
		// StallCycles are cycles the CPU was halted for (pure extra elapsed time on top of it).
		private int preciseAccessCycles;
		private int preciseStallCycles;

		/// <summary>
		/// The active APU if it can predict its DMC fetches, else null (which leaves NES.RunFrame
		/// on its plain batched path). Resolved per frame rather than cached on core-switch since
		/// the APU core can be swapped from many places, including savestate loads.
		/// </summary>
		public IDmcDmaSchedulable? GetDmcSchedulable() => activeApu as IDmcDmaSchedulable;

		public void BeginPreciseWindow() { preciseWindow = true; preciseAccessCycles = 0; preciseStallCycles = 0; }

		/// <summary>
		/// Closes the window. accessCycles = CPU cycles advanced by bus accesses (one per access);
		/// stallCycles = extra cycles PPU/APU advanced while the CPU sat halted for a DMA.
		/// </summary>
		public (int accessCycles, int stallCycles) EndPreciseWindow()
		{
			preciseWindow = false;
			return (preciseAccessCycles, preciseStallCycles);
		}

		private void PreciseTick()
		{
			if (insidePreciseTick) return;
			insidePreciseTick = true;
			ppu!.Step(3); StepAPU(1); preciseAccessCycles++;
			// If that cycle triggered a DMA, the CPU is halted for its duration right here - so
			// advance PPU/APU across the stall while the CPU stands still, which is exactly what
			// the DMA does on hardware. Outside this window the same cycles get consumed at the
			// next FlushBatch instead (see ConsumePendingCpuStallCycles).
			int stall = PendingDmcStallCycles;
			if (stall > 0)
			{
				PendingDmcStallCycles = 0;
				ppu!.Step(stall * 3);
				StepAPU(stall);
				preciseStallCycles += stall;
			}
			insidePreciseTick = false;
		}
		// Count a PPU/APU batch flush
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public void CountBatchFlush() { instr.BatchFlushes++; }
		// Core dictionaries
		private readonly System.Collections.Generic.Dictionary<string, ICPU> _cpuCores; // eager for now
		// Lazy APU & PPU: cache types and create instances on demand to avoid early large buffer allocations
		private readonly System.Collections.Generic.Dictionary<string, System.Type> _apuTypes;
		private readonly System.Collections.Generic.Dictionary<string, IAPU> _apuInstances = new(System.StringComparer.OrdinalIgnoreCase);
		// Lazy PPU: cache types and create instances on demand to avoid early large buffer allocations
		private readonly System.Collections.Generic.Dictionary<string, System.Type> _ppuTypes;
		private readonly System.Collections.Generic.Dictionary<string, IPPU> _ppuInstances = new(System.StringComparer.OrdinalIgnoreCase);
		// Active instances & public handles
		private ICPU activeCpu;
		private IPPU activePpu;
		private IAPU activeApu;
		public ICPU cpu; // points to activeCpu
		public IPPU ppu; // points to activePpu
		public IAPU apu; // points to activeApu
		public IAPU apuJank; // famiclone
		public IAPU apuQN; // QuickNes
		private readonly byte[] apuRegLatch = new byte[0x18]; // $4000-$4017 last written values
		// MMC5 expansion audio
		private MMC5Audio mmc5Audio;
	public Cartridge cartridge;
	public byte[] ram; //2KB RAM
	public Input input = new Input();
	// Second controller port (player 2)
	public Input input2 = new Input();
	// Global speed configuration instance (mutable toggles)
	public SpeedConfig SpeedConfig { get; } = new SpeedConfig();

	public Bus(Cartridge cartridge)
	{
		this.cartridge = cartridge;
		_cpuCores = CoreRegistry.CreateInstances<ICPU>(this, "CPU_");
		_apuTypes = new System.Collections.Generic.Dictionary<string, System.Type>(CoreRegistry.ApuTypes, System.StringComparer.OrdinalIgnoreCase);
		_ppuTypes = new System.Collections.Generic.Dictionary<string, System.Type>(CoreRegistry.PpuTypes, System.StringComparer.OrdinalIgnoreCase);
		// Defaults
		// Prefer speed-optimized core (SPD) for immediate benchmarking if available; fallback to FMC then first.
		if (_cpuCores.TryGetValue("SPD", out var cpuSpd))
		{
			activeCpu = cpuSpd!;
		}
		else
		{
			activeCpu = _cpuCores.TryGetValue("FMC", out var cpuFmc)
				? cpuFmc!
				: (_cpuCores.Count > 0 ? System.Linq.Enumerable.First(_cpuCores.Values) : throw new System.Exception("No CPU cores found"));
		}
		cpu = activeCpu;
		// Prefer FMC PPU by default; fall back to any available. Create lazily, but never allow null assignment.
		activePpu = GetOrCreatePpu("FMC") ?? GetOrCreatePpu("CUBE") ?? CreateFirstAvailablePpu() ?? throw new System.Exception("No PPU cores found");
		ppu = activePpu;
		// APU defaults (lazy)
		apu = GetOrCreateApu("FIX") ?? CreateFirstAvailableApu() ?? throw new System.Exception("No APU cores found");
		apuJank = GetOrCreateApu("FMC") ?? apu;
		apuQN = GetOrCreateApu("QN") ?? apu;
		activeApu = apuJank; // default famiclone selection
		ram = new byte[2048];
		InitializeRamPowerOnPattern(ram);
		BuildPageTable();
		// Optional: allow cores to run deferred initialization that requires a constructed Bus
		TryInitializeCores();
		// Initialize MMC5 audio with IRQ callback into CPU
		mmc5Audio = new MMC5Audio((irq) => { if (irq) cpu?.RequestIRQ(true); });
	}

	/// <summary>
	/// Fills power-on RAM with the repeating 8-byte pattern 00 00 00 00 FF FF FF FF, matching
	/// FCEUX's default RAMInitOption=0 (its FCEU_MemoryRand, fceu.cpp) - and, per FCEUX's own
	/// comment there, "used in FCEUX since time immemorial".
	///
	/// This is not cosmetic. Several real games read uninitialized RAM at boot (RNG seeding,
	/// debug-mode checks, high-score tables) - FCEUX's source names Cybernoid, Huang Di, F-15 City
	/// War, 1942 and Cheetahmen II as examples that visibly change behavior with the fill pattern.
	/// Zero-filling instead makes those games take a different path from their very first frame,
	/// which is exactly what breaks .fm2 movie portability: a movie recorded against one power-on
	/// RAM state desyncs when replayed against another. Matching FCEUX's pattern is what lets the
	/// same .fm2 replay identically in both emulators.
	///
	/// Deliberately matches FCEUX's *default* only; its other RAMInitOption modes (all-FF, all-00,
	/// seeded-random) exist for hardware-variation testing and aren't modeled here.
	/// </summary>
	private static void InitializeRamPowerOnPattern(byte[] target)
	{
		for (int i = 0; i < target.Length; i++)
			target[i] = (i & 4) != 0 ? (byte)0xFF : (byte)0x00;
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
		// Drop large transient buffers on the PPU before switching to reduce memory
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
		if (ReferenceEquals(newApu, activeApu)) { return true; }
		var previousApu = activeApu;
		try { previousApu?.Reset(); } catch { } // silences MIDI/SF2 voices on the outgoing core
		activeApu = newApu; // reapply latched registers so new core inherits state
		try { activeApu.ClearAudioBuffers(); } catch { }
		for (int i=0;i<apuRegLatch.Length;i++)
		{
			ushort addr = (ushort)(0x4000 + i);
			if (addr == 0x4014) continue;
			try { activeApu.WriteAPURegister(addr, apuRegLatch[i]); } catch { }
		}
		return true;
	}

	// === PPU Core Hot-Swap Support ===
		public enum PpuCore { FMC = 0, FIX = 1, LQ = 2, ULQ = 3, CUBE = 4, LOW = 5, BFR = 6 }
		public void SetPpuCore(PpuCore core)
	{
		var prevState = activePpu != null ? activePpu.GetState() : new object();
			IPPU? newPpu = core switch {
				PpuCore.FMC => GetPpu("FMC") ?? activePpu,
				PpuCore.FIX => GetPpu("FIX") ?? activePpu,
				PpuCore.LQ => GetPpu("LQ") ?? activePpu,
				PpuCore.ULQ => GetPpu("ULQ") ?? activePpu,
				PpuCore.CUBE => GetPpu("CUBE") ?? activePpu,
				PpuCore.LOW => GetPpu("LOW") ?? activePpu,
				PpuCore.BFR => GetPpu("BFR") ?? activePpu,
				_ => GetPpu("FMC") ?? activePpu
			};
		if (newPpu != null && !ReferenceEquals(newPpu, activePpu))
		{
			// Drop buffers on core to reduce memory pressure during swaps
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
					return kv.Key switch { "FMC" => PpuCore.FMC, "FIX" => PpuCore.FIX, "LQ" => PpuCore.LQ, "ULQ" => PpuCore.ULQ, "CUBE" => PpuCore.CUBE, "LOW" => PpuCore.LOW, "BFR" => PpuCore.BFR, _ => PpuCore.FMC };
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
		var previousApu = activeApu;
		switch(core)
		{
			case ApuCore.Modern: activeApu = apu ?? GetOrCreateApu("FIX") ?? activeApu; break;
			case ApuCore.Jank: activeApu = apuJank ?? GetOrCreateApu("FMC") ?? activeApu; break;
			case ApuCore.QuickNes: activeApu = apuQN ?? GetOrCreateApu("QN") ?? activeApu; break;
		}
		if (!ReferenceEquals(previousApu, activeApu))
		{
			try { previousApu?.Reset(); } catch { } // silences MIDI/SF2 voices on the outgoing core
			try { activeApu?.ClearAudioBuffers(); } catch { }
		}
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

	public IAPU ActiveAPU => activeApu;

		// --- APU Hard Reset Support ---
		// When switching games rapidly, leftover ring buffer audio or latched register values
		// could audibly "bleed" into the next title or cause famiclone/native mode confusion.
		// Recreate cores and clear latches so the new cartridge starts from a pristine state.
		public void HardResetAPUs()
		{
		   var prev = GetActiveApuCore();
		   // Silence MIDI/SF2 voices and drop queued audio on the active core before recreating
		   try { activeApu?.Reset(); } catch {}
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
		   // Clear latches to avoid carrying writes between games
		   System.Array.Clear(apuRegLatch, 0, apuRegLatch.Length);
		}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	public byte Read(ushort address)
	{
		if (preciseWindow) PreciseTick(); // see BeginPreciseWindow - normally false, branch is free
		instr.Reads++;
		// Page table fast path: internal RAM and any future linear mapped regions
		var page = pages[address >> 8];
		if (page.data != null)
		{
			// address & 0xFF + page.offset gives direct index (mirroring handled in offset computation)
			byte v = page.data[page.offset + (address & 0xFF)];
			lastBusValue = v;
			return v;
		}
		byte r = ReadSlow(address);
		lastBusValue = r;
		return r;
	}

	private byte ReadSlow(ushort address)
	{
		// PPU registers 0x2000-0x3FFF (mirrored every 8)
		if (address < 0x4000)
		{
			ushort reg = (ushort)(0x2000 + (address & 0x0007));
			return ppu.ReadPPURegister(reg);
		}
		// Controllers are read from 0x4016 (P1) and 0x4017 (P2) on real hardware
		if (address == 0x4016) return input.Read4016(lastBusValue);
		if (address == 0x4017) return input2.Read4016(lastBusValue);
		// APU registers (e.g., 0x4015 status) remain handled here
		if (address <= 0x4017 && address >= 0x4000) return activeApu.ReadAPURegister(address);
	// Mapper expansion registers (e.g., MMC5 $5000-$5FFF)
	if (address >= 0x5000 && address < 0x6000)
	{
		if (cartridge.IsCpuReadOpenBus(address)) return lastBusValue;
		byte val = cartridge.CPURead(address);
		// Merge in MMC5 audio status/IRQ where applicable
		if (address == 0x5015)
		{
			byte exp = mmc5Audio != null ? mmc5Audio.Read5015() : (byte)0;
			val |= (byte)(exp & 0x03);
		}
		else if (address == 0x5010)
		{
			byte exp = mmc5Audio != null ? mmc5Audio.Read5010() : (byte)0;
			val |= (byte)(exp & 0x80);
		}
		return val;
	}
		if (address >= 0x6000)
		{
			if (cartridge.IsCpuReadOpenBus(address)) return lastBusValue;
			byte v = cartridge.CPURead(address);
			// Trigger MMC5 PCM only on PRG ROM range $8000-$BFFF
			if (address >= 0x8000 && address <= 0xBFFF) mmc5Audio?.ReadROMTrigger(v);
			return v;
		}
		// Open bus: no device drove the data bus for this address, so it holds whatever byte
		// was last transferred anywhere on the bus (see lastBusValue's declaration).
		return lastBusValue;
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	public void Write(ushort address, byte value)
	{
		if (preciseWindow) PreciseTick(); // see BeginPreciseWindow - normally false, branch is free
		instr.Writes++;
		// A write always drives its value onto the data bus, whether or not any device at this
		// address latches it - so it always updates the open-bus value (see lastBusValue).
		lastBusValue = value;
		var page = pages[address >> 8];
		if (page.data != null && page.writable)
		{
			page.data[page.offset + (address & 0xFF)] = value; return; // RAM fast path (mirrors handled)
		}
		WriteSlow(address, value);
	}

	private void WriteSlow(ushort address, byte value)
	{
		// Touch idle loop detector (direct call; avoids reflection cost)
		if (cpu is CPU_SPD spdCpu) spdCpu.IdleLoopMaybeWriteTouch(address);
		if (address < 0x4000)
		{
			ushort reg = (ushort)(0x2000 + (address & 0x0007));
			ppu.WritePPURegister(reg, value); return;
		}
		// Writing bit0 to 0x4016 controls controller strobe; apply to both ports
		if (address == 0x4016) { input.Write4016(value); input2.Write4016(value); return; }
		if (address == 0x4014) {
			ppu.WriteOAMDMA(value); instr.OamDmaWrites++;
			// The CPU stall is real hardware behavior (513 or 514 cycles depending on
			// alignment, approximated here as a flat 513), not a speed/accuracy trade-off -
			// CpuFastOamDmaStall previously gated this off entirely, meaning "strict" mode
			// (which disables SpeedConfig toggles) made OAM DMA cost zero CPU cycles instead
			// of restoring accuracy. Always apply it.
			PendingCpuStallCycles += 513;
			return; }
		if (address <= 0x4017 && address >= 0x4000)
		{
			int idx = address - 0x4000;
			if (idx >=0 && idx < apuRegLatch.Length) apuRegLatch[idx] = value;
			activeApu.WriteAPURegister(address, value); return;
		}
	// Mapper expansion registers (e.g., MMC5 $5000-$5FFF)
	if (address >= 0x5000 && address < 0x6000)
	{
		cartridge.CPUWrite(address, value);
		// Mirror writes to MMC5 audio range ($5000-$5015)
		if (address <= 0x5015) mmc5Audio?.WriteExp(address, value);
		return;
	}
		if (address >= 0x6000) { cartridge.CPUWrite(address, value); return; }
	}

	// === OAM DMA Fast Path ===
	// If source page is internal RAM (0x0000-0x1FFF mirrors) perform a single BlockCopy instead of 256 bus.Read calls.
	// For now only RAM pages fast path; future: detect linear PRG ROM banks for immediate copy.
	public void FastOamDma(byte page, byte[] destOam, ref byte oamAddr)
	{
		ushort baseAddr = (ushort)(page << 8);
		if (!pageTableInitialized) { // safety fallback
			for (int i=0;i<256;i++) destOam[oamAddr++] = Read((ushort)(baseAddr + i));
			return;
		}
		int pageIndex = baseAddr >> 8;
		
		// Safety bounds check for corrupted state
		if (pageIndex < 0 || pageIndex >= pages.Length)
		{
			// Corrupted page index - use safe fallback
			for (int i=0;i<256;i++) destOam[oamAddr++] = Read((ushort)(baseAddr + i));
			return;
		}
		
		var srcPage = pages[pageIndex];
		bool isRam = srcPage.data == ram; // all mirrors point to same array
		if (isRam)
		{
			// Compute linear offset inside 2KB RAM mirroring
			int mirrorBase = (pageIndex % 0x08) * 0x100; // same as BuildPageTable
			
			// Safety bounds check before BlockCopy
			if (ram != null && destOam != null && 
			    mirrorBase >= 0 && mirrorBase + 256 <= ram.Length &&
			    oamAddr + 256 <= destOam.Length)
			{
				System.Buffer.BlockCopy(ram, mirrorBase, destOam, oamAddr, 256);
				oamAddr = (byte)(oamAddr + 256);
			}
			else
			{
				// Bounds would be violated - use safe per-byte copy with bounds checks
				for (int i = 0; i < 256; i++)
				{
					if (oamAddr < destOam.Length)
						destOam[oamAddr++] = Read((ushort)(baseAddr + i));
					else
						break;
				}
			}
			return;
		}
		// Fallback per-byte for non-linear / mapper controlled sources
		for (int i=0;i<256;i++) destOam[oamAddr++] = Read((ushort)(baseAddr + i));
	}

	// === Debug Peek/Poke (raw CPU address space) ===
	public byte PeekByte(ushort address) => Read(address);
	public void PokeByte(ushort address, byte value) => Write(address, value);
	public byte PeekRam(int index) => (index >=0 && index < ram.Length) ? ram[index] : (byte)0;
	public void PokeRam(int index, byte value) { if (index>=0 && index < ram.Length) ram[index]=value; }

	public void StepAPU(int cpuCycles)
	{
		int apuCycles = cpuCycles;
		if (activeCpu is IApuCycleScaler scaler)
			apuCycles = scaler.ScaleApuCycles(cpuCycles);
		activeApu.Step(apuCycles);
		mmc5Audio?.Step(apuCycles);
		instr.ApuSteps += apuCycles;
	}
	public float[] GetAudioSamples(int max=0)
	{
		var samples = activeApu.GetAudioSamples(max);
		if (samples.Length == 0 || mmc5Audio == null) return samples;
		float add = mmc5Audio.GetCurrentSample();
		if (add == 0f) return samples;
		for (int i = 0; i < samples.Length; i++) samples[i] += add;
		return samples;
	}
	public int GetQueuedSamples() => activeApu.GetQueuedSampleCount();
	public int GetAudioSampleRate() => activeApu.GetSampleRate();

	// Optional: expose expansion audio current sample for other mixers
	public float GetExpansionAudioSample() => mmc5Audio?.GetCurrentSample() ?? 0f;

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
