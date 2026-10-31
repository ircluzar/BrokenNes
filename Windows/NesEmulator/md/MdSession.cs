using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>
/// A Genesis / Mega Drive game on <see cref="BOARD_MD"/>: the 68000 core, the VDP and the sound unit the console's menus selected. <see cref="Frame"/> is the VDP's picture at its real size (256 or 320 wide,
/// 224 or 240 high; the width can change from frame to frame when a game switches modes), or, with <see cref="DebugView"/> set, the VDP's memory view (VRAM as tiles in the palette line
/// <see cref="DebugPalette"/> with the colour RAM under it).
/// </summary>
/// <remarks>
/// Pad mapping: the apps send the twelve generic buttons, the Genesis pad has eight. A, B, X, Y, Start and the directions are themselves; C is <see cref="PadButtons.C"/> or R, Z is <see cref="PadButtons.Z"/> or L and
/// Mode is <see cref="PadButtons.Mode"/> or Select (so the six-button pad is reachable from the layout the apps already have). Player 1 has a six-button pad, player 2 a three-button one.
/// </remarks>
public sealed class MdSession : IConsoleSession
{
    public const int DebugWidth = 512, DebugHeight = 272;

    public BOARD_MD Board { get; }
    private readonly uint[] frame = new uint[Math.Max(DebugWidth * DebugHeight, MdVdp.PictureStride * MdVdp.PictureMaxHeight * 2)];
    private int frameWidth = 320, frameHeight = 224;
    /// <summary>Show the VDP's memory view instead of the picture.</summary>
    public bool DebugView { get; set; }
    private readonly string cpuId;
    private string ppuId, apuId;
    private readonly short[] scratch = new short[0];
    private readonly string title;
    private readonly string gameId;
    private readonly int sampleRate;

    /// <summary>Which of the four palette lines the debug view draws the tiles in.</summary>
    public int DebugPalette { get; set; }

    public MdSession(byte[] rom, string cpu, string ppu, string apu, RegionChoice? region = null, GenesisModelChoice? model = null, int sampleRate = 44100)
    {
        cpuId = cpu; ppuId = ppu; apuId = apu;
        this.sampleRate = sampleRate;
        gameId = ConsoleSessions.Sha1(rom);
        var cart = new MdCartridge(rom);
        var r = SegaSettings.ResolveRegion(region, ConsoleKind.Genesis, rom);
        var m = SegaSettings.ResolveGenesisModel(model, rom);
        var parts = new SegaBoardParts<MdBus, M68k<MdBus>, MdVdp, IGenesisSound> { Apu = machine => GenesisSoundFactories.Create(apu, machine) };
        Board = new BOARD_MD(cart, r, m, sampleRate, parts);
        title = cart.OverseasTitle.Length > 0 ? cart.OverseasTitle : cart.DomesticTitle;
        FramesPerSecond = Board.Timeline.FramesPerSecond;
    }

    public ConsoleKind Console => ConsoleKind.Genesis;
    public string Title => title;
    public string Description =>
        $"Genesis {title} ({Board.Cart.Mapper}) | {Board.Region} {Board.Model} | CPU {cpuId} | PPU {ppuId} | APU {apuId}: {Board.Sound.CoreName}";
    public double FramesPerSecond { get; }
    public string GameId => gameId;

    public void SetPad(int player, PadButtons buttons)
    {
        if ((buttons & PadButtons.R) != 0) buttons |= PadButtons.C;
        if ((buttons & PadButtons.L) != 0) buttons |= PadButtons.Z;
        if ((buttons & PadButtons.Select) != 0) buttons |= PadButtons.Mode;
        Board.SetPad(player, buttons);
    }

    public void RunFrame()
    {
        Board.RunFrame();
        var v = Board.Vdp;
        if (DebugView)
        {
            v.RenderDebugView(frame, DebugPalette);
            frameWidth = DebugWidth; frameHeight = DebugHeight;
            return;
        }
        CopyPicture(v);
    }

