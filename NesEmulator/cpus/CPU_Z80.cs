using System;

namespace NesEmulator
{
  public sealed class CPU_Z80 : ICPU
  {
    // Z80-flavored metadata (still a 6502 core under the hood)
    public string CoreName => "Z80 Chip";
    public string Description => "Why would you even think that this works?";
    public int Performance => -200;
    public int Rating => 0;
    public string Category => "Experimental";

    // Delegate all functional work to a proven 6502 core, keeping compatibility intact.
    private readonly ICPU inner;
    private readonly Bus bus; // keep a direct line to the NES bus so we can map SMS-like port IO

    private static readonly Random rng = new Random();

    // Z80 personality state (purely cosmetic/introspective, does not affect correctness)
    private byte refreshR; // Z80 refresh register vibes
    private byte iRegister; // Z80 I register spirit
    private bool iff1, iff2; // interrupt flip-flops for vibe only
    private ushort wz; // WZ scratch pair used by some Z80 flows (cosmetic)
    private ushort af, bc, de, hl; // primary bank mirrors of 6502 registers
    private ushort shadowAF, shadowBC, shadowDE, shadowHL; // shadow bank to mimic EXX/EX AF,AF'
    private bool shadowBankActive;
    private int driftCycles; // accumulate cycles to occasionally rotate shadow bank
    private int pendingCyclePenalty; // accumulates port stall or other penalties to fold into the next return value
    private long instructionCounter; // tracks executed instructions to drive cadence-based chaos

    // === Z80 Chaos Flags (randomized each Reset/enable) ===
    // Master System vibe: swaps ghost banks and enables port/PPU/APU flavor mapping
    public bool MasterSystemVibeEnabled { get; set; } = true;
    // How many CPU cycles between ghost-bank swaps (vibe intensity). Randomized each boot.
    public int SoulCrushIntensity { get; set; } = 341; // default ~ NTSC scanline
    // Extra cycles to inject on port accesses to feel heavier. Randomized each boot.
    public int PortStallPenaltyCycles { get; set; } = 2;

    // Emits Z80 “reality pulses” every instruction for AV overlays. Randomized each boot.
    public bool RealityWarpEnabled { get; set; } = true;
    // Pulse interval (cycles) controlling pulse density. Randomized each boot.
    public int WarpPulseInterval { get; set; } = 113;
    // Listener for pulses (must be set by host; not randomized).
    public Action<Z80Pulse>? OnRealityPulse { get; set; }

    // Adds phantom cycles to distort timing. Randomized each boot.
    public bool UnsafeInstructionJitter { get; set; } = true;
    public int JitterMagnitudeCycles { get; set; } = 5;

    // Shifts PC to mangle instruction flow. Randomized each boot.
    public bool UnsafePhaseShiftPC { get; set; } = true;
    public int PhaseShiftStride { get; set; } = 1;

    // Inserts idle bubbles every few instructions. Randomized each boot.
    public bool TemporalStutterEnabled { get; set; } = true;
    public int TemporalStutterInterval { get; set; } = 16;
    public int TemporalStutterCycles { get; set; } = 3;

    // Introduces occasional PC phase noise to bend execution. Randomized each boot.
    public bool PhaseNoiseEnabled { get; set; } = true;
    public int PhaseNoiseChancePercent { get; set; } = 8;
    public int PhaseNoiseSpan { get; set; } = 1;

    // Penalizes refresh wrap to emulate bus steal. Randomized each boot.
    public bool RefreshWrapPenaltyEnabled { get; set; } = true;
    public int RefreshWrapPenaltyCycles { get; set; } = 1;

    // Throttles when drift overheats to simulate contention. Randomized each boot.
    public bool HeatThrottleEnabled { get; set; } = true;
    public int HeatPenaltyThreshold { get; set; } = 2048;
    public int HeatPenaltyCycles { get; set; } = 12;

    // Auto-randomize all chaos flags on each Reset/enable.
    public bool AutoRandomizeFeatures { get; set; } = true;

    public CPU_Z80(Bus bus)
    {
      // Compatibility-first: use the LOW core as the execution engine.
      this.bus = bus;
      inner = new CPU_LOW(bus);
      Reset();
    }

