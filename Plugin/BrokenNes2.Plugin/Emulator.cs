using BrokenNes.Fruity;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>
/// One emulator: a sound chip (Direct mode) or a running game with its picture (ROM mode), of one console (NES, Game Boy or SNES),
/// with up to five channels that FL instances take: pulse 1, pulse 2, triangle, noise and (ROM mode) the game's whole mix. The
/// configuration below is shared by all instances attached to it; the game and its channel stems are in <c>Emulator.Rom.cs</c>.
/// </summary>
public sealed partial class Emulator : IDisposable
{
    private readonly List<Bn2Plugin> members = new();            // under EmulatorHub.Gate
    private readonly Bn2Plugin?[] owners = new Bn2Plugin?[Ch.Count];
    private int mode, chip;
    private int configVersion;
    private int claimedTones;                                     // bit per tone channel somebody sits on (audio threads read it)
    private readonly string?[] romPaths = new string?[Cx.Count];   // the game of each console (NES: null = the built-in one)
    private readonly int[] activity = new int[Ch.Count];
    private int runaway, padBits;
    private SpcSnapshot? snapshot;
    private GbSnapshot? gbState;
    private readonly Dictionary<object, int> padSources = new();

    internal Emulator(int id, bool autoCreated)
    {
        Id = id;
        AutoCreated = autoCreated;
    }

    /// <summary>1..8: the number the Emulator parameter names.</summary>
    public int Id { get; }
    /// <summary>Made by an Auto instance (only those fill up with other Auto instances).</summary>
    public bool AutoCreated { get; }

    /// <summary>0 Direct (FL's notes play bare chips), 1 ROM (a game runs).</summary>
    public int Mode => Volatile.Read(ref mode);
    /// <summary>The sound chip, as an index into <see cref="Cx.Chips"/>. The console is whatever console that chip belongs to.</summary>
    public int Chip => Volatile.Read(ref chip);
    public int Console => Cx.ConsoleOf(Chip);
    public string ChipId => Cx.Chips[Math.Clamp(Chip, 0, Cx.Chips.Count - 1)].Id;
    /// <summary>Counts up whenever the configuration changed (instances compare it to know when to look again).</summary>
    public int ConfigVersion => Volatile.Read(ref configVersion);
    public int ClaimedTones => Volatile.Read(ref claimedTones);

    public int MemberCount { get { lock (EmulatorHub.Gate) return members.Count; } }
    internal List<Bn2Plugin> MembersSnapshot() { lock (EmulatorHub.Gate) return members.ToList(); }

    /// <summary>The instance sitting on a channel, or null.</summary>
    public Bn2Plugin? Owner(int channel) { lock (EmulatorHub.Gate) return owners[channel]; }

    /// <summary>How loud a channel is right now, 0..15 (the owning instance writes it; the editor shows it).</summary>
    public int Activity(int channel) => Volatile.Read(ref activity[channel]);
    internal void SetActivity(int channel, int level) => Volatile.Write(ref activity[channel], level);

    // ---- the game's controller ----

    /// <summary>Player 1's buttons (<see cref="NesEmulator.Systems.PadButtons"/> flags): every open editor adds what its keyboard, gamepad and
    /// on-screen pad hold, so the controls are the union of them.</summary>
    public int PadBits => Volatile.Read(ref padBits);

    public void SetPad(object source, int bits)
    {
        lock (padSources)
        {
            if (bits == 0) padSources.Remove(source); else padSources[source] = bits;
            int all = 0;
            foreach (var b in padSources.Values) all |= b;
            Volatile.Write(ref padBits, all);
        }
    }

    public void ClearPad(object source) => SetPad(source, 0);

    // ---- Instrument Runaway ----

