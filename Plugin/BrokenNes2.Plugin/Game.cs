using NesEmulator;
using NesEmulator.Plugin;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>A game running inside an emulator in ROM mode, whatever its console: it is advanced one frame at a time, its sound read
/// at the host's rate, its picture copied out. Used under the emulator's lock only.</summary>
internal interface IGame : IDisposable
{
    /// <summary>0 NES, 1 Game Boy, 2 SNES, 3 Master System / Game Gear (<see cref="Cx"/>).</summary>
    int Console { get; }
    /// <summary>The NES machine when the game is a NES one: it gives per-channel stems by replaying the register writes.</summary>
    NesAudioMachine? Nes { get; }
    /// <summary>The stems of a Sega game's sound unit (its PSG, FM and DAC voices on their own), or null for every other game.</summary>
    IGameStems? Stems { get; }
    /// <summary>The sound chip (index into <see cref="Cx.Chips"/>) the game was built with.</summary>
    int ChipIndex { get; set; }
    int HostSampleRate { get; set; }
    double FramesPerSecond { get; }
    long FramesRun { get; }
    bool Crashed { get; }
    string? CrashInfo { get; }
    /// <summary>The cores and chips it runs on, for a status line.</summary>
    string Describe();
    void RunFrame();
    /// <summary>Mono samples at the host rate that the last frames made, without running another.</summary>
    int ReadAvailable(Span<float> dest);
    /// <summary>The last picture as RGBA into <paramref name="rgba"/> (at most 512 x 480); <c>dispW</c> x <c>dispH</c> is the shape it is shown in.</summary>
    void CopyFrame(byte[] rgba, out int w, out int h, out int dispW, out int dispH);
    /// <summary>Switches the sound chip while the game runs; false when the game has to start again on it.</summary>
    bool TrySetChip(Chip chip);
    /// <summary>Player 1's buttons for the next frames (<see cref="NesEmulator.Systems.PadButtons"/> flags).</summary>
    void SetPad(int buttons);
    /// <summary>A copy of the SNES game's sound memory and DSP registers (null for every other game).</summary>
    SpcSnapshot? CaptureSpc();
    /// <summary>A copy of the Game Boy game's sound registers and wave RAM (null for every other game).</summary>
    GbSnapshot? CaptureGb();
    /// <summary>A copy of a Master System, Game Gear or Genesis game's sound chips: the PSG's registers, the YM2413's, or the YM2612's channel patches and the DAC sounds found in its recent writes (null for every other game).</summary>
    SegaSnapshot? CaptureSega();

    // ---- the whole machine, for a project (see GameState.cs): taken and put back between frames, under the emulator's lock ----

    /// <summary>A stable id of the game: the ROM's SHA-1 in lower-case hex (a NES game's has "nes" in front, a SNES game's "sfc").</summary>
    string GameId { get; }
    /// <summary>Whether <see cref="SaveGameState"/> and <see cref="LoadGameState"/> work for this game (a SNES game has no state yet).</summary>
    bool StateSupported { get; }
    /// <summary>Why not, in words fit for the user.</summary>
    string StateUnsupportedReason { get; }
    /// <summary>The whole machine as bytes: CPU, picture chip, sound chips, memory, cartridge. Throws when it cannot be written (a part that would be left out is a failure, never a half machine).</summary>
    byte[] SaveGameState();
    /// <summary>Puts such bytes back and carries the frame count on from <paramref name="framesRun"/>. Throws <see cref="InvalidDataException"/> (the message is for the user) when the state belongs to another game, another sound unit or is damaged; the machine is then as it was.</summary>
    void LoadGameState(byte[] state, long framesRun);
    /// <summary>The main work RAM, for tests and for the corruption features to come: its size in bytes (0: the game has none to reach), a byte by offset (-1 outside) and a write (false outside).
    /// NES: the 2 KB of system RAM ($0000-$07FF). Master System / Game Gear: the 8 KB at $C000. Genesis: the 64 KB at $FF0000. Game Boy / Color: the 8 KB at $C000.</summary>
    int WorkRamSize { get; }
    int PeekWorkRam(int offset);
    bool PokeWorkRam(int offset, byte value);

    // ---- corruption (see Corruption.cs): the farm that records what the game was handed, for the games that run on a console session ----

