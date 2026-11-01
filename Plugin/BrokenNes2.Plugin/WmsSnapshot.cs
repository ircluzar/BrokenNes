// WideMess milestone 7a: a copy of the WideMess sound chip's memory and registers, played by WmsSampler. Two kinds: the chip's built-in instruments (Direct mode on chip WMS, WmsBank) and a frozen WideMess
// game's (Instrument Runaway on a game whose sound unit is unlocked: its 1 MB of sound RAM, the S-DSP's registers, voices 8-31's second register page and their directory).
using System.IO.Compression;

namespace BrokenNes2;

/// <summary>
/// The WideMess sound chip's memory (1 MB: the SPC700's 64 KB, then the 960 KB only the new voices and the main CPU's port reach), its S-DSP registers (voices 0-7 and the shared ones: master volume, echo,
/// FLG, DIR), the three banks of voices 8-31 (their second register page) and the new voices' directory (docs/projects/widemess-spec.md section 4). Immutable once made; every instance plays from a
/// private copy of the first 64 KB (the S-DSP's echo writes there) and shares the 960 KB above it, which a DSP only reads.
/// </summary>
public sealed class WmsSnapshot
{
    public const int RamSize = 0x100000, LowSize = 0x10000, DspSize = 128, WideSize = 3 * 128;
    public const uint Tag = 0x31534D57;    // "WMS1"
    public const byte Version = 1;
    /// <summary>The biggest section a project may carry (the 1 MB deflated is far smaller; a stored one is 1 MB and a few hundred bytes).</summary>
    public const int MaxSection = RamSize + 4096;

    public readonly byte[] Low = new byte[LowSize];             // $00000-$0FFFF: the SPC700's RAM (each sampler takes its own copy)
    public readonly byte[] High;                                 // $10000-$FFFFF: shared by every sampler of this snapshot (read only)
    public readonly byte[] Dsp = new byte[DspSize];
    public readonly byte[] Wide = new byte[WideSize];
    /// <summary>The new voices' directory (a 256-byte boundary in the 1 MB).</summary>
    public readonly int WideDirectory;
    /// <summary>The chip's built-in instruments (Direct mode on chip WMS), not a game's: the Instrument parameter picks a <see cref="WmsBank"/> patch.</summary>
    public readonly bool BuiltIn;

    public WmsSnapshot(ReadOnlySpan<byte> ram, ReadOnlySpan<byte> dsp, ReadOnlySpan<byte> wide, int wideDirectory, bool builtIn = false)
    {
        if (ram.Length != RamSize || dsp.Length != DspSize || wide.Length != WideSize) throw new ArgumentException("a WideMess snapshot is 1 MB of sound RAM, 128 DSP registers and 384 bytes of the second page");
        ram[..LowSize].CopyTo(Low);
        High = ram[LowSize..].ToArray();
        dsp.CopyTo(Dsp);
        wide.CopyTo(Wide);
        WideDirectory = wideDirectory & 0xFFF00;
        BuiltIn = builtIn;
        Instruments();        // (worked out now, on the thread that makes the snapshot: the first note on the audio thread must not have to)
    }

    /// <summary>A line for the window: what was captured.</summary>
    public string ListenNote { get; set; } = "";

    public byte RamByte(int a) { a &= RamSize - 1; return a < LowSize ? Low[a] : High[a - LowSize]; }
    private int Address20(int a) => RamByte(a) | RamByte(a + 1) << 8 | (RamByte(a + 2) & 0x0F) << 16;

    // ---- the built-in instruments ----

    private static WmsSnapshot? builtIn;

    /// <summary>The chip's built-in instruments (made once per process; on the UI thread that places the first instance on the chip).</summary>
    public static WmsSnapshot BuiltInBank
    {
        get
        {
            var b = Volatile.Read(ref builtIn);
            if (b != null) return b;
            var dsp = new byte[DspSize];
            dsp[0x0C] = dsp[0x1C] = 0x7F;      // master volume
            dsp[0x6C] = 0x20;                   // FLG: echo writes off, noise clock 0
            var wide = new byte[WideSize];
            b = new WmsSnapshot(WmsBank.Image, dsp, wide, WmsBank.DirectoryAt, builtIn: true) { ListenNote = "the WMS chip's built-in instruments" };
            Interlocked.CompareExchange(ref builtIn, b, null);
            return Volatile.Read(ref builtIn)!;
        }
    }

    // ---- a game's instruments: the samples its two directories list ----