    public bool IgnoreInvalidOpcodes
    {
      get => inner.IgnoreInvalidOpcodes;
      set => inner.IgnoreInvalidOpcodes = value;
    }

    public void Reset()
    {
      inner.Reset();
      refreshR = 0;
      iRegister = 0;
      iff1 = iff2 = false;
      wz = 0;
      shadowAF = shadowBC = shadowDE = shadowHL = 0;
      shadowBankActive = false;
      driftCycles = 0;
      instructionCounter = 0;
      if (AutoRandomizeFeatures) RandomizeFeatureSet();
      Mirror6502IntoZ80View();
    }

    public int ExecuteInstruction()
    {
      TickRefreshRegister();
      instructionCounter++;
      int cycles = inner.ExecuteInstruction();
      driftCycles += cycles;

      if (HeatThrottleEnabled && HeatPenaltyThreshold > 0 && HeatPenaltyCycles > 0 && driftCycles >= HeatPenaltyThreshold)
      {
        int hits = driftCycles / HeatPenaltyThreshold;
        int penalty = hits * HeatPenaltyCycles;
        pendingCyclePenalty += penalty;
        driftCycles -= hits * HeatPenaltyThreshold;
      }

      if (RealityWarpEnabled)
      {
        EmitPulse(cycles);
      }

      // Periodically swap ghost banks to keep the Z80 dual-register attitude alive without touching real state.
      if (MasterSystemVibeEnabled && SoulCrushIntensity > 0 && driftCycles >= SoulCrushIntensity)
      {
        driftCycles -= SoulCrushIntensity;
        RotateShadowBank();
      }

      // Gameplay-altering knobs (now default-on)
      if (UnsafeInstructionJitter && JitterMagnitudeCycles > 0)
      {
        cycles += JitterMagnitudeCycles;
        driftCycles += JitterMagnitudeCycles;
      }

      if (UnsafePhaseShiftPC && PhaseShiftStride != 0)
      {
        inner.AddToPC(PhaseShiftStride);
      }

      if (TemporalStutterEnabled && TemporalStutterInterval > 0 && TemporalStutterCycles > 0 && instructionCounter % TemporalStutterInterval == 0)
      {
        cycles += TemporalStutterCycles;
        driftCycles += TemporalStutterCycles;
      }

      if (PhaseNoiseEnabled && PhaseNoiseChancePercent > 0 && PhaseNoiseSpan > 0 && rng.Next(0, 100) < PhaseNoiseChancePercent)
      {
        int offset = rng.Next(-PhaseNoiseSpan, PhaseNoiseSpan + 1);
        if (offset != 0)
        {
          inner.AddToPC(offset);
          pendingCyclePenalty += Math.Abs(offset);
        }
      }

      // Fold any accumulated bus/port penalties into the cycle return once per instruction.
      if (pendingCyclePenalty != 0)
      {
        cycles += pendingCyclePenalty;
        driftCycles += pendingCyclePenalty;
        pendingCyclePenalty = 0;
      }

      Mirror6502IntoZ80View();
      return cycles;
    }

    public void RequestIRQ(bool line)
    {
      inner.RequestIRQ(line);
      if (MasterSystemVibeEnabled) iff1 = line; // track vibe flip-flop (cosmetic)
    }

    public void RequestNMI()
    {
      inner.RequestNMI();
      if (MasterSystemVibeEnabled) iff2 = true; // treat as latent non-maskable flip-flop
    }

    public object GetState() => new Z80State
    {
      Inner = inner.GetState(),
      RefreshR = refreshR,
      I = iRegister,
      Iff1 = iff1,
      Iff2 = iff2,
      WZ = wz,
      AF = af,
      BC = bc,
      DE = de,
      HL = hl,
      ShadowAF = shadowAF,
      ShadowBC = shadowBC,
      ShadowDE = shadowDE,
      ShadowHL = shadowHL,
      ShadowBankActive = shadowBankActive,
      DriftCycles = driftCycles
    };

