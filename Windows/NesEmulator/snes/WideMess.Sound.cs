using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The WideMess sound chip (docs/projects/widemess-spec.md section 4; menu id <c>WMS</c>): <see cref="APU_SFC"/> itself - the SPC700, the S-DSP, the boot loader, the 64 KB, all
/// untouched - plus 960 KB of sound RAM above the SPC700's 64 KB, the S-DSP's voices 8-31 (<c>DSP_SFC.WideMess.cs</c>) and the main CPU's sound RAM port ($21F0-$21F3, through
/// <see cref="WmsExtension"/>). Locked (power-on, reset, a SNES game) nothing of that is live, so it sounds bit-identical to <c>SFC</c>; the main CPU's unlock turns it on
/// (<see cref="Unlock"/>). Its SNES part saves as <see cref="APU_SFC"/>'s state, the WideMess part as <see cref="SaveWide"/> (the session's WideMess payload).
/// </summary>
public sealed class APU_WMS : APU_SFC, ISnesApu
{
    public const int RamBytes = DSP_SFC.SoundRamBytes;

    /// <summary>Sound RAM $10000-$FFFFF (the first 64 KB is <see cref="APU_SFC.Aram"/>).</summary>
    public readonly byte[] SoundRamHigh = new byte[RamBytes - 0x10000];
    private int portAddress;

    public APU_WMS() { Dsp.AttachWide(SoundRamHigh); }

    public new string CoreName => "WMS";

    /// <summary>True once the console's unlock reached the sound unit (until the next reset).</summary>
    public bool Unlocked => Dsp.WideOn;

    public new void Reset()
    {
        base.Reset();
        PowerOnWide();
    }

    /// <summary>The WideMess part as at power-on (locked, the sound RAM above 64 KB cleared, voices 8-31 off); the SNES part is left as it is.</summary>
    public void PowerOnWide()
    {
        Array.Clear(SoundRamHigh);
        portAddress = 0;
        Dsp.ResetWide();
    }

    /// <summary>The unlock line (spec 4): the main CPU's WMUNLOCK unlocks the sound unit too.</summary>
    public void Unlock() => Dsp.SetWideOn(true);

    /// <summary>A byte of the 1 MB sound RAM (tools and tests).</summary>
    public byte SoundRam(int address) { address &= RamBytes - 1; return address < 0x10000 ? Aram[address] : SoundRamHigh[address - 0x10000]; }
    private void SetSoundRam(int address, byte value) { address &= RamBytes - 1; if (address < 0x10000) Aram[address] = value; else SoundRamHigh[address - 0x10000] = value; }

    /// <summary>The port's address (tests).</summary>
    public int PortAddress => portAddress;

    /// <summary>A main-CPU (or DMA) write to $21F0-$21F7, made at <paramref name="masterClock"/>; the board hands it over only once unlocked.</summary>
    public void WriteSoundPort(byte reg, byte value, long masterClock)
    {
        switch (reg)
        {
            case 0xF0: portAddress = (portAddress & 0xFFF00) | value; return;
            case 0xF1: portAddress = (portAddress & 0xF00FF) | value << 8; return;
            case 0xF2: portAddress = (portAddress & 0x0FFFF) | (value & 0x0F) << 16; return;
            case 0xF3:
                RunTo(masterClock);                     // the byte lands now: the SPC700 and the DSP have run up to the write
                SetSoundRam(portAddress, value);
                portAddress = (portAddress + 1) & (RamBytes - 1);
                return;
        }
    }

    /// <summary>A main-CPU (or DMA) read of $21F0-$21F7 at <paramref name="masterClock"/>: the data port; the address registers are write-only (open bus); the rest reserved (0).</summary>
    public byte ReadSoundPort(byte reg, byte openBus, long masterClock)
    {
        if (reg < 0xF3) return openBus;
        if (reg > 0xF3) return 0;
        RunTo(masterClock);
        byte v = SoundRam(portAddress);
        portAddress = (portAddress + 1) & (RamBytes - 1);
        return v;
    }

    public new string Describe() => base.Describe() + (Unlocked ? " | WideMess sound unlocked" : "");

    private const byte WideStateVersion = 1;

    /// <summary>The WideMess part of the unit's state: the port, the sound RAM above 64 KB, voices 8-31 and the second page.</summary>
    public void SaveWide(BinaryWriter w)
    {
        SnesStateIo.Begin(w, WideStateVersion);
        w.Write(portAddress);
        SnesStateIo.Put(w, SoundRamHigh);
        Dsp.SaveWide(w);
    }

    public void LoadWide(BinaryReader r)
    {
        SnesStateIo.Expect(r, WideStateVersion, "WideMess sound");
        portAddress = r.ReadInt32() & (RamBytes - 1);
        SnesStateIo.Get(r, SoundRamHigh);
        Dsp.LoadWide(r);
    }
}