    /// <summary>One instrument of a frozen WideMess game: from the new voices' directory (<see cref="Wide"/> true: 16-byte entries, BRR or 16-bit PCM anywhere in the 1 MB) or the S-DSP's own (4-byte entries,
    /// BRR in the first 64 KB). <see cref="Length"/> is in samples; <see cref="Sounding"/> = a voice of the game was playing it when it froze.</summary>
    public readonly record struct SampleInfo(int Index, bool WideDir, int Slot, bool Pcm, int Start, int Loop, int End, int Length, bool Loops, bool Sounding)
    {
        public double Milliseconds => Length / 32.0;
        public string Name => WideDir ? $"W#{Slot}  {(Pcm ? "PCM16" : "BRR")}  ${Start:X5}" : $"S#{Slot}  BRR  ${Start:X4}";
    }

    private SampleInfo[]? instruments;

    /// <summary>
    /// The game's instruments, as the Instrument picker numbers them: every well-formed entry of the new voices' directory (all 256 are looked at; an entry is well-formed when its sample lies inside the 1 MB
    /// with a sane length and loop: PCM in whole groups of 4 samples, BRR a chain of blocks with range 12 or less that ends with an end block, the loop on a block boundary inside it), then the S-DSP's own
    /// directory read the way the SNES sampler reads it (from the first well-formed slot until one that is not). At most 256. Empty for the built-in bank (its instruments are <see cref="WmsBank.Patches"/>).
    /// </summary>
    public IReadOnlyList<SampleInfo> Instruments()
    {
        if (instruments != null) return instruments;
        var list = new List<SampleInfo>();
        if (!BuiltIn)
        {
            for (int slot = 0; slot < 256 && list.Count < 256; slot++)
                if (TryWide(slot, out var info)) list.Add(info with { Index = list.Count, Sounding = WideSounding(slot) });
            var snes = new SpcSnapshot(Low, Dsp);
            foreach (var s in snes.Instruments())
            {
                if (list.Count >= 256) break;
                list.Add(new SampleInfo(list.Count, false, s.Slot, false, s.Start, s.Loop, s.Start + s.Blocks * 9, s.Blocks * 16, s.Loops, s.Sounding));
            }
        }
        return instruments = list.ToArray();
    }

    private bool WideSounding(int slot)
    {
        for (int v = 0; v < 24; v++) { int r = (v >> 3) * 128 + ((v & 7) << 4); if (Wide[r + 4] == slot && Wide[r + 8] != 0) return true; }
        return false;
    }

    /// <summary>Entry <paramref name="slot"/> of the new voices' directory, when it holds a well-formed sample.</summary>
    private bool TryWide(int slot, out SampleInfo info)
    {
        info = default;
        int e = WideDirectory + slot * 16;
        if (e + 16 > RamSize) return false;
        int start = Address20(e), loop = Address20(e + 4), end = Address20(e + 8), fmt = RamByte(e + 3);
        if (start == 0 && loop == 0 && end == 0) return false;
        if ((fmt & 0xFC) != 0) return false;                       // reserved bits set: not an entry
        bool pcm = (fmt & 1) != 0;
        if (pcm)
        {
            bool loops = (fmt & 2) != 0;
            if (end <= start || (end - start) % 8 != 0 || end > RamSize) return false;
            if (loops && (loop < start || loop >= end || (loop - start) % 8 != 0)) return false;
            info = new SampleInfo(0, true, slot, true, start, loop, end, (end - start) / 2, loops, false);
            return true;
        }
        // BRR at a 20-bit address
        if (start < 0x100) return false;
        int a = start, blocks = 0;
        while (blocks < 65536 && a + 9 <= RamSize)
        {
            int h = RamByte(a);
            if ((h >> 4) > 12) return false;
            blocks++;
            if ((h & 1) != 0)
            {
                bool loops = (h & 2) != 0;
                if (loops && (loop < start || loop > a || (loop - start) % 9 != 0)) return false;
                info = new SampleInfo(0, true, slot, false, start, loop, a + 9, blocks * 16, loops, false);
                return true;
            }
            a += 9;
        }
        return false;
    }

    /// <summary>The instrument an instance with this Instrument setting plays: that entry of the list when there is one, else the one the game was playing when it froze, else the first.</summary>
    public int Resolve(int index)
    {
        var list = Instruments();
        if (list.Count == 0 || (uint)index < (uint)list.Count) return index;
        for (int k = 0; k < list.Count; k++) if (list[k].Sounding) return k;
        return 0;
    }

    public bool HasInstrument(int index) => (uint)index < (uint)Instruments().Count;