    public void SetState(object state)
    {
      switch (state)
      {
        case Z80State snap:
          inner.SetState(snap.Inner);
          refreshR = snap.RefreshR;
          iRegister = snap.I;
          iff1 = snap.Iff1;
          iff2 = snap.Iff2;
          wz = snap.WZ;
          af = snap.AF;
          bc = snap.BC;
          de = snap.DE;
          hl = snap.HL;
          shadowAF = snap.ShadowAF;
          shadowBC = snap.ShadowBC;
          shadowDE = snap.ShadowDE;
          shadowHL = snap.ShadowHL;
          shadowBankActive = snap.ShadowBankActive;
          driftCycles = snap.DriftCycles;
          break;
        default:
          inner.SetState(state);
          Mirror6502IntoZ80View();
          break;
      }
    }

    public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => inner.GetRegisters();
    public void AddToPC(int delta) => inner.AddToPC(delta);

    // --- Master System flavored I/O (cosmetic helpers) ---
    public byte ReadPort(byte port)
    {
      if (!MasterSystemVibeEnabled) return 0xFF;
      byte value = port switch
      {
        0xBE => bus.Read(0x2007), // map to PPU data
        0xBF => bus.Read(0x2002), // map to PPU status
        0x7E => bus.Read(0x2004), // OAM data feels like VRAM read
        0x7F => bus.Read(0x2005), // PPU scroll latch
        0x3E => bus.Read(0x4015), // APU status
        _    => 0xFF
      };
      // Optional stall feel (does not affect inner timing; only reported)
      driftCycles += PortStallPenaltyCycles;
      pendingCyclePenalty += PortStallPenaltyCycles;
      return value;
    }

    public void WritePort(byte port, byte value)
    {
      if (!MasterSystemVibeEnabled) return;
      switch (port)
      {
        case 0xBE: bus.Write(0x2007, value); break; // PPU data
        case 0xBF: bus.Write(0x2006, value); break; // PPU address latch approximation
        case 0x7E: bus.Write(0x2004, value); break; // OAM data write
        case 0x7F: bus.Write(0x2000, value); break; // PPU control to mimic VDP control flavor
        case 0x3E: bus.Write(0x4015, value); break; // APU status/control
        default: break;
      }
      driftCycles += PortStallPenaltyCycles;
      pendingCyclePenalty += PortStallPenaltyCycles;
    }

    public byte GetRefreshR() => refreshR;
    public byte GetIRegister() => iRegister;
    public void SetIRegister(byte value) { iRegister = value; }
    public (bool IFF1, bool IFF2) GetInterruptFlipFlops() => (iff1, iff2);


    private void EmitPulse(int cycles)
    {
      // Pulse conveys Z80-ness to external systems without mutating NES state.
      int phase = (refreshR << 8) | iRegister;
      int intensity = (cycles << 1) ^ phase ^ wz;
      int pseudoAudio = (phase ^ intensity) & 0xFF;
      int pseudoVisual = (phase >> 1) & 0xFF;
      int mangled = af ^ bc ^ de ^ hl ^ wz;

      // Consumer can render scanline shaders or play tones based on this pulse.
      OnRealityPulse?.Invoke(new Z80Pulse
      {
        CycleContribution = cycles,
        Phase = phase,
        Intensity = intensity,
        AudioByte = (byte)pseudoAudio,
        VisualByte = (byte)pseudoVisual,
        MangledSignature = (ushort)mangled,
        PC = hl,
        AF = af,
        BC = bc,
        DE = de,
        HL = hl,
        RefreshR = refreshR,
        I = iRegister,
        WZ = wz,
      });

      // Optional cadence bump for more pulse density.
      driftCycles += WarpPulseInterval;
    }


    private void TickRefreshRegister()
    {
      // Preserve top bit, bump low 7 bits like Z80 R.
      refreshR = (byte)(((refreshR + 1) & 0x7F) | (refreshR & 0x80));
      bool wrapped = (refreshR & 0x7F) == 0;
      if (MasterSystemVibeEnabled)
      {
        // let WZ shadow track refresh heartbeat for vibe
        wz = (ushort)((wz + 1) & 0xFFFF);
      }

      if (RefreshWrapPenaltyEnabled && wrapped && RefreshWrapPenaltyCycles > 0)
      {
        pendingCyclePenalty += RefreshWrapPenaltyCycles;
      }
    }

