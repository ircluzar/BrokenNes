namespace NesEmulator
{
	/// <summary>
	/// A read-only, emulator-independent snapshot of the 2A03 sound hardware, for cross-emulator
	/// differential tracing (Workshop --trace, VRUN tools/state_trace.lua on the Mesen side).
	///
	/// WHY THIS EXISTS AS ITS OWN INTERFACE RATHER THAN REUSING IAPU.GetState():
	/// GetState() returns each core's private save-state blob. The field names, units and even the
	/// set of fields differ per core, so it is unusable as a comparison oracle - and reflecting over
	/// it would silently start comparing nothing the day a field is renamed. This interface is a
	/// deliberate, named contract: every value below is defined in HARDWARE terms, so the same
	/// number can be produced by Mesen from emu.getState() and compared digit for digit.
	///
	/// WHAT IS DELIBERATELY *NOT* HERE - and this is the load-bearing design decision:
	/// the sub-instruction phase of each channel (pulse duty position, triangle sequence position,
	/// the noise LFSR, the timer divider counters, the per-cycle channel output level).
	/// Measured on Mesen 2.1.1: emu.getState() reports the APU lazily, from wherever
	/// NesApu::Run() last caught it up, which at an arbitrary instruction boundary is up to ~2500
	/// CPU cycles behind the CPU. Forcing a catch-up (a side-effecting $4015 read) changes exactly
	/// those fields and nothing else - see the measured census in TraceCli's header text. Comparing
	/// them across the two emulators would therefore be comparing two different instants, and they
	/// would diverge on essentially every frame for a harness reason. Everything in this struct is
	/// in the measured lag-immune set: it only changes on a register write or on a quarter/half
	/// frame tick, both of which are instruction-aligned events.
	/// </summary>
	public readonly struct ApuStateSnapshot
	{
		// --- Pulse 1 / Pulse 2 -------------------------------------------------------------
		/// <summary>11-bit raw period register ($4002/$4003 low+high), i.e. Mesen's apu.squareN.realPeriod.</summary>
		public readonly ushort Pulse1Period, Pulse2Period;
		/// <summary>Volume actually in force: the constant-volume parameter, or the envelope decay level.</summary>
		public readonly byte Pulse1Volume, Pulse2Volume;
		/// <summary>Length counter, 0..254.</summary>
		public readonly byte Pulse1Length, Pulse2Length;
		/// <summary>Duty select, 0..3 ($4000/$4004 bits 7-6).</summary>
		public readonly byte Pulse1Duty, Pulse2Duty;
		/// <summary>Sweep enable bit ($4001/$4005 bit 7).</summary>
		public readonly bool Pulse1SweepEnabled, Pulse2SweepEnabled;
		/// <summary>Sweep negate bit ($4001/$4005 bit 3).</summary>
		public readonly bool Pulse1SweepNegate, Pulse2SweepNegate;
		/// <summary>Sweep shift count, 0..7 ($4001/$4005 bits 2-0).</summary>
		public readonly byte Pulse1SweepShift, Pulse2SweepShift;

		// --- Triangle ----------------------------------------------------------------------
		public readonly ushort TrianglePeriod;   // 11-bit raw period register
		public readonly byte TriangleLength;     // length counter
		public readonly byte TriangleLinear;     // linear counter, 0..127

		// --- Noise -------------------------------------------------------------------------
		/// <summary>
		/// Period in CPU CYCLES from the NTSC lookup table (4,8,16,...,4068), NOT the 4-bit register
		/// index and NOT the emulator's internal divider reload. Mesen stores this minus one; the
		/// Mesen tracer adds it back. Choosing the hardware-defined cycle count as the unit is what
		/// makes the two sides comparable at all.
		/// </summary>
		public readonly ushort NoisePeriod;
		public readonly byte NoiseVolume;        // constant-volume parameter or envelope decay
		public readonly byte NoiseLength;        // length counter
		public readonly bool NoiseMode;          // $400E bit 7, the short-mode / 93-step tap

		// --- DMC ---------------------------------------------------------------------------
		public readonly byte DmcOutput;          // 7-bit delta counter / output level, 0..127
		public readonly ushort DmcCurrentAddr;   // the running sample pointer
		public readonly ushort DmcBytesRemaining;
		public readonly bool DmcIrqEnabled;      // $4010 bit 7
		public readonly bool DmcIrqFlag;         // latched DMC IRQ (see the note in the trace header)
		public readonly bool DmcLoop;            // $4010 bit 6

		// --- Frame counter -----------------------------------------------------------------
		public readonly bool FrameMode5;         // $4017 bit 7: false = 4-step, true = 5-step
		public readonly byte FrameStep;          // sequencer position, 0..3 (4-step) or 0..4 (5-step)
		public readonly bool FrameIrqInhibit;    // $4017 bit 6
		public readonly bool FrameIrqFlag;       // latched frame IRQ

		// --- Length-counter enables and halt/constant-volume flags --------------------------
		public readonly bool Pulse1Enabled, Pulse2Enabled, TriangleEnabled, NoiseEnabled;
		public readonly bool Pulse1Halt, Pulse2Halt, TriangleHalt, NoiseHalt;
		public readonly bool Pulse1ConstVol, Pulse2ConstVol, NoiseConstVol;

		public ApuStateSnapshot(
			ushort p1Period, ushort p2Period, byte p1Vol, byte p2Vol, byte p1Len, byte p2Len,
			byte p1Duty, byte p2Duty,
			bool p1SwEn, bool p2SwEn, bool p1SwNeg, bool p2SwNeg, byte p1SwShift, byte p2SwShift,
			ushort triPeriod, byte triLen, byte triLinear,
			ushort noisePeriod, byte noiseVol, byte noiseLen, bool noiseMode,
			byte dmcOutput, ushort dmcCurrentAddr, ushort dmcBytesRemaining,
			bool dmcIrqEnabled, bool dmcIrqFlag, bool dmcLoop,
			bool frameMode5, byte frameStep, bool frameIrqInhibit, bool frameIrqFlag,
			bool p1En, bool p2En, bool triEn, bool noiseEn,
			bool p1Halt, bool p2Halt, bool triHalt, bool noiseHalt,
			bool p1Const, bool p2Const, bool noiseConst)
		{
			Pulse1Period = p1Period; Pulse2Period = p2Period;
			Pulse1Volume = p1Vol; Pulse2Volume = p2Vol;
			Pulse1Length = p1Len; Pulse2Length = p2Len;
			Pulse1Duty = p1Duty; Pulse2Duty = p2Duty;
			Pulse1SweepEnabled = p1SwEn; Pulse2SweepEnabled = p2SwEn;
			Pulse1SweepNegate = p1SwNeg; Pulse2SweepNegate = p2SwNeg;
			Pulse1SweepShift = p1SwShift; Pulse2SweepShift = p2SwShift;
			TrianglePeriod = triPeriod; TriangleLength = triLen; TriangleLinear = triLinear;
			NoisePeriod = noisePeriod; NoiseVolume = noiseVol; NoiseLength = noiseLen; NoiseMode = noiseMode;
			DmcOutput = dmcOutput; DmcCurrentAddr = dmcCurrentAddr; DmcBytesRemaining = dmcBytesRemaining;
			DmcIrqEnabled = dmcIrqEnabled; DmcIrqFlag = dmcIrqFlag; DmcLoop = dmcLoop;
			FrameMode5 = frameMode5; FrameStep = frameStep;
			FrameIrqInhibit = frameIrqInhibit; FrameIrqFlag = frameIrqFlag;
			Pulse1Enabled = p1En; Pulse2Enabled = p2En; TriangleEnabled = triEn; NoiseEnabled = noiseEn;
			Pulse1Halt = p1Halt; Pulse2Halt = p2Halt; TriangleHalt = triHalt; NoiseHalt = noiseHalt;
			Pulse1ConstVol = p1Const; Pulse2ConstVol = p2Const; NoiseConstVol = noiseConst;
		}

		/// <summary>
		/// The value a CPU read of $4015 would return, MINUS the side effect. Bit 5 (open bus) and
		/// bit 7 (DMC IRQ) are deliberately masked off: bit 5 is open bus and therefore not an APU
		/// property at all, and Mesen 2.1.1's emu.getState() exposes no DMC IRQ flag, so the other
		/// side of the diff cannot produce bit 7. Reading the real register would clear the frame
		/// IRQ flag and perturb the very run being traced, which is why this is synthesized.
		/// </summary>
		public byte StatusBits()
		{
			int v = 0;
			if (Pulse1Length > 0) v |= 0x01;
			if (Pulse2Length > 0) v |= 0x02;
			if (TriangleLength > 0) v |= 0x04;
			if (NoiseLength > 0) v |= 0x08;
			if (DmcBytesRemaining > 0) v |= 0x10;
			if (FrameIrqFlag) v |= 0x40;
			return (byte)v;
		}

		/// <summary>Length-counter enables plus the two frame-counter mode bits and the DMC IRQ enable.</summary>
		public byte EnableBits()
		{
			int v = 0;
			if (Pulse1Enabled) v |= 0x01;
			if (Pulse2Enabled) v |= 0x02;
			if (TriangleEnabled) v |= 0x04;
			if (NoiseEnabled) v |= 0x08;
			// bit 4 reserved: Mesen exposes no DMC channel-enable, only bytesRemaining (in StatusBits).
			if (FrameMode5) v |= 0x20;
			if (FrameIrqInhibit) v |= 0x40;
			if (DmcIrqEnabled) v |= 0x80;
			return (byte)v;
		}

		/// <summary>Length-counter halt flags (bits 0-3) and constant-volume flags (bits 4-6).</summary>
		public byte FlagBits()
		{
			int v = 0;
			if (Pulse1Halt) v |= 0x01;
			if (Pulse2Halt) v |= 0x02;
			if (TriangleHalt) v |= 0x04;
			if (NoiseHalt) v |= 0x08;
			if (Pulse1ConstVol) v |= 0x10;
			if (Pulse2ConstVol) v |= 0x20;
			if (NoiseConstVol) v |= 0x40;
			if (DmcLoop) v |= 0x80;
			return (byte)v;
		}

		/// <summary>Sweep enable/negate/shift packed the way $4001 lays them out, minus the period field.</summary>
		public byte Pulse1SweepBits() => PackSweep(Pulse1SweepEnabled, Pulse1SweepNegate, Pulse1SweepShift);
		public byte Pulse2SweepBits() => PackSweep(Pulse2SweepEnabled, Pulse2SweepNegate, Pulse2SweepShift);

		// The 3-bit sweep PERIOD field is left out on purpose: BrokenNes stores it already
		// incremented by one and Mesen's exposed value could not be confirmed to use the same
		// convention, so including it would manufacture a permanent off-by-one "divergence".
		// Its only observable effect is on the period register, which IS compared every frame.
		private static byte PackSweep(bool enabled, bool negate, byte shift)
			=> (byte)((enabled ? 0x80 : 0) | (negate ? 0x08 : 0) | (shift & 0x07));
	}

	/// <summary>
	/// Implemented by an APU core that can hand out an <see cref="ApuStateSnapshot"/>. Optional:
	/// a core that does not implement it simply has no APU trace coverage, and the tracer says so
	/// in its header rather than quietly emitting zeros.
	/// </summary>
	public interface IApuStateProbe
	{
		ApuStateSnapshot ProbeApuState();
	}
}
