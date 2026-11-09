// The x86 console as a BrokenNes 2 console session: a disk image is the "ROM", the disk's own changes are its save. Written for BrokenNes.
// The x86 console is ISOLATED (docs/projects/next-pool-plans.md, P3): one CPU, one picture chip, the AdLib (YM3812 OPL2) and the PC speaker, none of them another console's.

using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Systems;

namespace NesEmulator.X86;

/// <summary>
/// A PC (<see cref="PcMachine"/>) booting a disk image: a raw hard disk (a FreeDOS boot disk with a game, say) or a raw floppy (1.44 MB and the other standard sizes). The pad is a keyboard
/// (<see cref="KeyFor"/>). The disk is the battery: whatever DOS writes to it (a game's save file) comes back the next time the same image is opened. Save states hold the whole machine with its disks.
/// </summary>
public sealed class PcSession : IConsoleSession
{
    /// <summary>The machine's speed in instructions per emulated second. The interpreter runs about 60 M a second on a desktop PC (more while the game waits in a spin); a faster machine clock than the host
    /// can give makes the whole game run slower than real time, a slower one makes the game itself lag.</summary>
    public const long DefaultIps = 60_000_000;
    private const int Rate = 44100;

    private readonly PcMachine _m = new();
    private readonly byte[] _original;
    private readonly bool _floppy;
    private bool _powered;
    private uint[] _frame = new uint[720 * 480];
    private int _w = 720, _h = 400;
    private PadButtons _pad;
    private byte[]? _exported;

    /// <param name="image">A disk image, or a DOS program (<see cref="PcDisks.IsDosProgram"/>: it is put on a FreeDOS boot disk made for it, <see cref="PcDisks.ForProgram"/>).</param>
    public PcSession(byte[] image, string fileName = "", string apu = "ADLIB", string cpu = "I386", string ppu = "VGA")
    {
        Title = fileName.Length > 0 ? Path.GetFileNameWithoutExtension(fileName) : "x86 disk";
        if (PcDisks.IsDosProgram(image, fileName))
        {
            string fd = PcDisks.FindFreeDos() ?? throw new FileNotFoundException($"a DOS program needs the FreeDOS boot disk ({PcDisks.FreeDosImageName} in the app's x86/freedos folder, or BROKENNES_FREEDOS); open a disk image instead");
            Program = PcDisks.DosName(fileName);
            GameId = "exe" + ConsoleSessions.Sha1(image);   // the program's id: the disk made for it (and its save) is the same every time
            image = PcDisks.ForProgram(File.ReadAllBytes(fd), image, fileName, PcSound.DosEnv(apu));   // the card's SET BLASTER / ULTRASND, fixed at creation (a live swap does not rewrite it)
        }
        else GameId = ConsoleSessions.Sha1(image);
        if (image.Length < 512 || image.Length % 512 != 0) throw new InvalidDataException("an x86 disk image is a whole number of 512-byte sectors");
        _original = image;
        _floppy = image.Length <= 2949120 && !(image[510] == 0x55 && image[511] == 0xaa && image[0x1c2] != 0);   // a floppy-sized image without a partition table
        _m.InstructionsPerSecond = IpsOf(cpu); _cpuId = cpu;
        _m.Speaker.Enabled = true;
        _m.Opl.Enabled = true;
        InsertDisk((byte[])image.Clone());
        if (!PcSound.Apply(_m, apu)) throw new ArgumentException($"the x86 PC has no sound card '{apu}'");
        ApuId = apu.ToUpperInvariant();
        _m.Vga.Picture = VgaPictures.Create(_m.Vga, ppu);
        PpuId = ppu.ToUpperInvariant();
    }

    /// <summary>The picture chip (an x86 PPU id: VGA, BOCHS20, DOSBOX072, PCEM_CGA).</summary>
    public string PpuId { get; private set; } = "VGA";

    /// <summary>The sound card and engine heard (an x86 APU id: ADLIB, ADLIB_D072, ...).</summary>
    public string ApuId { get; private set; } = "ADLIB";

    private void InsertDisk(byte[] data)
    {
        var d = new DiskImage(data, _floppy);
        if (_floppy) _m.Floppy[0] = d; else _m.Hdd[0] = d;
        _exported = null;
    }

    private DiskImage Disk => (_floppy ? _m.Floppy[0] : _m.Hdd[0])!;