    /// <summary>The game is frozen (its CPU and picture chip stop, so its music and sound effects stop) and silent.</summary>
    public bool Runaway => Volatile.Read(ref runaway) != 0;
    /// <summary>The sound memory captured when Runaway started on a SNES game; null for the other consoles.</summary>
    public SpcSnapshot? Snapshot => Volatile.Read(ref snapshot);
    /// <summary>The sound registers and wave RAM captured when Runaway started on a Game Boy game; null for the other consoles.</summary>
    public GbSnapshot? GbState => Volatile.Read(ref gbState);
    /// <summary>Runaway on a SNES game: every instance plays the game's samples through its own S-DSP.</summary>
    public bool SamplerMode => Runaway && Snapshot != null;
    /// <summary>Runaway on a Game Boy game: every instance plays one Game Boy channel, with the game's settings, on its own sound unit.</summary>
    public bool GbSamplerMode => Runaway && GbState != null;
    /// <summary>Runaway needs a running game.</summary>
    public bool CanRunaway => Mode == 1 && RomLoaded;

    /// <summary>Freezes the game and keeps its instruments: the game's CPU, picture and sound driver stop (no more music or effects), the picture
    /// stays as it was, and on a SNES game the sound memory is captured so FL notes can play the game's samples. Off: the game goes on where it stopped.</summary>
    public void SetRunaway(bool on)
    {
        lock (EmulatorHub.Gate)
        {
            if (on == Runaway) return;
            if (on)
            {
                if (!CanRunaway) return;
                SpcSnapshot? snap; GbSnapshot? gb;
                lock (romLock) { ApplyPending(); snap = game?.CaptureSpc(); gb = game?.CaptureGb(); }
                Volatile.Write(ref snapshot, snap);
                Volatile.Write(ref gbState, gb);
                Volatile.Write(ref runaway, 1);
            }
            else ClearRunaway();
            Interlocked.Increment(ref epoch);     // readers start again from now: nothing buffered from before the freeze (or the release) is heard
            EmulatorHub.ReResolveAll(this);
            Changed();
        }
    }

    /// <summary>Test hook: puts a made-up snapshot in place of the captured one (a test ROM has no samples).</summary>
    internal void ReplaceSnapshotForTests(SpcSnapshot snap)
    {
        lock (EmulatorHub.Gate)
        {
            Volatile.Write(ref snapshot, snap);
            Volatile.Write(ref runaway, 1);
            EmulatorHub.ReResolveAll(this);
            Changed();
        }
    }

    private void ClearRunaway()
    {
        Volatile.Write(ref runaway, 0);
        Volatile.Write(ref snapshot, null);
        Volatile.Write(ref gbState, null);
    }

    // ---- claims (EmulatorHub.Gate held) ----

    internal void RemoveMember(Bn2Plugin who)
    {
        ReleaseClaim(who);
        members.Remove(who);
    }

    internal void ReleaseClaim(Bn2Plugin who)
    {
        for (int c = 0; c < Ch.Count; c++)
            if (owners[c] == who) { owners[c] = null; Volatile.Write(ref activity[c], 0); }
        UpdateClaimed();
    }

    private void UpdateClaimed()
    {
        int m = 0;
        for (int c = 0; c < Ch.ToneCount; c++) if (owners[c] != null) m |= 1 << c;
        Volatile.Write(ref claimedTones, m);
    }

    /// <summary>Which channels exist: in Direct mode the four tone channels (every console's chip takes the NES registers); in ROM mode
    /// the game's Mix, and for a NES game also each tone channel on its own (a Game Boy or SNES game has no per-channel stems yet).</summary>
    private bool Usable(int channel) => channel == Ch.Mix ? Mode == 1 : !(Mode == 1 && Console != Cx.Nes);

    private int[] AutoOrder() =>
        Mode == 1 ? (Console == Cx.Nes ? [Ch.Mix, Ch.Pulse1, Ch.Pulse2, Ch.Triangle, Ch.Noise] : [Ch.Mix]) : [Ch.Pulse1, Ch.Pulse2, Ch.Triangle, Ch.Noise];

