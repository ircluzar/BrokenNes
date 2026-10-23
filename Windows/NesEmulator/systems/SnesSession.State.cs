using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Snes;

namespace NesEmulator.Systems;

/// <summary>
/// The SNES session's save states (the shared contract in <c>ConsoleSystems.cs</c>, the container in <c>ConsoleStates.cs</c>). The payload is the board (<see cref="BOARD_SFC.SaveState"/>: scheduler, 65816, S-PPU, work RAM, cartridge RAM,
/// the cartridge chip) followed by the sound unit that was playing, whichever it is:
/// <list type="bullet">
/// <item><b>SFC</b> (the SPC700 + S-DSP): its whole state.</item>
/// <item><b>NES:&lt;chip&gt;</b> (the game's sound replayed on a NES sound chip): the SPC700 + S-DSP that keeps running the game's sound program behind the bridge, whole. The NES chip is a listener, not part of the machine: it keeps playing
/// what it was and listens to the loaded voices from the next sync on (<see cref="NesApuOnSnes.Rebase"/>), so the audio of such a session after a load is alive but not sample-identical to the session that carried on; the machine, the pictures and the SPC700 / S-DSP are.</item>
/// <item><b>HLE</b> (the silent stand-in): the stand-in's state, and the real SPC700 + S-DSP that was swapped out behind it, if the game ran on one first (swapping back resumes it).</item>
/// </list>
/// A state belongs to the sound unit it was saved with (<c>apu</c> in the header) and to the cartridge chip it ran with (<c>chip</c>: the homemade DSP-1 and Nintendo's firmware are different chips, and the firmware's hash is part of the id):
/// the header is checked before a byte of the machine is touched. The picture chip is a view of the S-PPU (the NES chips draw it from the S-PPU's registers line by line), so it is not part of a state; a bridge is rebuilt from the loaded machine.
/// <para><b>Where a state can be taken</b>: between two 65816 instructions, i.e. at the frame boundary <see cref="RunFrame"/> stops at (the apps' save point) and at any line <see cref="RunToLine"/> reaches. DMA / HDMA run inside the write
/// that starts them, so the machine is never stopped inside a transfer.</para>
/// </summary>
public sealed partial class SnesSession
{
    /// <summary>The layout of the payload: bump it whenever the board, a core or a chip changes what it writes, so an old state is refused rather than misread.</summary>
    public const string StateFormat = "sfc-1";
    private const byte PayloadVersion = 1;
    private const byte UnitSfc = 0, UnitHle = 1, UnitNes = 2, UnitChip = 3;

    public bool SupportsState => true;

    /// <summary>The parts a state depends on and that must be equal for it to load.</summary>
    private Dictionary<string, string> StateUnits() => new()
    {
        ["apu"] = apuId.ToUpperInvariant(),
        ["chip"] = board.Coprocessor?.StateId ?? "none",
        ["format"] = StateFormat,
    };

    /// <summary>Tool / test: runs to the first instruction boundary on or after a scanline of the current frame (see <see cref="BOARD_SFC.RunToLine"/>); a state can be taken there.</summary>
    public void RunToLine(int line) { mid = null; board.RunToLine(line); }

    // The SNES board accessor (`Board`) is declared once, in ConsoleSessions.cs (the corruption library's accessors and this state code both wanted it).

    /// <summary>The real SPC700 + S-DSP while the stand-in unit plays in its place (tests).</summary>
    internal APU_SFC? ParkedSpc => parkedSpc;

    /// <summary>The raw payload (no container, no compression): what the state test changes one field at a time.</summary>
    internal byte[] PayloadForTest() => SnapshotPayload();

    /// <summary>Loads a raw payload with no header check and no undo (the state test feeds it values a loader may refuse).</summary>
    internal void LoadPayloadForTest(byte[] payload)
    {
        using var r = new BinaryReader(new MemoryStream(payload), System.Text.Encoding.UTF8);
        ReadPayload(r);
        if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("The payload has more data than this machine reads.");
        AfterStateLoad();
    }