    public PcMachine Machine => _m;
    /// <summary>The DOS program the disk was made for (its 8.3 name), or null for a disk image opened as it is.</summary>
    public string? Program { get; }
    public ConsoleKind Console => ConsoleKind.Pc;
    public string Title { get; }
    public string Description => $"x86 PC | CPU I386 (486-class, {_m.InstructionsPerSecond / 1_000_000} M instr/s) | PPU {PpuId} ({VgaPictures.Describe(PpuId)}) | APU {ApuId} ({PcSound.Describe(ApuId)}) | {(Program != null ? $"{Program} on a FreeDOS 1.4 disk" : _floppy ? "floppy A:" : "hard disk C:")} {_original.Length / 1024:N0} KB{(_m.Stopped != null ? " | STOPPED: " + _m.Stopped : "")}";
    public double FramesPerSecond => Pit8254.Freq / Vga.FrameTicks;
    public string GameId { get; }

    // ================= input: the pad as keys =================

    /// <summary>The key (scan code set 1) each pad button presses: the arrows, Ctrl (A), Alt (B), Space (X), Esc (Y), Tab (L), right Shift (R), Enter (Start), Backspace (Select). Vaults DOS reads
    /// Ctrl / Space as its A, Alt as B, Enter as Start, Tab / Backspace / right Shift as Select.</summary>
    public static byte KeyFor(PadButtons b) => b switch
    {
        PadButtons.Up => 0x48, PadButtons.Down => 0x50, PadButtons.Left => 0x4b, PadButtons.Right => 0x4d,
        PadButtons.A => 0x1d, PadButtons.B => 0x38, PadButtons.X => 0x39, PadButtons.Y => 0x01,
        PadButtons.L => 0x0f, PadButtons.R => 0x36, PadButtons.Start => 0x1c, PadButtons.Select => 0x0e,
        _ => 0,
    };

    private static readonly PadButtons[] Keys =
    {
        PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right, PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y,
        PadButtons.L, PadButtons.R, PadButtons.Start, PadButtons.Select,
    };

    public void SetPad(int player, PadButtons buttons)
    {
        if (player != 0) return;
        var changed = buttons ^ _pad;
        if (changed == 0) return;
        foreach (var k in Keys)
        {
            if ((changed & k) == 0) continue;
            byte code = KeyFor(k);
            _m.Kbc.KeyBytes((buttons & k) != 0 ? code : (byte)(code | 0x80));
        }
        _pad = buttons;
    }

    // ================= input: the host's real keyboard =================

    /// <summary>A key of the host's real keyboard (the desktop app passes its keyboard through while an x86 session runs, alongside the pad's keys): the PC/XT
    /// scan code (set 1) of the key, <paramref name="extended"/> for the E0-prefixed keys (right Ctrl / Alt, the arrows and the editing block, keypad Enter and /),
    /// <paramref name="down"/> false for the release (the break code: make | 80h). Pause is its own sequence (E1 1D 45 E1 9D C5, make only). Repeats are passed as
    /// repeated makes, as a real keyboard's typematic does.</summary>
    public void HostKey(int scan, bool extended, bool down)
    {
        if (scan <= 0 || scan > 0x7f) return;
        if (scan == 0x45 && extended) { if (down) _m.Kbc.KeyBytes(0xe1, 0x1d, 0x45, 0xe1, 0x9d, 0xc5); return; }   // Pause (the host names it 45h extended)
        byte code = (byte)(down ? scan : scan | 0x80);
        if (extended) _m.Kbc.KeyBytes(0xe0, code); else _m.Kbc.KeyBytes(code);
    }


    public void RunFrame()
    {
        if (!_powered) { _m.PowerOn(); _powered = true; }
        _m.RunFrame();
        _m.Speaker.CatchUp();   // the PC speaker's and the AdLib's samples up to the end of this frame
        _m.Opl.CatchUp();
        _m.Card?.Advance();
        CopyFrame();
    }

    private void CopyFrame()
    {
        var v = _m.Vga;
        int w = v.Width, h = v.Height, n = w * h;
        if (_frame.Length < n) _frame = new uint[n];
        for (int i = 0; i < n; i++) _frame[i] = 0xff000000u | (uint)v.Frame[i];
        _w = w; _h = h;
    }

