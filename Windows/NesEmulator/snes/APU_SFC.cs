using System;

namespace NesEmulator.Snes;

/// <summary>
/// The SNES audio unit - SFC family: SPC700 (<see cref="SMP_SFC"/>) + S-DSP (<see cref="DSP_SFC"/>) +
/// 64KB ARAM + three timers + the boot loader, behind the board's <see cref="ISnesApu"/> ports.
///
/// Clocking: the unit has its own 24.576 MHz crystal; the SPC700 runs at 1.024 MHz and the DSP
/// emits one stereo sample every 32 SPC cycles (32 kHz). <see cref="RunTo"/> converts the SNES
/// master clock exactly (integer math, no accumulated drift) and runs whole instructions until
/// the audio unit has caught up.
///
/// Boot loader: this is NOT Nintendo's IPL ROM. It is a clean-room SPC700 routine written for
/// BrokenNes (listing below) that implements the documented upload protocol. Like the original
/// it lives at $FFC0-$FFFF, runs from reset, and can be re-entered by a sound driver jumping to $FFC0.
/// </summary>
public sealed class APU_SFC : ISnesApu
{
    public string CoreName => "SFC";
    public int SampleRate => 32000;

    public readonly byte[] Aram = new byte[0x10000];
    public SMP_SFC Smp { get; }
    public DSP_SFC Dsp { get; }

    private readonly byte[] cpuToApu = new byte[4];   // $2140-3 writes, seen at $F4-$F7
    private readonly byte[] apuToCpu = new byte[4];   // $F4-$F7 writes, seen at $2140-3
    private bool iplEnabled = true;
    private byte dspAddr;

    // Timers 0/1 tick at 8 kHz (every 128 SPC cycles), timer 2 at 64 kHz (every 16).
    private readonly byte[] timerTarget = new byte[3];
    private readonly byte[] timerStage = new byte[3];
    private readonly byte[] timerOut = new byte[3];
    private readonly bool[] timerOn = new bool[3];
    private int timerDiv8k, timerDiv64k;

    private long smpCycles;    // SPC cycles run since reset
    private int dspCycles;     // SPC cycles toward the next DSP sample

    // Output ring buffer (interleaved L,R). When nobody drains it, the oldest audio is dropped.
    private readonly short[] ring = new short[65536];
    private int ringRead, ringCount;

    public long SamplesProduced { get; private set; }

    public APU_SFC()
    {
        Smp = new SMP_SFC(this);
        Dsp = new DSP_SFC(Aram) { Output = PushSample };
        Reset();
    }

    public void Reset()
    {
        Array.Clear(Aram);
        Array.Clear(cpuToApu); Array.Clear(apuToCpu);
        iplEnabled = true; dspAddr = 0;
        Array.Clear(timerTarget); Array.Clear(timerStage); Array.Clear(timerOut); Array.Clear(timerOn);
        timerDiv8k = timerDiv64k = 0;
        smpCycles = 0; dspCycles = 0;
        ringRead = ringCount = 0;
        Dsp.Reset();
        Smp.Reset();
    }

    // =====================================================================================
    //  ISnesApu
    // =====================================================================================

    public byte ReadPort(int port) => apuToCpu[port & 3];
    public void WritePort(int port, byte value) => cpuToApu[port & 3] = value;