    /// <summary>The farm of a console game: it records the pad and every corruption write from the game's power-on, so a project (or a stockpile entry) can describe the game's place; null for a NES game.</summary>
    NesEmulator.Corruption.ConsoleFarm? Farm { get; }
}

/// <summary>A running game's voices on their own, by plugin channel (<see cref="Ch"/>), at the host's rate and in step with the game's whole mix.</summary>
internal interface IGameStems
{
    /// <summary>Whether the game's sound unit has this channel's voice (a Game Gear game has no FM; a Master System game on the PSG chip has none either).</summary>
    bool Has(int channel);
    /// <summary>Which channels are wanted (a bit per <see cref="Ch"/> id); the unit makes only those, and a newly wanted one starts in step with the mix from the next frame.</summary>
    void Want(int channelMask);
    /// <summary>Moves the finished frames of a wanted channel out (after a frame has run).</summary>
    int Read(int channel, Span<float> dest);
}

/// <summary>A NES game on <see cref="NesAudioMachine"/>: the best CPU and PPU (FIX), the chosen NES sound chip.</summary>
internal sealed class NesGame : IGame
{
    private readonly NesAudioMachine m;

    public NesGame(byte[] rom, Chip chip, int hostRate)
    {
        m = new NesAudioMachine(rom, null, Cx.BestCpu(Cx.Nes), Cx.BestPpu(Cx.Nes), chip.Id) { HostSampleRate = hostRate };
        GameId = "nes" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(rom)).ToLowerInvariant();
    }

    // ---- the whole machine: the NES core's own JSON state plus the APU register latch (the latch is not in the state, and a per-channel stem is made from it) ----

    private static readonly byte[] StateMagic = "NES1"u8.ToArray();
    private const int LatchLength = 0x18;

    public string GameId { get; }
    public bool StateSupported => true;
    public string StateUnsupportedReason => "";

    public byte[] SaveGameState()
    {
        string json = m.Nes.SaveState();
        // The NES writer leaves out a part it cannot write (and says nothing): a state without its CPU, picture chip or sound chip must never be kept as if it were the machine.
        if (string.IsNullOrEmpty(json)) throw new InvalidOperationException("the NES machine wrote no state");
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
            foreach (var part in new[] { "cpu", "ppu", "apu" })
                if (!doc.RootElement.TryGetProperty(part, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.String || (el.GetString()?.Length ?? 0) <= 2)
                    throw new InvalidOperationException($"the NES machine wrote no {part} state");
        var latch = new byte[LatchLength];
        uint written = m.CopyApuLatch(latch);
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var all = new byte[4 + 4 + LatchLength + body.Length];
        StateMagic.CopyTo(all, 0);
        BitConverter.TryWriteBytes(all.AsSpan(4), written);
        latch.CopyTo(all, 8);
        body.CopyTo(all, 8 + LatchLength);
        return all;
    }

    public void LoadGameState(byte[] state, long framesRun)
    {
        if (state.Length < 8 + LatchLength + 2 || !state.AsSpan(0, 4).SequenceEqual(StateMagic)) throw new InvalidDataException("This is not a NES game state.");
        uint written = BitConverter.ToUInt32(state, 4);
        var latch = state.AsSpan(8, LatchLength).ToArray();
        string json = System.Text.Encoding.UTF8.GetString(state, 8 + LatchLength, state.Length - 8 - LatchLength);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var (key, running, what) in new[] { ("cpuCoreId", m.CpuCoreId, "CPU core"), ("ppuCoreId", m.PpuCoreId, "picture core"), ("apuCoreId", m.ApuCoreId, "sound unit") })
            {
                string saved = root.TryGetProperty(key, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() ?? "" : "";
                // a state is never loaded into a different part, and no part is swapped to make it fit
                if (!string.Equals(saved, running, StringComparison.Ordinal)) throw new InvalidDataException($"The game was saved with the {saved.Replace("CPU_", "").Replace("PPU_", "").Replace("APU_", "")} {what}, but this build runs it on the {running.Replace("CPU_", "").Replace("PPU_", "").Replace("APU_", "")} one.");
            }
            foreach (var part in new[] { "cpu", "ppu", "apu", "ram" })
                if (!root.TryGetProperty(part, out _)) throw new InvalidDataException($"The saved NES state has no {part} data.");
        }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException("The saved NES state is damaged: " + e.Message); }
        m.RestoreState(json, framesRun);
        m.Nes.SetApuLatch(latch, written);
    }

    /// <summary>The state with the sound chip's audio-buffer bookkeeping zeroed (what is queued, the resampling phase, the output filters' memory): the NES core drops its queued audio on purpose when a state is loaded, so a machine that came back from a state and the machine
    /// that never left have the same emulation state but different bookkeeping. Tests compare machines through this.</summary>
    internal byte[] SaveComparableState()
    {
        var all = SaveGameState();
        string json = System.Text.Encoding.UTF8.GetString(all, 8 + LatchLength, all.Length - 8 - LatchLength);
        foreach (var name in new[] { "ringWrite", "ringRead", "ringCount", "samplePhase", "lpLast", "dcLastIn", "dcLastOut" }) json = ZeroField(json, name);
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        return [.. all.AsSpan(0, 8 + LatchLength), .. body];
    }

    private static string ZeroField(string s, string name)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        while (true)
        {
            int k = s.IndexOf(name, i, StringComparison.Ordinal);
            if (k < 0) { sb.Append(s, i, s.Length - i); break; }
            int p = k + name.Length;
            while (p < s.Length && (s[p] == '\\' || s[p] == '"')) p++;           // the closing quote, escaped or not
            if (p >= s.Length || s[p] != ':') { sb.Append(s, i, p - i); i = p; continue; }
            p++;
            int q = p;
            while (q < s.Length && (char.IsDigit(s[q]) || s[q] is '-' or '+' or '.' or 'e' or 'E')) q++;
            sb.Append(s, i, p - i).Append('0');
            i = q;
        }
        return sb.ToString();
    }

    public int WorkRamSize => 0x800;
    public int PeekWorkRam(int offset) => (uint)offset < 0x800u ? m.PeekRam((ushort)offset) : -1;
    public bool PokeWorkRam(int offset, byte value) { if ((uint)offset >= 0x800u) return false; m.PokeRam((ushort)offset, value); return true; }

    public int Console => Cx.Nes;
    public int ChipIndex { get; set; }
    public NesAudioMachine? Nes => m;
    public IGameStems? Stems => null;
    public int HostSampleRate { get => m.HostSampleRate; set => m.HostSampleRate = value; }
    public double FramesPerSecond => NesAudioMachine.FramesPerSecond;
    public long FramesRun => m.FramesRun;
    public bool Crashed => m.Crashed;
    public string? CrashInfo => m.CrashInfo;
    public string Describe() => $"CPU {m.CpuCoreId.Replace("CPU_", "")}   PPU {m.PpuCoreId.Replace("PPU_", "")}   sound {m.ApuCoreId.Replace("APU_", "")} ({m.NativeSampleRate} Hz)";
    public void RunFrame() => m.RunFrame();
    public int ReadAvailable(Span<float> dest) => m.ReadAvailable(dest);

    public void CopyFrame(byte[] rgba, out int w, out int h, out int dispW, out int dispH)
    {
        var src = m.FrameBuffer;
        Buffer.BlockCopy(src, 0, rgba, 0, Math.Min(src.Length, 256 * 240 * 4));
        w = dispW = 256; h = dispH = 240;
    }

    public bool TrySetChip(Chip chip)
    {
        m.SetApuCore(chip.Id);
        return true;
    }

    private readonly bool[] padNes = new bool[8];

    public void SetPad(int buttons)
    {
        Inputs.ToNes((PadButtons)buttons, padNes);
        m.Nes.SetInput(padNes);
    }

    public SpcSnapshot? CaptureSpc() => null;
    public GbSnapshot? CaptureGb() => null;
    public SegaSnapshot? CaptureSega() => null;

    public NesEmulator.Corruption.ConsoleFarm? Farm => null;      // (the NES keeps its own corruption path in the desktop app; the plugin's corruption window is for the console games)

    public void Dispose() { }
}