    /// <summary>The picture the VDP finished: in interlace mode 2 the double-height one with both fields woven (448 or 480 rows, shown at twice the width so the proportions hold: see <see cref="DisplayWidth"/>), else the normal one.</summary>
    private void CopyPicture(MdVdp v)
    {
        if (v.WovenValid) { frameWidth = v.PictureWidth; frameHeight = v.WovenHeight; v.CopyWovenPicture(frame); }
        else { frameWidth = v.PictureWidth; frameHeight = v.PictureHeight; v.CopyPicture(frame); }
    }

    /// <summary>Run to scanline <paramref name="line"/> (see <see cref="BOARD_MD.RunUntilLine"/>) instead of to the end of the frame, and show the picture as it stands: for comparisons with an emulator that is stepped a frame at a time.</summary>
    public void RunToLine(int line, int clocksEarly = 0)
    {
        Board.RunUntilLine(line, clocksEarly);
        CopyPicture(Board.Vdp);
    }

    public void Reset() => Board.Reset();

    public uint[] Frame => frame;
    public int FrameWidth => frameWidth;
    public int FrameHeight => frameHeight;
    /// <summary>The picture fills the same width of screen in both modes (a 256-wide line is stretched to the 320 of the wide one); the double-height picture of interlace mode 2 (more than 240 rows) is shown at twice that width so that it keeps its proportions.</summary>
    public int DisplayWidth => DebugView ? DebugWidth : frameHeight > 240 ? 640 : 320;
    public int SampleRate => sampleRate;

    public int ReadSamples(short[] buffer) => Board.Sound.ReadSamples(buffer);

    public bool HasBattery => Board.Cart.HasBattery;
    public byte[] ExportSave() => (byte[])Board.Cart.Sram.Clone();
    public void ImportSave(byte[] data) { Array.Copy(data, Board.Cart.Sram, Math.Min(data.Length, Board.Cart.Sram.Length)); Board.Cart.SramImported(); }

    // ---- save states (the shared contract in ConsoleSystems.cs; the container in ConsoleStates.cs) ----

    /// <summary>The layout of the board's payload: bump it whenever the board, the 68000, the VDP, the cartridge, the Z80 or a sound unit changes what it writes, so an old state is refused rather than misread.</summary>
    public const string StateFormat = "md-1";

    public bool SupportsState => true;

    /// <summary>The parts a state depends on and that must be equal for it to load: the sound unit (a state saved with the stock unit does not fit the NES-chip unit), the board model and the timing region, and the payload layout.</summary>
    private Dictionary<string, string> StateUnits() => new() { ["apu"] = apuId, ["model"] = Board.Model.ToString(), ["region"] = Board.Region.ToString(), ["format"] = StateFormat };

