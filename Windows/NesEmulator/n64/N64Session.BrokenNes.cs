// [brokennes_int2] The N64 as a BrokenNes 2 console session: IConsoleSession over cor64's N64Session.
// Staged in cer64plus (integration/brokennes); it lands in BrokenNes at Windows/NesEmulator/n64/N64Session.BrokenNes.cs
// (MERGE_PLAN.md). It needs ConsoleKind.N64 (registration.patch). It never fakes support: what the N64 core cannot do
// is refused with a reason (SwapRefusal, StateUnsupportedReason, InvalidDataException).

using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Systems;
using cor64.Cores;
using cor64.PIF;
using Cor = cor64.Session;

namespace NesEmulator.N64;

/// <summary>
/// An N64 cartridge (.z64 / .n64 / .v64, any byte order: cor64's Cartridge normalises it) running on cor64.
/// The picture is 0xAARRGGBB at the VI's own size (320x240, 640x480, ...) shown 4:3; sound is stereo interleaved at the AI's rate.
/// </summary>
public sealed class N64ConsoleSession : IConsoleSession
{
    /// <summary>Stick deflection the d-pad gives (the analog stick's full range is about +-80 on a real pad).</summary>
    public const sbyte StickFull = 80;

    private readonly Cor.N64Session _s;
    private readonly sbyte[] _stickX = new sbyte[4], _stickY = new sbyte[4];
    private readonly bool[] _hasStick = new bool[4];
    private readonly PadButtons[] _pad = new PadButtons[4];

    /// <param name="rom">The cartridge image.</param>
    /// <param name="fileName">Its file name (the title falls back to it when the header has none).</param>
    /// <param name="ppu">A cor64 PPU id (N64Cores: "MADCAT" software, "PRDP" parallel-rdp on Vulkan), or null for the best available.</param>
    /// <param name="apu">A cor64 APU id ("AI"), or null.</param>
    public N64ConsoleSession(byte[] rom, string fileName = "", string? ppu = null, string? apu = null)
    {
        if (rom == null || rom.Length < 0x1000) throw new InvalidDataException("an N64 cartridge is at least 4 KB (header and boot code)");
        if (ppu != null && !N64Cores.IsAvailable(N64Slot.Ppu, ppu))
            throw new NotSupportedException($"the N64 PPU core {ppu} is not available on this machine" + (ppu.Equals(N64Cores.PpuParallel, StringComparison.OrdinalIgnoreCase) ? " (parallel-rdp needs cor64_prdp.dll and a Vulkan GPU)" : ""));
        if (apu != null && !N64Cores.IsAvailable(N64Slot.Apu, apu)) throw new NotSupportedException($"the N64 APU core {apu} is not available");
        _s = new Cor.N64Session(rom, new Cor.N64SessionOptions { Ppu = ppu, Apu = apu });
        string t = _s.Title;
        Title = t.Length > 0 ? t : (fileName.Length > 0 ? Path.GetFileNameWithoutExtension(fileName) : "N64 cartridge");
    }

    /// <summary>The cor64 session underneath (the debugger adapter, Controller Pak menus and tools reach it here).</summary>
    public Cor.N64Session Core => _s;

    public ConsoleKind Console => ConsoleKind.N64;
    public string Title { get; }
    public string Description => _s.Description;
    public double FramesPerSecond => _s.FramesPerSecond;
    public string GameId => _s.GameId;   // SHA-1 of the ROM image as given, lower-case hex (same as ConsoleSessions.Sha1)

    // ---- input ----
    // PadButtons has no analog stick and no C buttons, so the generic pad maps as follows (Pads.cs / the input menus show this table):
    //   A -> A, B -> B, Start -> Start, L -> Z (the trigger games use most), R -> R, Select -> L shoulder,
    //   X -> C-Up, Y -> C-Left, C -> C-Right, Z -> C-Down,
    //   Up/Down/Left/Right -> the analog stick at full deflection, or the d-pad while Mode is held.
    // SetStick gives a real analog position (an XInput thumbstick); while it is set the directions drive the d-pad.

    public void SetPad(int player, PadButtons buttons)
    {
        if ((uint)player >= 4) return;
        _pad[player] = buttons;
        Apply(player);
    }

    /// <summary>The analog stick of a pad (-128..127, up is positive), outside the generic pad. null clears it (the directions drive the stick again).</summary>
    public void SetStick(int player, sbyte? x, sbyte? y)
    {
        if ((uint)player >= 4) return;
        _hasStick[player] = x.HasValue || y.HasValue;
        _stickX[player] = x ?? 0; _stickY[player] = y ?? 0;
        Apply(player);
    }

