using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The stand-in sound unit that lets a Genesis board run before the real one exists (as <c>APU_HLE</c> does for the SNES): it speaks the whole handshake truthfully, holds the
/// Z80's 8 KB of RAM and the bank register, and plays the PSG when the 68000 writes it directly; it does not run a Z80 and has no FM chip, so a game whose sound driver
/// lives in Z80 RAM is silent. It reports what it is (<see cref="CoreName"/> and <see cref="Describe"/>) so that silence is never mistaken for a working sound system.
/// </summary>
public sealed class GenesisSoundStub : IGenesisSound
{
    private readonly SegaMachine machine;
    private readonly Sn76489 psg;
    private readonly byte[] ram = new byte[0x2000];
    private IGenesisSoundHost? host;
    private bool requested, inReset = true, interrupt;
    private int bank;                // the 9-bit bank register
    private long lastMaster;

    /// <summary>The register writes the PSG saw, for tests and the Mix tools to count.</summary>
    public long PsgWrites { get; private set; }

    public GenesisSoundStub(SegaMachine machine)
    {
        this.machine = machine;
        psg = new Sn76489(machine.Timeline.MasterHz / machine.Timeline.Psg.Divider, machine.SampleRate, PsgVariant.MegaDrive);
    }

    public string CoreName => "STUB";
    public int SampleRate => machine.SampleRate;

    /// <summary>The 68000 address the Z80's $8000-$FFFF window shows (bank register shifted up 15).</summary>
    public int BankBase => bank << 15;
    public Sn76489 Psg => psg;

    public void Attach(IGenesisSoundHost host) => this.host = host;

    public void Reset()
    {
        Array.Clear(ram);
        requested = false; inReset = true; interrupt = false; bank = 0; lastMaster = 0; PsgWrites = 0;
        psg.Reset();
    }

    public void RunTo(long masterClock)
    {
        if (masterClock <= lastMaster) return;
        psg.Run(machine.Timeline.Psg.Due(lastMaster, masterClock));
        lastMaster = masterClock;
    }

    public void WriteBusRequest(bool request, long masterClock) { RunTo(masterClock); requested = request; }

    /// <summary>The stub grants at once: it has no Z80 cycle in progress to wait for. A held reset also reports the bus granted (the Z80 is not running).</summary>
    public bool IsBusGranted(long masterClock) { RunTo(masterClock); return requested; }

    public void WriteReset(bool released, long masterClock)
    {
        RunTo(masterClock);
        inReset = !released;
        if (inReset) psg.Reset();   // RESET also resets the YM2612; the PSG is on the Z80 side of it and goes silent too
    }

    public bool IsInReset => inReset;

    public byte Read(int offset, long masterClock)
    {
        RunTo(masterClock);
        offset &= 0xFFFF;
        if (!requested) return 0xFF;
        if (offset < 0x4000) return ram[offset & 0x1FFF];
        if (offset < 0x6000) return 0x00;            // YM2612 status: never busy, no timer flags
        return 0xFF;
    }

    public void Write(int offset, byte value, long masterClock)
    {
        RunTo(masterClock);
        offset &= 0xFFFF;
        if (!requested) return;
        if (offset < 0x4000) ram[offset & 0x1FFF] = value;
        else if (offset >= 0x6000 && offset < 0x6100) bank = (bank >> 1) | ((value & 1) << 8);
        else if (offset >= 0x7F00 && offset < 0x8000 && (offset & 0xFF) is >= 0x11 and <= 0x17 && (offset & 1) != 0) { psg.Write(value); PsgWrites++; }
    }

    public void SetInterrupt(bool asserted, long masterClock) { RunTo(masterClock); interrupt = asserted; }

    public void WritePsg(byte value, long masterClock) { RunTo(masterClock); psg.Write(value); PsgWrites++; }

    public int ReadSamples(Span<short> dest) => psg.ReadSamples(dest);

    public void SaveState(BinaryWriter w)
    {
        w.Write(ram); w.Write(requested); w.Write(inReset); w.Write(interrupt); w.Write(bank); w.Write(lastMaster);
        psg.SaveState(w);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(ram, 0, ram.Length); requested = r.ReadBoolean(); inReset = r.ReadBoolean(); interrupt = r.ReadBoolean(); bank = r.ReadInt32(); lastMaster = r.ReadInt64();
        psg.LoadState(r);
    }

    public string Describe() =>
        $"STUB sound unit (no Z80, no FM; PSG from the 68000 only): reset={inReset} busreq={requested} bank=${bank:X3} z80-int={interrupt} psg-writes={PsgWrites}";
}