    /// <summary>Puts an instance on this emulator and a channel of it. <paramref name="requested"/> is the Channel parameter (0 = Auto).
    /// Returns null when an Auto emulator cannot give the instance what it asks for (the hub then tries another); an explicitly
    /// named emulator always takes the instance in, with a Problem and no channel when the channel is not available.</summary>
    internal Assignment? Attach(Bn2Plugin who, int requested, int hintChannel, bool explicitEmulator)
    {
        if (SamplerMode || GbSamplerMode)
        {
            if (!members.Contains(who)) members.Add(who);
            return new Assignment(this, Ch.Sampler, null);
        }
        int ch = -1;
        string? problem = null;
        if (requested > 0)
        {
            int c = requested - 1;
            if (!Usable(c) && c != Ch.Mix && Mode == 1 && (owners[Ch.Mix] == null || owners[Ch.Mix] == who))
                ch = Ch.Mix;                       // a Game Boy or SNES game has no tone channels of its own: its whole sound goes to this instance
            else if (!Usable(c))
                problem = c == Ch.Mix ? "Mix is the game's whole sound: it exists in ROM mode only"
                                      : $"{Ch.ShortNames[c]} on its own exists for NES games only: a {Cx.Names[Console]} game plays through Mix";
            else if (owners[c] != null && owners[c] != who) problem = $"{Ch.ShortNames[c]} of emulator #{Id} is already used by another instance";
            else ch = c;
        }
        else
        {
            var order = AutoOrder();
            if (hintChannel >= 0 && Array.IndexOf(order, hintChannel) >= 0 && (owners[hintChannel] == null || owners[hintChannel] == who)) ch = hintChannel;
            else foreach (int c in order) if (owners[c] == null || owners[c] == who) { ch = c; break; }
            if (ch < 0) problem = $"all channels of emulator #{Id} are in use";
        }

        if (ch < 0 && !explicitEmulator) return null;
        if (!members.Contains(who)) members.Add(who);
        if (ch >= 0) owners[ch] = who;
        UpdateClaimed();
        return new Assignment(this, ch, problem);
    }

    // ---- configuration (any thread; each change reaches every instance on the emulator) ----

    private void Changed()
    {
        Interlocked.Increment(ref configVersion);
        foreach (var m in members) m.SyncFromEmulator(this);
    }

    /// <summary>The console or the mode changed what can run: the game is (re)loaded when in ROM mode, outputs restart, channels are chosen again.</summary>
    private void PlaceChanged()
    {
        ResetOutputs();
        if (Mode == 1) EnsureRomLoaded();
        EmulatorHub.ReResolveAll(this);
    }

    public void SetMode(int value)
    {
        lock (EmulatorHub.Gate)
        {
            value = Math.Clamp(value, 0, 1);
            if (value == mode) return;
            Volatile.Write(ref mode, value);
            if (value == 0) ClearRunaway();
            PlaceChanged();
            Changed();
        }
    }

    /// <summary>Picks the sound chip; when it belongs to another console, the emulator becomes that console.</summary>
    public void SetChip(int value)
    {
        lock (EmulatorHub.Gate)
        {
            SetChipLocked(Math.Clamp(value, 0, Cx.Chips.Count - 1));
            Changed();
        }
    }

    private void SetChipLocked(int value)
    {
        if (value == chip) return;
        int before = Console;
        Volatile.Write(ref chip, value);
        if (Console != before) PlaceChanged();
    }

    /// <summary>Switches the console: the emulator takes that console's first chip (FIX, DMG, SNES), and in ROM mode its game.</summary>
    public void SetConsole(int console)
    {
        lock (EmulatorHub.Gate)
        {
            console = Math.Clamp(console, 0, Cx.Count - 1);
            if (console != Console) SetChipLocked(Cx.DefaultChip(console));
            Changed();
        }
    }