/// <summary>A Game Boy or SNES game on BrokenNes's console sessions: the console's own CPU, picture chip and sound unit (the best of its
/// family). Its stereo sound is mixed to mono and resampled to the host's rate.</summary>
internal sealed class SessionGame : IGame, IGameStems
{
    private readonly IConsoleSession s;
    private readonly byte[] pristineRom;
    private readonly string fileName;
    private readonly ConsoleKind kind;
    private int lastPad;
    private readonly StreamResampler resampler = new();
    private readonly short[] pcm = new short[16384];
    private readonly float[] mono = new float[8192];
    private int hostRate;
    private long frames;
    private string? crash;

    // ---- stems (Master System, Game Gear and Genesis games): the sound unit's voices on their own, brought to the host rate by a resampler each that starts in step with the mix's ----
    private readonly StreamResampler?[] stemRes = new StreamResampler?[Ch.Total];
    private readonly float[] stemTmp = new float[8192];
    private IVoiceStems? armedUnit;
    private int wantedMask;

    public SessionGame(byte[] rom, ConsoleKind kind, Chip chip, int hostRate, string fileName = "")
        : this(rom, kind, chip, hostRate, fileName,
               // the file name matters to the Master System family: its region tag and its SG-1000 / SC-3000 extensions come from it. The session gets its own copy of the ROM: a corruption of the ROM must not reach the bytes a later replay starts from.
               ConsoleSessions.Create(kind, (byte[])rom.Clone(), CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), ApuFor(kind, chip), null, fileName), null, null) { }

    /// <summary>A game around a session that was built elsewhere (a stockpile entry's replay or saved state): <paramref name="anchor"/> and <paramref name="appliedWrites"/> say how it got where it is, so the farm goes on recording from there.</summary>
    public SessionGame(byte[] pristineRom, ConsoleKind kind, Chip chip, int hostRate, string fileName, IConsoleSession session, NesEmulator.Corruption.Anchor? anchor, IReadOnlyList<NesEmulator.Corruption.CorruptionWrite>? appliedWrites, DacRecorder? dac = null)
    {
        Console = Cx.FromKind(kind);
        this.kind = kind; this.pristineRom = pristineRom; this.fileName = fileName; s = session;
        if (dac != null) dacRecorder = dac;
        HostSampleRate = hostRate;
        HookDacRecorder();
        HookSpcLog();
        Farm = new NesEmulator.Corruption.ConsoleFarm();
        Farm.Attach(s, pristineRom, fileName, new NesEmulator.Corruption.MachineSpec { Cpu = CoreCatalog.Default(kind, CoreSlot.Cpu), Ppu = CoreCatalog.Default(kind, CoreSlot.Ppu), Apu = ApuFor(kind, chip), Note = s.Description }, coresAreOwn: true);
        if (anchor != null)
        {
            Farm.Rebase(s, anchor, appliedWrites?.Where(w => w.At == 0).ToList());
            frames = anchor.Kind == NesEmulator.Corruption.AnchorKind.State ? anchor.Frame : anchor.Frames;      // the game has run as many frames as its place says (the frame count of a game is frames since power-on)
        }
    }

    public NesEmulator.Corruption.ConsoleFarm? Farm { get; }
    internal IConsoleSession Session => s;
    internal byte[] PristineRom => pristineRom;
    internal string FileName => fileName;
    internal ConsoleKind Kind => kind;
    internal DacRecorder Dac => dacRecorder;

    // ---- Instrument Runaway on a Genesis game: the writes the game makes to the YM2612's DAC register, kept so the sounds in them can be found ----
    private DacRecorder dacRecorder = new();
    private MdSoundUnit? dacHooked;

    /// <summary>Taps the Genesis sound unit's port writes (a unit that was swapped for another gets tapped when it first runs).</summary>
    private void HookDacRecorder()
    {
        if (Concrete is not NesEmulator.Sega.MdSession md || md.Board.Sound is not MdSoundUnit unit || ReferenceEquals(unit, dacHooked)) return;
        if (dacHooked != null) { dacHooked.YmPortTap = null; dacHooked.PsgPortTap = null; }
        genLog ??= new GenWriteLog();
        genLog.Begin(unit, md.Board.Timeline.MasterHz);       // the log of the chips' writes starts from this unit's registers as they are now
        unit.YmPortTap = TapYm;
        unit.PsgPortTap = TapPsg;
        dacHooked = unit;
    }

    // ---- Instrument Runaway on a Genesis game: every write to the YM2612's registers and the PSG, kept so the recipes of what its driver does can be learned (see GenRecipe.cs) ----
    private GenWriteLog? genLog;
    private void TapYm(long master, int port, byte value) { dacRecorder.OnWrite(master, port, value); genLog!.OnYm(master, port, value); }
    private void TapPsg(long master, byte value) => genLog!.OnPsg(master, value);

    // ---- Instrument Runaway on a SNES game: every write the SPC700 makes to the S-DSP, kept so the recipes of what its driver does can be learned (see SpcRecipe.cs) ----
    private SpcWriteLog? spcLog;
    private NesEmulator.Snes.APU_SFC? spcHooked;
    /// <summary>How many seconds of the driver's chip writes Instrument Runaway turns into recipes when it starts (the last ones: a game that has played for longer is listened to for its last <see cref="ListenWindowSeconds"/>).</summary>
    public const double ListenWindowSeconds = SpcRecipes.ListenWindowSeconds;

    /// <summary>Taps the SPC700's DSP writes (a unit that was swapped for another, or parked and swapped back, gets tapped when it first runs). The ring is made once, with the game.</summary>
    private void HookSpcLog()
    {
        if (s is not SnesSession snes || snes.Apu is not NesEmulator.Snes.APU_SFC spc || ReferenceEquals(spc, spcHooked)) return;
        if (spcHooked != null) spcHooked.DspWriteTap = null;
        spcLog ??= new SpcWriteLog();
        spcLog.Attach(spc);                                  // the log starts from this unit's registers as they are now
        spcHooked = spc;
    }

    internal SpcWriteLog? SpcLog => spcLog;

    /// <summary>Forgets what the sound driver did so far: from now on the log (and the recipes learned from it) are of the game as it is after a change made to the running machine.</summary>
    internal void RestartListening()
    {
        if (spcHooked != null) spcLog?.Begin(spcHooked.Dsp.Regs);
        if (dacHooked != null && Concrete is NesEmulator.Sega.MdSession md) genLog?.Begin(dacHooked, md.Board.Timeline.MasterHz);
    }

    public int Console { get; }
    public int ChipIndex { get; set; }
    public NesAudioMachine? Nes => null;
    public int HostSampleRate { get => hostRate; set { hostRate = value; resampler.SetRates(s.SampleRate, value); foreach (var r in stemRes) r?.SetRates(s.SampleRate, value); } }
    public double FramesPerSecond => s.FramesPerSecond;
    public long FramesRun => frames;
    public bool Crashed => crash != null;
    public string? CrashInfo => crash;
    public string Describe() => s.Description;

    public void RunFrame()
    {
        if (crash != null) return;
        try
        {
            HookDacRecorder();
            HookSpcLog();
            Farm?.OnFrame((ushort)lastPad, 0);          // what the game was handed for this frame (allocation-free), so its place can be described
            s.RunFrame();
            frames++;
            int n;
            while ((n = s.ReadSamples(pcm)) > 0)
            {
                int count = n / 2;
                for (int i = 0; i < count; i += mono.Length)
                {
                    int take = Math.Min(Math.Min(mono.Length, count - i), resampler.FreeSpace);
                    if (take <= 0) break;
                    for (int k = 0; k < take; k++) mono[k] = (pcm[(i + k) * 2] + pcm[(i + k) * 2 + 1]) * (0.5f / 32768f);
                    resampler.Write(mono.AsSpan(0, take));
                }
            }
            DrainStems();
        }
        catch (Exception e)
        {
            crash = e.Message;
            BrokenNes.Fruity.Diag.Log("game crashed: " + e);
        }
    }

    public int ReadAvailable(Span<float> dest) => resampler.Read(dest);

    /// <summary>The console session underneath the picture-bridge wrapper (a Master System, Game Gear or Genesis session is always built wrapped, so the plugin's pattern matches on the concrete session type look through it).</summary>
    private NesEmulator.Systems.IConsoleSession? Concrete => s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;

    // ---- stems ----

    /// <summary>The session's sound unit when it has stems (Master System, Game Gear, Genesis); read each time, since an APU swap replaces the unit.</summary>
    private IVoiceStems? Unit => Concrete switch
    {
        NesEmulator.Sega.SmsSession sms => sms.Board.Sound as IVoiceStems,
        NesEmulator.Sega.MdSession md => md.Board.Sound as IVoiceStems,
        _ => null,
    };

    public IGameStems? Stems => Unit != null ? this : null;

    /// <summary>The unit's stem for a plugin channel, or -1 when the unit has none. The PSG's four voices are channels 0 to 3 everywhere; FM 1 to 9 follow the PSG's stems (the Genesis has six); the Genesis DAC and the
    /// Master System chip's five rhythm voices have their channels.</summary>
    internal static int StemOf(IVoiceStems u, int channel)
    {
        if (channel is >= Ch.Pulse1 and <= Ch.Noise) return channel;
        if (u is NesEmulator.Sega.MdSoundUnit)
        {
            if (channel is >= Ch.Fm1 and < Ch.Fm1 + 6) return NesEmulator.Sega.SegaStems.MdFm1 + (channel - Ch.Fm1);
            if (channel == Ch.Dac) return NesEmulator.Sega.SegaStems.MdDac;
        }
        else if (u is NesEmulator.Sega.SmsFmSound)
        {
            if (channel is >= Ch.Fm1 and <= Ch.Fm9) return NesEmulator.Sega.SegaStems.SmsFm1 + (channel - Ch.Fm1);
            if (channel is >= Ch.Bass and <= Ch.Cymbal) return NesEmulator.Sega.SegaStems.SmsRhythm1 + (channel - Ch.Bass);
        }
        return -1;
    }

    public bool Has(int channel) => (uint)channel < Ch.Total && Unit is { } u && StemOf(u, channel) >= 0;

    public void Want(int channelMask)
    {
        var u = Unit;
        if (u == null) return;
        if (!ReferenceEquals(u, armedUnit))
        {
            // a new unit (the game started, or the sound chip was swapped): it starts with nothing wanted
            armedUnit = u; wantedMask = 0;
            foreach (var r in stemRes) r?.Clear();
        }
        if (channelMask == wantedMask) return;
        long unitMask = 0;
        for (int c = 0; c < Ch.Total; c++)
        {
            int idx = StemOf(u, c);
            if (idx < 0 || (channelMask >> c & 1) == 0) continue;
            unitMask |= 1L << idx;
            if ((wantedMask >> c & 1) == 0)
            {
                stemRes[c] ??= new StreamResampler();
                stemRes[c]!.CopyPhaseFrom(resampler);
            }
        }
        u.StemMask = unitMask;
        wantedMask = channelMask;
    }

    /// <summary>After a frame: every wanted voice's finished frames go through its own resampler (the same rates and phase as the mix's, so they add up to it).</summary>
    private void DrainStems()
    {
        var u = Unit;
        if (u == null || wantedMask == 0 || !ReferenceEquals(u, armedUnit)) return;
        for (int c = 0; c < Ch.Total; c++)
        {
            if ((wantedMask >> c & 1) == 0) continue;
            int idx = StemOf(u, c);
            if (idx < 0) continue;
            var res = stemRes[c]!;
            int n;
            while ((n = u.ReadStem(idx, stemTmp)) > 0)
                for (int off = 0; off < n; )
                {
                    int take = Math.Min(n - off, res.FreeSpace);
                    if (take <= 0) break;
                    res.Write(stemTmp.AsSpan(off, take));
                    off += take;
                }
        }
    }

    public int Read(int channel, Span<float> dest) => (uint)channel < Ch.Total ? stemRes[channel]?.Read(dest) ?? 0 : 0;

    public void CopyFrame(byte[] rgba, out int w, out int h, out int dispW, out int dispH)
    {
        var f = s.Frame;
        w = Math.Min(s.FrameWidth, 512);
        h = Math.Min(s.FrameHeight, 480);
        int stride = s.FrameWidth;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = f[y * stride + x];
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(c >> 16); rgba[o + 1] = (byte)(c >> 8); rgba[o + 2] = (byte)c; rgba[o + 3] = 255;
            }
        dispW = s.DisplayWidth;
        dispH = h >= 400 ? h / 2 : h;     // a SNES hi-res / interlaced frame has twice the lines it is shown with
    }

    public void SetPad(int buttons) { lastPad = buttons; s.SetPad(0, (PadButtons)buttons); }

    public SpcSnapshot? CaptureSpc()
    {
        if (s is not SnesSession snes || snes.Apu is not NesEmulator.Snes.APU_SFC spc) return null;
        var snap = new SpcSnapshot(spc.Aram, spc.Dsp.Regs);
        if (spcLog == null) { snap.ListenNote = "this sound unit cannot be listened to"; return snap; }
        var recipes = SpcRecipes.Derive(spcLog, spc.SamplesProduced, (long)(ListenWindowSeconds * 32000), spc.Dsp.Regs, out var stats);
        snap.Recipes = recipes;
        snap.ListenNote = recipes.Length > 0 ? stats.Short() : stats.KeyOns == 0 ? $"The game played no notes in the {stats.WindowSeconds:0.#} s it was listened to: let it play, then switch Runaway on." : "Listened, but no voice could be learned: " + stats;
        return snap;
    }

    public GbSnapshot? CaptureGb() =>
        s is GbSession gb && gb.Apu is NesEmulator.Gb.APU_GB apu ? GbSnapshot.FromApu(apu) : null;

    public SegaSnapshot? CaptureSega()
    {
        switch (Concrete)
        {
            case NesEmulator.Sega.SmsSession sms:
            {
                var t = sms.Board.Timeline;
                double clock = t.MasterHz / t.Z80.Divider;
                return sms.Board.Sound switch
                {
                    SmsFmSound fm => SegaSnapshot.FromOpll(fm, clock),
                    SmsSound psg => SegaSnapshot.FromPsg(psg.Psg, clock),
                    _ => null,                       // a NES chip playing the Master System's registers (the mix lab): no Sega chip to copy
                };
            }
            case NesEmulator.Sega.MdSession md when md.Board.Sound is MdSoundUnit unit:
            {
                var snap = SegaSnapshot.FromOpn(unit, md.Board.Timeline.MasterHz, dacRecorder);
                if (genLog == null) { snap.ListenNote = "this sound unit cannot be listened to"; return snap; }
                // which voices sound now (a voice that sounds when the listening ends and was not keyed on inside it is a held voice)
                Span<NesEmulator.Sega.HubVoice> voices = stackalloc NesEmulator.Sega.HubVoice[Ym2612.VoiceCount];
                unit.Fm.DescribeVoices(voices);
                var fmNow = new bool[6]; var psgNow = new bool[4];
                for (int i = 0; i < 6; i++) fmNow[i] = voices[i].Level > 0;
                for (int i = 0; i < 4; i++) psgNow[i] = unit.Psg.Volume(i) < 15;
                snap.Recipes = GenRecipes.Derive(genLog, unit.Clock, (long)(GenRecipes.ListenWindowSeconds * 32000), fmNow, psgNow, out var stats);
                snap.ListenNote = snap.Recipes.Length > 0 ? stats.Short() : stats.KeyOns == 0 ? $"The game played no notes in the {stats.WindowSeconds:0.#} s it was listened to: let it play, then switch Runaway on." : "Listened, but no voice could be learned: " + stats;
                return snap;
            }
        }
        return null;
    }

    /// <summary>Test hook: starts a tone on the running SNES sound unit, as a game's sound driver would (a test ROM has no sound driver).</summary>
    internal void PokeSnesTone()
    {
        if (s is not SnesSession snes || snes.Apu is not NesEmulator.Snes.APU_SFC spc) return;
        Farm?.Poison("a test tone was written to the sound unit");
        var t = SpcSnapshot.TestSample();
        Array.Copy(t.Aram, 0x100, spc.Aram, 0x100, 0x120);
        for (int i = 0; i < SpcSnapshot.DspSize; i++) spc.Dsp.Write(i, t.Dsp[i]);
        spc.Dsp.Write(0x00, 0x50); spc.Dsp.Write(0x01, 0x50);      // voice 0: volume
        spc.Dsp.Write(0x02, 0x00); spc.Dsp.Write(0x03, 0x10);      // pitch $1000
        spc.Dsp.Write(0x04, 0x00);                                 // sample 0
        spc.Dsp.Write(0x05, 0xFF); spc.Dsp.Write(0x06, 0xE0);      // envelope
        spc.Dsp.Write(0x4C, 0x01);                                 // key on
    }

    /// <summary>The sound unit a game of this kind runs on: the chip's own (<see cref="Cx.RomApu"/>), except that a Game Gear game takes the Game Gear's menu id (the same unit, with its stereo port).</summary>
    internal static string ApuFor(ConsoleKind kind, Chip chip) => kind == ConsoleKind.GameGear ? CoreCatalog.Default(kind, CoreSlot.Apu) : Cx.RomApu(chip);

    public bool TrySetChip(Chip chip)
    {
        if (!s.TrySwapCore(CoreSlot.Apu, ApuFor(s.Console, chip))) return false;
        resampler.SetRates(s.SampleRate, hostRate);
        Farm?.Poison($"the sound chip was changed to {chip.Id}");
        return true;
    }

    // ---- the whole machine: the session's own state file (a ConsoleStateFile: it names the game and the sound unit it belongs to, and loading refuses another) ----

    public string GameId => s.GameId;
    /// <summary>Tests only (hook 121): every console game reports that it cannot keep its state, as a console without save states would, so the project path that rebuilds a game by replaying its history stays tested now that every console has states.</summary>
    internal static volatile bool NoStateForTests;

    public bool StateSupported => s.SupportsState && !NoStateForTests;
    /// <summary>Said to the user when a project is saved or opened: short, and the same for every console that has no state (the session's own words are for its menus).</summary>
    public string StateUnsupportedReason => StateSupported ? "" : $"{Cx.RomNames[Console]} games cannot keep their state in a project yet";
    public byte[] SaveGameState() => s.SaveState();

    public void LoadGameState(byte[] state, long framesRun)
    {
        s.LoadState(state);                       // throws InvalidDataException and leaves the machine as it was
        frames = framesRun;
        RestartListening();                                               // the chip writes it holds are those of another machine: it starts again from the registers of this one
        Farm?.Poison("the project's saved machine was put back");         // what the farm recorded is the game since power-on, which is not how this machine got here
        // the sound that was queued before is not the sound of the restored machine: every resampler starts clean at the rates it runs at
        resampler.Clear();
        foreach (var r in stemRes) r?.Clear();
    }

    public int WorkRamSize => Concrete switch
    {
        NesEmulator.Sega.SmsSession => 0x2000,
        NesEmulator.Sega.MdSession => 0x10000,
        GbSession gb => gb.WorkRamSize,
        SnesSession => 0x20000,
        _ => 0,
    };

    public int PeekWorkRam(int offset) => Concrete switch
    {
        NesEmulator.Sega.SmsSession sms => (uint)offset < (uint)sms.Board.Ram.Length ? sms.Board.Ram[offset] : -1,
        NesEmulator.Sega.MdSession md => (uint)offset < (uint)md.Board.Ram.Length ? md.Board.Ram[offset] : -1,
        GbSession gb => gb.PeekWorkRam(offset),
        SnesSession snes => (uint)offset < (uint)snes.Board.Wram.Length ? snes.Board.Wram[offset] : -1,
        _ => -1,
    };

    public bool PokeWorkRam(int offset, byte value)
    {
        bool done = false;
        switch (Concrete)
        {
            case NesEmulator.Sega.SmsSession sms when (uint)offset < (uint)sms.Board.Ram.Length: sms.Board.Ram[offset] = value; done = true; break;
            case NesEmulator.Sega.MdSession md when (uint)offset < (uint)md.Board.Ram.Length: md.Board.Ram[offset] = value; done = true; break;
            case GbSession gb: done = gb.PokeWorkRam(offset, value); break;
            case SnesSession snes when (uint)offset < (uint)snes.Board.Wram.Length: snes.Board.Wram[offset] = value; done = true; break;
        }
        // a write the farm did not make is remembered all the same (as a corruption event), or the place the game is in could no longer be described
        if (done) Farm?.NoteWrites(new[] { new NesEmulator.Corruption.CorruptionWrite(Concrete is GbSession or SnesSession ? "WRAM" : "RAM", offset, value) });
        return done;
    }

    public void Dispose() { Farm?.Detach(); s.Dispose(); }
}