    public byte[] SaveState()
    {
        var units = StateUnits();
        units["cpu"] = cpuId; units["ppu"] = ppuId;   // information only: the 65816 is the only CPU and the picture chip is a view
        return ConsoleStateFile.Wrap(Console, GameId, Title, units, SnapshotPayload());
    }

    private byte[] SnapshotPayload()
    {
        using var ms = new MemoryStream(1 << 20);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) WritePayload(w);
        return ms.ToArray();
    }

    public void LoadState(byte[] state)
    {
        // the header is checked before anything is touched; a refusal leaves the machine as it was
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());
        byte[] before = SnapshotPayload();
        try
        {
            using var r = new BinaryReader(new MemoryStream(payload), System.Text.Encoding.UTF8);
            ReadPayload(r);
            if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("The state has more data than this machine reads (it was made by another layout).");
            AfterStateLoad();
        }
        catch (Exception e)
        {
            // a payload that passed the checksum but does not fit (a layout the format name did not catch): put the machine back as it was and say so, never run on half a state
            using var r = new BinaryReader(new MemoryStream(before), System.Text.Encoding.UTF8);
            ReadPayload(r);
            AfterStateLoad();
            throw new InvalidDataException("The state could not be loaded and the game was left as it was: " + e.Message, e);
        }
    }

    private void WritePayload(BinaryWriter w)
    {
        w.Write(PayloadVersion);
        board.SaveState(w);
        switch (board.Apu)
        {
            case APU_SFC sfc: w.Write(UnitSfc); sfc.SaveState(w); break;
            case APU_HLE hle:
                w.Write(UnitHle); hle.SaveState(w);
                w.Write(parkedSpc != null);
                parkedSpc?.SaveState(w);
                break;
            case NesApuOnSnes { Front: APU_SFC front }: w.Write(UnitNes); front.SaveState(w); break;
            case SnesApuOnChip { Front: APU_SFC chipFront }: w.Write(UnitChip); chipFront.SaveState(w); break;      // another console's enhanced sound chip: a listener, like the NES chip; the SPC700 + S-DSP is the machine
            default: throw new NotSupportedException($"The sound unit {board.Apu.CoreName} keeps no state that can be saved.");
        }
    }

    private void ReadPayload(BinaryReader r)
    {
        int version = r.ReadByte();
        if (version != PayloadVersion) throw new InvalidDataException($"The state's payload is version {version}; this build reads version {PayloadVersion}.");
        board.LoadState(r);
        byte unit = r.ReadByte();
        switch (board.Apu)
        {
            case APU_SFC sfc:
                RequireUnit(unit, UnitSfc);
                sfc.LoadState(r);
                break;
            case APU_HLE hle:
                RequireUnit(unit, UnitHle);
                hle.LoadState(r);
                if (r.ReadBoolean()) { parkedSpc ??= new APU_SFC(); parkedSpc.LoadState(r); }
                else parkedSpc = null;
                break;
            case NesApuOnSnes { Front: APU_SFC front } bridge:
                RequireUnit(unit, UnitNes);
                front.LoadState(r);
                bridge.Rebase(board.MasterClock);   // the NES chip listens to the loaded voices from the board's new time; its own phases are not part of the machine
                break;
            case SnesApuOnChip { Front: APU_SFC chipFront } onChip:
                RequireUnit(unit, UnitChip);
                chipFront.LoadState(r);
                onChip.Rebase(board.MasterClock);
                break;
            default: throw new NotSupportedException($"The sound unit {board.Apu.CoreName} keeps no state that can be loaded.");
        }
    }

    private static void RequireUnit(byte saved, byte running)
    {
        if (saved != running) throw new InvalidDataException("The state was saved with a different kind of sound unit than the one running.");
    }

    /// <summary>What the session derives from the board after a state load: the picture the S-PPU last finished (so that the frame shown right after a load is the one that was saved), and a picture bridge built afresh from the loaded machine.</summary>
    private void AfterStateLoad()
    {
        mid = null;
        if (down != null) { SetPicture(ppuId); return; }   // the translator's caches and the line capture describe the old picture; the bridge's frame appears with the next RunFrame
        PublishNativePicture();
    }
}