    public void Reset() { _m.PowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); _powered = true; _pad = PadButtons.None; }
    public uint[] Frame => _frame;
    public int FrameWidth => _w;
    public int FrameHeight => _h;
    public int DisplayWidth => _h * 4 / 3;   // a 4:3 monitor (720 x 400 text and 640 x 400 mode 13h are shown 4:3 too)
    public int SampleRate => Rate;

    /// <summary>The PC speaker with the AdLib and the sound card (if any) added on top (both drawn up to the end of the last frame on the same 44.1 kHz timeline).</summary>
    public int ReadSamples(short[] buffer)
    {
        int n = _m.Speaker.Read(buffer);
        _m.Opl.MixInto(buffer, n);
        _m.Card?.MixInto(buffer, n);
        return n;
    }

    // ================= the disk is the battery =================

    public bool HasBattery => true;

    /// <summary>The disk as it is now. The same array comes back until DOS writes to the disk again (so an unchanged disk costs no copy and compares in no time).</summary>
    public byte[] ExportSave()
    {
        var d = Disk;
        if (_exported == null || d.Dirty) { _exported = (byte[])d.Data.Clone(); d.Dirty = false; }
        return _exported;
    }

    /// <summary>A disk saved earlier from this same image: it replaces the disk (before the machine starts, or with a cold boot). Another size is refused, the disk stays as it was.</summary>
    public void ImportSave(byte[] data)
    {
        if (data.Length != _original.Length) return;
        InsertDisk((byte[])data.Clone());
        if (_powered) { _m.PowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); }
    }

    /// <summary>Puts a file on the disk before the machine starts (BrokenDev's x86 runner: a test script such as TEST.TXT beside the program). Refused once the machine runs.</summary>
    public void PutFile(string dosPath, byte[] data)
    {
        if (_powered) throw new InvalidOperationException("files go on the x86 disk before the first frame");
        new FatImage(Disk.Data).WriteFile(dosPath, data);
        _exported = null;
    }

    /// <summary>A file on the disk as it is now (what the program wrote: a save, a status file), or null when there is none.</summary>
    public byte[]? ReadFile(string dosPath) => new FatImage(Disk.Data).ReadFile(dosPath);

    /// <summary>CPU harvest: "I386_D074" = DOSBox 0.74's default speed. Its core_simple (and core_normal) count one "cycle" per instruction whatever the
    /// instruction, exactly as this core does, so cycles=3000 (the 0.74 default) is 3,000 instructions a millisecond: delay-loop music and games run
    /// at DOSBox's pace (20x slower than the stock 60 M instr/s here); the PIT and the sound cards keep real time.</summary>
    private string _cpuId = "I386";
    // the speed is the CPU option's, also after a state load (a state saved at the other speed carries its own number)
    public static long IpsOf(string cpu) => cpu.Equals("I386_D074", StringComparison.OrdinalIgnoreCase) ? 3_000_000 : DefaultIps;

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(ConsoleKind.Pc, slot, id)) return false;
        if (slot == CoreSlot.Cpu) { _m.InstructionsPerSecond = IpsOf(id); _cpuId = id; }
        if (slot == CoreSlot.Apu) { if (!PcSound.Apply(_m, id)) return false; ApuId = id.ToUpperInvariant(); }
        if (slot == CoreSlot.Ppu)
        {   // a live swap: the new chip starts cold on the card as it is (a CGA swapped in under a VGA mode shows B8000 until the next mode set)
            _m.Vga.Picture = VgaPictures.Create(_m.Vga, id);
            PpuId = id.ToUpperInvariant();
        }
        return true;
    }
    public string SwapRefusal(CoreSlot slot, string id) => $"the x86 PC has one {slot} core (it is an isolated console: no other console's chips)";

    // ================= save states =================

    public bool SupportsState => true;

    private Dictionary<string, string> StateUnits() => new() { ["format"] = "x86-1", ["disk"] = _floppy ? "floppy" : "hdd" };

    public byte[] SaveState()
    {
        if (!_powered) { _m.PowerOn(); _powered = true; }
        return ConsoleStateFile.Wrap(Console, GameId, Title, StateUnits(), _m.SaveState());
    }

    public void LoadState(byte[] state)
    {
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());
        var before = _m.SaveState();
        try { _m.LoadState(payload); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); _m.InstructionsPerSecond = IpsOf(_cpuId); _powered = true; _exported = null; _pad = PadButtons.None; }
        catch (Exception e) when (e is not InvalidDataException) { _m.LoadState(before); throw new InvalidDataException("the x86 state is damaged: " + e.Message); }
    }

    public void Dispose() { }
}
