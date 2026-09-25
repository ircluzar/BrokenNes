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

		// Absolute CPU cycle at which the current instruction started (-1 = unknown), and the bus
		// access count at that moment; together they give the cycle of any access inside the
		// instruction without adding work to Read/Write, which already count accesses.
		private long instructionStartCycle = -1;
		private long accessCountAtInstructionStart;
		/// <summary>Called by NES.RunFrame before each instruction with the absolute CPU cycle it starts on.</summary>
		public void MarkInstructionStart(long cpuCycle)
		{
			instructionStartCycle = cpuCycle;
			accessCountAtInstructionStart = instr.Reads + instr.Writes;
		}
		// The access being performed now: it has already been counted, hence the -1.
		private long CurrentAccessCycle() => instructionStartCycle + (instr.Reads + instr.Writes - accessCountAtInstructionStart - 1);
		// Parity alignment between NES.globalCpuCycle's origin and the hardware get/put phase.
		public static int OamDmaParityOffset = 0;

		/// <summary>
		/// Set by NES.RunFrame for the frame: true when the PPU is caught up after every instruction
		/// (or within it, in precise mode), so at any CPU access it is at most one instruction
		/// behind. PPU_FIX gates timing-sensitive models on it - see its CollideDataAccessWithRender.
		/// </summary>
		public bool PpuCaughtUpPerInstruction;

		/// <summary>
		/// Closes the window. accessCycles = CPU cycles advanced by bus accesses (one per access);
		/// stallCycles = extra cycles PPU/APU advanced while the CPU sat halted for a DMA.
		/// </summary>
		public (int accessCycles, int stallCycles) EndPreciseWindow()
		{
			preciseWindow = false;
			if (preciseCarryDots > 0) { ppu!.Step(preciseCarryDots); preciseCarryDots = 0; }
			return (preciseAccessCycles, preciseStallCycles);
		}

		// Where in a CPU cycle the bus access falls, in PPU dots. The 3 dots of each cycle used to be
		// stepped entirely BEFORE the access, which left register writes landing a whole CPU cycle
		// (3 dots) later than Mesen 2.1.1 on every one of Bayou Billy's 270 per-line $2005 writes.
		// Now a read sees the PPU 2 dots into its cycle and a write 0; the rest of each cycle's dots
		// carry to the next access (settled at the end of the instruction, so per-instruction totals
		// are unchanged). Fitted to two independent Mesen measurements and exact on both: Bayou's
		// write dots (270/270) and Zelda II's sprite-0-polled title split ($2000/$2006 at 143:154,
		// 184, 196 - a read-timing test). game.nes 6000-frame parity is unchanged.
		private const int PreDotsRead = 2, PreDotsWrite = 0;
		private int preciseCarryDots;
		/// <summary>For CPU_FIX's interrupt-poll timing: the bus access of the running instruction the PPU/APU is
		/// being stepped for (0-based); int.MaxValue in precise mode outside the window (the instruction's tail);
		/// -1 when not in precise stepping at all.</summary>
		public int PreciseInterruptPhase => preciseWindow ? preciseAccessCycles : (PreciseSteppingActive ? int.MaxValue : -1);
		public bool PreciseSteppingActive;
		private void PreciseTick(bool isWrite)
		{
			if (insidePreciseTick) return;
			insidePreciseTick = true;
			int pre = isWrite ? PreDotsWrite : PreDotsRead;
			int now = preciseCarryDots + pre; preciseCarryDots = 3 - pre;
			if (now > 0) ppu!.Step(now);
			StepAPU(1); preciseAccessCycles++;
			// If that cycle triggered a DMA, the CPU is halted for its duration right here - so
			// advance PPU/APU across the stall while the CPU stands still, which is exactly what
			// the DMA does on hardware. Outside this window the same cycles get consumed at the
			// next FlushBatch instead (see ConsumePendingCpuStallCycles).
			int stall = PendingDmcStallCycles;
			if (stall > 0)
			{
				PendingDmcStallCycles = 0;
				if (preciseCarryDots > 0) { ppu!.Step(preciseCarryDots); preciseCarryDots = 0; }
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
		// Which of those latches the ROM has actually written, as a bitmask over the 24 registers.
		// Without this, a core hot-swap replays the whole array - including registers the ROM never
		// touched, whose latch still reads 0 - and a write of 0 to an APU register is NOT a no-op:
		// $4000/$4004/$400C set the envelope start flag (loading the decay counter to 15), $4015
		// silences every channel, $4017 resets the frame sequencer. Those phantom writes gave the
		// incoming core state the cartridge never asked for, which showed up as pulse/noise
		// envelope volumes disagreeing with Mesen on the first frames after a swap.
		private uint apuRegLatchWritten;
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
			if ((apuRegLatchWritten & (1u << i)) == 0) continue; // never written - replaying 0 is a real write
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
			if ((apuRegLatchWritten & (1u << i)) == 0) continue; // never written - replaying 0 is a real write
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
		   apuRegLatchWritten = 0;
		}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	public byte Read(ushort address)
	{
		if (preciseWindow) PreciseTick(false); // see BeginPreciseWindow - normally false, branch is free
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
		if (preciseWindow) PreciseTick(true); // see BeginPreciseWindow - normally false, branch is free
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
			ppu.WritePPURegister(reg, value);
			// Zero cost when nobody is watching: one null test on a reference field, on a path that
			// is already the SLOW path (the page table sends every RAM write straight to pages[],
			// so WriteSlow is only reached for $2000-$3FFF, $4000-$401F and cartridge space). The
			// dispatch itself lives in a separate non-inlined method so the JIT keeps this branch a
			// predictable, never-taken test instead of inlining an observer's body into WriteSlow.
			if (PpuRegisterWriteObserver != null) NotifyPpuRegisterWrite(reg, value);
			return;
		}
		// Writing bit0 to 0x4016 controls controller strobe; apply to both ports
		if (address == 0x4016) { input.Write4016(value); input2.Write4016(value); return; }
		if (address == 0x4014) {
			// The copy itself must not tick the precise window. FastOamDma normally BlockCopies out
			// of RAM and touches no bus at all, but its fallback for mapper-controlled source pages
			// issues 256 Read()s - inside a precise window each of those would advance PPU/APU by a
			// CPU cycle, on top of the flat 513-cycle stall applied just below, and the DMA would
			// cost the PPU roughly 770 cycles instead of 513. The stall is the accounting for this
			// transfer; the reads are not. (`insidePreciseTick` is PreciseTick's own re-entrancy
			// guard, and reusing it here is exactly the same statement: these bus accesses belong to
			// a cycle that has already been charged.)
			bool wasInside = insidePreciseTick; insidePreciseTick = true;
			try { ppu.WriteOAMDMA(value); } finally { insidePreciseTick = wasInside; }
			instr.OamDmaWrites++;
			// The CPU stall is real hardware behavior, not a speed/accuracy trade-off -
			// CpuFastOamDmaStall previously gated this off entirely, meaning "strict" mode
			// (which disables SpeedConfig toggles) made OAM DMA cost zero CPU cycles instead
			// of restoring accuracy. Always apply it.
			//
			// 513 or 514: the DMA must start on an even ("get") CPU cycle, so a write landing on an
			// odd cycle costs one extra alignment cycle. CPU_FIX gets the exact figure; the other
			// CPU cores keep the flat 513 their goldens were recorded with. It matters beyond the
			// cycle itself: a flat 513 drifts the NMI's landing point in the game's idle loop by
			// half a cycle a frame, and Lifeforce stirs its RNG in exactly that loop - measured
			// against Mesen 2.1.1, whose log shows a write on an odd cycle costing 514, even 513.
			PendingCpuStallCycles += (cpu is CPU_FIX && instructionStartCycle >= 0
				&& ((CurrentAccessCycle() + OamDmaParityOffset) & 1) == 1) ? 514 : 513;
			return; }
		if (address <= 0x4017 && address >= 0x4000)
		{
			int idx = address - 0x4000;
			if (idx >=0 && idx < apuRegLatch.Length) { apuRegLatch[idx] = value; apuRegLatchWritten |= 1u << idx; }
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
		if (address >= 0x6000)
		{
			cartridge.CPUWrite(address, value);
			// Same zero-cost-when-unattached shape as the PPU observer above: one null test on a
			// reference field, on a path that is ALREADY the slow path (the page table routes every
			// RAM write straight to pages[], so WriteSlow only ever sees $2000-$3FFF, $4000-$401F
			// and cartridge space). The $8000 test is second so the common unattached case costs a
			// single predictable never-taken branch. The dispatch lives in a separate non-inlined
			// method so the JIT cannot pull an observer body into WriteSlow.
			//
			// Restricted to $8000-$FFFF because that is where every mapper's control registers live
			// (and, on mapper 30's flash variant, the flash command protocol); $6000-$7FFF is cart
			// WRAM, whose traffic is ordinary data and would bury the register writes.
			if (MapperRegisterWriteObserver != null && address >= 0x8000) NotifyMapperRegisterWrite(address, value);
			return;
		}
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
	// === PPU register write observer ===================================================
	//
	// Fires once for every CPU write that reaches a PPU register ($2000-$2007, and therefore every
	// mirror of them up to $3FFF), carrying the register, the byte, and WHERE IN THE FRAME the
	// write landed. That last part is the whole point: a $2005/$2007 write is only safe inside
	// vblank, and "did this write land on a visible scanline" is a question no amount of
	// after-the-fact memory comparison can answer - the evidence is gone by the end of the frame.
	// See Workshop/VrunCorruptCli.cs (the late-PPU-write check) for the consumer this was added for.
	//
	// Contract:
	//   * called AFTER ppu.WritePPURegister has applied the write, so an observer that wants to
	//     read back the effect (e.g. the new PPUMASK) sees the post-write state.
	//   * `mask` is PPUMASK sampled at that same instant. It is passed rather than left for the
	//     observer to fetch because "was the PPU rendering when this happened" has to be answered
	//     at the write's instant; asking a frame later is a different question with a different
	//     answer.
	//   * `scanline`/`dot` are -1/-1 when the active PPU core does not implement IPpuProbe (most
	//     of the gimmick cores do not). An observer must treat -1 as "unknown", never as a line
	//     number. Mask is 0 in that case for the same reason - see IPpuProbe's doc comment.
	//   * `scanline`/`dot` carry the batching lag documented on PPU_FIX's IPpuProbe members: the
	//     PPU is caught up between CPU instructions, not between CPU cycles.
	//
	// Instance state, not static: two NES instances in one process (the desktop app supports it)
	// must not see each other's writes.
	public delegate void PpuRegisterWriteHandler(ushort reg, byte value, int scanline, int dot, byte mask);
	public PpuRegisterWriteHandler? PpuRegisterWriteObserver;

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private void NotifyPpuRegisterWrite(ushort reg, byte value)
	{
		var obs = PpuRegisterWriteObserver;
		if (obs == null) return;
		if (ppu is IPpuProbe probe) obs(reg, value, probe.ProbeScanline, probe.ProbeDot, probe.ProbeMask);
		else obs(reg, value, -1, -1, 0);
	}

	// === Mapper register write observer ==============================================
	//
	// The mapper-side twin of PpuRegisterWriteObserver, and it exists for the same reason: a
	// per-frame sample cannot see a register that is written and restored INSIDE one frame.
	//
	// Concretely, and this is the measurement that motivated it: a frame-boundary sample of
	// mapper 30's CHR bank on a 60,000-frame VRUN run reported bank 0 on 59,997 frames and bank 1
	// on 3. Not because the game barely banks CHR - it banks mid-frame, several times per frame,
	// and restores bank 0 before the frame ends. "Zero divergences" from such a sample is not
	// evidence of anything. A per-WRITE stream is, because it records the event itself rather
	// than the state left behind afterwards.
	//
	// Contract:
	//   * fires AFTER cartridge.CPUWrite has applied the write, so PrgBank/ChrBank/Mirroring are
	//     the values the write PRODUCED, not the ones it replaced.
	//   * only $8000-$FFFF; see the call site for why $6000-$7FFF is excluded.
	//   * fires on EVERY write in that range, whether or not the mapper did anything with it -
	//     including the flash command bytes of a mapper-30 battery cart, which change no bank at
	//     all. A comparison against another emulator has to see the writes it ignored too, or a
	//     one-sided extra write shifts the whole stream and every later line "diverges".
	//   * Scanline/Dot are -1/-1 when the PPU core does not implement IPpuProbe, and carry the
	//     same up-to-one-instruction batching lag documented there. Never read -1 as a line.
	//   * EffectiveValue is the byte the BANK REGISTER most recently latched, which is this write's
	//     byte exactly when LatchCount advanced on this write. It differs from Value only on boards
	//     with BUS CONFLICTS, where the latched byte is the CPU's byte ANDed with the ROM byte at
	//     the same address. Mapper 30's flash variant has no conflict (the write is decoded by the
	//     flash chip, not driven against a ROM output), so the two are equal there - but a harness
	//     must not assume that, because the plain-ROM variant of the same mapper does conflict.
	//     When LatchCount did NOT advance the mapper ignored this write (a flash command byte, say)
	//     and EffectiveValue still shows the previous latch; that is a fact about the write, not a
	//     bug, and the consumer should print it as "no latch" rather than as this write's value.
	//   * Instance state, not static: two NES instances in one process must not see each other's
	//     writes.
	public readonly struct MapperRegisterWrite
	{
		public readonly ushort Address;
		public readonly byte Value;           // byte the CPU drove onto the bus
		public readonly byte EffectiveValue;  // after any bus conflict; == Value when there is none
		// Bank-register latches since power-on. The consumer diffs it across consecutive writes to
		// tell "this write reprogrammed the register" from "the mapper ignored this write" - a
		// question the bank numbers alone cannot answer, since rewriting the same value changes
		// nothing observable. -1 when the mapper exposes no IMapperRegisterProbe.
		public readonly long LatchCount;
		public readonly int PrgBank;          // 16KB bank index visible at $8000, -1 if unresolvable
		public readonly int ChrBank;          // mapper's CHR bank signature (mapper 30: 0-3)
		public readonly int Mirroring;        // (int)Cartridge.mirroringMode after the write
		// Mapper-private, mapper-defined: for mapper 30 it packs the flash command state machine.
		// Compared only for interest, never as a divergence criterion - two emulators can model the
		// same flash chip with different internal encodings and still behave identically.
		public readonly int MapperStatus;
		public readonly int Scanline;
		public readonly int Dot;

		public MapperRegisterWrite(ushort address, byte value, byte effectiveValue, long latchCount,
			int prgBank, int chrBank, int mirroring, int mapperStatus, int scanline, int dot)
		{
			Address = address; Value = value; EffectiveValue = effectiveValue; LatchCount = latchCount;
			PrgBank = prgBank; ChrBank = chrBank; Mirroring = mirroring; MapperStatus = mapperStatus;
			Scanline = scanline; Dot = dot;
		}
	}

	/// <summary>Optional mapper-side detail for MapperRegisterWriteObserver. Declared here rather
	/// than on IMapper so that adding it costs nothing to the two dozen mappers that would only
	/// ever return defaults - a mapper opts in by implementing it, and the observer degrades to
	/// EffectiveValue == Value / LatchCount == -1 for every mapper that does not.</summary>
	public interface IMapperRegisterProbe
	{
		byte ProbeLastRegisterValue { get; }
		long ProbeRegisterLatchCount { get; }
		int ProbeMapperStatus { get; }
	}

	public delegate void MapperRegisterWriteHandler(in MapperRegisterWrite write);
	public MapperRegisterWriteHandler? MapperRegisterWriteObserver;

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private void NotifyMapperRegisterWrite(ushort address, byte value)
	{
		var obs = MapperRegisterWriteObserver;
		if (obs == null) return;
		var mapper = cartridge?.mapper;
		if (mapper == null) return;

		// $8000 rather than the written address on purpose: the question the log answers is "which
		// bank is the CPU looking at now", and on a UxROM-style board only the LOW window moves.
		// Resolving the written address instead would report the fixed bank for any write above
		// $BFFF, which is exactly where mapper 30's flash carts put their bank-select writes.
		int prgBank = mapper.TryCpuToPrgIndex(0x8000, out int prgIndex) ? prgIndex / 0x4000 : -1;
		int chrBank = (int)mapper.GetChrBankSignature();
		int mirroring = (int)cartridge.mirroringMode;

		byte effective = value;
		long latchCount = -1;
		int status = 0;
		if (mapper is IMapperRegisterProbe mp)
		{
			effective = mp.ProbeLastRegisterValue;
			latchCount = mp.ProbeRegisterLatchCount;
			status = mp.ProbeMapperStatus;
		}

		int sl = -1, dot = -1;
		if (ppu is IPpuProbe probe) { sl = probe.ProbeScanline; dot = probe.ProbeDot; }

		var w = new MapperRegisterWrite(address, value, effective, latchCount, prgBank, chrBank, mirroring, status, sl, dot);
		obs(in w);
	}

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
