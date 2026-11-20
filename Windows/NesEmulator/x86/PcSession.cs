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

    private readonly PcMachine _m;
    private readonly CpuModel? _model;   // a CPU model (I386.Model.cs); null = the original console's CPU and its IPS menu
    private readonly byte[] _original;
    private readonly bool _floppy;
    private bool _powered;
    private uint[] _frame = new uint[720 * 480];
    private int _w = 720, _h = 400;
    private PadButtons _pad;
    private byte[]? _exported;

    /// <param name="image">A disk image, or a DOS program (<see cref="PcDisks.IsDosProgram"/>: it is put on a FreeDOS boot disk made for it, <see cref="PcDisks.ForProgram"/>).</param>
    public PcSession(byte[] image, string fileName = "", string apu = "ADLIB", string cpu = "I386", string ppu = "VGA", CpuModel? model = null, string machine = PcMachinePreset.ClassicKey)
    {
        var preset = PcMachinePreset.Find(machine);
        // X86-02: background (non-blocking) gen2 collections while a PC runs: the JIT's Reflection.Emit garbage made blocking collections the worst frames (A/B: p99 163 -> 124 ms). BROKENNES_X86_GCLAT=0 opts out.
        if (Environment.GetEnvironmentVariable("BROKENNES_X86_GCLAT") != "0") System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        MachineKey = preset.Key;
        if (!preset.IsClassic) model = preset.Cpu;
        _model = model != null && !model.Legacy ? model : null;
        _m = preset.IsClassic ? new PcMachine(PcMachine.DefaultMemory, _model) : new PcMachine(preset.Config, _model, preset.Video);
        // the network card's cable: the built-in NAT (DHCP, DNS, the HTTPS bridge) only when asked for (BROKENNES_X86_NET=1); unplugged by default
        if (_m.Nic != null && Environment.GetEnvironmentVariable("BROKENNES_X86_NET") == "1") _m.Nic.Link = _nat = new PcNat();
        // the LAN (X86-02): BROKENNES_X86_LAN=host:PORT / join:HOST:PORT puts the card on a hub shared with other BrokenNes PCs (IPX games); with the NAT too, the cable goes to both
        if (_m.Nic != null && Environment.GetEnvironmentVariable("BROKENNES_X86_MAC") is { Length: > 0 } mac) { _m.Nic.Mac = PcMac.Parse(mac); _m.Nic.Reset(); }
        if (_m.Nic != null && Environment.GetEnvironmentVariable("BROKENNES_X86_LAN") is { Length: > 0 } lan)
        {
            _lan = PcLanUdp.FromSpec(lan);
            if (_nat != null) _nat.LanShared = true;
            _m.Nic.Link = _m.Nic.Link != null ? new PcLinkPair(_lan, _m.Nic.Link) : _lan;
        }
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
        _m.InstructionsPerSecond = _model?.Ips ?? IpsOf(cpu); _cpuId = cpu;
        _m.Speaker.Enabled = true;
        _m.Opl.Enabled = true;
        InsertDisk(image);   // the base: never written (DiskImage is copy-on-write), so no copy
        // a second hard disk (X86-02: StarCraft's 1.2 GB install does not fit next to Win98 on a 1 GB C:): BROKENNES_X86_HDD2 = an image (primary slave)
        if (!_floppy && Environment.GetEnvironmentVariable("BROKENNES_X86_HDD2") is { Length: > 0 } hdd2 && File.Exists(hdd2) && _m.Ata != null) _m.Hdd[1] = new DiskImage(File.ReadAllBytes(hdd2), false);
        // the CD drive's disc (X86-02): BROKENNES_X86_CD = an .iso, .cue or .bin (IDE machines; read from the file, never written)
        if (Environment.GetEnvironmentVariable("BROKENNES_X86_CD") is { Length: > 0 } cdPath && File.Exists(cdPath)) InsertCd(CdImage.Open(cdPath));
        if (!PcSound.Apply(_m, apu)) throw new ArgumentException($"the x86 PC has no sound card '{apu}'");
        ApuId = apu.ToUpperInvariant();
        _m.Vga.Picture = VgaPictures.Create(_m.Vga, ppu);
        PpuId = ppu.ToUpperInvariant();
    }

    /// <summary>The picture chip (an x86 PPU id: VGA, BOCHS20, DOSBOX072, PCEM_CGA).</summary>
    public string PpuId { get; private set; } = "VGA";

    /// <summary>The sound card and engine heard (an x86 APU id: ADLIB, ADLIB_D072, ...).</summary>
    public string ApuId { get; private set; } = "ADLIB";

    /// <summary>Puts a disc in the CD drive (null: empty). Takes effect for the guest at once (the drive reports a new medium on its next command).</summary>
    public void InsertCd(CdImage? cd) { _m.Cd = cd; if (_m.Ata != null) { _m.Ata.Secondary.Cd[0] = cd; _m.Ata.Secondary.MediaChanged(0); } }

    /// <summary>Puts the disk in the drive: always on the opened image as its read-only base (a savestate carries only the diff from it), reading as <paramref name="data"/>.</summary>
    private void InsertDisk(byte[] data)
    {
        var d = DiskImage.FromFull(_original, data, _floppy);
        if (_floppy) _m.Floppy[0] = d; else _m.Hdd[0] = d;
        _exported = null;
    }

    private DiskImage Disk => (_floppy ? _m.Floppy[0] : _m.Hdd[0])!;

    public PcMachine Machine => _m;
    /// <summary>The machine preset (<see cref="PcMachinePreset"/>) the session was built as.</summary>
    public string MachineKey { get; }
    /// <summary>The DOS program the disk was made for (its 8.3 name), or null for a disk image opened as it is.</summary>
    public string? Program { get; }
    public ConsoleKind Console => ConsoleKind.Pc;
    public string Title { get; }
    public string Description => $"x86 PC{(MachineKey != PcMachinePreset.ClassicKey ? $" ({PcMachinePreset.Find(MachineKey).Label})" : "")} | CPU {(_model != null ? $"{_model.Name}" : "I386 (486-class")}{(_model != null ? $" ({_m.InstructionsPerSecond / 1_000_000} M instr/s)" : $", {_m.InstructionsPerSecond / 1_000_000} M instr/s)")} | PPU {PpuId} ({VgaPictures.Describe(PpuId)}) | APU {ApuId} ({PcSound.Describe(ApuId)}) | {(Program != null ? $"{Program} on a FreeDOS 1.4 disk" : _floppy ? "floppy A:" : "hard disk C:")} {_original.Length / 1024:N0} KB{(_m.Stopped != null ? " | STOPPED: " + _m.Stopped : "")}";
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
    /// <summary>True when the PC has a PS/2 mouse (the Windows 98 machine).</summary>
    public bool HasMouse => _m.Kbc.HasMouse;
    /// <summary>The host's mouse: relative motion since the last call (x right, y down) and the buttons (bit 0 left, 1 right, 2 middle).</summary>
    public void HostMouse(int dx, int dy, int buttons) => _m.Kbc.MouseInput(dx, dy, buttons);

    public void HostKey(int scan, bool extended, bool down)
    {
        if (scan <= 0 || scan > 0x7f) return;
        if (scan == 0x45 && extended) { if (down) _m.Kbc.KeyBytes(0xe1, 0x1d, 0x45, 0xe1, 0x9d, 0xc5); return; }   // Pause (the host names it 45h extended)
        byte code = (byte)(down ? scan : scan | 0x80);
        if (extended) _m.Kbc.KeyBytes(0xe0, code); else _m.Kbc.KeyBytes(code);
    }


    private bool _stateHookUsed;
    // BROKENNES_X86_MACHINESTATE: start from a machine state saved by the Workshop's x86boot ({save:PATH}) on the same disks (X86-02: StarCraft's menu); used once
    private void FirstPowerOn()
    {
        _m.PowerOn(); _powered = true;
        if (!_stateHookUsed && Environment.GetEnvironmentVariable("BROKENNES_X86_MACHINESTATE") is { Length: > 0 } ms && File.Exists(ms))
        {
            _stateHookUsed = true;
            _m.LoadState(File.ReadAllBytes(ms));
            // the disc was put in at creation (InsertCd: UNIT ATTENTION) but the state was saved with it already in: without this Win98 saw a new disc, AutoPlay opened the
            // disc's autorun window over the running game and a 256-colour game (Age of Empires) lost its palette to the desktop's (the scrambled-colours bug)
            _m.Ata?.Secondary.ClearMediaChanged(0);
        }
    }

    public void RunFrame()
    {
        if (!_powered) { FirstPowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); }
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

    public void Reset() { FirstPowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); _powered = true; _pad = PadButtons.None; }
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
        _m.Ata?.Secondary.MixCdAudio(buffer, n);
        return n;
    }

    // ================= the disk is the battery =================

    public bool HasBattery => true;

    /// <summary>The disk as it is now. The same array comes back until DOS writes to the disk again (so an unchanged disk costs no copy and compares in no time).</summary>
    public byte[] ExportSave()
    {
        var d = Disk;
        if (_exported == null || d.Dirty) { _exported = d.ToArray(); d.Dirty = false; }
        return _exported;
    }

    /// <summary>The periodic save without stopping the machine: null when the disk is unchanged since the last export, else a function that builds the disk
    /// as it is now (run it on another thread: a Win98 disk is 1 GB, building, comparing and writing it on the emulation thread stalled the game for seconds every 10 s).</summary>
    public Func<byte[]>? ExportSaveLater()
    {
        var d = Disk;
        if (_exported != null && !d.Dirty) return null;
        var build = d.Snapshot(); d.Dirty = false;
        return () => { var a = build(); _exported = a; return a; };
    }

    /// <summary>A disk saved earlier from this same image: it replaces the disk (before the machine starts, or with a cold boot). Another size is refused, the disk stays as it was.</summary>
    public void ImportSave(byte[] data)
    {
        if (data.Length != _original.Length) return;
        InsertDisk(data);
        if (_powered) { _m.PowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); }
    }

    /// <summary>Puts a file on the disk before the machine starts (BrokenDev's x86 runner: a test script such as TEST.TXT beside the program). Refused once the machine runs.</summary>
    public void PutFile(string dosPath, byte[] data)
    {
        if (_powered) throw new InvalidOperationException("files go on the x86 disk before the first frame");
        var full = Disk.ToArray();
        new FatImage(full).WriteFile(dosPath, data);
        Disk.SetContent(full);
        _exported = null;
    }

    /// <summary>A file on the disk as it is now (what the program wrote: a save, a status file), or null when there is none.</summary>
    public byte[]? ReadFile(string dosPath) => new FatImage(Disk.ToArray()).ReadFile(dosPath);

    /// <summary>CPU harvest: "I386_D074" = DOSBox 0.74's default speed. Its core_simple (and core_normal) count one "cycle" per instruction whatever the
    /// instruction, exactly as this core does, so cycles=3000 (the 0.74 default) is 3,000 instructions a millisecond: delay-loop music and games run
    /// at DOSBox's pace (20x slower than the stock 60 M instr/s here); the PIT and the sound cards keep real time.</summary>
    private string _cpuId = "I386";
    // the speed is the CPU option's, also after a state load (a state saved at the other speed carries its own number)
    public static long IpsOf(string cpu) => cpu.Equals("I386_D074", StringComparison.OrdinalIgnoreCase) ? 3_000_000 : DefaultIps;

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(ConsoleKind.Pc, slot, id)) return false;
        if (slot == CoreSlot.Cpu) { _m.InstructionsPerSecond = _model?.Ips ?? IpsOf(id); _cpuId = id; }
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
        if (!_powered) { FirstPowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); }
        // one buffer for the payload, reused (the bug capture takes a state a second: ~110 MB of Win98 RAM, video memory and disk diff; four
        // growing copies of it a second were GBs of large-object garbage, a 20 GB window), packed in parallel pieces by Wrap (~1 s -> ~0.1 s)
        var ms = _stateBuf ??= new MemoryStream();
        ms.SetLength(0);
        _m.SaveState(ms);
        WriteSoundOutput(ms);
        var file = ConsoleStateFile.Wrap(Console, GameId, Title, StateUnits(), new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length));
        if (ms.Capacity > 512 << 20) _stateBuf = null;   // (never keep a freak-sized buffer)
        return file;
    }
    private MemoryStream? _stateBuf;

    /// <summary>
    /// The bug capture's state, split: the machine's payload is copied now (between frames, under the emulation lock: ~70 ms of Win98 RAM,
    /// video memory and disk diff), the packing (CRC + deflate, ~115 ms) is returned to run on any thread. Two payload buffers alternate, so
    /// the caller must finish one pack before it begins the next-but-one (the ring waits on the previous pack before it takes a slot).
    /// </summary>
    public Func<byte[]> BeginSaveState()
    {
        if (!_powered) { FirstPowerOn(); _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart(); }
        _deferFlip ^= 1;
        var ms = _deferBuf[_deferFlip] ??= new MemoryStream();
        ms.SetLength(0);
        _m.SaveState(ms);
        WriteSoundOutput(ms);
        var units = StateUnits(); string game = GameId, title = Title;
        var seg = new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        int flip = _deferFlip;
        return () =>
        {
            var file = ConsoleStateFile.Wrap(Console, game, title, units, seg);
            if (ms.Capacity > 512 << 20) _deferBuf[flip] = null;
            return file;
        };
    }
    private readonly MemoryStream?[] _deferBuf = new MemoryStream?[2];
    private int _deferFlip;

    // FA-ST-3: the sound already drawn but not yet read (the speaker, the AdLib, the card) follows the machine as a trailer: [machine][PCAU section][its length][PCAU].
    // The machine state is unchanged; a state without the trailer (older) loads as before: the drawn samples are dropped and the sound carries on from the present.
    private const int SoundMagic = 0x55414350;   // "PCAU"

    private byte[] WithSoundOutput(byte[] machine)
    {
        using var ms = new MemoryStream();
        ms.Write(machine);
        WriteSoundOutput(ms);
        return ms.ToArray();
    }

    /// <summary>The sound trailer, appended at the stream's end.</summary>
    private void WriteSoundOutput(MemoryStream ms)
    {
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            long start = ms.Position;
            w.Write(1);   // section version
            _m.Speaker.SaveOutput(w);
            _m.Opl.SaveOutput(w);
            var card = _m.Card as IPcSoundOutput;
            w.Write(_m.Card?.Id ?? ""); w.Write(card != null);
            card?.SaveOutput(w);
            w.Write((int)(ms.Position - start)); w.Write(SoundMagic);
        }
    }

    /// <summary>Splits a payload into the machine state and the sound trailer (null when there is none).</summary>
    private static (byte[] machine, byte[]? sound) SplitSoundOutput(byte[] payload)
    {
        if (payload.Length < 8 || BitConverter.ToInt32(payload, payload.Length - 4) != SoundMagic) return (payload, null);
        int len = BitConverter.ToInt32(payload, payload.Length - 8);
        if (len <= 0 || len > payload.Length - 8) return (payload, null);
        int at = payload.Length - 8 - len;
        return (payload[..at], payload[at..(payload.Length - 8)]);
    }

    private void RestoreSoundOutput(byte[]? sound)
    {
        if (sound != null)
        {
            using var r = new BinaryReader(new MemoryStream(sound));
            if (r.ReadInt32() == 1)
            {
                _m.Speaker.LoadOutput(r);
                _m.Opl.LoadOutput(r);
                string id = r.ReadString(); bool has = r.ReadBoolean();
                if (has && _m.Card is IPcSoundOutput c && _m.Card.Id == id) c.LoadOutput(r);
                else _m.Card?.Restart();   // another card (or none) was saved: it started cold in the machine load
                return;
            }
        }
        _m.Speaker.Restart(); _m.Opl.Restart(); _m.Card?.Restart();
    }

    public void LoadState(byte[] state)
    {
        byte[] payload = ConsoleStateFile.Unwrap(state, Console, GameId, Title, StateUnits());
        var before = _m.SaveState();
        var (machine, sound) = SplitSoundOutput(payload);
        try { _m.LoadState(machine); RestoreSoundOutput(sound); _m.InstructionsPerSecond = _model?.Ips ?? IpsOf(_cpuId); _powered = true; _exported = null; _pad = PadButtons.None; }
        catch (Exception e) when (e is not InvalidDataException) { _m.LoadState(before); throw new InvalidDataException("the x86 state is damaged: " + e.Message); }
    }

    private PcNat? _nat;
    private PcLanUdp? _lan;
    public void Dispose() { _nat?.Dispose(); _lan?.Dispose(); }
}