    private void Apply(int player)
    {
        var b = _pad[player];
        var n = JoyController.ButtonPress.None;
        void M(PadButtons p, JoyController.ButtonPress q) { if ((b & p) != 0) n |= q; }
        M(PadButtons.A, JoyController.ButtonPress.A); M(PadButtons.B, JoyController.ButtonPress.B);
        M(PadButtons.Start, JoyController.ButtonPress.Start); M(PadButtons.L, JoyController.ButtonPress.Z);
        M(PadButtons.R, JoyController.ButtonPress.ShoulderR); M(PadButtons.Select, JoyController.ButtonPress.ShoulderL);
        M(PadButtons.X, JoyController.ButtonPress.CUp); M(PadButtons.Y, JoyController.ButtonPress.CLeft);
        M(PadButtons.C, JoyController.ButtonPress.CRight); M(PadButtons.Z, JoyController.ButtonPress.CDown);
        sbyte sx = _stickX[player], sy = _stickY[player];
        bool dpad = _hasStick[player] || (b & PadButtons.Mode) != 0;
        if (dpad)
        {
            M(PadButtons.Up, JoyController.ButtonPress.DPadUp); M(PadButtons.Down, JoyController.ButtonPress.DPadDown);
            M(PadButtons.Left, JoyController.ButtonPress.DPadLeft); M(PadButtons.Right, JoyController.ButtonPress.DPadRight);
        }
        else
        {
            sx = (sbyte)(((b & PadButtons.Right) != 0 ? StickFull : 0) - ((b & PadButtons.Left) != 0 ? StickFull : 0));
            sy = (sbyte)(((b & PadButtons.Up) != 0 ? StickFull : 0) - ((b & PadButtons.Down) != 0 ? StickFull : 0));
        }
        _s.SetPad(player, new Cor.N64Pad(n, sx, sy));
    }

    // ---- running ----
    public void RunFrame() => _s.RunFrame();
    public void Reset() => _s.Reset();

    // ---- picture and sound ----
    public uint[] Frame => _s.Frame;
    public int FrameWidth => _s.FrameWidth;
    public int FrameHeight => _s.FrameHeight;
    public int DisplayWidth => _s.DisplayWidth;
    public int SampleRate => _s.SampleRate;
    public int ReadSamples(short[] buffer) => _s.ReadSamples(buffer);

    // ---- battery (the cartridge's EEPROM / SRAM / FlashRAM, sized by its type; Controller Paks are separate, below) ----
    public bool HasBattery => _s.HasBattery;
    public byte[] ExportSave() => _s.ExportSave();
    public void ImportSave(byte[] data) => _s.ImportSave(data);
    /// <summary>"Eeprom4K", "Eeprom16K", "Sram", "FlashRam", ... for the save file's label.</summary>
    public string SaveType => _s.SaveType;
    public byte[] ExportControllerPak(int port) => _s.ExportControllerPak(port);
    public void ImportControllerPak(int port, byte[] data) => _s.ImportControllerPak(port, data);

    // ---- core swaps ----
    private static N64Slot SlotOf(CoreSlot s) => s switch { CoreSlot.Cpu => N64Slot.Cpu, CoreSlot.Ppu => N64Slot.Ppu, _ => N64Slot.Apu };

    /// <summary>The PPU (MADCAT / PRDP) and APU swap in place at the frame boundary; the CPU has one core and is refused.
    /// Ids not on the N64 menu (another console's chips) are refused: the N64 is isolated.</summary>
    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(ConsoleKind.N64, slot, id)) return false;
        return _s.TrySwapCore(SlotOf(slot), id);
    }

    public string SwapRefusal(CoreSlot slot, string id) =>
        !ConsoleSessions.Offers(ConsoleKind.N64, slot, id) ? ConsoleSessions.NotOffered(ConsoleKind.N64, slot, id) + " (the N64 is isolated: no other console's chips)"
        : _s.SwapRefusal(SlotOf(slot), id);

    // ---- save states ----
    // The payload is cor64's "cor64-st2" stream (the whole machine: CPU, COP0/FPU, RCP, RDRAM, PIF, cart save). The PPU is not in the
    // units: its internal state is rebuilt within a frame (as TrySwapCore relies on), so a state taken on MADCAT loads on PRDP.
    // The APU id is: a state is never loaded into a different sound unit.
    private Dictionary<string, string> StateUnits() => new()
    {
        ["format"] = _s.StateFormat,
        ["apu"] = _s.Apu.Id,
        ["rdram"] = _s.System.ExpansionPak ? "8M" : "4M",
    };

    public bool SupportsState => _s.SupportsState;
    public string StateUnsupportedReason => "The N64 save states need the cor64 session (this one has none).";

    public byte[] SaveState() => ConsoleStateFile.Wrap(Console, GameId, Title, StateUnits(), _s.SaveState());

    public void LoadState(byte[] state)
    {
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());   // throws InvalidDataException for another game / unit
        byte[] before = _s.SaveState();
        try { _s.LoadState(payload); }
        catch (Exception e) when (e is not InvalidDataException)
        {
            _s.LoadState(before);
            throw new InvalidDataException("the N64 state is damaged: " + e.Message);
        }
        catch (InvalidDataException) { _s.LoadState(before); throw; }
        for (int p = 0; p < 4; p++) Apply(p);   // the pads the app holds now, not the ones in the state
    }

    /// <summary>The raw cor64 stream (no deflate, no checksum): Timeflow takes one a frame.</summary>
    public byte[] SaveStatePayload() => _s.SaveStatePayload();

    public void Dispose() => _s.Dispose();
}