    /// <summary>Loads a game file of any console: the emulator becomes that console and goes to ROM mode. Returns why not when the file is not a ROM.</summary>
    public string? LoadRomFile(string path)
    {
        string name = Path.GetFileName(path);
        int console;
        try
        {
            var rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out string inner);
            var kind = RomDetect.Detect(rom, inner);
            if (kind == null) { RomStatus = "not a NES, Game Boy or SNES ROM: " + name; return RomStatus; }
            // The Sega consoles join the plugin in a later wave; Cx.FromKind would quietly call such a ROM a NES game.
            if (Consoles.IsSega(kind.Value)) { RomStatus = "Sega consoles are not supported in the plugin yet: " + name; return RomStatus; }
            console = Cx.FromKind(kind.Value);
        }
        catch (Exception e)
        {
            RomStatus = $"could not read {name}: {e.Message}";
            return RomStatus;
        }
        lock (EmulatorHub.Gate)
        {
            romPaths[console] = path;
            Volatile.Write(ref mode, 1);
            if (console != Console) Volatile.Write(ref chip, Cx.DefaultChip(console));
            ResetOutputs();
            StartLoad();
            EmulatorHub.ReResolveAll(this);
            Changed();
        }
        return null;
    }

    /// <summary>Starts the emulator over, for every instance on it: instruments are cleared (voices dropped, chips rebuilt, meters zeroed) and,
    /// in ROM mode, the game restarts from power-on (which also recovers one that crashed). Nothing else about the emulator changes.</summary>
    public void Reset()
    {
        lock (EmulatorHub.Gate)
        {
            foreach (var m in members) m.RequestReset();
            for (int c = 0; c < Ch.Count; c++) Volatile.Write(ref activity[c], 0);
            ResetOutputs();
            bool wasRunaway = Runaway;
            if (Mode == 1) StartLoad();       // the running game goes on until the new one is ready, then it is replaced
            if (wasRunaway) EmulatorHub.ReResolveAll(this);
        }
    }

    /// <summary>Back to the game built into the plugin (the NES one: VRUN), in ROM mode.</summary>
    public void UseBuiltInRom()
    {
        lock (EmulatorHub.Gate)
        {
            romPaths[Cx.Nes] = null;
            Volatile.Write(ref mode, 1);
            if (Console != Cx.Nes) Volatile.Write(ref chip, Cx.DefaultChip(Cx.Nes));
            ResetOutputs();
            StartLoad();
            EmulatorHub.ReResolveAll(this);
            Changed();
        }
    }

    /// <summary>Applies what a restored instance carries, when it is the first on a new emulator.</summary>
    internal void Adopt(int newMode, int newChip, string? rom, SpcSnapshot? restoredSnapshot = null, GbSnapshot? restoredGb = null)
    {
        lock (EmulatorHub.Gate)
        {
            newChip = Math.Clamp(newChip, 0, Cx.Chips.Count - 1);
            int console = Cx.ConsoleOf(newChip);
            int modeBefore = mode, consoleBefore = Console;
            Volatile.Write(ref chip, newChip);
            romPaths[console] = string.IsNullOrWhiteSpace(rom) ? null : rom;
            Volatile.Write(ref mode, Math.Clamp(newMode, 0, 1));
            if (mode != modeBefore || console != consoleBefore)
            {
                ResetOutputs();
                EmulatorHub.ReResolveAll(this);     // (channels keep their places when nothing about the emulator changed)
            }
            if (Mode == 1)
            {
                var path = romPaths[console];
                if (path == null || File.Exists(path)) StartLoad();
                else RomStatus = "the project's game was not found: " + path;
                if (restoredGb != null && console == Cx.GameBoy)
                {
                    Volatile.Write(ref gbState, restoredGb);
                    Volatile.Write(ref runaway, 1);
                    EmulatorHub.ReResolveAll(this);
                }
                if (restoredSnapshot != null && console == Cx.Snes)
                {
                    // a project saved in Instrument Runaway: the game starts frozen and its instruments come back from the copy in the project
                    Volatile.Write(ref snapshot, restoredSnapshot);
                    Volatile.Write(ref runaway, 1);
                    EmulatorHub.ReResolveAll(this);
                }
            }
            Changed();
        }
    }

    /// <summary>The current console's game file (null: the built-in game for the NES, none for the others).</summary>
    public string? RomPath { get { lock (EmulatorHub.Gate) return romPaths[Console]; } }

    public string RomDisplayName
    {
        get
        {
            var path = romPaths[Console];
            return path != null ? Path.GetFileName(path) : Console == Cx.Nes ? "VRUN (built-in)" : "none loaded";
        }
    }

    /// <summary>The picture area's text when there is no picture to show.</summary>
    public string? PictureMessage => Mode == 0 ? "Direct Mode: Bypassing the ROM" : (PictureValid ? null : Runaway ? "Instrument Runaway: game frozen" : RomStatus);

    internal void HostRateChanged() { }

    public void Dispose() => DisposeRom();
}