    private void Mirror6502IntoZ80View()
    {
      var (pc, a, x, y, p, sp) = inner.GetRegisters();
      af = (ushort)((a << 8) | p);
      bc = (ushort)((x << 8) | y); // let X/Y play the BC duo
      de = (ushort)(0x0100 | (sp & 0xFF)); // stack page as DE to honor NES stack page feel
      hl = pc; // PC maps cleanly to HL for quick peeks

      if (MasterSystemVibeEnabled)
      {
        // tie the I register loosely to high byte of PC for a Z80-ish interrupt vector vibe
        iRegister = (byte)(pc >> 8);
      }
    }

    private void RotateShadowBank()
    {
      shadowBankActive = !shadowBankActive;
      (af, shadowAF) = (shadowAF == 0 ? af : shadowAF, af);
      (bc, shadowBC) = (shadowBC == 0 ? bc : shadowBC, bc);
      (de, shadowDE) = (shadowDE == 0 ? de : shadowDE, de);
      (hl, shadowHL) = (shadowHL == 0 ? hl : shadowHL, hl);
    }

    private void RandomizeFeatureSet()
    {
      // Random toggles for vibe controls
      MasterSystemVibeEnabled = rng.Next(0, 2) == 0;
      RealityWarpEnabled = rng.Next(0, 2) == 0;
      UnsafeInstructionJitter = rng.Next(0, 2) == 0;
      UnsafePhaseShiftPC = rng.Next(0, 2) == 0;
      TemporalStutterEnabled = rng.Next(0, 2) == 0;
      PhaseNoiseEnabled = rng.Next(0, 2) == 0;
      RefreshWrapPenaltyEnabled = rng.Next(0, 2) == 0;
      HeatThrottleEnabled = rng.Next(0, 2) == 0;

      // Random intensities within reasonable bounds
      SoulCrushIntensity = 120 + rng.Next(0, 401); // 120..520 cycles
      PortStallPenaltyCycles = rng.Next(0, 5); // 0..4 cycles
      JitterMagnitudeCycles = rng.Next(0, 8); // 0..7 cycles

      int stridePick = rng.Next(-1, 3); // -1,0,1,2
      PhaseShiftStride = stridePick;

      WarpPulseInterval = 60 + rng.Next(0, 181); // 60..240 cycles
      TemporalStutterInterval = 4 + rng.Next(0, 29); // 4..32 instructions
      TemporalStutterCycles = 1 + rng.Next(0, 5); // 1..5 cycles
      PhaseNoiseChancePercent = rng.Next(0, 16); // 0..15%
      PhaseNoiseSpan = 1 + rng.Next(0, 3); // 1..3 bytes
      RefreshWrapPenaltyCycles = rng.Next(0, 4); // 0..3 cycles
      HeatPenaltyThreshold = 512 + rng.Next(0, 3585); // 512..4096 drift cycles
      HeatPenaltyCycles = 4 + rng.Next(0, 21); // 4..24 cycles
    }

    private sealed class Z80State
    {
      public object Inner { get; set; } = new object();
      public byte RefreshR { get; set; }
      public byte I { get; set; }
      public bool Iff1 { get; set; }
      public bool Iff2 { get; set; }
      public ushort WZ { get; set; }
      public ushort AF { get; set; }
      public ushort BC { get; set; }
      public ushort DE { get; set; }
      public ushort HL { get; set; }
      public ushort ShadowAF { get; set; }
      public ushort ShadowBC { get; set; }
      public ushort ShadowDE { get; set; }
      public ushort ShadowHL { get; set; }
      public bool ShadowBankActive { get; set; }
      public int DriftCycles { get; set; }
    }

    public sealed class Z80Pulse
    {
      public int CycleContribution { get; set; }
      public int Phase { get; set; }
      public int Intensity { get; set; }
      public byte AudioByte { get; set; }
      public byte VisualByte { get; set; }
      public ushort MangledSignature { get; set; }
      public ushort PC { get; set; }
      public ushort AF { get; set; }
      public ushort BC { get; set; }
      public ushort DE { get; set; }
      public ushort HL { get; set; }
      public byte RefreshR { get; set; }
      public byte I { get; set; }
      public ushort WZ { get; set; }
    }
  }
}