    public byte[] SaveState()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) Board.SaveState(w);
        var units = StateUnits();
        units["cpu"] = cpuId; units["ppu"] = ppuId;   // information only: the 68000 and the VDP are what they are
        return ConsoleStateFile.Wrap(Console, GameId, Title, units, ms.ToArray());
    }

    /// <summary>The payload <see cref="SaveState"/> wraps (no container, no compression): what BrokenDev Timeflow takes every frame.</summary>
    public byte[] SaveStatePayload()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        using (NesEmulator.Sega.BandLimitedMixer.HostIndependent()) Board.SaveState(w);   // the machine's state, not its host's (the sound it has not read: see BandLimitedMixer)
        return ms.ToArray();
    }

    public void LoadState(byte[] state)
    {
        // the header is checked before anything is touched; a refusal leaves the machine as it was
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());
        byte[] before = SnapshotForUndo();
        try
        {
            using var r = new BinaryReader(new MemoryStream(payload), System.Text.Encoding.UTF8);
            Board.LoadState(r);
            if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("The state has more data than this machine reads (it was made by another layout).");
            AfterStateLoad();
        }
        catch (Exception e)
        {
            // a payload that passed the checksum but does not fit (a layout the format name did not catch): put the machine back as it was and say so, never run on half a state
            using var r = new BinaryReader(new MemoryStream(before), System.Text.Encoding.UTF8);
            Board.LoadState(r);
            AfterStateLoad();
            throw new InvalidDataException("The state could not be loaded and the game was left as it was: " + e.Message, e);
        }
    }

    private byte[] SnapshotForUndo()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) Board.SaveState(w);
        return ms.ToArray();
    }

    /// <summary>What the session derives from the board after a state load: the picture the VDP last finished (so the frame shown right after a load is the one that was saved).</summary>
    private void AfterStateLoad()
    {
        if (DebugView) { Board.Vdp.RenderDebugView(frame, DebugPalette); frameWidth = DebugWidth; frameHeight = DebugHeight; return; }
        frameWidth = Board.Vdp.PictureWidth; frameHeight = Board.Vdp.PictureHeight;
        Board.Vdp.CopyPicture(frame);
    }

    // ---- hot swapping (the console's Core menus, mid-game) ----

    /// <summary>
    /// Swaps a core while the game runs. <b>CPU</b>: the 68000 is the only choice the Genesis has; asking for it again is a yes, anything else a no (the app then restarts the game on the new choice and says so). <b>PPU</b>:
    /// the VDP is the only picture chip the board has; the NES picture chips (<c>NES:&lt;chip&gt;</c>) are drawn from the VDP's state by <c>SegaPictureSession</c>, which wraps this session and handles those swaps
    /// itself, so here only the VDP's own id is a yes. <b>APU</b>: the sound unit is replaced in place (<see cref="BOARD_MD.SwapSound"/>): the Z80, its RAM, the YM2612 registers and the PSG are carried into the new unit, so the
    /// game's sound driver goes on playing without noticing; an id that is not a Genesis sound unit, or a unit that cannot take the state, is a no and nothing changes.
    /// </summary>
    public bool TrySwapCore(CoreSlot slot, string id)
    {
        switch (slot)
        {
            case CoreSlot.Apu:
            {
                if (id.Equals(apuId, StringComparison.OrdinalIgnoreCase)) return true;
                IGenesisSound next;
                try { next = GenesisSoundFactories.Create(id, new SegaMachine(Board.Timeline, Board.Model, sampleRate)); }
                catch (ArgumentException) { return false; }   // not a sound unit this console has
                try { Board.SwapSound(next); }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException) { return false; }   // a unit with a different state layout cannot take the Z80's: the game is restarted on it instead
                apuId = id;
                return true;
            }
            case CoreSlot.Ppu:
                // the VDP's own ids: the stock chip and the enhanced one (a subclass that draws a Genesis game on the stock pipeline); a NES chip or another console's is the wrapper's
                if (id.Contains(':') || !System.Linq.Enumerable.Any(CoreCatalog.Options(ConsoleKind.Genesis, CoreSlot.Ppu), o => o.Section == CoreSection.Own && o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return false;
                ppuId = id; return true;
            default: return id.Equals(cpuId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Why <see cref="TrySwapCore"/> said no (the apps print it when the game restarts on the new part). The sound unit is swapped live (<see cref="BOARD_MD.SwapSound"/>), so the sound answer is one of two things: the id is not a
    /// sound unit this console has, or its state layout cannot take the Z80, YM2612 and PSG state of the unit that is playing.
    /// </summary>
    public string SwapRefusal(CoreSlot slot, string id) => slot switch
    {
        CoreSlot.Cpu => $"the Genesis has one main CPU, the 68000 ({cpuId}); {id} is not a choice it has",
        CoreSlot.Ppu => $"the Genesis has one picture chip of its own ({ppuId}); {id} is not a choice it has",
        _ => SoundSwapRefusal(id),
    };

    private string SoundSwapRefusal(string id)
    {
        try { GenesisSoundFactories.Create(id, new SegaMachine(Board.Timeline, Board.Model, sampleRate)); }
        catch (ArgumentException) { return $"{id} is not a sound unit the Genesis has (the Z80 sound driver with the YM2612 and the PSG)"; }
        return $"{id} keeps its state in a layout that cannot take the Z80, YM2612 and PSG state of the unit that is playing, so the game restarts on it";
    }

    public void Dispose() { }
}