    public void RunTo(long masterClock)
    {
        long target = masterClock * 1_024_000 / 21_477_272;
        if (smpCycles >= target) return;
        long t0 = SnesProfiler.Begin();
        while (smpCycles < target)
        {
            int c = Smp.Step();
            smpCycles += c;
            ClockTimers(c);
            dspCycles += c;
            while (dspCycles >= 32)
            {
                dspCycles -= 32;
                long t1 = SnesProfiler.Begin();
                Dsp.RunSample();
                if (t1 != 0) SnesProfiler.DspTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            }
        }
        if (t0 != 0) SnesProfiler.ApuTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    public int ReadSamples(Span<short> dest)
    {
        int n = Math.Min(dest.Length & ~1, ringCount);
        for (int i = 0; i < n; i++) dest[i] = ring[(ringRead + i) & 0xFFFF];
        ringRead = (ringRead + n) & 0xFFFF;
        ringCount -= n;
        return n;
    }

    public string Describe() =>
        $"SFC pc=${Smp.PC:X4} ports={apuToCpu[0]:X2} {apuToCpu[1]:X2} {apuToCpu[2]:X2} {apuToCpu[3]:X2} " +
        $"samples={SamplesProduced:N0} kon/endx=${Dsp.Regs[0x4C]:X2}/${Dsp.Regs[0x7C]:X2}{(Smp.Halted ? " HALTED" : "")}";

    private void PushSample(short l, short r)
    {
        SamplesProduced++;
        if (ringCount > ring.Length - 2) { ringRead = (ringRead + 2) & 0xFFFF; ringCount -= 2; }
        int w = (ringRead + ringCount) & 0xFFFF;
        ring[w] = l; ring[(w + 1) & 0xFFFF] = r;
        ringCount += 2;
    }

    // =====================================================================================
    //  SPC700 memory map
    // =====================================================================================

    public byte SmpRead(ushort a)
    {
        if (a >= 0xF0 && a <= 0xFF) return ReadIo(a);
        if (a >= 0xFFC0 && iplEnabled) return Ipl[a - 0xFFC0];
        return Aram[a];
    }

    public void SmpWrite(ushort a, byte v)
    {
        if (a >= 0xF0 && a <= 0xFF) WriteIo(a, v);
        Aram[a] = v;   // writes always land in RAM, including under the I/O block and the IPL
    }

    private byte ReadIo(ushort a)
    {
        switch (a)
        {
            case 0xF2: return dspAddr;
            case 0xF3: return Dsp.Read(dspAddr & 0x7F);
            case 0xF4: case 0xF5: case 0xF6: case 0xF7: return cpuToApu[a - 0xF4];
            case 0xF8: case 0xF9: return Aram[a];
            case 0xFD: case 0xFE: case 0xFF:
            {
                int t = a - 0xFD;
                byte v = timerOut[t];
                timerOut[t] = 0;   // read-to-clear
                return v;
            }
            default: return 0;     // $F0, $F1, $FA-$FC are write-only
        }
    }

    private void WriteIo(ushort a, byte v)
    {
        switch (a)
        {
            case 0xF1:   // CONTROL
                for (int t = 0; t < 3; t++)
                {
                    bool on = (v & (1 << t)) != 0;
                    if (on && !timerOn[t]) { timerStage[t] = 0; timerOut[t] = 0; }
                    timerOn[t] = on;
                }
                if ((v & 0x10) != 0) { cpuToApu[0] = 0; cpuToApu[1] = 0; }
                if ((v & 0x20) != 0) { cpuToApu[2] = 0; cpuToApu[3] = 0; }
                iplEnabled = (v & 0x80) != 0;
                break;
            case 0xF2: dspAddr = v; break;
            case 0xF3: if (dspAddr < 0x80) Dsp.Write(dspAddr, v); break;
            case 0xF4: case 0xF5: case 0xF6: case 0xF7: apuToCpu[a - 0xF4] = v; break;
            case 0xFA: case 0xFB: case 0xFC: timerTarget[a - 0xFA] = v; break;
        }
    }

    private void ClockTimers(int cycles)
    {
        timerDiv8k += cycles;
        while (timerDiv8k >= 128) { timerDiv8k -= 128; TickTimer(0); TickTimer(1); }
        timerDiv64k += cycles;
        while (timerDiv64k >= 16) { timerDiv64k -= 16; TickTimer(2); }
    }

    private void TickTimer(int t)
    {
        if (!timerOn[t]) return;
        timerStage[t]++;                                     // byte wrap: a target of 0 means 256
        if (timerStage[t] == timerTarget[t]) { timerStage[t] = 0; timerOut[t] = (byte)((timerOut[t] + 1) & 0x0F); }
    }

    // =====================================================================================
    //  Clean-room boot loader (64 bytes at $FFC0)
    // =====================================================================================
    //
    //  FFC0  20        clrp               ; direct page 0 even on a warm re-entry
    //  FFC1  CD EF     mov  x,#$EF
    //  FFC3  BD        mov  sp,x
    //  FFC4  E8 00     mov  a,#$00
    //  FFC6  C6        mov  (x),a         ; clear $01-$EF
    //  FFC7  1D        dec  x
    //  FFC8  D0 FC     bne  $FFC6
    //  FFCA  8F AA F4  mov  $F4,#$AA      ; "ready" signature on ports 0/1
    //  FFCD  8F BB F5  mov  $F5,#$BB
    //  FFD0  78 CC F4  cmp  $F4,#$CC      ; wait for the CPU's kick
    //  FFD3  D0 FB     bne  $FFD0
    //  command:
    //  FFD5  BA F6     movw ya,$F6        ; destination address from ports 2/3 ...
    //  FFD7  DA 02     movw $02,ya        ; ... kept at $02/$03
    //  FFD9  FA F4 F4  mov  $F4,$F4       ; acknowledge by echoing port 0
    //  FFDC  F8 F5     mov  x,$F5         ; port 1: 0 = execute, else transfer
    //  FFDE  D0 03     bne  transfer
    //  FFE0  1F 02 00  jmp  [$0002+x]     ; x = 0: jump to the address
    //  transfer:
    //  FFE3  8D 00     mov  y,#$00
    //  FFE5  7E F4     cmp  y,$F4         ; first byte is index 0
    //  FFE7  D0 FC     bne  $FFE5
    //  byte:
    //  FFE9  E4 F5     mov  a,$F5         ; data (written by the CPU before the index)
    //  FFEB  CB F4     mov  $F4,y         ; acknowledge at once, so the CPU can send the next byte
    //  FFED  D7 02     mov  [$02]+y,a
    //  FFEF  FC        inc  y
    //  FFF0  F0 08     beq  page          ; wrapped: next 256-byte page
    //  poll:
    //  FFF2  7E F4     cmp  y,$F4
    //  FFF4  F0 F3     beq  byte          ; next index arrived
    //  FFF6  10 FA     bpl  poll          ; port 0 still behind: keep waiting
    //  FFF8  2F DB     bra  command       ; jumped ahead by >= 2: new command
    //  page:
    //  FFFA  AB 03     inc  $03
    //  FFFC  2F F4     bra  poll
    //  FFFE  C0 FF     reset vector
    //
    //  Timing is part of the protocol: games pace their uploads on the acknowledgement, so the
    //  steady-state byte loop (byte..beq byte) is 25 SPC cycles with the ack 7 cycles in, the
    //  same as the console's loader. The first version stored before acknowledging (27 cycles,
    //  ack 14 cycles in) and made every boot and song load ~24% slower than hardware (measured
    //  against Mesen 2: SMW reached its first screen 11 frames late).
    private static readonly byte[] Ipl =
    {
        0x20, 0xCD, 0xEF, 0xBD, 0xE8, 0x00, 0xC6, 0x1D, 0xD0, 0xFC, 0x8F, 0xAA, 0xF4, 0x8F, 0xBB, 0xF5,
        0x78, 0xCC, 0xF4, 0xD0, 0xFB, 0xBA, 0xF6, 0xDA, 0x02, 0xFA, 0xF4, 0xF4, 0xF8, 0xF5, 0xD0, 0x03,
        0x1F, 0x02, 0x00, 0x8D, 0x00, 0x7E, 0xF4, 0xD0, 0xFC, 0xE4, 0xF5, 0xCB, 0xF4, 0xD7, 0x02, 0xFC,
        0xF0, 0x08, 0x7E, 0xF4, 0xF0, 0xF3, 0x10, 0xFA, 0x2F, 0xDB, 0xAB, 0x03, 0x2F, 0xF4, 0xC0, 0xFF,
    };
}