    /// <summary>The envelope the game had on a voice that played this instrument (ADSR1, ADSR2, GAIN), if one did.</summary>
    public bool TryEnvelope(in SampleInfo s, out byte adsr1, out byte adsr2, out byte gain)
    {
        int best = -1, bestEnv = 0;
        if (s.WideDir)
        {
            for (int v = 0; v < 24; v++)
            {
                int r = (v >> 3) * 128 + ((v & 7) << 4);
                if (Wide[r + 4] != s.Slot || ((Wide[r + 5] & 0x80) == 0 && Wide[r + 7] == 0)) continue;
                if (best < 0 || (Wide[r + 8] != 0 && bestEnv == 0)) { best = r; bestEnv = Wide[r + 8]; }
            }
            if (best >= 0) { adsr1 = Wide[best + 5]; adsr2 = Wide[best + 6]; gain = Wide[best + 7]; return true; }
        }
        else
        {
            for (int v = 0; v < 8; v++)
            {
                int r = v * 16;
                if (Dsp[r + 4] != s.Slot || ((Dsp[r + 5] & 0x80) == 0 && Dsp[r + 7] == 0)) continue;
                if (best < 0 || (Dsp[r + 8] != 0 && bestEnv == 0)) { best = r; bestEnv = Dsp[r + 8]; }
            }
            if (best >= 0) { adsr1 = Dsp[best + 5]; adsr2 = Dsp[best + 6]; gain = Dsp[best + 7]; return true; }
        }
        adsr1 = adsr2 = gain = 0;
        return false;
    }

    /// <summary>The captured copy of a running WideMess sound chip (Instrument Runaway), or null when the unit is not unlocked (a SNES game, a locked WideMess: the SNES sampler plays those).</summary>
    public static WmsSnapshot? FromUnit(NesEmulator.Snes.APU_WMS unit)
    {
        if (!unit.Unlocked) return null;
        var ram = new byte[RamSize];
        unit.Aram.AsSpan(0, LowSize).CopyTo(ram);
        unit.SoundRamHigh.CopyTo(ram.AsSpan(LowSize));
        return new WmsSnapshot(ram, unit.Dsp.Regs, unit.Dsp.WideRegisters, unit.Dsp.WideDirectory);
    }

    // ---- saved with the project (section WMS1): version, directory, flags, then the 1 MB, the DSP and the second page deflated, a CRC-32 over all of it ----

    public byte[] ToBytes()
    {
        var raw = new byte[RamSize + DspSize + WideSize];
        Low.CopyTo(raw, 0); High.CopyTo(raw, LowSize); Dsp.CopyTo(raw, RamSize); Wide.CopyTo(raw, RamSize + DspSize);
        using var ms = new MemoryStream();
        ms.WriteByte(Version);
        ms.Write(BitConverter.GetBytes(WideDirectory));
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        uint crc = Crc32(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        ms.Write(BitConverter.GetBytes(crc));
        return ms.ToArray();
    }

    /// <summary>The snapshot a project carried, or null with the reason: a damaged (CRC, length, deflate), newer or cut section is refused, never half used.</summary>
    public static WmsSnapshot? FromBytes(byte[] b, out string? problem)
    {
        problem = null;
        if (b.Length < 9) { problem = "cut short"; return null; }
        uint crc = BitConverter.ToUInt32(b, b.Length - 4);
        if (Crc32(b.AsSpan(0, b.Length - 4)) != crc) { problem = "damaged (its checksum)"; return null; }
        if (b[0] != Version) { problem = b[0] > Version ? $"made by a newer BrokenNes2 (version {b[0]})" : "damaged (its version)"; return null; }
        int dir = BitConverter.ToInt32(b, 1);
        var raw = new byte[RamSize + DspSize + WideSize];
        try
        {
            using var z = new DeflateStream(new MemoryStream(b, 5, b.Length - 9), CompressionMode.Decompress);
            int got = 0, n;
            while (got < raw.Length && (n = z.Read(raw, got, raw.Length - got)) > 0) got += n;
            if (got != raw.Length || z.ReadByte() != -1) { problem = "damaged (its length)"; return null; }
        }
        catch (InvalidDataException) { problem = "damaged (its contents)"; return null; }
        return new WmsSnapshot(raw.AsSpan(0, RamSize), raw.AsSpan(RamSize, DspSize), raw.AsSpan(RamSize + DspSize, WideSize), dir) { ListenNote = "the WideMess instruments from the project" };
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in data) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; }
        return ~c;
    }
}
